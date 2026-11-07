using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MetaCodex.AI
{
    /// Client of the local AI service (qvac/server, docs/qvac.md): OpenAI-style speech-to-text, chat with tools
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

        /// Fills the LLM's prompt cache for a system prompt + tool set, so the next command answers fast.
        public void Warmup(JObject body, Action<JToken, string> cb) => Call(() => Json(HttpMethod.Post, "/v1/warmup", body, 180), cb);

        /// Text-to-speech: a mono 16-bit WAV.
        public void Speak(string text, string voice, Action<byte[], string> cb)
        {
            var body = new JObject { ["input"] = text };
            if (!string.IsNullOrEmpty(voice)) body["voice"] = voice;
            Call(() => SendRaw(HttpMethod.Post, "/v1/audio/speech", new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"), 60), cb);
        }
    }
}
