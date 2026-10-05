using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ChatGptBridge {
    public static class JsonUtil {
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public static string Encode(object value) { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }.Serialize(value); }
        public static Dictionary<string, object> Decode(string value) { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }.Deserialize<Dictionary<string, object>>(value); }
        public static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static object Get(Dictionary<string, object> obj, string key, object fallback = null) { object v; return obj.TryGetValue(key, out v) ? v : fallback; }
        public static string Text(Dictionary<string, object> obj, string key, string fallback = "") { return Convert.ToString(Get(obj, key, fallback)); }
        public static bool Flag(Dictionary<string, object> obj, string key, bool fallback = false) { return Convert.ToBoolean(Get(obj, key, fallback)); }
        public static IEnumerable<object> Items(object value) { return value as IEnumerable<object> ?? (value is ArrayList ? ((ArrayList)value).Cast<object>() : Enumerable.Empty<object>()); }
        public static void Save(string path, object value) { WriteAtomic(path, Utf8.GetBytes(Encode(value))); }
        public static void WriteAtomic(string path, byte[] bytes) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) {
                    string previous = temp + ".previous";
                    File.Replace(temp, path, previous);
                    File.Delete(previous);
                } else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public sealed class AppSettings {
        public const int BridgePort = 18881;
        public const int ControllerPort = 9090;
        public string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"com.follow\clashx");
        public string ClientExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"FlClashX\FlClashX.exe");
        public string UsNode = "";
        public bool Installed;
        public bool AutoStart = true;
        public string BackupDirectory = "";
        static string activeRoot;
        static readonly object rootLock = new object();
        static string PreferredRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatGPTBridge"); } }
        static string ExecutableDirectory { get { return Path.GetDirectoryName(typeof(AppSettings).Assembly.Location); } }
        static string ExistingRoot() {
            // The installed copy lives alongside its own settings, including in
            // portable mode. Do not create a nested data directory on next boot.
            string exeDir = ExecutableDirectory;
            if (Path.GetFileName(exeDir).Equals("ChatGPTBridge-data", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(exeDir, "settings.json"))) return exeDir;
            if (File.Exists(Path.Combine(PreferredRoot, "settings.json"))) return PreferredRoot;
            string portable = Path.Combine(exeDir, "ChatGPTBridge-data");
            return File.Exists(Path.Combine(portable, "settings.json")) ? portable : null;
        }
        internal static bool CanWriteDirectory(string directory) {
            string probe = Path.Combine(directory, "write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                Directory.CreateDirectory(directory);
                using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.WriteByte(1);
                File.Delete(probe); return true;
            } catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
            finally { try { if (File.Exists(probe)) File.Delete(probe); } catch { } }
        }
        public static string Root {
            get {
                lock (rootLock) {
                    if (activeRoot != null) return activeRoot;
                    if (Path.GetFileName(ExecutableDirectory).Equals("ChatGPTBridge-data", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(ExecutableDirectory, "settings.json"))) return activeRoot = ExecutableDirectory;
                    string portable = Path.Combine(ExecutableDirectory, "ChatGPTBridge-data");
                    if (CanWriteDirectory(portable)) return activeRoot = portable;
                    throw new UnauthorizedAccessException("Не удалось записать настройки рядом с приложением. Переместите папку программы в доступное вам место, например C:\\Users\\ваше-имя\\ChatGPTBridge.");
                }
            }
        }
        public static string FilePath { get { return Path.Combine(Root, "settings.json"); } }
        public static AppSettings Load(bool probeStorage = true) {
            string path = probeStorage ? FilePath : Path.Combine(ExistingRoot() ?? PreferredRoot, "settings.json");
            if (!File.Exists(path)) return new AppSettings();
            var d = JsonUtil.Decode(File.ReadAllText(path, JsonUtil.Utf8));
            return new AppSettings { DataDir = JsonUtil.Text(d, "DataDir"), ClientExe = JsonUtil.Text(d, "ClientExe"), UsNode = JsonUtil.Text(d, "UsNode"),
                Installed = JsonUtil.Flag(d, "Installed"), AutoStart = JsonUtil.Flag(d, "AutoStart", true), BackupDirectory = JsonUtil.Text(d, "BackupDirectory") };
        }
        public void Save() { JsonUtil.Save(FilePath, this); }
    }
}
