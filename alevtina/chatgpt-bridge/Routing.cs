using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatGptBridge {
    public sealed class DomainEntry {
        public int Type;
        public string Value;
        public DomainEntry(int type, string value) { Type = type; Value = type == 1 ? value : value.ToLowerInvariant().TrimEnd('.'); }
    }
    public sealed class Routing {
        public const string TunName = "FlClashX";
        public readonly List<DomainEntry> Domains = new List<DomainEntry>();
        public readonly List<string> Processes = new List<string> { "ChatGPT.exe", "codex.exe", "Codex.exe", "codex-code-mode-host.exe" };
        public Routing(string dataDir) {
            foreach (string domain in new[] { "openai.com", "chatgpt.com", "chat.com", "oaistatic.com", "oaiusercontent.com", "openaiapi-site.azureedge.net", "chatgpt.livekit.cloud", "o33249.ingest.sentry.io" }) Domains.Add(new DomainEntry(2, domain));
            string geo = Path.Combine(dataDir, "GeoSite.dat");
            if (File.Exists(geo)) {
                // Read the openai category only. No runtime dependency on a geosite database.
                foreach (DomainEntry e in GeoSite.ReadOpenAi(File.ReadAllBytes(geo))) if (!Domains.Any(d => d.Type == e.Type && d.Value == e.Value)) Domains.Add(e);
            }
        }
        public bool IsProtected(string host, string process = "") {
            host = (host ?? "").ToLowerInvariant().TrimEnd('.');
            process = Path.GetFileName(process ?? "");
            if (Processes.Any(p => p.Equals(process, StringComparison.OrdinalIgnoreCase)) || Regex.IsMatch(process, @"^codex(?:-.*)?\.exe$", RegexOptions.IgnoreCase)) return true;
            foreach (var d in Domains) {
                if (d.Type == 0 && host.Contains(d.Value)) return true;
                if (d.Type == 1 && Regex.IsMatch(host, d.Value)) return true;
                if (d.Type == 2 && (host == d.Value || host.EndsWith("." + d.Value, StringComparison.Ordinal))) return true;
                if (d.Type == 3 && host == d.Value) return true;
            }
            return false;
        }
        public List<string> Rules(string usNode, string catchAll) {
            ValidateNode(usNode); ValidateNode(catchAll);
            var rules = new List<string> { "DOMAIN,localhost,DIRECT", "DOMAIN-SUFFIX,local,DIRECT", "IP-CIDR,127.0.0.0/8,DIRECT,no-resolve", "IP-CIDR,10.0.0.0/8,DIRECT,no-resolve", "IP-CIDR,172.16.0.0/12,DIRECT,no-resolve", "IP-CIDR,192.168.0.0/16,DIRECT,no-resolve", "IP-CIDR6,::1/128,DIRECT,no-resolve", "IP-CIDR6,fc00::/7,DIRECT,no-resolve", "IP-CIDR6,fe80::/10,DIRECT,no-resolve" };
            foreach (var d in Domains) rules.Add(new[] { "DOMAIN-KEYWORD", "DOMAIN-REGEX", "DOMAIN-SUFFIX", "DOMAIN" }[d.Type] + "," + d.Value + "," + usNode);
            foreach (var p in Processes) rules.Add("PROCESS-NAME," + p + "," + usNode);
            rules.Add(@"PROCESS-NAME-REGEX,(?i)^codex(?:-.*)?\.exe$," + usNode);
            rules.Add("MATCH," + catchAll);
            return rules;
        }
        public string PacScript() {
            var script = new StringBuilder("function FindProxyForURL(url, host) { host = host.toLowerCase().replace(/\\.$/, '');\n");
            foreach (var d in Domains) {
                string v = JsonUtil.Encode(d.Value);
                string condition = d.Type == 0 ? "host.indexOf(" + v + ") >= 0" : d.Type == 1 ? "new RegExp(" + v + ").test(host)" : d.Type == 2 ? "(host === " + v + " || host.slice(-(" + d.Value.Length + " + 1)) === '.' + " + v + ")" : "host === " + v;
                script.Append("if (").Append(condition).Append(") return 'PROXY 127.0.0.1:").Append(AppSettings.BridgePort).Append("';\n");
            }
            return script.Append("return 'DIRECT';\n}\n").ToString();
        }
        public static void ValidateNode(string node) { if (string.IsNullOrWhiteSpace(node) || node.IndexOfAny(new[] { ',', '\r', '\n' }) >= 0) throw new InvalidOperationException("Неподдерживаемое название сервера."); }
        public string RuntimeYaml(string original, string usNode, string catchAll, int mixedPort, string secret) {
            var remove = new HashSet<string>(new[] { "rules", "mode", "external-controller", "secret", "allow-lan", "tun", "mixed-port", "log-level", "find-process-mode" });
            var s = new StringBuilder(); bool skip = false;
            foreach (string line in original.Replace("\r", "").Split('\n')) {
                Match root = Regex.Match(line, @"^([a-zA-Z][\w-]*):");
                if (root.Success) skip = remove.Contains(root.Groups[1].Value);
                if (!skip) s.AppendLine(line);
            }
            s.AppendLine("mode: rule").AppendLine("allow-lan: false").AppendLine("log-level: warning").AppendLine("find-process-mode: always");
            s.AppendLine("mixed-port: " + mixedPort).AppendLine("external-controller: 127.0.0.1:" + AppSettings.ControllerPort);
            s.AppendLine("secret: " + JsonUtil.Encode(secret));
            s.AppendLine("tun:").AppendLine("  enable: true").AppendLine("  device: " + TunName).AppendLine("  auto-route: true").AppendLine("  stack: mixed").AppendLine("  dns-hijack:").AppendLine("    - any:53");
            s.AppendLine("rules:"); foreach (string rule in Rules(usNode, catchAll)) s.AppendLine("  - " + JsonUtil.Encode(rule));
            return s.ToString();
        }
    }
    public sealed class FlProfile {
        public string PreferencesPath, ProfilePath, OriginalYaml;
        public Dictionary<string, object> Outer, Config, Active;
        public List<string> Nodes;
        public int MixedPort;
        public string Secret;
        public static FlProfile Read(string dataDir) {
            var f = new FlProfile();
            f.PreferencesPath = Path.Combine(dataDir, "shared_preferences.json");
            if (!File.Exists(f.PreferencesPath)) throw new InvalidOperationException("Не найдены настройки FlClashX. Сначала добавьте подписку в клиент.");
            f.Outer = JsonUtil.Decode(File.ReadAllText(f.PreferencesPath, JsonUtil.Utf8));
            f.Config = JsonUtil.Decode(JsonUtil.Text(f.Outer, "flutter.config"));
            string id = JsonUtil.Text(f.Config, "currentProfileId");
            if (!Regex.IsMatch(id, @"^\d+$")) throw new InvalidOperationException("Не выбран профиль FlClashX.");
            f.Active = JsonUtil.Items(JsonUtil.Get(f.Config, "profiles")).Select(JsonUtil.Obj).SingleOrDefault(p => JsonUtil.Text(p, "id") == id);
            if (f.Active == null) throw new InvalidOperationException("Активный профиль FlClashX не найден.");
            f.ProfilePath = Path.Combine(dataDir, "profiles", id + ".yaml");
            f.OriginalYaml = File.ReadAllText(f.ProfilePath, JsonUtil.Utf8);
            f.Nodes = ProxyNames(f.OriginalYaml);
            var patch = JsonUtil.Obj(JsonUtil.Get(f.Config, "patchClashConfig"));
            f.MixedPort = Convert.ToInt32(JsonUtil.Get(patch, "mixed-port", 7890));
            if (f.MixedPort < 1 || f.MixedPort > 65535 || f.MixedPort == AppSettings.BridgePort || f.MixedPort == AppSettings.ControllerPort) throw new InvalidOperationException("Конфликт локальных портов клиента.");
            Match sec = Regex.Match(f.OriginalYaml, @"(?m)^secret:\s*(.*?)\s*$");
            f.Secret = sec.Success ? ParseScalar(sec.Groups[1].Value) : "";
            return f;
        }
        public static string ParseScalar(string value) {
            value = value.Trim();
            if (value.StartsWith("\"")) return Convert.ToString(new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(value));
            if (value.StartsWith("'") && value.EndsWith("'")) return value.Substring(1, value.Length - 2).Replace("''", "'");
            return Regex.Replace(value, @"\s+#.*$", "");
        }
        public static List<string> ProxyNames(string yaml) {
            var result = new List<string>(); bool inside = false;
            foreach (string line in yaml.Replace("\r", "").Split('\n')) {
                if (Regex.IsMatch(line, @"^proxies:\s*$")) { inside = true; continue; }
                if (inside && Regex.IsMatch(line, @"^\S")) break;
                Match name = Regex.Match(line, @"^\s*-?\s*name:\s*(.*?)\s*$");
                if (inside && name.Success) result.Add(ParseScalar(name.Groups[1].Value));
            }
            if (result.Count == 0) throw new InvalidOperationException("В подписке не найдены серверы. Этот формат профиля пока не поддерживается.");
            return result;
        }
        public string FindUs(string chosen) {
            if (Nodes.Contains(chosen)) return chosen;
            var matches = Nodes.Where(n => Regex.IsMatch(n, @"(^|\s)(США|USA|United States)(\s|$)", RegexOptions.IgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Выберите сервер США в списке надстройки.");
            return matches[0];
        }
        public void PrepareSettings(Routing routing, string usNode, string catchAll = "DIRECT") {
            var ruleObjects = routing.Rules(usNode, catchAll).Select(r => new Dictionary<string, object> { { "id", Guid.NewGuid().ToString() }, { "value", r } }).ToArray();
            Active["overrideData"] = new Dictionary<string, object> { { "enable", true }, { "rule", new Dictionary<string, object> { { "type", "override" }, { "overrideRules", ruleObjects }, { "addedRules", new object[0] } } } };
            var patch = JsonUtil.Obj(JsonUtil.Get(Config, "patchClashConfig"));
            patch["mode"] = "rule"; patch["external-controller"] = "127.0.0.1:9090"; patch["allow-lan"] = false; patch["find-process-mode"] = "always";
            var tun = JsonUtil.Obj(JsonUtil.Get(patch, "tun")); tun["enable"] = true; tun["device"] = Routing.TunName; tun["auto-route"] = true; patch["tun"] = tun;
            Config["patchClashConfig"] = patch;
            var app = JsonUtil.Obj(JsonUtil.Get(Config, "appSetting")); app["autoLaunch"] = false; app["silentLaunch"] = true; app["autoRun"] = true; app["closeConnections"] = true;
            if (app.ContainsKey("overrideProviderSettings")) app["overrideProviderSettings"] = true;
            Config["appSetting"] = app;
            var network = JsonUtil.Obj(JsonUtil.Get(Config, "networkProps")); network["systemProxy"] = false; Config["networkProps"] = network;
            var scripts = JsonUtil.Obj(JsonUtil.Get(Config, "scriptProps")); scripts["currentId"] = null; Config["scriptProps"] = scripts;
            Outer["flutter.config"] = JsonUtil.Encode(Config);
        }
        public void Save() { JsonUtil.Save(PreferencesPath, Outer); }
    }
    public static class GeoSite {
        private sealed class Reader {
            internal byte[] Data; internal int Position, End;
            internal Reader(byte[] data, int start, int length) { Data = data; Position = start; End = start + length; }
            internal long Varint() { long n = 0; for (int shift = 0; shift < 64; shift += 7) { if (Position >= End) throw new InvalidDataException("GeoSite: truncated data."); byte b = Data[Position++]; n |= (long)(b & 127) << shift; if ((b & 128) == 0) return n; } throw new InvalidDataException("GeoSite: invalid integer."); }
            internal Reader Slice() { int length = checked((int)Varint()); if (length < 0 || length > End - Position) throw new InvalidDataException("GeoSite: invalid length."); var r = new Reader(Data, Position, length); Position += length; return r; }
            internal string Text() { return JsonUtil.Utf8.GetString(Data, Position, End - Position); }
            internal void Skip(int wire) { if (wire == 0) Varint(); else if (wire == 2) Slice(); else if (wire == 1) Position += 8; else if (wire == 5) Position += 4; else throw new InvalidDataException("GeoSite: unsupported field."); if (Position > End) throw new InvalidDataException("GeoSite: truncated field."); }
        }
        public static List<DomainEntry> ReadOpenAi(byte[] bytes) {
            var result = new List<DomainEntry>(); var list = new Reader(bytes, 0, bytes.Length);
            while (list.Position < list.End) {
                int key = (int)list.Varint(); if (key != 10) { list.Skip(key & 7); continue; }
                Reader group = list.Slice(); string country = ""; var domains = new List<DomainEntry>();
                while (group.Position < group.End) {
                    key = (int)group.Varint();
                    if (key == 10) country = group.Slice().Text();
                    else if (key == 18) {
                        Reader domain = group.Slice(); int type = 0; string value = "";
                        while (domain.Position < domain.End) { int dk = (int)domain.Varint(); if (dk == 8) type = (int)domain.Varint(); else if (dk == 18) value = domain.Slice().Text(); else domain.Skip(dk & 7); }
                        if (type >= 0 && type <= 3 && value.Length > 0 && value.IndexOfAny(new[] { ',', '\r', '\n' }) < 0) domains.Add(new DomainEntry(type, value));
                    } else group.Skip(key & 7);
                }
                if (country.Equals("openai", StringComparison.OrdinalIgnoreCase)) { result.AddRange(domains); break; }
            }
            return result;
        }
    }
}
