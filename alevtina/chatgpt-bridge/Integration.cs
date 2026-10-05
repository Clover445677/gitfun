using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ChatGptBridge {
    public sealed class Integration {
        public const string ChromeKey = @"Software\Policies\Google\Chrome";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public readonly AppSettings Settings;
        string expectedChromePolicy;
        public Integration(AppSettings settings) { Settings = settings; }
        static Dictionary<string, object> RegistryRecord(string path, string name) {
            using (var k = Registry.CurrentUser.OpenSubKey(path)) {
                bool exists = k != null && k.GetValueNames().Contains(name);
                return new Dictionary<string, object> { { "Path", path }, { "Name", name }, { "Exists", exists }, { "Kind", exists ? k.GetValueKind(name).ToString() : "String" }, { "Value", exists ? k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null } };
            }
        }
        public void Preflight() {
            if (!File.Exists(Settings.ClientExe)) {
                var proc = Process.GetProcessesByName("FlClashX").FirstOrDefault();
                if (proc != null) { try { Settings.ClientExe = proc.MainModule.FileName; } catch { } finally { proc.Dispose(); } }
                if (!File.Exists(Settings.ClientExe)) throw new InvalidOperationException("Не найден FlClashX. Выберите его файл в настройках.");
            }
            using (var k = Registry.LocalMachine.OpenSubKey(ChromeKey)) {
                if (k != null && new[] { "ProxySettings", "ProxyMode", "ProxyPacUrl", "ProxyServer" }.Any(n => k.GetValue(n) != null)) throw new InvalidOperationException("В Chrome уже задан прокси администратором компьютера. Надстройка не будет его заменять.");
            }
            if (!Settings.Installed) using (var k = Registry.CurrentUser.OpenSubKey(ChromeKey)) {
                if (k != null && new[] { "ProxySettings", "ProxyMode", "ProxyPacUrl", "ProxyServer" }.Any(n => k.GetValue(n) != null)) throw new InvalidOperationException("В Chrome уже настроено отдельное правило прокси. Сначала проверьте совместимость с ним.");
            }
            string vscode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Code\User\settings.json");
            if (File.Exists(vscode) && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(vscode), "\"chatgpt.runCodexInWindowsSubsystemForLinux\"\\s*:\\s*true")) throw new InvalidOperationException("Codex запущен внутри WSL. Эта версия надстройки защищает Codex для Windows; переключите его на Windows перед настройкой.");
        }
        public void CreateBackup(FlProfile profile) {
            if (Settings.BackupDirectory.Length > 0 && Directory.Exists(Settings.BackupDirectory)) return;
            string backup = Path.Combine(AppSettings.Root, "backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(backup);
            File.Copy(profile.PreferencesPath, Path.Combine(backup, "flclash-settings.json"));
            var records = new List<Dictionary<string, object>>();
            foreach (string name in new[] { "ProxySettings", "QuicAllowed", "WebRtcIPHandling" }) records.Add(RegistryRecord(ChromeKey, name));
            records.Add(RegistryRecord(RunKey, "ChatGPTBridge"));
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey)) if (run != null) foreach (string name in run.GetValueNames()) {
                string value = Convert.ToString(run.GetValue(name));
                if (name != "ChatGPTBridge" && value.IndexOf("FlClashX", StringComparison.OrdinalIgnoreCase) >= 0) records.Add(RegistryRecord(RunKey, name));
            }
            JsonUtil.Save(Path.Combine(backup, "registry.json"), new Dictionary<string, object> { { "Records", records }, { "PreferencesPath", profile.PreferencesPath } });
            Settings.BackupDirectory = backup; Settings.Save();
        }
        public void InstallChrome(Routing routing) {
            string pac = routing.PacScript();
            string encoded = "data:application/x-ns-proxy-autoconfig;base64," + Convert.ToBase64String(JsonUtil.Utf8.GetBytes(pac));
            string policy = JsonUtil.Encode(new Dictionary<string, object> { { "ProxyMode", "pac_script" }, { "ProxyPacUrl", encoded }, { "ProxyPacMandatory", true } });
            expectedChromePolicy = encoded;
            using (var key = Registry.CurrentUser.CreateSubKey(ChromeKey)) {
                key.SetValue("ProxySettings", policy, RegistryValueKind.String);
                key.SetValue("QuicAllowed", 0, RegistryValueKind.DWord);
                key.SetValue("WebRtcIPHandling", "disable_non_proxied_udp", RegistryValueKind.String);
            }
            File.WriteAllText(Path.Combine(AppSettings.Root, "openai-route.pac"), pac, JsonUtil.Utf8);
        }
        public bool ChromePolicyActive() {
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(ChromeKey)) {
                    if (key == null) return false;
                    var d = JsonUtil.Decode(Convert.ToString(key.GetValue("ProxySettings", "")));
                    if (expectedChromePolicy == null) expectedChromePolicy = "data:application/x-ns-proxy-autoconfig;base64," + Convert.ToBase64String(JsonUtil.Utf8.GetBytes(new Routing(Settings.DataDir).PacScript()));
                    return JsonUtil.Text(d, "ProxyMode") == "pac_script" && JsonUtil.Flag(d, "ProxyPacMandatory") && JsonUtil.Text(d, "ProxyPacUrl") == expectedChromePolicy && Convert.ToInt32(key.GetValue("QuicAllowed", 1)) == 0 && Convert.ToString(key.GetValue("WebRtcIPHandling", "")) == "disable_non_proxied_udp";
                }
            } catch { return false; }
        }
        public void SetStartup(bool enabled) {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) {
                if (enabled) key.SetValue("ChatGPTBridge", Program.Quote(Process.GetCurrentProcess().MainModule.FileName) + " --tray"); else key.DeleteValue("ChatGPTBridge", false);
            }
            Settings.AutoStart = enabled; Settings.Save();
        }
        public void RemoveClientStartup() {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) foreach (string name in key.GetValueNames()) {
                if (name != "ChatGPTBridge" && Convert.ToString(key.GetValue(name)).IndexOf("FlClashX", StringComparison.OrdinalIgnoreCase) >= 0) key.DeleteValue(name, false);
            }
        }
        public void CopyApp() {
            Directory.CreateDirectory(AppSettings.Root);
            string target = Process.GetCurrentProcess().MainModule.FileName;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType); dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, "ChatGPT через США.lnk"));
            shortcut.TargetPath = target; shortcut.WorkingDirectory = Path.GetDirectoryName(target); shortcut.Description = "ChatGPT через США; VPN для всего по кнопке"; shortcut.Save();
        }
        public async Task<bool> InstallGuardAsync() {
            if (await Task.Run(() => GuardService.Healthy())) return true;
            // On logon the task and GUI start together. Let the existing worker
            // finish its first check rather than stopping it during initialization.
            if (await Task.Run(() => GuardService.TaskRunning())) {
                for (int i = 0; i < 30; ++i) {
                    await Task.Delay(500);
                    if (await Task.Run(() => GuardService.Healthy())) return true;
                }
            }
            Directory.CreateDirectory(AppSettings.Root);
            string resultFile = Path.Combine(AppSettings.Root, "guard-install-" + Guid.NewGuid().ToString("N") + ".txt");
            try {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                // With UAC disabled an administrator already has a full token. Do
                // not ask the shell to elevate that token again.
                bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                var info = new ProcessStartInfo(exe, "--install-guard " + Program.Quote(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) + " " + Program.Quote(resultFile)) { UseShellExecute = !admin, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = admin };
                if (!admin) info.Verb = "runas";
                using (var proc = Process.Start(info)) {
                    await Task.Run(() => proc.WaitForExit());
                    if (proc.ExitCode != 0) {
                        string reason = File.Exists(resultFile) ? File.ReadAllText(resultFile, JsonUtil.Utf8) : "Установщик завершился с кодом " + proc.ExitCode + ".";
                        throw new InvalidOperationException("Не удалось настроить фоновую защиту.\n\n" + reason);
                    }
                }
                for (int i = 0; i < 30; ++i) { if (await Task.Run(() => GuardService.Healthy())) return true; await Task.Delay(500); }
                string detail = "Фоновая защита не подтвердила готовность за 15 секунд.";
                if (File.Exists(GuardService.StatusPath)) detail = JsonUtil.Text(JsonUtil.Decode(File.ReadAllText(GuardService.StatusPath, JsonUtil.Utf8)), "Message", detail);
                throw new InvalidOperationException("Фоновая задача создана, но защита не готова.\n\n" + detail);
            } catch (System.ComponentModel.Win32Exception ex) {
                throw new InvalidOperationException(GuardInstallLaunchError(ex.NativeErrorCode), ex);
            }
        }
        internal static string GuardInstallLaunchError(int code) {
            if (code == 1223) return "Windows не разрешила запуск установщика с правами администратора (код 1223). Если запроса не было, запустите приложение через правую кнопку мыши → «Запуск от имени администратора» под учётной записью администратора.";
            if (code == 5 || code == 740) return "Установщику не предоставлены права администратора (код " + code + "). Запустите приложение через правую кнопку мыши → «Запуск от имени администратора».";
            return "Windows не смогла запустить установщик защиты. Код ошибки: " + code + ".";
        }
        public async Task RemoveGuardAsync() {
            if (!GuardService.Exists()) return;
            using (var proc = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--remove-guard") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })) {
                await Task.Run(() => proc.WaitForExit()); if (proc.ExitCode != 0) throw new InvalidOperationException("Не удалось удалить защиту приложений.");
            }
        }
        public void RestoreBackup() {
            string dir = Settings.BackupDirectory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new InvalidOperationException("Не найдена резервная копия.");
            var snapshot = JsonUtil.Decode(File.ReadAllText(Path.Combine(dir, "registry.json"), JsonUtil.Utf8));
            foreach (var record in JsonUtil.Items(JsonUtil.Get(snapshot, "Records")).Select(JsonUtil.Obj)) {
                string path = JsonUtil.Text(record, "Path"), name = JsonUtil.Text(record, "Name");
                if (path != ChromeKey && path != RunKey) throw new InvalidOperationException("Некорректная резервная копия реестра.");
                using (var key = Registry.CurrentUser.CreateSubKey(path)) {
                    if (!JsonUtil.Flag(record, "Exists")) key.DeleteValue(name, false);
                    else { RegistryValueKind kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), JsonUtil.Text(record, "Kind")); object value = JsonUtil.Get(record, "Value"); if (kind == RegistryValueKind.DWord) value = Convert.ToInt32(value); key.SetValue(name, value, kind); }
                }
            }
            string prefs = JsonUtil.Text(snapshot, "PreferencesPath");
            if (!Path.GetFullPath(prefs).Equals(Path.GetFullPath(Path.Combine(Settings.DataDir, "shared_preferences.json")), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Папка настроек не совпадает с резервной копией.");
            JsonUtil.WriteAtomic(prefs, File.ReadAllBytes(Path.Combine(dir, "flclash-settings.json")));
            Settings.Installed = false; Settings.BackupDirectory = ""; Settings.Save();
        }
        public static void StopClient() {
            foreach (var p in Process.GetProcessesByName("FlClashX")) using (p) { p.Kill(); if (!p.WaitForExit(6000)) throw new InvalidOperationException("Не удалось перезапустить FlClashX."); }
        }
        public void StartClient() {
            if (Process.GetProcessesByName("FlClashX").Length > 0) return;
            Process.Start(new ProcessStartInfo(Settings.ClientExe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }
}
