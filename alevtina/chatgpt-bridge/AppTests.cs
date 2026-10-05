using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ChatGptBridge {
    static class AppTests {
        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        public static int Run(string output) {
            Directory.CreateDirectory(output); string fixture = Path.Combine(output, "fixture"); Directory.CreateDirectory(Path.Combine(fixture, "profiles"));
            var report = new List<string>();
            try {
                string blockedStorage = Path.Combine(fixture, "blocked-storage"); File.WriteAllText(blockedStorage, "This is a file, not a writable directory.");
                Assert(!AppSettings.CanWriteDirectory(blockedStorage), "Unavailable storage was reported as writable.");
                Assert(AppSettings.CanWriteDirectory(Path.Combine(fixture, "portable-storage")), "Writable portable storage was rejected.");
                report.Add("PASS storage probing rejects unavailable directories and accepts portable storage without changing access rights.");
                Assert(Integration.GuardInstallLaunchError(1223).Contains("1223") && Integration.GuardInstallLaunchError(1223).Contains("Если запроса не было"), "Missing-UAC failure lost actionable diagnostics.");
                Assert(Integration.GuardInstallLaunchError(5).Contains("права администратора"), "Access-denied failure lost actionable diagnostics.");
                report.Add("PASS installer failures distinguish absent/denied elevation and preserve Windows error codes.");
                string yaml = "mixed-port: 7890\nmode: global\nsecret: 'test-secret'\nproxies:\n  - name: 'USA'\n    type: vless\n    server: example.invalid\n    uuid: synthetic-not-a-real-secret\n  - name: 'Latvia'\n    type: vless\nproxy-groups:\n  - name: old-group\n    type: select\n    proxies: [USA, Latvia]\nrules:\n  - MATCH,old-group\n";
                var config = new Dictionary<string, object> {
                    { "profiles", new[] { new Dictionary<string, object> { { "id", "123" }, { "url", "https://example.invalid/subscription" }, { "overrideData", new Dictionary<string, object> { { "enable", false } } } } } },
                    { "currentProfileId", "123" }, { "patchClashConfig", new Dictionary<string, object> { { "mode", "global" }, { "mixed-port", 7890 }, { "tun", new Dictionary<string, object> { { "enable", true } } } } },
                    { "appSetting", new Dictionary<string, object> { { "autoLaunch", true } } }, { "networkProps", new Dictionary<string, object>() }, { "scriptProps", new Dictionary<string, object> { { "currentId", "old-script" } } }
                };
                JsonUtil.Save(Path.Combine(fixture, "shared_preferences.json"), new Dictionary<string, object> { { "flutter.config", JsonUtil.Encode(config) }, { "other", "unchanged" } });
                File.WriteAllText(Path.Combine(fixture, "profiles", "123.yaml"), yaml, JsonUtil.Utf8);
                var profile = FlProfile.Read(fixture); var routing = new Routing(fixture);
                Assert(profile.FindUs("") == "USA" && profile.Secret == "test-secret", "Profile discovery failed.");
                var full = routing.Rules("USA", "Latvia");
                Assert(full.Last() == "MATCH,Latvia", "General VPN route missing.");
                Assert(full.Where(r => r.Contains("chatgpt.com") || r.Contains("PROCESS-NAME,")).All(r => r.EndsWith(",USA")), "OpenAI moved away from US in full mode.");
                Assert(!full.Any(r => r.StartsWith("PROCESS-NAME,Code.exe")), "Entire VS Code was unnecessarily proxied.");
                Assert(routing.IsProtected("AUTH.OPENAI.COM.") && !routing.IsProtected("chatgpt.com.evil.invalid"), "Domain boundary matching failed.");
                string runtime = routing.RuntimeYaml(yaml, "USA", "Latvia", 7890, profile.Secret);
                Assert(runtime.Contains("mode: rule") && !runtime.Contains("mode: global") && !runtime.Contains("MATCH,old-group"), "Old global/MATCH settings survived.");
                Assert(runtime.Contains("uuid: synthetic-not-a-real-secret") && runtime.Contains("proxy-groups:"), "Subscription credentials or groups were lost.");
                File.WriteAllText(Path.Combine(output, "sample-runtime.yaml"), runtime, JsonUtil.Utf8);
                File.WriteAllText(Path.Combine(output, "openai-route.pac"), routing.PacScript(), JsonUtil.Utf8);
                profile.PrepareSettings(routing, "USA"); profile.Save(); var updated = FlProfile.Read(fixture);
                Assert(JsonUtil.Text(updated.Active, "url") == "https://example.invalid/subscription", "Subscription URL changed.");
                Assert(JsonUtil.Text(updated.Outer, "other") == "unchanged", "Unrelated preference changed.");
                Assert(File.ReadAllText(updated.ProfilePath) == yaml, "Source profile was edited.");
                Assert(JsonUtil.Text(JsonUtil.Obj(JsonUtil.Get(updated.Config, "scriptProps")), "currentId") == "", "Conflicting override script stayed enabled.");
                updated.PrepareSettings(routing, "USA", "Latvia"); updated.Save();
                var savedRules = JsonUtil.Items(JsonUtil.Get(JsonUtil.Obj(JsonUtil.Get(JsonUtil.Obj(JsonUtil.Get(FlProfile.Read(fixture).Active, "overrideData")), "rule")), "overrideRules")).Select(JsonUtil.Obj).Select(r => JsonUtil.Text(r, "value")).ToList();
                Assert(savedRules.SequenceEqual(full), "Persistent fallback did not preserve full VPN and US pinning.");
                report.Add("PASS profile discovery, credentials preservation, persistent overrides, US pinning in full VPN, domain boundaries.");

                using (var core = new FakeCore(full)) {
                    var api = new CoreApi(core.Port, "test-secret");
                    Assert(api.Safe(full, "USA"), "Valid core rules were rejected.");
                    core.Mode = "global"; Assert(!api.Safe(full, "USA"), "Global mode was accepted."); core.Mode = "rule";
                    core.Tun = false; Assert(!api.Safe(full, "USA"), "Disabled TUN was accepted."); core.Tun = true;
                    core.Tamper = true; Assert(!api.Safe(full, "USA"), "Non-US protected route was accepted."); core.Tamper = false;
                    var delays = api.MeasureAsync(new[] { "USA", "Latvia", "offline" }, null).GetAwaiter().GetResult();
                    Assert(DelayResult.Fastest(delays).Node == "Latvia", "Lowest-latency available node was not selected.");
                    Assert(DelayResult.Fastest(new[] { new DelayResult("offline", 0) }) == null, "Offline node was selected.");
                    report.Add("PASS core safety rejects global mode, disabled TUN, and modified US routes; latency selection excludes unavailable nodes.");
                }
                TestGate(routing, report);
                bool badNode = false; try { routing.Rules("USA,DIRECT", "DIRECT"); } catch { badNode = true; } Assert(badNode, "Rule injection was accepted.");
                Assert(!ProxyGate.ValidAuthority("chatgpt.com:443\r\nHost: evil.invalid"), "Header injection was accepted.");
                report.Add("PASS server-name and CONNECT input validation.");
                File.WriteAllLines(Path.Combine(output, "test-report.txt"), report, JsonUtil.Utf8);
                return 0;
            } catch (Exception ex) {
                report.Add("FAIL " + ex.ToString()); File.WriteAllLines(Path.Combine(output, "test-report.txt"), report, JsonUtil.Utf8); return 1;
            }
        }
        static string RequestGate(int port, string authority, bool echo) {
            using (var c = new TcpClient()) {
                c.Connect(IPAddress.Loopback, port); var s = c.GetStream(); s.ReadTimeout = 5000;
                byte[] bytes = Encoding.ASCII.GetBytes("CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\n\r\n"); s.Write(bytes, 0, bytes.Length);
                string header = Encoding.ASCII.GetString(ProxyGate.ReadHeader(s));
                if (echo && header.Contains(" 200 ")) { byte[] test = Encoding.ASCII.GetBytes("end-to-end-tls-bytes"); s.Write(test, 0, test.Length); var received = new byte[test.Length]; int n = 0; while (n < received.Length) { int got = s.Read(received, n, received.Length - n); if (got == 0) break; n += got; } Assert(n == test.Length && Encoding.ASCII.GetString(received) == "end-to-end-tls-bytes", "TLS relay corrupted the stream."); }
                return header;
            }
        }
        static void TestGate(Routing routing, List<string> report) {
            var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start(); int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
            var allocation = new TcpListener(IPAddress.Loopback, 0); allocation.Start(); int port = ((IPEndPoint)allocation.LocalEndpoint).Port; allocation.Stop();
            bool safe = false; int upstreamConnections = 0;
            var server = Task.Run(async () => {
                try {
                    using (var c = await upstream.AcceptTcpClientAsync()) {
                        System.Threading.Interlocked.Increment(ref upstreamConnections); var s = c.GetStream(); s.ReadTimeout = 5000;
                        string header = Encoding.ASCII.GetString(ProxyGate.ReadHeader(s)); Assert(header.StartsWith("CONNECT chatgpt.com:443"), "Destination was changed.");
                        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"); s.Write(response, 0, response.Length);
                        byte[] data = new byte[4096]; int n = s.Read(data, 0, data.Length); s.Write(data, 0, n);
                    }
                } catch (ObjectDisposedException) { }
            });
            try {
                using (var gate = new ProxyGate(port, () => safe, () => upstreamPort, host => routing.IsProtected(host))) {
                    gate.Start();
                    Assert(RequestGate(port, "chatgpt.com:443", false).Contains(" 503 "), "Unsafe route did not fail closed.");
                    Assert(upstreamConnections == 0, "An unsafe request reached the upstream proxy.");
                    safe = true;
                    Assert(RequestGate(port, "127.0.0.1:9090", false).Contains(" 403 "), "Arbitrary local destination was accepted.");
                    Assert(RequestGate(port, "chatgpt.com:443", true).Contains(" 200 "), "Safe proxy request was rejected.");
                    Assert(server.Wait(5000), "Relay did not finish.");
                    if (server.IsFaulted) throw server.Exception;
                    upstream.Stop();
                    Assert(RequestGate(port, "chatgpt.com:443", false).Contains(" 503 "), "Missing upstream did not fail closed.");
                }
                report.Add("PASS real loopback CONNECT relay preserves bytes; unsafe route and stopped upstream return 503; local destinations are rejected; no direct fallback.");
            } finally { upstream.Stop(); }
        }
        sealed class FakeCore : IDisposable {
            readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            readonly List<string> rules;
            bool stopped;
            public string Mode = "rule";
            public bool Tun = true, Tamper;
            public int Port;
            public FakeCore(List<string> rules) { this.rules = rules; listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; Task.Run((Func<Task>)Accept); }
            async Task Accept() { while (!stopped) { try { TcpClient c = await listener.AcceptTcpClientAsync(); Task.Run(() => Serve(c)); } catch { if (stopped) break; } } }
            void Serve(TcpClient client) {
                using (client) {
                    var stream = client.GetStream(); stream.ReadTimeout = 5000;
                    string request = Encoding.ASCII.GetString(ProxyGate.ReadHeader(stream)); string path = request.Split(' ')[1]; object answer;
                    if (path == "/configs") answer = new { mode = Mode, tun = new { enable = Tun, device = Routing.TunName } };
                    else if (path == "/rules") answer = new { rules = rules.Select(r => {
                        string[] parts = r.Split(','); string type = parts[0].Replace("-", ""); if (type == "IPCIDR6") type = "IPCIDR";
                        return new { type = type, payload = type == "MATCH" ? "" : parts[1], proxy = type == "MATCH" ? parts[1] : (Tamper && parts[1] == "chatgpt.com" ? "Latvia" : parts[2]) };
                    }).ToArray() };
                    else if (path.Contains("/delay?")) answer = new { delay = path.Contains("Latvia") ? 32 : path.Contains("offline") ? 0 : 145 };
                    else answer = new { type = "Vless" };
                    byte[] body = JsonUtil.Utf8.GetBytes(JsonUtil.Encode(answer)); byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length); stream.Write(body, 0, body.Length);
                }
            }
            public void Dispose() { stopped = true; listener.Stop(); }
        }
    }
}
