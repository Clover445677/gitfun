using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ChatGptBridge {
    static class LiveTests {
        static readonly object logLock = new object();
        static string logPath;
        static void Log(string line) { lock (logLock) File.AppendAllText(logPath, DateTime.UtcNow.ToString("HH:mm:ss") + " " + line + Environment.NewLine, JsonUtil.Utf8); }
        static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Log("PASS " + message); }
        static string Fetch(string url, bool bridge, int timeout = 15000) {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = bridge ? new WebProxy("http://127.0.0.1:" + AppSettings.BridgePort) : null;
            request.Timeout = request.ReadWriteTimeout = timeout; request.KeepAlive = false;
            using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
        }
        static string Country(string trace) { return trace.Split('\n').FirstOrDefault(l => l.StartsWith("loc=")) ?? "country not returned"; }
        public static int NativeProbe(string output, bool waitForGuard, bool literalIp = false) {
            try {
                if (waitForGuard) System.Threading.Thread.Sleep(18000);
                string country = Country(Fetch(literalIp ? "https://1.1.1.1/cdn-cgi/trace" : "https://chatgpt.com/cdn-cgi/trace", false, literalIp ? 5000 : 15000));
                JsonUtil.Save(output, new { Success = true, Country = country, Process = Process.GetCurrentProcess().ProcessName }); return 0;
            } catch (WebException ex) { JsonUtil.Save(output, new { Success = false, Error = ex.Status.ToString(), Process = Process.GetCurrentProcess().ProcessName }); return 2; }
        }
        static async Task<bool> ProbeNative(string output, bool wait) {
            string path = Path.Combine(Path.GetDirectoryName(typeof(LiveTests).Assembly.Location), "codex-route-probe.exe");
            if (!File.Exists(path)) File.Copy(typeof(LiveTests).Assembly.Location, path);
            using (var process = Process.Start(new ProcessStartInfo(path, (wait ? "--native-probe-wait " : "--native-probe ") + Program.Quote(output)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })) {
                await Task.Run(() => process.WaitForExit());
            }
            var result = JsonUtil.Decode(File.ReadAllText(output, JsonUtil.Utf8));
            Log("Native probe: " + JsonUtil.Encode(result));
            return JsonUtil.Flag(result, "Success") && JsonUtil.Text(result, "Country") == "loc=US";
        }
        public static int Run(string output) {
            Directory.CreateDirectory(output); logPath = Path.Combine(output, "live-test-report.txt"); File.WriteAllText(logPath, "", JsonUtil.Utf8);
            var settings = AppSettings.Load();
            using (var controller = new Controller(settings)) {
                controller.Status += (text, good) => Log("STATUS " + text);
                try { RunAsync(controller).GetAwaiter().GetResult(); Log("COMPLETE real setup and routing checks passed"); return 0; }
                catch (Exception ex) { Log("FAIL " + ex.ToString()); return 1; }
            }
        }
        static async Task RunAsync(Controller controller) {
            Log("Starting real ConnectAsync handler");
            await controller.ConnectAsync();
            Assert(controller.Connected && controller.Settings.Installed, "real setup completed and settings persisted");
            Assert(GuardService.Healthy(), "portable guard task and actual firewall rules are healthy");
            Assert(!Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ChatGPTBridge")) && !Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ChatGPTBridge")), "legacy application files removed from Program Files and ProgramData");
            Assert(controller.Api.Safe(controller.Routing.Rules(controller.Settings.UsNode, "DIRECT"), controller.Settings.UsNode), "live core pins OpenAI and Codex to US and other traffic to DIRECT");
            string trace = Fetch("https://chatgpt.com/cdn-cgi/trace", true);
            Log("ChatGPT trace through protected bridge: " + Country(trace));
            Assert(Country(trace) == "loc=US", "ChatGPT endpoint sees US egress");
            Log("Other sites in partial mode: " + Country(Fetch("https://www.cloudflare.com/cdn-cgi/trace", false)));
            Assert(await ProbeNative(Path.Combine(AppSettings.Root, "native-probe-good.json"), true), "native Codex-like process reaches ChatGPT through US with physical-interface blocks enabled");
            controller.Integration.SetStartup(false);
            using (var run = Registry.CurrentUser.OpenSubKey(Integration.RunKey)) Assert(run == null || run.GetValue("ChatGPTBridge") == null, "startup checkbox off removes startup entry");
            controller.Integration.SetStartup(true);
            using (var run = Registry.CurrentUser.OpenSubKey(Integration.RunKey)) Assert(Convert.ToString(run.GetValue("ChatGPTBridge")).Contains(Process.GetCurrentProcess().MainModule.FileName), "startup checkbox on references this application folder");
            await controller.ToggleFullAsync();
            Assert(controller.FullVpn && controller.GeneralNode != "DIRECT", "full VPN selects an available latency-tested node");
            Assert(controller.Api.Safe(controller.Routing.Rules(controller.Settings.UsNode, controller.GeneralNode), controller.Settings.UsNode), "full VPN leaves OpenAI rules pinned to US");
            Log("Other sites in full mode: " + Country(Fetch("https://www.cloudflare.com/cdn-cgi/trace", false)));
            Assert(Country(Fetch("https://chatgpt.com/cdn-cgi/trace", true)) == "loc=US", "ChatGPT stays in US while general VPN is on");
            await controller.ToggleFullAsync();
            Assert(!controller.FullVpn && controller.Api.Safe(controller.Routing.Rules(controller.Settings.UsNode, "DIRECT"), controller.Settings.UsNode), "full VPN off restores DIRECT while US routing remains");
            Log("TEST accidental foreign routing: select another GLOBAL node and change core mode; always restore in finally");
            try {
                string other = controller.Profile.Nodes.First(n => n != controller.Settings.UsNode);
                controller.Api.Request("PUT", "/proxies/GLOBAL", new Dictionary<string, object> { { "name", other } });
                controller.Api.Request("PATCH", "/configs", new Dictionary<string, object> { { "mode", "global" } });
                bool denied = false; try { Fetch("https://chatgpt.com/cdn-cgi/trace", true); } catch (WebException) { denied = true; }
                Assert(denied, "mandatory bridge immediately rejects a modified global route to a different server");
                await Task.Delay(2500);
                string output = Path.Combine(AppSettings.Root, "native-probe-wrong-route.json");
                await ProbeNative(output, false);
                Assert(!JsonUtil.Flag(JsonUtil.Decode(File.ReadAllText(output, JsonUtil.Utf8)), "Success"), "independent guard blocks native clients after foreign/global routing is detected");
            } finally { controller.ApplyAsync("DIRECT").GetAwaiter().GetResult(); controller.CheckAsync().GetAwaiter().GetResult(); }
            Log("TEST unavailable upstream: temporarily replace only the selected US endpoint; always restore in finally");
            string original = controller.Profile.OriginalYaml;
            string broken = BreakUsEndpoint(original, controller.Settings.UsNode);
            try {
                controller.Api.Request("PUT", "/configs?force=true", new Dictionary<string, object> { { "payload", controller.Routing.RuntimeYaml(broken, controller.Settings.UsNode, "DIRECT", controller.Profile.MixedPort, controller.Profile.Secret) } }, 15000);
                await controller.CheckAsync();
                bool blocked = false; try { Fetch("https://chatgpt.com/cdn-cgi/trace", true); } catch (WebException) { blocked = true; }
                Assert(blocked && controller.UsDelay == 0, "unavailable US endpoint blocks the real bridge without direct fallback");
                string blockedOutput = Path.Combine(AppSettings.Root, "native-probe-blocked.json");
                await ProbeNative(blockedOutput, false);
                Assert(!JsonUtil.Flag(JsonUtil.Decode(File.ReadAllText(blockedOutput, JsonUtil.Utf8)), "Success"), "native Codex-like process cannot bypass the unavailable US endpoint");
            } finally { controller.ApplyAsync("DIRECT").GetAwaiter().GetResult(); controller.CheckAsync().GetAwaiter().GetResult(); }
            bool recovered = false; var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!recovered && DateTime.UtcNow < deadline) {
                await controller.CheckAsync();
                if (controller.UsDelay > 0) { try { recovered = Country(Fetch("https://chatgpt.com/cdn-cgi/trace", true)) == "loc=US"; } catch (WebException) { } }
                if (!recovered) await Task.Delay(2000);
            }
            Assert(recovered, "US route recovers automatically after the endpoint is restored");
            Assert(File.ReadAllText(controller.Profile.ProfilePath, JsonUtil.Utf8) == original, "real subscription source unchanged by all tests");
        }
        static string BreakUsEndpoint(string yaml, string usNode) {
            var lines = yaml.Replace("\r", "").Split('\n'); bool proxies = false, selected = false, changed = false;
            for (int i = 0; i < lines.Length; ++i) {
                if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"^proxies:\s*$")) { proxies = true; continue; }
                if (proxies && System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"^\S")) break;
                var name = System.Text.RegularExpressions.Regex.Match(lines[i], @"^\s*-?\s*name:\s*(.*?)\s*$");
                if (proxies && name.Success) selected = FlProfile.ParseScalar(name.Groups[1].Value) == usNode;
                if (selected && System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"^\s+server:")) { lines[i] = "    server: 127.0.0.1"; changed = true; }
                if (selected && System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"^\s+port:")) lines[i] = "    port: 1";
            }
            if (!changed) throw new InvalidOperationException("Cannot safely isolate the US endpoint for the outage test.");
            return string.Join("\n", lines);
        }
    }
}
