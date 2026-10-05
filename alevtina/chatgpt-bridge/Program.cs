using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ChatGptBridge {
    static class Program {
        [STAThread]
        public static int Main(string[] args) {
            try {
                if (args.Contains("--guard-loop")) { GuardService.RunLoop(); return 0; }
                if ((args.Length == 2 || args.Length == 3) && args[0] == "--install-guard") {
                    try { GuardService.Install(Process.GetCurrentProcess().MainModule.FileName, args[1]); return 0; }
                    catch (Exception ex) {
                        if (args.Length == 3) { try { File.WriteAllText(args[2], ex.GetType().Name + ": " + ex.Message, JsonUtil.Utf8); } catch { } }
                        else MessageBox.Show(ex.Message, "Ошибка установки защиты", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    }
                }
                if (args.Contains("--remove-guard")) { GuardService.Uninstall(); return 0; }
                if (args.Length == 2 && args[0] == "--self-test") return AppTests.Run(args[1]);
                if (args.Length == 2 && args[0] == "--integration-test") return LiveTests.Run(args[1]);
                if (args.Length == 2 && (args[0] == "--native-probe" || args[0] == "--native-probe-wait")) return LiveTests.NativeProbe(args[1], args[0] == "--native-probe-wait");
                if (args.Length == 2 && (args[0] == "--native-ip-probe" || args[0] == "--native-ip-probe-wait")) return LiveTests.NativeProbe(args[1], args[0] == "--native-ip-probe-wait", true);
                if (args.Length == 2 && args[0] == "--test-storage") {
                    string probe = Path.Combine(AppSettings.Root, "write-check-" + Guid.NewGuid().ToString("N") + ".json");
                    try {
                        JsonUtil.Save(probe, new { Value = "first" });
                        JsonUtil.Save(probe, new { Value = "replacement" });
                        if (JsonUtil.Text(JsonUtil.Decode(File.ReadAllText(probe, JsonUtil.Utf8)), "Value") != "replacement") throw new IOException("Запись настроек не прошла проверку.");
                        File.WriteAllText(args[1], "PASS application can create and atomically replace a file in its selected settings directory; existing settings unchanged.\nDirectory: " + AppSettings.Root, JsonUtil.Utf8);
                        return 0;
                    } catch (Exception ex) {
                        File.WriteAllText(args[1], "FAIL " + ex.Message + "\nIdentity: " + System.Security.Principal.WindowsIdentity.GetCurrent().Name + "\nAdmin: " + new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator), JsonUtil.Utf8);
                        return 1;
                    } finally { if (File.Exists(probe)) File.Delete(probe); }
                }
                if (args.Length == 2 && args[0] == "--inspect") {
                    var settings = AppSettings.Load(false); var p = FlProfile.Read(settings.DataDir); var routing = new Routing(settings.DataDir);
                    File.WriteAllText(args[1], JsonUtil.Encode(new { ClientVersion = FileVersionInfo.GetVersionInfo(settings.ClientExe).ProductVersion, UsNode = p.FindUs(settings.UsNode), DomainCount = routing.Domains.Count, MixedPort = p.MixedPort, Rules = routing.Rules(p.FindUs(settings.UsNode), "DIRECT") }), JsonUtil.Utf8); return 0;
                }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length == 2 && args[0] == "--preview") { using (var f = new MainForm(true, false)) f.RenderPreview(args[1]); return 0; }
                bool created;
                using (var mutex = new Mutex(true, @"Local\ChatGPTBridge-" + Environment.UserName, out created)) {
                    if (!created) { MessageBox.Show("Мост уже работает. Откройте его через значок US в трее.", "ChatGPT через США"); return 0; }
                    Application.Run(new MainForm(args.Contains("--demo"), args.Contains("--tray")));
                }
                return 0;
            } catch (Exception ex) {
                if (args.Any(a => a.StartsWith("--")) && !args.Contains("--demo") && !args.Contains("--tray")) { try { Console.Error.WriteLine(ex.Message); } catch { } return 1; }
                MessageBox.Show(ex.Message, "ChatGPT через США", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1;
            }
        }
        public static string Quote(string argument) {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in argument) {
                if (c == '\\') { ++slashes; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                result.Append('\\', slashes).Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
