using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace ChatGptBridge {
    public sealed class CoreApi {
        public readonly int Port;
        public readonly string Secret;
        public CoreApi(int port, string secret) { Port = port; Secret = secret; }
        public Dictionary<string, object> Request(string method, string path, object data = null, int timeout = 4000) {
            if (!path.StartsWith("/") || path.StartsWith("//")) throw new InvalidOperationException("Некорректный адрес локального клиента.");
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port + path);
            req.Proxy = null; req.Method = method; req.Timeout = timeout; req.ReadWriteTimeout = timeout;
            req.KeepAlive = false;
            if (!string.IsNullOrEmpty(Secret)) req.Headers["Authorization"] = "Bearer " + Secret;
            if (data != null) { byte[] bytes = JsonUtil.Utf8.GetBytes(JsonUtil.Encode(data)); req.ContentType = "application/json"; req.ContentLength = bytes.Length; using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length); }
            using (var resp = req.GetResponse()) using (var stream = resp.GetResponseStream()) using (var reader = new StreamReader(stream, JsonUtil.Utf8)) {
                string body = reader.ReadToEnd(); return body.Length == 0 ? new Dictionary<string, object>() : JsonUtil.Decode(body);
            }
        }
        public int Delay(string node, string testUrl = "https://www.gstatic.com/generate_204") {
            try { return Convert.ToInt32(JsonUtil.Get(Request("GET", "/proxies/" + Uri.EscapeDataString(node) + "/delay?timeout=4000&url=" + Uri.EscapeDataString(testUrl), null, 5500), "delay", 0)); }
            catch (WebException) { return 0; }
        }
        public bool Safe(List<string> expectedRules, string usNode) {
            try {
                var config = Request("GET", "/configs");
                var tun = JsonUtil.Obj(JsonUtil.Get(config, "tun"));
                if (JsonUtil.Text(config, "mode") != "rule" || !JsonUtil.Flag(tun, "enable") || JsonUtil.Text(tun, "device") != Routing.TunName) return false;
                var actual = JsonUtil.Items(JsonUtil.Get(Request("GET", "/rules"), "rules")).Select(JsonUtil.Obj).ToArray();
                if (actual.Length != expectedRules.Count) return false;
                for (int i = 0; i < actual.Length; ++i) {
                    string[] parts = expectedRules[i].Split(',');
                    string type = parts[0].Replace("-", "").Replace("_", "");
                    if (type == "IPCIDR6") type = "IPCIDR"; // Mihomo reports both IP families as IPCIDR.
                    if (!type.Equals(JsonUtil.Text(actual[i], "type").Replace("-", "").Replace("_", ""), StringComparison.OrdinalIgnoreCase)) return false;
                    string payload = parts[0] == "MATCH" ? "" : parts[1];
                    string target = parts[0] == "MATCH" ? parts[1] : parts[2];
                    if (JsonUtil.Text(actual[i], "payload") != payload || JsonUtil.Text(actual[i], "proxy") != target) return false;
                    if (JsonUtil.Flag(JsonUtil.Obj(JsonUtil.Get(actual[i], "extra")), "disabled")) return false;
                }
                Request("GET", "/proxies/" + Uri.EscapeDataString(usNode));
                return true;
            } catch { return false; }
        }
        public void CloseUnprotected(Routing routing) {
            foreach (var c in JsonUtil.Items(JsonUtil.Get(Request("GET", "/connections"), "connections")).Select(JsonUtil.Obj)) {
                var meta = JsonUtil.Obj(JsonUtil.Get(c, "metadata"));
                if (routing.IsProtected(JsonUtil.Text(meta, "host"), JsonUtil.Text(meta, "process")) || routing.IsProtected("", JsonUtil.Text(meta, "processPath"))) continue;
                string id = JsonUtil.Text(c, "id"); if (id.Length > 0) Request("DELETE", "/connections/" + Uri.EscapeDataString(id));
            }
        }
        public async Task<List<DelayResult>> MeasureAsync(IEnumerable<string> nodes, Action<int, int> progress) {
            var names = nodes.Distinct().ToArray(); var limiter = new SemaphoreSlim(4); int done = 0;
            var tasks = names.Select(async n => {
                await limiter.WaitAsync();
                try {
                    var samples = new List<int>();
                    for (int i = 0; i < 2; ++i) { int v = await Task.Run(() => Delay(n)); if (v > 0) samples.Add(v); }
                    int measured = samples.Count == 0 ? 0 : (int)Math.Round(samples.Average());
                    if (progress != null) progress(Interlocked.Increment(ref done), names.Length);
                    return new DelayResult(n, measured);
                } finally { limiter.Release(); }
            }).ToArray();
            return (await Task.WhenAll(tasks)).OrderBy(d => d.Delay == 0 ? int.MaxValue : d.Delay).ThenBy(d => d.Node, StringComparer.Ordinal).ToList();
        }
    }
    public sealed class DelayResult {
        public string Node; public int Delay;
        public DelayResult(string node, int delay) { Node = node; Delay = delay; }
        public static DelayResult Fastest(IEnumerable<DelayResult> results) { return results.Where(d => d.Delay > 0).OrderBy(d => d.Delay).ThenBy(d => d.Node, StringComparer.Ordinal).FirstOrDefault(); }
    }
}
