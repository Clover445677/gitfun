using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace ChatGptBridge {
    public sealed class GuardService {
        public const string Id = "ChatGPTBridgeGuard";
        public const string RuleGroup = "ChatGPT Bridge: protected applications";
        public static string InstallRoot { get { return AppSettings.Root; } }
        public static string StatusRoot { get { return AppSettings.Root; } }
        public static string StatusPath { get { return Path.Combine(StatusRoot, "guard-status.json"); } }
        Timer timer;
        int busy;
        string profileRoot, fingerprint = "";
        List<string> programs;
        DateTime lastScan = DateTime.MinValue;
        static Routing cachedRouting;
        static string cachedDataDir, lastStatus;
        static DateTime cachedGeoTime, lastStatusWrite = DateTime.MinValue;
        public static string TaskName { get { return Id + "-" + WindowsIdentity.GetCurrent().User.Value; } }
        void Start() {
            var cfg = JsonUtil.Decode(File.ReadAllText(Path.Combine(InstallRoot, "guard.json"), JsonUtil.Utf8));
            profileRoot = JsonUtil.Text(cfg, "UserProfile");
            programs = ReadKnownPrograms();
            timer = new Timer(Tick, null, 0, 1000);
            NetworkChange.NetworkAddressChanged += NetworkChanged;
        }
        void NetworkChanged(object sender, EventArgs e) { lastScan = DateTime.MinValue; ThreadPool.QueueUserWorkItem(Tick); }
        void Stop() {
            if (timer != null) timer.Dispose(); NetworkChange.NetworkAddressChanged -= NetworkChanged;
            WriteStatus(false, "Служба защиты остановлена.", 0);
            // Keep persistent firewall rules in force when this service stops.
        }
        public static void RunLoop() {
            bool created;
            using (var mutex = new Mutex(true, @"Local\" + TaskName, out created)) {
                if (!created) return;
                var guard = new GuardService();
                try { guard.Start(); Thread.Sleep(Timeout.Infinite); }
                finally { guard.Stop(); }
            }
        }
        void Tick(object state) {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try {
                string[] adapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.Name != Routing.TunName)
                    .Select(n => n.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
                if (programs == null || (DateTime.UtcNow - lastScan).TotalSeconds >= 15) {
                    var discovered = DiscoverPrograms(profileRoot).Concat(programs ?? Enumerable.Empty<string>()).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                    if (programs == null || !programs.SequenceEqual(discovered, StringComparer.OrdinalIgnoreCase)) JsonUtil.Save(Path.Combine(InstallRoot, "protected-programs.json"), new { Programs = discovered });
                    programs = discovered; lastScan = DateTime.UtcNow;
                }
                var exes = programs;
                if (adapters.Length == 0 || exes.Count == 0) { WriteStatus(false, "Не найдены приложения или сетевые интерфейсы для защиты.", 0); return; }
                dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                foreach (int p in new[] { 1, 2, 4 }) {
                    bool enabled = Convert.ToBoolean(((object)policy).GetType().InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, (object)policy, new object[] { p }));
                    if (!enabled) { WriteStatus(false, "Брандмауэр Windows выключен. Защита приложений не подтверждена.", 0); return; }
                }
                string current = string.Join("|", adapters) + "\n" + string.Join("|", exes);
                if (current != fingerprint || !RulesPresent(policy, exes, adapters)) {
                    foreach (string exe in exes) {
                        dynamic rule;
                        string name = RuleName(exe);
                        try { rule = policy.Rules.Item(name); } catch { rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")); rule.Name = name; }
                        rule.Description = "Blocks direct egress outside the FlClashX tunnel; installed by ChatGPT Bridge.";
                        rule.ApplicationName = exe; rule.Direction = 2; rule.Action = 0; rule.Protocol = 256;
                        rule.Profiles = 7; rule.Interfaces = adapters.Cast<object>().ToArray(); rule.Grouping = RuleGroup; rule.Enabled = true;
                        try { policy.Rules.Item(name); } catch { policy.Rules.Add(rule); }
                    }
                    fingerprint = current;
                }
                // The mandatory Chrome proxy remains installed after the GUI exits.
                // Native clients also get a persistent all-interface block unless
                // a living controller has confirmed the exact US routing rules.
                bool confirmed = ConfirmRoute();
                foreach (string exe in exes) {
                    string name = EmergencyName(exe); dynamic rule;
                    try { rule = policy.Rules.Item(name); }
                    catch {
                        rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
                        rule.Name = name; rule.Description = "Blocks native clients unless ChatGPT Bridge confirms the US route.";
                        rule.ApplicationName = exe; rule.Direction = 2; rule.Action = 0; rule.Protocol = 256; rule.Profiles = 7;
                        rule.Grouping = RuleGroup; rule.Enabled = true; policy.Rules.Add(rule);
                    }
                    if ((bool)rule.Enabled == confirmed) rule.Enabled = !confirmed;
                }
                WriteStatus(true, confirmed ? "Защита включена; маршрут США подтверждён." : "Приложения заблокированы: запустите мост и восстановите маршрут США.", exes.Count);
            } catch (Exception ex) { WriteStatus(false, "Не удалось проверить правила брандмауэра Windows: " + ex.Message, 0); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }
        static bool RulesPresent(dynamic policy, List<string> exes, string[] adapters) {
            foreach (string exe in exes) {
                try {
                    dynamic r = policy.Rules.Item(RuleName(exe));
                    if (!(bool)r.Enabled || (int)r.Action != 0 || (int)r.Direction != 2 || (int)r.Profiles != 7 ||
                        !((string)r.ApplicationName).Equals(exe, StringComparison.OrdinalIgnoreCase)) return false;
                    var actual = ((Array)r.Interfaces).Cast<object>().Select(Convert.ToString).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    if (!actual.SequenceEqual(adapters)) return false;
                } catch { return false; }
            }
            return true;
        }
        static string RuleName(string exe) {
            using (var sha = SHA256.Create()) return "ChatGPTBridge-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(exe.ToLowerInvariant()))).Replace("-", "").Substring(0, 24);
        }
        static string EmergencyName(string exe) { return RuleName(exe) + "-RouteRequired"; }
        static List<string> ReadKnownPrograms() {
            try {
                var catalog = JsonUtil.Decode(File.ReadAllText(Path.Combine(InstallRoot, "protected-programs.json"), JsonUtil.Utf8));
                return JsonUtil.Items(JsonUtil.Get(catalog, "Programs")).Select(Convert.ToString).Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            } catch { return new List<string>(); }
        }
        static bool ConfirmRoute() {
            try {
                var route = JsonUtil.Decode(File.ReadAllText(Path.Combine(AppSettings.Root, "guard-route.json"), JsonUtil.Utf8));
                if (!JsonUtil.Flag(route, "Ready")) return false;
                DateTime utc = DateTime.Parse(JsonUtil.Text(route, "UpdatedUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind);
                if ((DateTime.UtcNow - utc.ToUniversalTime()).TotalSeconds > 35) return false;
                using (var owner = Process.GetProcessById(Convert.ToInt32(JsonUtil.Get(route, "PID", 0)))) {
                    if (owner.HasExited || !owner.MainModule.FileName.Equals(typeof(GuardService).Assembly.Location, StringComparison.OrdinalIgnoreCase)) return false;
                }
                string directory = JsonUtil.Text(route, "DataDir");
                DateTime geoTime = File.GetLastWriteTimeUtc(Path.Combine(directory, "GeoSite.dat"));
                if (cachedRouting == null || cachedDataDir != directory || cachedGeoTime != geoTime) {
                    cachedRouting = new Routing(directory); cachedDataDir = directory; cachedGeoTime = geoTime;
                }
                var routing = cachedRouting;
                return new CoreApi(AppSettings.ControllerPort, JsonUtil.Text(route, "Secret")).Safe(routing.Rules(JsonUtil.Text(route, "UsNode"), JsonUtil.Text(route, "GeneralNode", "DIRECT")), JsonUtil.Text(route, "UsNode"));
            } catch { return false; }
        }
        static void AddExe(List<string> result, string path) { if (File.Exists(path)) result.Add(Path.GetFullPath(path)); }
        internal static List<string> DiscoverPrograms(string userRoot) {
            var result = new List<string>();
            string extensions = Path.Combine(userRoot, @".vscode\extensions");
            if (Directory.Exists(extensions)) foreach (string dir in Directory.GetDirectories(extensions, "openai.chatgpt-*")) {
                AddExe(result, Path.Combine(dir, @"bin\windows-x86_64\codex.exe"));
                AddExe(result, Path.Combine(dir, @"bin\windows-aarch64\codex.exe"));
            }
            AddExe(result, Path.Combine(userRoot, @"AppData\Local\Programs\Microsoft VS Code\Code.exe"));
            AddExe(result, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft VS Code\Code.exe"));
            AddExe(result, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft VS Code\Code.exe"));
            string apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(apps)) {
                try { foreach (string dir in Directory.GetDirectories(apps, "OpenAI.*")) AddNativeChildren(result, dir); } catch (UnauthorizedAccessException) { }
            }
            foreach (Process p in Process.GetProcesses()) {
                try { string name = p.ProcessName; if (name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) || name.Equals("Code", StringComparison.OrdinalIgnoreCase) || name.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) AddExe(result, p.MainModule.FileName); } catch { } finally { p.Dispose(); }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        static void AddNativeChildren(List<string> result, string directory) {
            try {
                foreach (string path in Directory.GetFiles(directory, "*.exe", SearchOption.AllDirectories)) {
                    string file = Path.GetFileName(path);
                    if (file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || file.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) AddExe(result, path);
                }
            } catch (UnauthorizedAccessException) { }
        }
        static void WriteStatus(bool ready, string message, int count) {
            string signature = ready + ":" + message + ":" + count;
            if (signature == lastStatus && (DateTime.UtcNow - lastStatusWrite).TotalSeconds < 5) return;
            try {
                JsonUtil.Save(StatusPath, new Dictionary<string, object> { { "Ready", ready }, { "Message", message }, { "Programs", count }, { "PID", Process.GetCurrentProcess().Id }, { "UpdatedUtc", DateTime.UtcNow.ToString("o") } });
                lastStatus = signature; lastStatusWrite = DateTime.UtcNow;
            } catch { }
        }
        public static bool Healthy() {
            try {
                var s = JsonUtil.Decode(File.ReadAllText(StatusPath, JsonUtil.Utf8));
                DateTime utc = DateTime.Parse(JsonUtil.Text(s, "UpdatedUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (!JsonUtil.Flag(s, "Ready") || (DateTime.UtcNow - utc.ToUniversalTime()).TotalSeconds >= 50) return false;
                using (var worker = Process.GetProcessById(Convert.ToInt32(JsonUtil.Get(s, "PID", 0)))) {
                    if (worker.HasExited || !worker.MainModule.FileName.Equals(typeof(GuardService).Assembly.Location, StringComparison.OrdinalIgnoreCase)) return false;
                }
                var cfg = JsonUtil.Decode(File.ReadAllText(Path.Combine(InstallRoot, "guard.json"), JsonUtil.Utf8));
                string[] adapters = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.Name != Routing.TunName).Select(n => n.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
                var exes = DiscoverPrograms(JsonUtil.Text(cfg, "UserProfile")).Concat(ReadKnownPrograms()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                return adapters.Length > 0 && exes.Count > 0 && RulesPresent(policy, exes, adapters);
            } catch { return false; }
        }
        static dynamic TaskFolder() { dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); service.Connect(); return service.GetFolder(@"\"); }
        public static bool Exists() { try { dynamic task = TaskFolder().GetTask(TaskName); return true; } catch { return LegacyExists(); } }
        public static bool TaskRunning() { try { dynamic task = TaskFolder().GetTask(TaskName); return (int)task.State == 4; } catch { return false; } }
        static bool LegacyExists() { try { using (var c = new ServiceController(Id)) { var s = c.Status; return true; } } catch { return false; } }
        static void RunSc(string args) {
            using (var p = Process.Start(new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) {
                string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0) throw new InvalidOperationException("Windows не смогла настроить службу защиты. Код: " + p.ExitCode + "\n" + output.Trim() + "\n" + error.Trim());
            }
        }
        public static void Install(string source, string userProfile) {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("Для первоначальной настройки защиты требуются права администратора.");
            Directory.CreateDirectory(InstallRoot);
            JsonUtil.Save(Path.Combine(InstallRoot, "guard.json"), new Dictionary<string, object> { { "UserProfile", Path.GetFullPath(userProfile) } });
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); scheduler.Connect();
            dynamic folder = scheduler.GetFolder(@"\");
            try {
                dynamic old = folder.GetTask(TaskName);
                old.Stop(0);
                // Stop is asynchronous. Running a replacement while the previous
                // instance is stopping can be ignored by MultipleInstances=IgnoreNew.
                for (int i = 0; i < 40 && (int)old.State == 4; ++i) Thread.Sleep(250);
                if ((int)old.State == 4) throw new InvalidOperationException("Предыдущая задача защиты ещё останавливается. Повторите подключение.");
            } catch (System.Runtime.InteropServices.COMException ex) { if (ex.HResult != unchecked((int)0x80070002)) throw; }
            dynamic definition = scheduler.NewTask(0);
            definition.RegistrationInfo.Description = "ChatGPT Bridge protection under the current Windows user; files stay in the application folder.";
            definition.Principal.UserId = WindowsIdentity.GetCurrent().User.Value; definition.Principal.LogonType = 3; definition.Principal.RunLevel = 1;
            definition.Settings.Enabled = true; definition.Settings.StartWhenAvailable = true; definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.DisallowStartIfOnBatteries = false; definition.Settings.StopIfGoingOnBatteries = false; definition.Settings.MultipleInstances = 2;
            dynamic trigger = definition.Triggers.Create(9); trigger.UserId = WindowsIdentity.GetCurrent().User.Value; trigger.Enabled = true;
            dynamic action = definition.Actions.Create(0); action.Path = source; action.Arguments = "--guard-loop"; action.WorkingDirectory = Path.GetDirectoryName(source);
            dynamic registered = folder.RegisterTaskDefinition(TaskName, definition, 6, WindowsIdentity.GetCurrent().User.Value, null, 3, null);
            registered.Run(null);
            for (int i = 0; i < 40; ++i) { if (Healthy()) { CleanupLegacy(); return; } Thread.Sleep(500); }
            string reason = File.Exists(StatusPath) ? JsonUtil.Text(JsonUtil.Decode(File.ReadAllText(StatusPath, JsonUtil.Utf8)), "Message") : "Фоновая защита не запустилась.";
            throw new InvalidOperationException(reason);
        }
        public static void CleanupLegacy() {
            if (LegacyExists()) { using (var c = new ServiceController(Id)) { if (c.Status != ServiceControllerStatus.Stopped) { c.Stop(); c.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); } } RunSc("delete " + Id); }
            // Remove only files created by older builds, never other directory contents.
            foreach (string directory in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ChatGPTBridge"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ChatGPTBridge") }) {
                foreach (string name in new[] { "ChatGPTBridge.exe", "guard.json", "guard-status.json" }) { string path = Path.Combine(directory, name); if (File.Exists(path)) File.Delete(path); }
                if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory, false);
            }
        }
        public static void Uninstall() {
            try { dynamic folder = TaskFolder(); dynamic task = folder.GetTask(TaskName); task.Stop(0); folder.DeleteTask(TaskName, 0); } catch (System.Runtime.InteropServices.COMException ex) { if (ex.HResult != unchecked((int)0x80070002)) throw; }
            CleanupLegacy();
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
            var names = new List<string>(); foreach (dynamic rule in policy.Rules) if ((string)rule.Grouping == RuleGroup && ((string)rule.Name).StartsWith("ChatGPTBridge-")) names.Add((string)rule.Name);
            foreach (string name in names) policy.Rules.Remove(name);
            WriteStatus(false, "Защита удалена.", 0);
        }
    }
}
