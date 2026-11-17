using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MetaCodex.Scripting;

namespace MetaCodex.Shell
{
    /// The fetcher of the plugins' net.get / net.post (ScriptHost checks the URL, the declared hosts and the limits first).
    public sealed partial class MetaCodexShell
    {
        void IScriptServices.NetFetch(NetRequest request) => PluginHttp.Start(request);
    }

    /// Plugin HTTP requests with System.Net.Http off the main thread. Why not UnityWebRequest: on Android that one runs on
    /// the platform's HTTP stack, which follows redirects to any host by itself (only a count limit) and keeps cookies in
    /// a shared store (hence UnityWebRequest.ClearCookieCache); here every rule is ours: no cookie jar at all, each
    /// redirect checked against the plugin's hosts before it is followed, the body read to 1 MB and no further, a timeout
    /// and a cancel per request. HttpClient is the stack the IL2CPP build already uses (MQH, the local AI, the vault's
    /// token test over https), so stripping keeps it and its TLS (Unity's, with the system's certificates) is in the APK.
    /// Nothing here touches Unity or Lua: the outcome goes to request.Done on a pool thread and the host takes it from
    /// there in its next Tick. Android's network security config does not reach managed sockets: the host's own rule (https
    /// only, plain http only to 127.0.0.1 and localhost) is the one that holds, and it is the same as the config's.
    public static class PluginHttp
    {
        private const int MaxRedirects = 3;
        private static readonly HttpClient Http = MakeClient();

        private static HttpClient MakeClient()
        {
            // Mono's HttpClient sits on HttpWebRequest: 2 connections a host by default (as MqhClient notes)
            try { ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 16); } catch (Exception) { }
            var handler = new HttpClientHandler
            {
                UseCookies = false,          // no cookie jar: nothing is stored, nothing is sent that the plugin did not set
                AllowAutoRedirect = false,   // redirects are followed below, each one checked first
                UseDefaultCredentials = false,
                PreAuthenticate = false,
                AutomaticDecompression = DecompressionMethods.None, // no Accept-Encoding: bodies come as they are
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// Starts the request on a pool thread and returns at once.
        public static void Start(NetRequest r)
        {
            if (r == null || r.Done == null) return;
            Task.Run(() => Fetch(r));
        }

        private static async Task Fetch(NetRequest r)
        {
            var reply = new NetReply();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(r.Cancel);
            try
            {
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, r.TimeoutSeconds)));
                var first = new Uri(r.Url);
                var url = first;
                string method = r.Method == "POST" ? "POST" : "GET";
                byte[] body = r.Body;
                for (int hop = 0; ; hop++)
                {
                    bool sameHost = string.Equals(url.Host, first.Host, StringComparison.OrdinalIgnoreCase);
                    using (var msg = Message(method, url, body, r, sameHost))
                    using (var res = await Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        int code = (int)res.StatusCode;
                        var loc = res.Headers.Location;
                        if (IsRedirect(code) && loc != null)
                        {
                            var next = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                            string why = hop >= MaxRedirects ? "more than " + MaxRedirects + " redirects" : r.CheckRedirect?.Invoke(next);
                            if (why != null) { reply.Status = code; reply.Error = "redirect refused: " + why; break; }
                            if (code == 303 || (code == 301 || code == 302) && method == "POST") { method = "GET"; body = null; }
                            url = next;
                            continue;
                        }
                        reply.Status = code;
                        if (res.Content != null)
                        {
                            reply.ContentType = res.Content.Headers.ContentType?.ToString();
                            // a stream read may not see the token (Mono): disposing the response stops it
                            using (cts.Token.Register(() => { try { res.Dispose(); } catch (Exception) { } }))
                                await ReadBody(res.Content, r.MaxBytes, reply, cts.Token).ConfigureAwait(false);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                reply.Status = 0; // no whole reply: a half-read body is not given
                reply.Body = null;
                reply.Length = 0;
                reply.TooLarge = false;
                reply.Error = r.Cancel.IsCancellationRequested ? "cancelled" : cts.IsCancellationRequested ? "timeout" : ErrorText(ex);
            }
            finally { cts.Dispose(); }
            try { r.Done(reply); } catch (Exception) { }
        }

        private static bool IsRedirect(int code) => code == 301 || code == 302 || code == 303 || code == 307 || code == 308;

        private static HttpRequestMessage Message(string method, Uri url, byte[] body, NetRequest r, bool sameHost)
        {
            var m = new HttpRequestMessage(method == "POST" ? HttpMethod.Post : HttpMethod.Get, url);
            foreach (var kv in r.Headers)
            {
                // a key the plugin set goes to the host it was meant for only, not to the host a redirect points to
                if (!sameHost && (kv.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase))) continue;
                m.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            if (body != null)
            {
                m.Content = new ByteArrayContent(body);
                if (!string.IsNullOrEmpty(r.ContentType)) m.Content.Headers.TryAddWithoutValidation("Content-Type", r.ContentType);
            }
            return m;
        }

        /// Reads at most max + 1 bytes (one more says "too large"); the transfer stops there.
        private static async Task ReadBody(HttpContent content, int max, NetReply reply, CancellationToken ct)
        {
            int cap = max + 1;
            long? len = content.Headers.ContentLength;
            var buf = new byte[(int)Math.Min(cap, len.HasValue && len.Value >= 0 ? len.Value + 1 : 16 * 1024)];
            int n = 0;
            using (var s = await content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                while (n < cap)
                {
                    if (n == buf.Length) Array.Resize(ref buf, (int)Math.Min(cap, buf.Length * 2L));
                    int got = await s.ReadAsync(buf, n, buf.Length - n, ct).ConfigureAwait(false);
                    if (got <= 0) break;
                    n += got;
                }
            }
            if (n > max) { reply.TooLarge = true; n = max; }
            reply.Body = buf;
            reply.Length = n;
        }

        /// The innermost message (Mono wraps the real cause: name resolution, TLS, refused).
        private static string ErrorText(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null) e = e.InnerException;
            string m = e.Message;
            if (string.IsNullOrEmpty(m)) m = e.GetType().Name;
            return m.Length > 160 ? m.Substring(0, 160) : m;
        }
    }
}
