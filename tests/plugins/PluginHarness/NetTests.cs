// The network API of plugins (net.get / net.post, plugin.json "network") and the weather example, offline: the REAL
// ScriptHost against FakeServices, whose fetcher answers from canned replies (NetFixture). Each line "ok   [net] ..." or
// "FAIL [net] ..."; fresh folders under <run dir>/net.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Scripting;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Linq;

namespace PluginHarness
{
    /// One plugin (or the weather example) under a fresh host, with its own fake app and canned replies.
    internal sealed class NetEnv
    {
        public FakeServices Fake;
        public ScriptHost Host;
        public readonly string Id, Scripts, Plugins, Dir;
        public double T = 100;
        /// The canned replies a new fake app starts with (Restart): a test may add to them.
        public readonly List<NetFixture> Fixtures;

        public NetEnv(string runDir, string id, string manifest, string lua, List<NetFixture> fixtures, double clock, Action<string> before = null)
        {
            Id = id;
            string root = Path.Combine(runDir, "net", id);
            if (Directory.Exists(root)) ScriptHost.DeleteTree(root);
            Scripts = Path.Combine(root, "scripts");
            Plugins = Path.Combine(root, "plugins");
            Dir = Path.Combine(Plugins, id);
            Directory.CreateDirectory(Scripts);
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "plugin.json"), manifest);
            File.WriteAllText(Path.Combine(Dir, "main.lua"), lua);
            before?.Invoke(Scripts);
            Fixtures = new List<NetFixture>(fixtures);
            Start(clock);
        }

        private void Start(double clock)
        {
            Fake = new FakeServices(clock);
            Fake.NetFixtures.Clear();
            Fake.NetFixtures.AddRange(Fixtures);
            Host = new ScriptHost(Fake, Scripts, Plugins, null);
            Host.Scan();
        }

        /// A new app and a new host on the same folders (the app restarted), its clock `later` seconds on.
        public void Restart(double later = 0)
        {
            Start(Fake.Clock + later);
        }

        public ScriptHost.Entry E => Host.Find("plugin/" + Id);
        public string Key => "plugin/" + Id;

        public void Step(double dt = 0.1)
        {
            Fake.Pump(Host);
            T += dt;
            Fake.Clock += dt;
            Host.Tick((float)T);
        }

        public void StepFor(double seconds, double dt = 0.1)
        {
            int n = Math.Max(1, (int)Math.Round(seconds / dt));
            for (int i = 0; i < n; i++) Step(seconds / n);
        }

        /// A Lua global by a dotted path ("R.ok.status"; numbers index lists).
        public DynValue G(string path)
        {
            var e = E;
            if (e?.Script == null) return DynValue.Nil;
            DynValue v = DynValue.NewTable(e.Script.Globals);
            foreach (var part in path.Split('.'))
            {
                if (v.Type != DataType.Table) return DynValue.Nil;
                v = int.TryParse(part, out int n) ? v.Table.Get(n) : v.Table.Get(part);
            }
            return v;
        }

        public string S(string path) { var v = G(path); return v.Type == DataType.String ? v.String : v.IsNil() ? null : v.ToPrintString(); }
        public double? N(string path) { var v = G(path); return v.Type == DataType.Number ? v.Number : (double?)null; }
        public bool B(string path) => G(path).CastToBool();

        /// A button event in the plugin's main window (the test plugins run a case by its id).
        public void Press(string id, string window = "main") => Host.WindowEvent("plugin-" + Id + "-" + window, "button", id, id);
        public void Event(string type, string id, string value) => Host.WindowEvent("plugin-" + Id + "-main", type, id, value);

        public WindowRec Win => Fake.Windows.TryGetValue("plugin-" + Id + "-main", out var w) ? w : null;

        /// The text of a component of the main window by its id (null when there is none).
        public string Text(string compId)
        {
            var comps = Win?.Spec?["components"] as JArray;
            var c = comps?.OfType<JObject>().FirstOrDefault(x => (string)x["id"] == compId);
            return c == null ? null : (string)c["text"];
        }

        public JObject Comp(Func<JObject, bool> pick) => (Win?.Spec?["components"] as JArray)?.OfType<JObject>().FirstOrDefault(pick);

        public List<NetRequest> Requests(string urlStart) => Fake.NetRequests.Where(r => r.Url.StartsWith(urlStart, StringComparison.Ordinal)).ToList();

        public bool LogHas(string start) => E != null && E.Log.Any(l => l.StartsWith(start, StringComparison.Ordinal));
    }

    internal static class NetTests
    {
        private static int _fails;
        private static readonly double Clock0 = new DateTimeOffset(new DateTime(2026, 11, 17, 9, 30, 0, DateTimeKind.Local)).ToUnixTimeSeconds();

        private static void Check(bool cond, string msg)
        {
            Console.WriteLine((cond ? "ok   [net] " : "FAIL [net] ") + msg);
            if (!cond) { _fails++; Program.Fails++; }
        }

        private static NetFixture Fx(string json) => NetFixture.From(JObject.Parse(json));

        private static List<NetFixture> ApiFixtures() => new List<NetFixture>
        {
            Fx("{ \"url\": \"https://api.example.com/ok\", \"match\": \"exact\", \"json\": { \"a\": 1, \"list\": [1, 2], \"name\": \"x\" } }"),
            Fx("{ \"url\": \"https://api.example.com/missing\", \"status\": 404, \"json\": { \"error\": true, \"reason\": \"nope\" } }"),
            Fx("{ \"url\": \"https://api.example.com/slow\", \"never\": true }"),
            Fx("{ \"url\": \"https://api.example.com/big\", \"bytes\": 1500000, \"type\": \"text/plain\" }"),
            Fx("{ \"url\": \"https://api.example.com/text\", \"body\": \"hello\" }"),
            Fx("{ \"url\": \"https://api.example.com/later\", \"delay\": 5, \"json\": { \"late\": true } }"),
            Fx("{ \"url\": \"https://api.example.com/echo\", \"method\": \"POST\", \"status\": 201, \"json\": { \"made\": true } }"),
            Fx("{ \"url\": \"http://127.0.0.1:8080/local\", \"body\": \"local\" }"),
            Fx("{ \"url\": \"https://deep.a.example.org/x\", \"body\": \"deep\" }"),
            Fx("{ \"url\": \"https://example.org/x\", \"body\": \"apex\" }"),
        };

        private const string ApiManifest = "{ \"id\": \"ID\", \"name\": \"Net test\", \"version\": \"1.0.0\", \"kind\": \"app\", \"network\": [\"api.example.com\", \"*.example.org\", \"127.0.0.1\"] }";

        /// A test plugin: each button id of its window runs one case (CASES[id]); results land in R, IDS, WHY.
        private const string ApiLua = @"
R, IDS, WHY, N = {}, {}, {}, 0
local function keep(name) return function(res) R[name] = res; N = N + 1 end end
local function get(name, url, opts) IDS[name], WHY[name] = net.get(url, opts or {}, keep(name)) end
CASES = {
  ok = function() get('ok', 'https://api.example.com/ok'); SYNC = (R.ok == nil) end,
  missing = function() get('missing', 'https://api.example.com/missing', { headers = { Accept = 'application/json' } }) end,
  slow = function() get('slow', 'https://api.example.com/slow', { timeout = 2 }) end,
  big = function() get('big', 'https://api.example.com/big') end,
  text = function() get('text', 'https://api.example.com/text') end,
  undeclared = function() get('undeclared', 'https://evil.example.net/x') end,
  apex = function() get('apex', 'https://example.org/x') end,
  deep = function() get('deep', 'https://deep.a.example.org/x') end,
  http = function() get('http', 'http://api.example.com/ok') end,
  loop = function() get('loopback', 'http://127.0.0.1:8080/local') end,
  userinfo = function() get('userinfo', 'https://user:pw@api.example.com/ok') end,
  ftp = function() get('ftp', 'ftp://api.example.com/x') end,
  posttable = function() IDS.pt, WHY.pt = net.post('https://api.example.com/echo', { a = 1, b = 'x' }, keep('pt')) end,
  posttext = function() IDS.ps, WHY.ps = net.post('https://api.example.com/echo', 'a,b', { headers = { ['Content-Type'] = 'text/csv', Authorization = 'Bearer typed-by-user' } }, keep('ps')) end,
  badheader = function() OKH, ERRH = pcall(net.get, 'https://api.example.com/ok', { headers = { Host = 'x' } }, keep('bh')) end,
  nofn = function() OKF, ERRF = pcall(net.get, 'https://api.example.com/ok') end,
  burst = function() for i = 1, 6 do get('b' .. i, 'https://api.example.com/later') end end,
  cancel = function() get('c', 'https://api.example.com/later'); CANCELLED = net.cancel(IDS.c) end,
  rate = function()
    local id, why = net.get('https://api.example.com/ok', function(res) end)
    if id then RATE_OK = (RATE_OK or 0) + 1 else RATE_NO = (RATE_NO or 0) + 1; RATE_WHY = why end
  end,
  late = function() net.get('https://api.example.com/later', function(res) ui.toast('late reply', 'info') end) end,
  boom = function() net.get('https://api.example.com/ok', function(res) error('boom in the callback') end) end,
  spin = function() net.get('https://api.example.com/ok', function(res) while true do end end) end,
}
HOSTS = #plugin.network
ui.panel{ id = 'main', title = 'Net test', components = { { type = 'text', text = 'net test' } },
          on = function(e) if e.type == 'button' and CASES[e.id] then CASES[e.id]() end end }
";

        public static void Run(string runDir, string examplesDir)
        {
            try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch (Exception) { } // ° and · in the lines below
            Console.WriteLine("== net: the network API (canned replies, no network)");
            Statics();
            Api(runDir);
            Limits(runDir);
            Lifetime(runDir);
            Manifest(runDir);
            if (!string.IsNullOrEmpty(examplesDir)) Weather(runDir, Path.Combine(examplesDir, "weather"));
            Console.WriteLine(_fails == 0 ? "PASS [net]" : "FAILED (" + _fails + ") [net]");
        }

        private static NetEnv Api(string runDir, string id, Action<string> before = null) =>
            new NetEnv(runDir, id, ApiManifest.Replace("ID", id), ApiLua, ApiFixtures(), Clock0, before);

        // ------------------------------------------------------------------ the pure checks

        private static void Statics()
        {
            var hosts = new[] { "api.example.com", "*.example.org", "127.0.0.1" };
            Check(ScriptHost.HostDeclared("api.example.com", hosts) && ScriptHost.HostDeclared("a.example.org", hosts) && ScriptHost.HostDeclared("deep.a.example.org", hosts),
                "hosts: a declared name and the subdomains of *.example.org match");
            Check(!ScriptHost.HostDeclared("example.org", hosts) && !ScriptHost.HostDeclared("badexample.org", hosts) && !ScriptHost.HostDeclared("api.example.com.evil.net", hosts),
                "hosts: example.org itself, badexample.org and api.example.com.evil.net do not");
            Check(ScriptHost.NetCheckUri(new Uri("https://api.example.com:8443/x?y=1"), hosts) == null, "url: https to a declared host on another port is allowed");
            string r1 = ScriptHost.NetCheckUri(new Uri("http://api.example.com/next"), hosts);
            string r2 = ScriptHost.NetCheckUri(new Uri("https://other.example.net/next"), hosts);
            Check(r1 != null && r1.Contains("http is refused") && r2 != null && r2.StartsWith("net: host not declared in plugin.json \"network\""),
                "a redirect to http or to an undeclared host is refused (" + r1 + " | " + r2 + ")");
            Check(ScriptHost.NetCheckUri(new Uri("http://localhost:5173/"), new[] { "localhost" }) == null && ScriptHost.NetCheckUri(new Uri("http://localhost:5173/"), hosts) != null,
                "http to localhost: allowed when declared, refused when not");
        }

        // ------------------------------------------------------------------ one request each: the outcomes

        private static void Api(string runDir)
        {
            var env = Api(runDir, "netapi", scripts => File.WriteAllText(Path.Combine(scripts, "netscript.lua"),
                "R = {}\nR.id, R.why = net.get('https://api.example.com/ok', function(res) R.res = res end)\n"));
            env.Step(0); env.Step();
            Check(env.E != null && env.E.Error == null, "the test plugin loads" + (env.E?.Error != null ? ": " + env.E.Error : ""));
            if (env.E == null || env.E.Error != null) return;
            Check(env.N("HOSTS") == 3, "plugin.network lists the 3 declared hosts");
            Check(env.Host.Check(env.Id).Contains("network: api.example.com, *.example.org, 127.0.0.1"), "plugin check shows the network line");

            // a script (no plugin.json) has no network
            var script = env.Host.Find("netscript");
            string sw = script?.Script?.Globals.Get("R").Table?.Get("why").CastToString();
            Check(sw != null && sw.Contains("only plugins"), "a script is refused: " + sw);

            env.Press("ok");
            Check(env.B("SYNC"), "the callback never runs inside net.get (it comes in a later tick)");
            Check(env.N("IDS.ok") > 0, "net.get returns a request id");
            env.Step(); env.Step();
            Check(env.B("R.ok.ok") && env.N("R.ok.status") == 200 && env.N("R.ok.json.a") == 1 && env.N("R.ok.json.list.2") == 2 &&
                  (env.S("R.ok.body") ?? "").Contains("\"a\":1") && env.G("R.ok.error").IsNil(),
                "success JSON: ok, status 200, json decoded (a = 1, list[2] = 2), body is the text, no error");
            var okReq = env.Requests("https://api.example.com/ok").LastOrDefault();
            Check(okReq != null && okReq.Method == "GET" && okReq.Headers.Any(h => h.Key == "User-Agent" && h.Value.Contains("netapi")) &&
                  !okReq.Headers.Any(h => h.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)),
                "the request: GET, a User-Agent with the plugin id, no cookie and no credentials");

            env.Press("missing"); env.Step(); env.Step();
            Check(!env.B("R.missing.ok") && env.N("R.missing.status") == 404 && env.S("R.missing.error") == "status 404" && env.S("R.missing.json.reason") == "nope",
                "404: ok false, status 404, error \"status 404\", the JSON body still decoded");

            env.Press("text"); env.Step(); env.Step();
            Check(env.B("R.text.ok") && env.S("R.text.body") == "hello" && env.G("R.text.json").IsNil(), "a text reply: body \"hello\", json nil");

            env.Press("big"); env.Step(); env.Step();
            Check(!env.B("R.big.ok") && env.S("R.big.error") == "too large" && (env.S("R.big.body") ?? "").Length == ScriptHost.NetMaxBytes && env.G("R.big.json").IsNil(),
                "too large (1.5 MB): ok false, error \"too large\", body cut at 1 MB (" + (env.S("R.big.body") ?? "").Length + " bytes)");

            int before = env.Fake.NetRequests.Count;
            env.Press("undeclared");
            Check(env.G("IDS.undeclared").IsNil() && (env.S("WHY.undeclared") ?? "").StartsWith("net: host not declared in plugin.json \"network\""),
                "undeclared host: net.get returns nil and why (" + env.S("WHY.undeclared") + ")");
            env.Step(); env.Step();
            Check((env.S("R.undeclared.error") ?? "").StartsWith("net: host not declared in plugin.json \"network\"") && !env.B("R.undeclared.ok") &&
                  env.Fake.NetRequests.Count == before && env.LogHas("warn net: host not declared in plugin.json \"network\": evil.example.net"),
                "undeclared host: the callback gets the error, nothing is fetched, the plugin log has the warn line");

            env.Press("apex"); env.Press("deep"); env.Step(); env.Step();
            Check((env.S("R.apex.error") ?? "").Contains("example.org") && env.Requests("https://example.org/").Count == 0 && env.S("R.deep.body") == "deep",
                "*.example.org: deep.a.example.org is fetched, example.org itself is refused");

            env.Press("http"); env.Press("loop"); env.Step(); env.Step();
            Check((env.S("R.http.error") ?? "").Contains("http is refused") && env.Requests("http://api.example.com").Count == 0,
                "http refused: http://api.example.com never leaves the host (" + env.S("R.http.error") + ")");
            Check(env.B("R.loopback.ok") && env.S("R.loopback.body") == "local", "http to 127.0.0.1 (declared) is fetched");

            env.Press("userinfo"); env.Press("ftp"); env.Step(); env.Step();
            Check((env.S("R.userinfo.error") ?? "").Contains("no user name or password") && (env.S("R.ftp.error") ?? "").Contains("only https"),
                "a user name in the url and ftp: refused");

            env.Press("posttable"); env.Step(); env.Step();
            var pt = env.Requests("https://api.example.com/echo").LastOrDefault();
            Check(pt != null && pt.Method == "POST" && pt.ContentType == "application/json" && System.Text.Encoding.UTF8.GetString(pt.Body) == "{\"a\":1,\"b\":\"x\"}" &&
                  env.N("R.pt.status") == 201 && env.B("R.pt.ok"),
                "net.post with a table: JSON body {\"a\":1,\"b\":\"x\"}, application/json, the 201 reply");
            env.Press("posttext"); env.Step(); env.Step();
            var ps = env.Requests("https://api.example.com/echo").LastOrDefault();
            Check(ps != null && ps.ContentType == "text/csv" && System.Text.Encoding.UTF8.GetString(ps.Body) == "a,b" &&
                  ps.Headers.Any(h => h.Key == "Authorization" && h.Value == "Bearer typed-by-user"),
                "net.post with text: its own Content-Type, the Authorization the plugin set is sent as set");

            env.Press("badheader"); env.Press("nofn"); env.Step();
            Check(!env.B("OKH") && (env.S("ERRH") ?? "").Contains("the app sets it") && !env.B("OKF") && (env.S("ERRF") ?? "").Contains("the function that gets the reply") && env.E.Error == null,
                "a Host header and a missing callback raise a Lua error (" + env.S("ERRH") + " | " + env.S("ERRF") + ")");

            env.Press("slow");
            env.StepFor(2.0);
            Check(env.G("R.slow").IsNil(), "timeout 2 s: no callback before the time");
            env.StepFor(1.5);
            var slow = env.Requests("https://api.example.com/slow").LastOrDefault();
            Check(env.S("R.slow.error") == "timeout" && env.N("R.slow.status") == 0 && !env.B("R.slow.ok") && slow != null && slow.Cancel.IsCancellationRequested &&
                  env.Host.NetInFlight(env.E) == 0,
                "timeout: a fetcher that never answers gives error \"timeout\" after 2 s (+1 s grace), the request is cancelled, none in flight");
        }

        // ------------------------------------------------------------------ limits

        private static void Limits(string runDir)
        {
            var env = Api(runDir, "netburst");
            env.Step(0); env.Step();
            env.Press("burst");
            Check(env.N("IDS.b4") > 0 && env.G("IDS.b5").IsNil() && (env.S("WHY.b5") ?? "").Contains("4 requests in flight") && env.Host.NetInFlight(env.E) == 4,
                "6 at once: 4 start, the 5th and 6th are refused (" + env.S("WHY.b5") + ")");
            env.Step(); env.Step();
            Check((env.S("R.b6.error") ?? "").Contains("4 requests in flight") && env.G("R.b1").IsNil(), "the refused ones get their callback at once, the others are still in flight");
            env.StepFor(5.5);
            Check(env.B("R.b1.ok") && env.B("R.b4.ok") && env.Host.NetInFlight(env.E) == 0, "after 5 s the 4 replies came");

            var rate = Api(runDir, "netrate");
            rate.Step(0); rate.Step();
            for (int i = 0; i < 70; i++) { rate.Press("rate"); rate.Step(0.2); }
            Check(rate.N("RATE_OK") == 60 && rate.N("RATE_NO") == 10 && (rate.S("RATE_WHY") ?? "").Contains("60 requests in the last minute"),
                "70 in 14 s: 60 start, 10 refused (" + rate.S("RATE_WHY") + ")");
            rate.StepFor(50, 1.0);
            rate.Press("rate");
            Check(rate.N("RATE_OK") == 61, "a minute after the first ones a request starts again");
            Check(rate.E.Log.Count(l => l.Contains("60 requests in the last minute")) <= 2, "the refusal is logged at most once in 10 s (" + rate.E.Log.Count(l => l.Contains("60 requests")) + " lines)");
        }

        // ------------------------------------------------------------------ unload, reload, cancel, errors in a callback

        private static void Lifetime(string runDir)
        {
            var env = Api(runDir, "netlife");
            env.Step(0); env.Step();
            env.Press("cancel");
            Check(env.B("CANCELLED"), "net.cancel returns true for a request in flight");
            env.StepFor(6);
            var c = env.Requests("https://api.example.com/later").LastOrDefault();
            Check(env.G("R.c").IsNil() && c != null && c.Cancel.IsCancellationRequested, "a cancelled request: its callback never runs, the fetch is cancelled");

            env.Press("late");
            env.Step(1.0);
            env.Host.SetEnabled(env.Key, false);
            env.StepFor(6);
            var late = env.Requests("https://api.example.com/later").LastOrDefault();
            Check(!env.Fake.Toasts.Any(t => t.text == "late reply") && late != null && late.Cancel.IsCancellationRequested && env.Host.NetInFlight(env.E) == 0,
                "unload (disable) while a request is in flight: it is cancelled, its callback never runs");

            env.Host.SetEnabled(env.Key, true);
            env.Step();
            env.Press("late");
            env.Step(1.0);
            File.WriteAllText(Path.Combine(env.Dir, "main.lua"), ApiLua + "\n-- edited\n");
            env.Host.SavePlugin(env.Id, new Dictionary<string, string> { ["main.lua"] = ApiLua + "\n-- edited again\n" });
            env.StepFor(6);
            Check(!env.Fake.Toasts.Any(t => t.text == "late reply") && env.E.Error == null, "reload (main.lua saved) while a request is in flight: the old callback never runs");

            env.Press("boom");
            env.Step(); env.Step();
            Check(env.E.Error != null && env.E.Error.Contains("boom in the callback") && env.Win == null,
                "an error in the callback stops the plugin as in any callback (its window is gone): " + env.E.Error);

            var spin = Api(runDir, "netspin");
            spin.Step(0); spin.Step();
            spin.Press("spin");
            spin.Step(); spin.Step();
            Check(spin.E.Error != null && spin.E.Error.Contains("instructions"), "an endless loop in the callback is stopped by the instruction budget: " + spin.E.Error);
        }

        // ------------------------------------------------------------------ plugin.json "network"

        private static void Manifest(string runDir)
        {
            var env = new NetEnv(runDir, "netmanifest",
                "{ \"id\": \"netmanifest\", \"name\": \"m\", \"version\": \"1.0.0\", \"kind\": \"app\", \"network\": [\"https://api.example.com\", \"*\", \"*.com\", \"api.example.com:443\", \"OK.Example.com\", 5, \"*.example.org\", \"ok.example.com\"] }",
                "ui.panel{ id = 'main', components = { { type = 'text', text = 'm' } } }\n", ApiFixtures(), Clock0);
            env.Step(0);
            var e = env.E;
            Check(e != null && e.NetworkHosts.SequenceEqual(new[] { "ok.example.com", "*.example.org" }),
                "\"network\": only real host names are kept, lower case, once (" + string.Join(", ", e?.NetworkHosts ?? new string[0]) + ")");
            int warns = e?.Log.Count(l => l.StartsWith("warn plugin.json \"network\":", StringComparison.Ordinal)) ?? 0;
            Check(warns == 5, "\"network\": a warn line for each bad entry (scheme, *, *.com, a port, a number): " + warns);
            foreach (var l in (e?.Log ?? new List<string>()).Where(l => l.StartsWith("warn plugin.json \"network\"")).Take(5)) Console.WriteLine("     " + l);

            var one = new NetEnv(runDir, "netmanifest1", "{ \"id\": \"netmanifest1\", \"name\": \"m\", \"version\": \"1.0.0\", \"kind\": \"app\", \"network\": \"api.example.com\" }",
                "ui.panel{ id = 'main', components = { { type = 'text', text = 'm' } } }\n", ApiFixtures(), Clock0);
            one.Step(0);
            Check(one.E.NetworkHosts.SequenceEqual(new[] { "api.example.com" }) && one.LogHas("warn plugin.json \"network\": write a list"),
                "\"network\" as one text: taken, with a warning to write a list");
        }

        // ------------------------------------------------------------------ the weather example

        private static void Weather(string runDir, string src)
        {
            Console.WriteLine("== net: the weather example (canned Open-Meteo replies)");
            if (!File.Exists(Path.Combine(src, "plugin.json"))) { Check(false, "weather: no example at " + src); return; }
            var fixtures = NetFixture.Loaded.Where(f => f.Url.Contains("open-meteo.com")).ToList();
            Check(fixtures.Count >= 4, "weather: " + fixtures.Count + " canned Open-Meteo replies (tests/plugins/fixtures/net)");
            string lua = File.ReadAllText(Path.Combine(src, File.Exists(Path.Combine(src, "main.lua")) ? "main.lua" : "main.lua.txt"));
            var env = new NetEnv(runDir, "weather", File.ReadAllText(Path.Combine(src, "plugin.json")), lua, fixtures, Clock0);
            env.Step(0); env.Step();
            var e = env.E;
            Check(e != null && e.Error == null, "weather loads" + (e?.Error != null ? ": " + e.Error : ""));
            if (e == null || e.Error != null) return;
            Check(e.NetworkHosts.SequenceEqual(new[] { "api.open-meteo.com", "geocoding-api.open-meteo.com" }), "weather declares api.open-meteo.com and geocoding-api.open-meteo.com");
            var tile = env.Fake.Tiles.FirstOrDefault(t => t.Owner == "weather");
            Check(tile != null && tile.Label == "WEATHER" && tile.Icon == "bolt" && tile.Window == "plugin-weather-main", "a WEATHER tile with an icon opens its window");
            env.Host.TileTapped("weather", "weather");
            env.Step(); env.Step();
            Check(env.Fake.NetRequests.Count == 0 && env.Text("status") == "Pick a city, or type one.", "first start: no city, no request; the window says to pick one");

            // a preset city
            env.Event("cell", "city-paris", "Paris");
            var paris = env.Requests("https://api.open-meteo.com/v1/forecast?latitude=48.8534&longitude=2.3488&");
            Check(paris.Count == 1 && paris[0].Url.Contains("forecast_days=3") && paris[0].Url.Contains("timezone=auto"), "Paris: one forecast request (" + (paris.FirstOrDefault()?.Url ?? "none") + ")");
            env.StepFor(1.2); // a change other than LIVE texts rebuilds the window at most once a second
            string sky = env.Text("sky") ?? "";
            Check(env.Text("now") == "13°C", "now: " + env.Text("now"));
            Check(sky.Contains("Slight rain") && sky.Contains("Paris, France") && sky.Contains("wind 13 km/h from S") && sky.Contains("humidity 90%"),
                "condition in words, place, wind, humidity: " + sky.Replace("\n", " / "));
            var table = env.Comp(c => (string)c["type"] == "table");
            var rows = (table?["rows"] as JArray)?.Select(r => string.Join(" | ", r.Select(x => (string)x))).ToList() ?? new List<string>();
            Check(rows.Count == 3 && rows[0].StartsWith("Today | Rain showers | 12° | 17° | 100%") && rows[1].StartsWith("Tomorrow | Overcast") && rows[2].StartsWith("Thursday"),
                "3-day forecast: " + string.Join(" ; ", rows));
            var credit = env.Comp(c => ((string)c["text"] ?? "").Contains("Open-Meteo"));
            Check(credit != null && ((string)credit["text"]).Contains("CC BY 4.0") && ((string)credit["text"]).Contains("GeoNames"), "the credit line: " + (string)credit?["text"]);
            Check(env.Text("status") == "Updated just now", "status: " + env.Text("status"));
            var cell = env.Comp(c => (string)c["type"] == "grid")?["cells"]?.OfType<JObject>().FirstOrDefault(x => (string)x["id"] == "city-paris");
            Check((string)cell?["tone"] == "accent", "the picked city is lit in the grid");

            // a typed city: the place search, then its forecast
            env.Event("input", "search", "Lyon");
            env.StepFor(0.5);
            var geo = env.Requests("https://geocoding-api.open-meteo.com/v1/search?name=Lyon&count=1");
            var lyon = env.Requests("https://api.open-meteo.com/v1/forecast?latitude=45.7491&longitude=4.8479&");
            Check(geo.Count == 1 && lyon.Count == 1 && env.Text("now") == "9°C" && (env.Text("sky") ?? "").Contains("Overcast") && (env.Text("sky") ?? "").Contains("Lyon, France"),
                "typed Lyon: place search, then its forecast: " + env.Text("now") + " " + (env.Text("sky") ?? "").Replace("\n", " / "));

            int n0 = env.Fake.NetRequests.Count;
            env.Event("input", "search", "Qwzxqq");
            env.StepFor(0.5);
            Check(env.Fake.NetRequests.Count == n0 + 1 && env.Fake.Toasts.Any(t => t.text.Contains("No place called Qwzxqq")) && (env.Text("sky") ?? "").Contains("Lyon"),
                "an unknown name: one search, a toast, Lyon stays");

            env.Press("unit"); env.StepFor(1.2);
            Check(env.Text("now") == "48°F" && (env.Text("sky") ?? "").Contains("13 mph"), "°F, MPH: " + env.Text("now") + ", " + (env.Text("sky") ?? "").Replace("\n", " / "));
            env.Press("unit"); env.StepFor(1.2);

            env.Press("source"); env.Step();
            Check(env.Fake.Actions.Contains("browser https://open-meteo.com/"), "SOURCE opens the data source's site (the licence's link)");

            // one request at a time
            env.Fake.NetFixtures.Insert(0, Fx("{ \"url\": \"https://api.open-meteo.com/v1/forecast?latitude=45.7491&longitude=4.8479&current=\", \"delay\": 3, \"json\": " + FixtureJson(fixtures, "latitude=45.7491") + " }"));
            int f0 = env.Requests("https://api.open-meteo.com/v1/forecast").Count;
            env.Press("refresh"); env.Step(); env.Press("refresh"); env.Step();
            Check(env.Requests("https://api.open-meteo.com/v1/forecast").Count == f0 + 1 && env.Fake.Toasts.Any(t => t.text == "Already updating") && (env.Text("status") ?? "").Contains("updating"),
                "REFRESH twice: one request; the second tap says it is updating already");
            env.StepFor(3.5);
            env.Fake.NetFixtures.RemoveAt(0);

            // the automatic refresh, every 30 minutes
            int a0 = env.Requests("https://api.open-meteo.com/v1/forecast").Count;
            int b0 = env.Win.Builds, u0 = env.Win.TextUpdates;
            env.StepFor(29 * 60, 1.0);
            int a29 = env.Requests("https://api.open-meteo.com/v1/forecast").Count;
            string st29 = env.Text("status") ?? "";
            Check(a29 == a0 && (st29 == "Updated 28 min ago" || st29 == "Updated 29 min ago"), "after 29 min: no request; the age line says " + st29);
            Check(env.Win.Builds == b0 && env.Win.TextUpdates - u0 >= 25, "the age line is LIVE: 29 min with " + (env.Win.Builds - b0) + " full builds and " + (env.Win.TextUpdates - u0) + " text updates");
            env.StepFor(2 * 60, 1.0);
            Check(env.Requests("https://api.open-meteo.com/v1/forecast").Count == a0 + 1 && (env.Text("status") ?? "").StartsWith("Updated"),
                "after 30 min: one automatic update (" + env.Text("status") + ")");

            // a failure keeps the data and tries again in 5 minutes
            env.Fake.NetFixtures.Insert(0, Fx("{ \"url\": \"https://api.open-meteo.com/v1/forecast?latitude=45.7491&longitude=4.8479&current=\", \"status\": 500, \"json\": { \"error\": true, \"reason\": \"down\" } }"));
            env.Press("refresh"); env.StepFor(0.5);
            Check(env.Text("now") == "9°C" && (env.Text("status") ?? "").Contains("no update: status 500"), "status 500: the old weather stays, the age line says why: " + env.Text("status"));
            int r0 = env.Requests("https://api.open-meteo.com/v1/forecast").Count;
            env.StepFor(4 * 60, 1.0);
            int r4 = env.Requests("https://api.open-meteo.com/v1/forecast").Count;
            env.Fake.NetFixtures.RemoveAt(0);
            env.StepFor(2 * 60, 1.0);
            Check(r4 == r0 && env.Requests("https://api.open-meteo.com/v1/forecast").Count == r0 + 1 && (env.Text("status") ?? "").StartsWith("Updated"),
                "it tries again after 5 minutes and recovers: " + env.Text("status"));

            // restart: the cached weather at once, no request while it is fresh
            env.Restart(60);
            env.Step(0);
            Check(env.E?.Error == null && env.Text("now") == "9°C" && env.Text("status") == "Updated 2 min ago" && env.Fake.NetRequests.Count == 0,
                "restart 1 min later: the cached weather at once (" + env.Text("now") + ", " + env.Text("status") + "), no request");
            // the reply takes 2 s here, so what shows before it is the cached weather
            env.Fixtures.Insert(0, Fx("{ \"url\": \"https://api.open-meteo.com/v1/forecast?latitude=45.7491&longitude=4.8479&current=\", \"delay\": 2, \"json\": " + FixtureJson(fixtures, "latitude=45.7491") + " }"));
            env.Restart(2 * 3600);
            env.Fixtures.RemoveAt(0);
            env.Step(0);
            Check(env.Text("now") == "9°C" && (env.Text("status") ?? "").StartsWith("Updated 2 h ago") && env.Requests("https://api.open-meteo.com/v1/forecast").Count == 1,
                "restart 2 h later: the cached weather with its age at once (" + env.Text("now") + ", " + env.Text("status") + "), and one request");
            env.StepFor(2.5);
            Check(env.Text("status") == "Updated just now", "then the new weather: " + env.Text("status"));

            // voice
            var cmd = env.Fake.Voice.All.FirstOrDefault(c => c.Name == "weather_report");
            Check(cmd != null && VoiceCommands.Lint(cmd).Count == 0, "voice weather_report: registered, lint clean");
            if (cmd != null)
            {
                string reply = cmd.Run(new JObject { ["city"] = "paris" });
                env.Step(); env.Step();
                Check(reply == "Getting the weather in Paris." && env.Fake.Notices.Any(x => x.Text.StartsWith("Paris: 13°C, slight rain") && x.Speak != null),
                    "voice \"weather in Paris\": " + reply + " -> " + env.Fake.Notices.LastOrDefault()?.Text);
                string now = cmd.Run(new JObject());
                Check(now.StartsWith("Paris: 13°C, slight rain, wind 13 km/h"), "voice \"how is the weather\": " + now);
            }

            // unload while a request is in flight
            env.Fake.NetFixtures.Insert(0, Fx("{ \"url\": \"https://api.open-meteo.com/v1/forecast?\", \"delay\": 4, \"json\": {} }"));
            env.Press("refresh"); env.Step();
            env.Host.SetEnabled(env.Key, false);
            env.StepFor(5);
            Check(env.Host.NetInFlight(env.E) == 0 && env.Win == null && env.Fake.Tiles.All(t => t.Owner != "weather"), "weather disabled mid-request: no window, no tile, nothing in flight");
            var logs = e.Log.Concat(env.E?.Log ?? new List<string>()).Where(l => l.StartsWith("ERROR") || l.StartsWith("warn")).ToList();
            Check(logs.Count == 0, "weather: no ERROR or warn line in its log" + (logs.Count > 0 ? ": " + string.Join(" | ", logs.Take(3)) : ""));
        }

        private static string FixtureJson(List<NetFixture> all, string urlPart) => all.First(f => f.Url.Contains(urlPart)).Body;
    }
}
