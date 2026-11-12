using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MetaCodex.AI
{
    /// Client of the local AI service (local-ai/server, docs/local-ai.md): OpenAI-style speech-to-text, chat with tools
    /// and text-to-speech on 127.0.0.1 with a bearer token. Requests run off the main thread; every callback
    /// is queued to the main thread with (result, error) where exactly one is null.
    public sealed class QvacClient
    {
        public string BaseUrl;
        public string Token;
        public string LastError { get; private set; }
        public bool Online { get; private set; }
        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private readonly ConcurrentQueue<Action> _main;
        private int _inFlight;
        /// Requests whose callback has not been queued yet (tests wait for 0).
        public int InFlight => Volatile.Read(ref _inFlight);

        public QvacClient(string baseUrl, string token, ConcurrentQueue<Action> mainThread)
        {
            BaseUrl = baseUrl;
            Token = token;
            _main = mainThread;
        }

        private HttpRequestMessage Req(HttpMethod m, string path, HttpContent content)
        {
            var r = new HttpRequestMessage(m, BaseUrl.TrimEnd('/') + path) { Content = content };
            if (!string.IsNullOrEmpty(Token)) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            return r;
        }

        private async Task<byte[]> SendRaw(HttpMethod m, string path, HttpContent content, int seconds)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
                using (var res = await Http.SendAsync(Req(m, path, content), cts.Token).ConfigureAwait(false))
                {
                    var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode)
                    {
                        string text = Encoding.UTF8.GetString(bytes);
                        string msg = text;
                        try { msg = (string)JObject.Parse(text)["error"] ?? text; } catch (Exception) { }
                        throw new Exception($"{(int)res.StatusCode}: {(msg.Length > 200 ? msg.Substring(0, 200) : msg)}");
                    }
                    Online = true;
                    LastError = null;
                    return bytes;
                }
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException) LastError = $"the AI service did not answer in {seconds} s ({path})";
                else if (ex is HttpRequestException) { Online = false; LastError = "AI service not reachable at " + BaseUrl; }
                else LastError = ex.Message;
                throw new Exception(LastError, ex);
            }
        }

        public async Task<JToken> Json(HttpMethod m, string path, JToken body = null, int seconds = 15)
        {
            var content = body == null ? null : new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            var bytes = await SendRaw(m, path, content, seconds).ConfigureAwait(false);
            string text = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(text) ? new JObject() : JToken.Parse(text);
        }

        /// Runs `work` on a worker thread; `onMain` gets (result, null) or (null, error) on the main thread.
        public void Call<T>(Func<Task<T>> work, Action<T, string> onMain) where T : class
        {
            Interlocked.Increment(ref _inFlight);
            Task.Run(async () =>
            {
                T r = null;
                string err = null;
                try { r = await work().ConfigureAwait(false); }
                catch (Exception ex) { err = ex.Message; }
                _main.Enqueue(() => onMain?.Invoke(r, err));
                Interlocked.Decrement(ref _inFlight);
            });
        }

        public void Health(Action<JToken, string> cb) => Call(() => Json(HttpMethod.Get, "/health", null, 6), cb);
        public void Status(Action<JToken, string> cb) => Call(() => Json(HttpMethod.Get, "/status", null, 5), cb);
        public void Install(string[] kinds, Action<JToken, string> cb) => Call(() => Json(HttpMethod.Post, "/v1/models/install", new JObject { ["kinds"] = new JArray(kinds) }), cb);
        public void Remove(string[] kinds, Action<JToken, string> cb) => Call(() => Json(HttpMethod.Post, "/v1/models/remove", new JObject { ["kinds"] = new JArray(kinds) }, 30), cb);
        public void Unload(string[] kinds, Action<JToken, string> cb) => Call(() => Json(HttpMethod.Post, "/unload", new JObject { ["kinds"] = new JArray(kinds) }, 30), cb);
        public void Config(JObject cfg, Action<JToken, string> cb) => Call(() => Json(HttpMethod.Post, "/config", cfg), cb);
        public void Pause(Action<JToken, string> cb) => Call(() => Json(HttpMethod.Post, "/pause"), cb);
        public void Resume(Action<JToken, string> cb) => Call(() => Json(HttpMethod.Post, "/resume"), cb);
        public void Shutdown() => Call(() => Json(HttpMethod.Post, "/shutdown", null, 2), (JToken _, string __) => { });

        /// Speech-to-text of a WAV (any rate; the service resamples to 16 kHz): {text, ms, audioMs}.
        public void Transcribe(byte[] wav, string language, Action<JToken, string> cb)
        {
            string q = string.IsNullOrEmpty(language) ? "" : "?language=" + Uri.EscapeDataString(language);
            Call(async () =>
            {
                var content = new ByteArrayContent(wav);
                content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                var bytes = await SendRaw(HttpMethod.Post, "/v1/audio/transcriptions" + q, content, 60).ConfigureAwait(false);
                return JToken.Parse(Encoding.UTF8.GetString(bytes));
            }, cb);
        }

        /// OpenAI-style chat completion (with `tools`); the first call after a load can take ~10 s.
        public void Chat(JObject body, Action<JToken, string> cb) => Call(() => Json(HttpMethod.Post, "/v1/chat/completions", body, 90), cb);

        /// Streamed chat (stream: true, server-sent events): `onDelta` gets each piece of answer text on the main thread
        /// as the model writes it, `onDone` the whole text (or the error) once. The returned source cancels it: the
        /// connection closes and the service stops generating (measured: ~20 ms). No overall timeout (a long answer may
        /// take a while), but 30 s without a byte ends it.
        public CancellationTokenSource ChatStream(JObject body, Action<string> onDelta, Action<string, string> onDone)
        {
            var cts = new CancellationTokenSource();
            var b = (JObject)body.DeepClone();
            b["stream"] = true;
            Interlocked.Increment(ref _inFlight);
            Task.Run(async () =>
            {
                var all = new StringBuilder();
                string err = null;
                try
                {
                    var content = new StringContent(b.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                    using (var res = await Http.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", content), HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        if (!res.IsSuccessStatusCode) throw new Exception($"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync().ConfigureAwait(false)}");
                        Online = true;
                        using (var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                        {
                            while (!cts.IsCancellationRequested)
                            {
                                var read = reader.ReadLineAsync();
                                if (await Task.WhenAny(read, Task.Delay(30000, cts.Token)).ConfigureAwait(false) != read) throw new TimeoutException("the AI service sent nothing for 30 s");
                                string line = await read.ConfigureAwait(false);
                                if (line == null) break;
                                string piece = SseDelta(line, out bool end, out string sseErr);
                                if (sseErr != null) throw new Exception(sseErr);
                                if (end) break;
                                if (string.IsNullOrEmpty(piece)) continue;
                                all.Append(piece);
                                _main.Enqueue(() => { if (!cts.IsCancellationRequested) onDelta?.Invoke(piece); });
                            }
                        }
                    }
                }
                catch (Exception ex) { err = cts.IsCancellationRequested ? "cancelled" : ex.Message; LastError = err; }
                string text = all.ToString();
                _main.Enqueue(() => onDone?.Invoke(err == null ? text : null, err));
                Interlocked.Decrement(ref _inFlight);
            });
            return cts;
        }

        /// One server-sent-events line of a streamed chat: its delta text, `end` at [DONE] or a finish_reason, or the error.
        public static string SseDelta(string line, out bool end, out string error)
        {
            end = false;
            error = null;
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data:")) return null;
            string data = line.Substring(5).Trim();
            if (data == "[DONE]") { end = true; return null; }
            JObject j;
            try { j = JObject.Parse(data); } catch (Exception) { return null; }
            if (j["error"] != null) { error = (string)j["error"]; return null; }
            var c = j["choices"]?[0];
            string piece = (string)c?["delta"]?["content"];
            string finish = (string)c?["finish_reason"];
            if (finish == "error") error = "the AI service stopped with an error";
            return piece;
        }

        /// Speculative prefill: the session prompt without its question, evaluated while the user still speaks; the next
        /// chat with the same messages before the question starts from it. Fire and forget (the service yields it to
        /// speech-to-text).
        public void Prefill(JArray messages) => Call(() => Json(HttpMethod.Post, "/v1/prefill", new JObject { ["messages"] = messages }, 10), (JToken _, string __) => { });

        /// Sentence vectors (the decision model, multilingual-e5-small; L2-normalised by the service). The JSON is parsed on
        /// the worker thread: a thousand vectors would otherwise cost the main thread a frame.
        public void Embed(IList<string> texts, Action<EmbedResult, string> cb) => Call(async () =>
        {
            var j = await Json(HttpMethod.Post, "/v1/embeddings", new JObject { ["input"] = new JArray(texts) }, 120).ConfigureAwait(false);
            var data = j["data"] as JArray;
            if (data == null) throw new Exception((string)j["error"] ?? "no embeddings");
            var vecs = new float[data.Count][];
            foreach (var d in data)
            {
                int i = (int)d["index"];
                var e = (JArray)d["embedding"];
                var v = new float[e.Count];
                for (int k = 0; k < v.Length; k++) v[k] = (float)e[k];
                vecs[i] = v;
            }
            return new EmbedResult { Vectors = vecs, Ms = (int?)j["ms"] ?? 0, Model = (string)j["model"] };
        }, cb);

        public sealed class EmbedResult { public float[][] Vectors; public int Ms; public string Model; }

        /// Fills the LLM's prompt cache for a system prompt + tool set, so the next command answers fast.
        public void Warmup(JObject body, Action<JToken, string> cb) => Call(() => Json(HttpMethod.Post, "/v1/warmup", body, 180), cb);

        /// Text-to-speech: a mono 16-bit WAV, as the service made it (silence around it, full scale).
        public void Speak(string text, string voice, Action<byte[], string> cb)
        {
            Call(() => SendRaw(HttpMethod.Post, "/v1/audio/speech", SpeechBody(text, voice), 60), cb);
        }

        /// Text-to-speech for playback: the WAV trimmed, levelled and with the voice effect (VoiceFx), done here on the
        /// worker thread after the reply arrives (never on the main thread). `first` / `last`: the sentence's place in
        /// its reply (the effect's squelch and tail).
        public void Speak(string text, string voice, VoiceFxPreset fx, bool first, bool last, Action<byte[], string> cb)
        {
            Call(async () =>
            {
                var wav = await SendRaw(HttpMethod.Post, "/v1/audio/speech", SpeechBody(text, voice), 60).ConfigureAwait(false);
                try { return VoiceFx.ProcessWav(wav, fx, first, last); }
                catch (Exception) { return wav; } // the effect failing never costs the sentence: it plays dry
            }, cb);
        }

        private static StringContent SpeechBody(string text, string voice)
        {
            var body = new JObject { ["input"] = text };
            if (!string.IsNullOrEmpty(voice)) body["voice"] = voice;
            return new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        }
    }
}
