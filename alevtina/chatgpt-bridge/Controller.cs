using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChatGptBridge {
    public sealed class Controller : IDisposable {
        public readonly AppSettings Settings;
        public readonly Integration Integration;
        public Routing Routing;
        public FlProfile Profile;
        public CoreApi Api;
        public ProxyGate Gate;
        public bool FullVpn;
        public string GeneralNode = "DIRECT";
        public bool Connected;
        public int UsDelay;
        public event Action<string, bool> Status;
        public event Action<List<DelayResult>> Delays;
        readonly object sync = new object();
        List<string> expected = new List<string>();
        volatile bool ready;
        int checking;
        DateTime lastRepair = DateTime.MinValue;
        public Controller(AppSettings settings) { Settings = settings; Integration = new Integration(settings); }
        void Report(string text, bool good) { var e = Status; if (e != null) e(text, good); }
        void PublishGuardRoute(bool confirmed) {
            JsonUtil.Save(System.IO.Path.Combine(AppSettings.Root, "guard-route.json"), new { Ready = confirmed, PID = Process.GetCurrentProcess().Id, DataDir = Settings.DataDir, UsNode = Settings.UsNode, GeneralNode = GeneralNode, Secret = Profile == null ? "" : Profile.Secret, UpdatedUtc = DateTime.UtcNow.ToString("o") });
        }
        public void ReadProfile() {
            Profile = FlProfile.Read(Settings.DataDir); Settings.UsNode = Profile.FindUs(Settings.UsNode);
            Routing = new Routing(Settings.DataDir); Api = new CoreApi(AppSettings.ControllerPort, Profile.Secret);
        }
        public async Task ConnectAsync() {
            ready = false; PublishGuardRoute(false); Report("Настраиваю защищённый маршрут…", false);
            Integration.Preflight(); ReadProfile();
            bool guardAlreadyPresent = GuardService.Exists();
            if (!await Integration.InstallGuardAsync()) throw new InvalidOperationException("Защита Windows не установлена. Подтвердите запрос администратора и убедитесь, что брандмауэр включён.");
            if (Gate == null) { Gate = new ProxyGate(AppSettings.BridgePort, IsSafe, () => Profile.MixedPort, host => Routing.IsProtected(host)); Gate.Start(); }
            bool first = !Settings.Installed;
            if (first) Integration.CreateBackup(Profile);
            try {
                if (first) {
                    Integration.CopyApp();
                    Integration.InstallChrome(Routing);
                    // Native apps are already guarded; Chrome now has a mandatory proxy.
                    Integration.StopClient();
                    Profile = FlProfile.Read(Settings.DataDir);
                    Profile.PrepareSettings(Routing, Settings.UsNode); Profile.Save();
                    Integration.RemoveClientStartup(); Integration.SetStartup(Settings.AutoStart);
                }
                Integration.StartClient();
                for (int i = 0; i < 40; ++i) {
                    try { await Task.Run(() => Api.Request("GET", "/version")); break; } catch { }
                    await Task.Delay(500);
                }
                // ApplyAsync can also repair a disabled controller using the client's
                // own persistent overrides and a controlled restart.
                await ApplyAsync("DIRECT");
                Settings.Installed = true; Settings.Save(); Connected = true;
                await CheckAsync();
            } catch (Exception original) {
                ready = false;
                if (first) {
                    try {
                        Integration.StopClient(); Integration.RestoreBackup(); Integration.StartClient();
                        if (!guardAlreadyPresent) Task.Run(() => Integration.RemoveGuardAsync()).GetAwaiter().GetResult();
                    } catch {
                        throw new InvalidOperationException(original.Message + "\n\nНе удалось полностью отменить настройку. Нажмите «Удалить настройку», чтобы убрать защиту и восстановить доступ.", original);
                    }
                }
                throw;
            }
        }
        bool IsSafe() {
            if (!ready || Routing == null || Api == null || !GuardService.Healthy() || !Integration.ChromePolicyActive()) return false;
            lock (sync) return Api.Safe(expected, Settings.UsNode);
        }
        public async Task ApplyAsync(string generalNode) {
            ready = false; PublishGuardRoute(false);
            await Task.Run(() => {
                lock (sync) {
                    // Refresh the subscription contents on each switch, without editing it.
                    var refreshed = FlProfile.Read(Settings.DataDir);
                    if (!refreshed.Nodes.Contains(Settings.UsNode) || (generalNode != "DIRECT" && !refreshed.Nodes.Contains(generalNode))) throw new InvalidOperationException("Выбранный сервер исчез из подписки. Обновите выбор сервера.");
                    Profile = refreshed;
                    string yaml = Routing.RuntimeYaml(Profile.OriginalYaml, Settings.UsNode, generalNode, Profile.MixedPort, Profile.Secret);
                    expected = Routing.Rules(Settings.UsNode, generalNode);
                    bool loaded = false;
                    try {
                        Api.Request("PUT", "/configs?force=true", new Dictionary<string, object> { { "payload", yaml } }, 15000);
                        loaded = WaitSafe(8);
                    } catch (WebException ex) {
                        var response = ex.Response as HttpWebResponse;
                        if (response != null && (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)) throw;
                    }
                    if (!loaded) {
                        // Some embedded core builds disable PUT /configs. Let FlClashX
                        // assemble its own configuration instead; never change its subscription.
                        if (Gate != null) Gate.BlockExisting();
                        Integration.StopClient();
                        Profile = FlProfile.Read(Settings.DataDir);
                        Profile.PrepareSettings(Routing, Settings.UsNode, generalNode); Profile.Save();
                        Integration.StartClient();
                        loaded = WaitSafe(40);
                    }
                    if (!loaded) throw new InvalidOperationException("Не удалось подтвердить маршрут США. Соединения Chrome остаются заблокированы. Откройте FlClashX и проверьте запуск TUN.");
                    GeneralNode = generalNode; FullVpn = generalNode != "DIRECT";
                    try { Api.CloseUnprotected(Routing); } catch { }
                }
            });
            ready = true; PublishGuardRoute(true);
        }
        bool WaitSafe(int attempts) {
            for (int i = 0; i < attempts; ++i) { if (Api.Safe(expected, Settings.UsNode)) return true; Thread.Sleep(500); }
            return false;
        }
        public async Task CheckAsync() {
            if (Interlocked.Exchange(ref checking, 1) == 1) return;
            try {
                if (!Connected) return;
                bool guarded = GuardService.Healthy() && Integration.ChromePolicyActive();
                bool rulesSafe = guarded && await Task.Run(() => { lock (sync) return Api.Safe(expected, Settings.UsNode); });
                if (!rulesSafe) {
                    ready = false; PublishGuardRoute(false); if (Gate != null) Gate.BlockExisting();
                    Report("Маршрут недоступен — восстанавливаю защиту…", false);
                    if (guarded && (DateTime.UtcNow - lastRepair).TotalSeconds > 20) {
                        lastRepair = DateTime.UtcNow;
                        try { Integration.StartClient(); await ApplyAsync(GeneralNode); } catch { }
                    }
                    return;
                }
                int delay = await Task.Run(() => Api.Delay(Settings.UsNode)); UsDelay = delay;
                ready = delay > 0;
                PublishGuardRoute(ready);
                if (ready) Report("ChatGPT подключён через США", true);
                else { if (Gate != null) Gate.BlockExisting(); Report("Сервер США недоступен — жду восстановления", false); }
            } catch { ready = false; try { PublishGuardRoute(false); } catch { } if (Gate != null) Gate.BlockExisting(); Report("Соединение недоступно — прямой маршрут запрещён", false); }
            finally { Interlocked.Exchange(ref checking, 0); }
        }
        public async Task<List<DelayResult>> TestDelaysAsync() {
            if (!Connected) throw new InvalidOperationException("Сначала подключите защищённый маршрут ChatGPT.");
            var results = await Api.MeasureAsync(Profile.Nodes, (done, total) => Report("Проверяю серверы: " + done + " из " + total, ready));
            var e = Delays; if (e != null) e(results);
            return results;
        }
        public async Task ToggleFullAsync() {
            if (!Connected) await ConnectAsync();
            if (FullVpn) { await ApplyAsync("DIRECT"); Report("ChatGPT через США · остальной интернет напрямую", ready); }
            else {
                var best = DelayResult.Fastest(await TestDelaysAsync());
                if (best == null) throw new InvalidOperationException("Нет доступных серверов для общего VPN. Маршрут ChatGPT остаётся прежним.");
                await ApplyAsync(best.Node); Report("Общий VPN: " + best.Node + " · " + best.Delay + " мс", ready);
            }
            await CheckAsync();
        }
        public async Task UninstallAsync() {
            ready = false; if (Gate != null) Gate.BlockExisting();
            await Integration.RemoveGuardAsync();
            if (!string.IsNullOrEmpty(Settings.BackupDirectory)) { Integration.StopClient(); Integration.RestoreBackup(); Integration.StartClient(); }
            else { Settings.Installed = false; Settings.Save(); }
            Connected = false;
        }
        public void Dispose() { ready = false; try { PublishGuardRoute(false); } catch { } if (Gate != null) Gate.Dispose(); }
    }
}
