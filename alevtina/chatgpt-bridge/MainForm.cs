using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChatGptBridge {
    public sealed class MainForm : Form {
        readonly bool demo;
        readonly Controller controller;
        readonly AppSettings settings;
        readonly Label status, detail, usNode, general;
        readonly Button connect, full, test;
        readonly CheckBox startup;
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer timer;
        bool busy, quitting, changingCheckbox;
        readonly Color bg = Color.FromArgb(17, 23, 35), card = Color.FromArgb(26, 35, 49), blue = Color.FromArgb(71, 139, 255), text = Color.FromArgb(236, 241, 249), muted = Color.FromArgb(150, 168, 191);
        public MainForm(bool demonstration, bool startHidden) {
            demo = demonstration; settings = demo ? new AppSettings { UsNode = "🇺🇸 США", Installed = true } : AppSettings.Load();
            if (!demo) controller = new Controller(settings);
            Text = "ChatGPT через США · v" + typeof(MainForm).Assembly.GetName().Version.ToString(3); ClientSize = new Size(650, 650); MinimumSize = MaximumSize = Size; FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; BackColor = bg; ForeColor = text; Font = new Font("Segoe UI", 10.5f); AutoScaleMode = AutoScaleMode.None;
            Icon = MakeIcon();
            AddLabel(this, "ChatGPT через США", 28, 23, 580, 38, 22, text, true);
            AddLabel(this, "Обычный Chrome · приложение ChatGPT · Codex", 29, 69, 580, 27, 10.5f, muted);
            var protectedCard = AddCard(27, 116, 596, 196);
            AddLabel(protectedCard, "ПОСТОЯННЫЙ МАРШРУТ", 20, 15, 540, 22, 9, muted, true);
            status = AddLabel(protectedCard, demo ? "ChatGPT подключён через США" : "Маршрут ещё не настроен", 20, 45, 548, 34, 17, demo ? Color.FromArgb(97, 216, 157) : text, true);
            usNode = AddLabel(protectedCard, "Сервер: " + (settings.UsNode.Length == 0 ? "будет найден автоматически" : settings.UsNode), 20, 85, 548, 26, 10.5f, muted);
            detail = AddLabel(protectedCard, demo ? "США: 148 мс · прямой возврат запрещён" : "США не заменяется другим сервером при сбое.", 20, 114, 548, 24, 10, muted);
            connect = AddButton(protectedCard, demo || settings.Installed ? "Проверить подключение" : "Настроить и подключить", 20, 149, 255, 33, blue);
            connect.Click += async (s, e) => await Run(async () => { if (!controller.Connected) await controller.ConnectAsync(); else await controller.CheckAsync(); });
            var vpnCard = AddCard(27, 329, 596, 146);
            AddLabel(vpnCard, "ОСТАЛЬНОЙ ИНТЕРНЕТ", 20, 14, 540, 22, 9, muted, true);
            general = AddLabel(vpnCard, "Работает напрямую", 20, 43, 545, 30, 16, text, true);
            full = AddButton(vpnCard, "Включить VPN для всего", 20, 89, 276, 36, Color.FromArgb(50, 65, 86));
            full.Click += async (s, e) => await Run(() => controller.ToggleFullAsync());
            test = AddButton(vpnCard, "Проверить задержку", 312, 89, 264, 36, Color.FromArgb(50, 65, 86));
            test.Click += async (s, e) => await Run(async () => { var results = await controller.TestDelaysAsync(); ShowDelays(results); await controller.CheckAsync(); });
            startup = new CheckBox { Text = "Запускать вместе с Windows", Checked = settings.AutoStart, Location = new Point(31, 494), Size = new Size(565, 30), ForeColor = text, BackColor = bg, FlatStyle = FlatStyle.Flat };
            Controls.Add(startup);
            startup.CheckedChanged += (s, e) => {
                if (demo || changingCheckbox) return;
                bool prior = settings.AutoStart;
                try { if (settings.Installed) controller.Integration.SetStartup(startup.Checked); else settings.AutoStart = startup.Checked; }
                catch (Exception ex) { changingCheckbox = true; startup.Checked = prior; changingCheckbox = false; ShowError(ex); }
            };
            var info = AddLabel(this, "Закрытие окна оставляет мост в трее. При сбое США\nChatGPT ждёт восстановления; остальные сайты работают.", 30, 539, 585, 47, 10, muted);
            AddLink("Открыть FlClashX", 30, 600, async () => { if (!demo) { controller.Integration.StartClient(); await Task.Delay(100); } });
            AddLink("Настройки", 230, 600, async () => { if (!demo) ShowSettings(); await Task.FromResult(0); });
            AddLink("Удалить настройку", 413, 600, async () => {
                if (demo || (!settings.Installed && !GuardService.Exists() && string.IsNullOrEmpty(settings.BackupDirectory))) return;
                if (MessageBox.Show(this, "Настройки Chrome и FlClashX вернутся к сохранённому состоянию. Постоянная защита будет удалена. Продолжить?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) await Run(async () => { await controller.UninstallAsync(); quitting = true; Close(); });
            });
            tray = new NotifyIcon { Icon = Icon, Text = "ChatGPT через США", Visible = !demo };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, (s, e) => RestoreWindow());
            menu.Items.Add("VPN для всего: переключить", null, async (s, e) => await Run(() => controller.ToggleFullAsync()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выйти (ChatGPT останется заблокирован)", null, (s, e) => { quitting = true; Close(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += (s, e) => RestoreWindow();
            timer = new System.Windows.Forms.Timer { Interval = 12000 };
            timer.Tick += async (s, e) => { if (!busy && controller != null && controller.Connected) await controller.CheckAsync(); };
            if (!demo) {
                controller.Status += (value, good) => OnUi(() => {
                    bool wasGood = status.ForeColor == Color.FromArgb(97, 216, 157);
                    status.Text = value; status.ForeColor = good ? Color.FromArgb(97, 216, 157) : Color.FromArgb(255, 184, 113);
                    if (controller.UsDelay > 0) detail.Text = "США: " + controller.UsDelay + " мс · прямой возврат запрещён";
                    if (wasGood && !good && value.Contains("недоступ")) { tray.BalloonTipTitle = "ChatGPT через США"; tray.BalloonTipText = "Сервер США недоступен. Ожидаю восстановления."; tray.ShowBalloonTip(5000); }
                    UpdateControls();
                    try { JsonUtil.Save(Path.Combine(AppSettings.Root, "runtime-status.json"), new { PID = System.Diagnostics.Process.GetCurrentProcess().Id, WindowTitle = Text, Connected = controller.Connected, ProtectedRouteReady = good, FullVpn = controller.FullVpn, UsNode = settings.UsNode, Status = value, UpdatedUtc = DateTime.UtcNow.ToString("o") }); } catch { }
                });
                Shown += async (s, e) => {
                    timer.Start();
                    if (startHidden && settings.Installed) Hide();
                    if (settings.Installed) await Run(() => controller.ConnectAsync());
                };
            }
            FormClosing += (s, e) => { if (!quitting && !demo) { e.Cancel = true; Hide(); } };
            FormClosed += (s, e) => { timer.Stop(); tray.Visible = false; tray.Dispose(); if (controller != null) controller.Dispose(); };
        }
        void OnUi(Action action) { if (IsDisposed) return; if (InvokeRequired) BeginInvoke(action); else action(); }
        async Task Run(Func<Task> operation) {
            if (demo || busy) return; busy = true; UpdateControls();
            try { await operation(); usNode.Text = "Сервер: " + settings.UsNode; }
            catch (Exception ex) {
                status.Text = "Подключение требует внимания"; status.ForeColor = Color.FromArgb(255, 184, 113);
                try { JsonUtil.Save(Path.Combine(AppSettings.Root, "runtime-status.json"), new { PID = System.Diagnostics.Process.GetCurrentProcess().Id, Connected = controller.Connected, ProtectedRouteReady = false, Status = status.Text, Error = ex.Message, UpdatedUtc = DateTime.UtcNow.ToString("o") }); } catch { }
                ShowError(ex);
            }
            finally { busy = false; UpdateControls(); }
        }
        void UpdateControls() {
            connect.Enabled = full.Enabled = test.Enabled = !busy;
            if (controller != null) {
                connect.Text = settings.Installed ? "Проверить подключение" : "Настроить и подключить";
                full.Text = controller.FullVpn ? "Выключить общий VPN" : "Включить VPN для всего";
                general.Text = controller.FullVpn ? "VPN: " + controller.GeneralNode : "Работает напрямую";
            }
        }
        void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        void ShowError(Exception ex) {
            string message = ex is System.Net.WebException ? "Не удалось связаться с локальным VPN-клиентом. Проверьте, что FlClashX запущен." : ex.Message;
            if (ex is UnauthorizedAccessException) message = "Windows запретила запись. Настройки не сохранены; окно остаётся открытым.\n\n" + ex.Message;
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        Panel AddCard(int x, int y, int w, int h) { var p = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = card }; Controls.Add(p); return p; }
        static Label AddLabel(Control parent, string value, int x, int y, int w, int h, float size, Color color, bool bold = false) { var l = new Label { Text = value, Location = new Point(x, y), Size = new Size(w, h), Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent }; parent.Controls.Add(l); return l; }
        Button AddButton(Control parent, string value, int x, int y, int w, int h, Color color) { var b = new Button { Text = value, Location = new Point(x, y), Size = new Size(w, h), BackColor = color, ForeColor = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 10, FontStyle.Bold) }; b.FlatAppearance.BorderSize = 0; parent.Controls.Add(b); return b; }
        void AddLink(string value, int x, int y, Func<Task> action) { var l = new LinkLabel { Text = value, Location = new Point(x, y), Size = new Size(190, 27), LinkColor = muted, ActiveLinkColor = blue, VisitedLinkColor = muted }; Controls.Add(l); l.LinkClicked += async (s, e) => await action(); }
        static Icon MakeIcon() {
            using (var b = new Bitmap(64, 64)) using (var g = Graphics.FromImage(b)) {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.FromArgb(28, 58, 105));
                using (var font = new Font("Segoe UI", 22, FontStyle.Bold)) using (var brush = new SolidBrush(Color.White)) g.DrawString("US", font, brush, 5, 10);
                IntPtr h = b.GetHicon(); try { return (Icon)Icon.FromHandle(h).Clone(); } finally { DestroyIcon(h); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        void ShowDelays(List<DelayResult> results) {
            using (var f = new Form { Text = "Задержка серверов", Size = new Size(470, 540), StartPosition = FormStartPosition.CenterParent, BackColor = bg, ForeColor = text, Font = Font }) {
                var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = card, ForeColor = text, BorderStyle = BorderStyle.None };
                list.Columns.Add("Сервер", 320); list.Columns.Add("Задержка", 115);
                foreach (var result in results) list.Items.Add(new ListViewItem(new[] { result.Node, result.Delay > 0 ? result.Delay + " мс" : "Недоступен" }));
                f.Controls.Add(list); f.ShowDialog(this);
            }
        }
        void ShowSettings() {
            if (controller.Connected) { MessageBox.Show(this, "Настройки маршрута уже применены. Чтобы изменить исходный профиль, сначала удалите настройку моста и добавьте нужную подписку в FlClashX.", Text); return; }
            using (var f = new Form { Text = "Настройки FlClashX", Size = new Size(590, 385), StartPosition = FormStartPosition.CenterParent, BackColor = bg, ForeColor = text, Font = Font }) {
                AddLabel(f, "Приложение FlClashX", 18, 20, 540, 25, 10, muted);
                var exe = new TextBox { Text = settings.ClientExe, Bounds = new Rectangle(18, 51, 445, 30) }; f.Controls.Add(exe);
                var browse = AddButton(f, "Выбрать", 470, 50, 83, 30, card); browse.Click += (s, e) => { using (var d = new OpenFileDialog { Filter = "FlClashX|FlClashX.exe" }) if (d.ShowDialog(f) == DialogResult.OK) exe.Text = d.FileName; };
                AddLabel(f, "Папка настроек (определяется отдельно на каждом ПК)", 18, 95, 540, 25, 10, muted);
                var root = new TextBox { Text = settings.DataDir, Bounds = new Rectangle(18, 126, 535, 30) }; f.Controls.Add(root);
                AddLabel(f, "Сервер США", 18, 175, 535, 24, 10, muted);
                var server = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(18, 204, 535, 30) }; f.Controls.Add(server);
                try {
                    var nodes = FlProfile.Read(settings.DataDir).Nodes.Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"(^|\s)(США|USA|United States)(\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
                    foreach (string n in nodes) server.Items.Add(n);
                    if (server.Items.Contains(settings.UsNode)) server.SelectedItem = settings.UsNode; else if (server.Items.Count > 0) server.SelectedIndex = 0;
                } catch { }
                var save = AddButton(f, "Сохранить", 358, 278, 195, 34, blue);
                save.Click += (s, e) => {
                    string previousExe = settings.ClientExe, previousDir = settings.DataDir, previousNode = settings.UsNode;
                    try {
                        settings.ClientExe = exe.Text.Trim(); settings.DataDir = root.Text.Trim();
                        if (server.SelectedItem != null) settings.UsNode = Convert.ToString(server.SelectedItem);
                        settings.Save(); f.Close();
                    } catch (Exception ex) {
                        settings.ClientExe = previousExe; settings.DataDir = previousDir; settings.UsNode = previousNode;
                        ShowError(ex);
                    }
                };
                f.ShowDialog(this);
            }
        }
        public void RenderPreview(string path) {
            ShowInTaskbar = false; Opacity = 0; StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000);
            Show(); Application.DoEvents(); PerformLayout();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
            Hide();
        }
    }
}
