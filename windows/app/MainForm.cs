// Ekolayzer ana penceresi: ön ayarlar, frekans yanıtı, 10 bant + ön kazanç, çıkış göstergesi,
// açılır limiter paneli (döner düğmeler + kazanç azaltma göstergesi), sol altta güncelleme düğmesi.
// Linux sürümündeki EqWindow ile aynı düzen ve davranış.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ekolayzer
{
    sealed class MainForm : Form
    {
        const int BaseW = 840, PanelW = 240, BaseH = 600;
        const int MeterFps = 30;

        readonly App app;
        public readonly Settings S;
        readonly Engine engine = new Engine();

        readonly ToggleSwitch power;
        readonly Label presetLbl, status;
        readonly DropDown presetDd;
        readonly IconButton saveBtn, delBtn, resetBtn, menuBtn, panelBtn;
        public readonly IconButton UpdateBtn;
        readonly ResponseView curve;
        readonly SliderColumn preamp;
        readonly SliderColumn[] bands;
        readonly Meter outMeter, grMeter;
        readonly Panel card;
        readonly Label cardTitle;
        readonly Knob[] knobs;
        readonly Toast toast = new Toast();
        readonly ToolTip tips = new ToolTip();
        readonly Timer meterTimer = new Timer(), saveTimer = new Timer { Interval = 400 }, statusTimer = new Timer { Interval = 3000 };
        bool updating;
        string[] presetNames = new string[0];

        readonly Stopwatch clock = Stopwatch.StartNew();
        double quietSince = -1, labelT, peakHoldT;
        double peakHold = -120;
        (string Name, bool Registered, bool Supported) device;
        bool engineOk = true;

        public MainForm(App app)
        {
            this.app = app;
            S = Settings.Load();
            Text = "Ekolayzer";
            Icon = TaskDlg.AppIcon;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.UiFont;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.S(S.LimiterPanel ? BaseW + PanelW : BaseW), Theme.S(BaseH));
            DoubleBuffered = true;
            KeyPreview = true;

            power = new ToggleSwitch(S.Enabled);
            power.Toggled += OnPower;
            tips.SetToolTip(power, "Ekolayzeri aç / kapat");

            presetLbl = new Label { Text = "Ön ayar:", AutoSize = true };
            presetDd = new DropDown();
            presetDd.SelectedChanged += OnPresetSelected;
            saveBtn = new IconButton("");
            saveBtn.Click += (s, e) => OnSavePreset();
            tips.SetToolTip(saveBtn, "Ön ayar olarak kaydet");
            delBtn = new IconButton("");
            delBtn.Click += (s, e) => OnDeletePreset();
            tips.SetToolTip(delBtn, "Özel ön ayarı sil");
            resetBtn = new IconButton("");
            resetBtn.Click += (s, e) => SelectPreset("Düz");
            tips.SetToolTip(resetBtn, "Düz ayara sıfırla");
            menuBtn = new IconButton("");
            menuBtn.Click += (s, e) => ShowMenu();
            tips.SetToolTip(menuBtn, "Menü");

            curve = new ResponseView(() => S);

            preamp = new SliderColumn("Ön kazanç", S.Preamp);
            preamp.ValueChanged += v => OnSlider(-1, v);
            bands = Core.BandFreqs.Select((f, i) =>
            {
                var c = new SliderColumn((f >= 1000 ? f / 1000 + "k" : f.ToString()) + " Hz", S.Gains[i]);
                c.ValueChanged += v => OnSlider(i, v);
                return c;
            }).ToArray();

            outMeter = new Meter(true) { Caption = "Çıkış", TopText = "−∞" };
            tips.SetToolTip(outMeter, "Çıkış seviyesi (dBFS)");
            panelBtn = new IconButton("");
            panelBtn.Click += (s, e) => TogglePanel();

            card = new Panel();
            cardTitle = new Label { Text = "Limiter", Font = Theme.Heading, AutoSize = true };
            knobs = Core.Limiter.Select(p =>
            {
                var k = new Knob(p, S.Limiter[p.Key]);
                k.ValueChanged += v => { S.Limiter[p.Key] = v; Changed(curveToo: false); };
                tips.SetToolTip(k, $"{p.Title} — sürükleyin veya tekerleği kullanın (Shift: ince), çift tıklama: varsayılan");
                return k;
            }).ToArray();
            grMeter = new Meter(false) { Caption = "Azaltma", TopText = "0.0" };
            tips.SetToolTip(grMeter, "Limiterin uyguladığı kazanç azaltma (dB)");
            card.Controls.Add(cardTitle);
            card.Controls.AddRange(knobs);
            card.Controls.Add(grMeter);
            card.Paint += PaintCard;
            card.Visible = S.LimiterPanel;

            UpdateBtn = new IconButton("") { Pill = true, Visible = false };
            UpdateBtn.Click += (s, e) => app.StartUpdate();
            status = new Label { AutoSize = false, AutoEllipsis = true, Font = Theme.Caption, TextAlign = ContentAlignment.MiddleLeft };

            Controls.Add(toast);
            Controls.AddRange(new Control[] { power, presetLbl, presetDd, saveBtn, delBtn, resetBtn, menuBtn, curve, preamp });
            Controls.AddRange(bands);
            Controls.AddRange(new Control[] { outMeter, panelBtn, card, UpdateBtn, status });

            ApplyTheme();
            Theme.Changed += OnThemeChanged;
            UpdatePanelBtn();
            RebuildPresets(S.Preset);
            RefreshLabels();

            // Ölçümler: yalnızca pencere görünürken, ~30 kare/sn; sessizlikte 4 Hz yoklamaya düşer
            meterTimer.Interval = 1000 / MeterFps;
            meterTimer.Tick += OnMeterTick;
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); DoSave(); };
            statusTimer.Tick += (s, e) => CheckEngine();
            MinimumSize = SizeFromClientSize(new Size(MinClientWidth, Theme.S(BaseH)));  // limiter kartı tam sığsın
            engine.Apply(S);  // bu kullanıcının ayarları motorda geçerli olsun
            CheckEngine();
        }

        // ------------------------------------------------------------ düzen

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.DarkTitleBar(Handle, Theme.Dark);
        }

        void OnThemeChanged()
        {
            if (IsDisposed) return;
            ApplyTheme();
            Native.DarkTitleBar(Handle, Theme.Dark);
            Invalidate(true);
        }

        void ApplyTheme()
        {
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            card.BackColor = Theme.Mix(Theme.Fg, Theme.Bg, 0.04);
            presetLbl.ForeColor = status.ForeColor = Theme.Mix(Theme.Fg, Theme.Bg, 0.6);
            cardTitle.ForeColor = Theme.Fg;
            cardTitle.BackColor = card.BackColor;
            presetLbl.BackColor = status.BackColor = Theme.Bg;
        }

        const int MinCol = 52;  // bant sütununun etiketleri iç içe girmeden sığdığı en küçük genişlik

        /// <summary>Limiter paneli kapalıyken sütunların sığdığı en küçük iç genişlik.</summary>
        int MinClientWidth =>
            2 * Theme.S(16) + 11 * Theme.S(MinCol) + 2 * Theme.S(13) + outMeter.PreferredWidth + Theme.S(8) + Theme.S(28) + Theme.S(4);

        /// <summary>Limiter paneliyle birlikte gereken en küçük iç genişlik.</summary>
        int MinClientWidthWithPanel => MinClientWidth + Theme.S(PanelW);

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (power == null) return;
            int W = ClientSize.Width, H = ClientSize.Height;
            if (S.LimiterPanel && W < MinClientWidthWithPanel && WindowState != FormWindowState.Minimized)
            {
                // pencere daraldı: limiter sığmıyor, gizle (ok düğmesi pencereyi büyütüp yeniden açar)
                S.LimiterPanel = false;
                card.Visible = false;
                UpdatePanelBtn();
                QueueSave();
            }
            int p = Theme.S(16), y = Theme.S(12), rowH = Theme.S(32);

            power.Location = new Point(p, y + (rowH - power.Height) / 2);
            menuBtn.Bounds = new Rectangle(W - p - Theme.S(32), y, Theme.S(32), rowH);
            int x = power.Right + Theme.S(16);
            presetLbl.Location = new Point(x, y + (rowH - presetLbl.Height) / 2);
            x = presetLbl.Right + Theme.S(8);
            int right = menuBtn.Left - Theme.S(12);
            resetBtn.Bounds = new Rectangle(right - Theme.S(32), y, Theme.S(32), rowH);
            delBtn.Bounds = new Rectangle(resetBtn.Left - Theme.S(36), y, Theme.S(32), rowH);
            saveBtn.Bounds = new Rectangle(delBtn.Left - Theme.S(36), y, Theme.S(32), rowH);
            presetDd.Bounds = new Rectangle(x, y, saveBtn.Left - Theme.S(8) - x, rowH);

            y += rowH + Theme.S(12);
            curve.Bounds = new Rectangle(p, y, W - 2 * p, Theme.S(150));
            y = curve.Bottom + Theme.S(12);

            int bottomH = Theme.S(32);
            int by = H - p - bottomH;
            int sh = by - Theme.S(12) - y;

            int cardW = S.LimiterPanel ? Theme.S(PanelW - 8) : 0;
            int cardX = W - p - cardW;
            int btnW = Theme.S(28);
            int meterW = outMeter.PreferredWidth + Theme.S(8);
            int sep = Theme.S(13);
            int bandsRight = (S.LimiterPanel ? cardX - Theme.S(8) : W - p) - btnW - Theme.S(4) - meterW - sep;
            int colW = Math.Max(Theme.S(30), (bandsRight - p - sep) / 11);
            preamp.Bounds = new Rectangle(p, y, colW, sh);
            sepX1 = preamp.Right + sep / 2;
            int bx = preamp.Right + sep;
            for (int i = 0; i < bands.Length; i++) bands[i].Bounds = new Rectangle(bx + i * colW, y, colW, sh);
            sepX2 = bx + bands.Length * colW + sep / 2;
            sepY = (y + Theme.S(18), y + sh - Theme.S(18));
            outMeter.Bounds = new Rectangle(sepX2 + sep / 2, y, meterW, sh);
            panelBtn.Bounds = new Rectangle(outMeter.Right + Theme.S(4), y + (sh - Theme.S(40)) / 2, btnW, Theme.S(40));
            card.Bounds = new Rectangle(cardX, y, cardW, sh);
            LayoutCard();

            UpdateBtn.Width = UpdateBtn.PreferredWidth;
            UpdateBtn.Bounds = new Rectangle(p, by, UpdateBtn.PreferredWidth, bottomH);
            int sx = UpdateBtn.Visible ? UpdateBtn.Right + Theme.S(10) : p;
            status.Bounds = new Rectangle(sx, by, W - p - sx, bottomH);
            Invalidate();
        }

        int sepX1, sepX2;
        (int, int) sepY;

        void LayoutCard()
        {
            int pad = Theme.S(10);
            cardTitle.Location = new Point(pad, Theme.S(8));
            var ks = Knob.Preferred;
            int gw = grMeter.PreferredWidth + Theme.S(6);
            int gridW = 2 * ks.Width + Theme.S(4);
            int gridH = 3 * ks.Height + 2 * Theme.S(6);
            int gy = cardTitle.Bottom + Math.Max(Theme.S(6), (card.Height - cardTitle.Bottom - gridH) / 2);
            for (int i = 0; i < knobs.Length; i++)
                knobs[i].Bounds = new Rectangle(pad + (i % 2) * (ks.Width + Theme.S(4)), gy + (i / 2) * (ks.Height + Theme.S(6)), ks.Width, ks.Height);
            grMeter.Bounds = new Rectangle(pad + gridW + Theme.S(8), Theme.S(8), gw, card.Height - Theme.S(16));
        }

        void PaintCard(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, card.Width - 1, card.Height - 1), Theme.S(10)))
            using (var b = new SolidBrush(card.BackColor))
            using (var pen = new Pen(Theme.A(Theme.Fg, 0.12)))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var pen = new Pen(Theme.A(Theme.Fg, 0.15)))
            {
                e.Graphics.DrawLine(pen, sepX1, sepY.Item1, sepX1, sepY.Item2);
                e.Graphics.DrawLine(pen, sepX2, sepY.Item1, sepX2, sepY.Item2);
            }
        }

        // ------------------------------------------------------------ limiter paneli

        void UpdatePanelBtn()
        {
            panelBtn.Glyph = S.LimiterPanel ? "" : "";
            tips.SetToolTip(panelBtn, S.LimiterPanel ? "Limiter ayarlarını gizle" : "Limiter ayarlarını göster");
            panelBtn.Invalidate();
        }

        void TogglePanel()
        {
            bool open = !S.LimiterPanel;
            var wa = Screen.FromControl(this).WorkingArea;
            if (open)
            {
                // panel açılırken pencere genişler; sığmıyorsa en az gerektiği kadar büyür
                int needW = SizeFromClientSize(new Size(MinClientWidthWithPanel, ClientSize.Height)).Width;
                if (WindowState == FormWindowState.Maximized)
                {
                    if (ClientSize.Width < MinClientWidthWithPanel) { Notify("Limiter paneli bu ekrana sığmıyor."); return; }
                }
                else
                {
                    int nw = Math.Min(Math.Max(Width + Theme.S(PanelW), needW), wa.Width);
                    if (nw < needW) { Notify("Limiter paneli bu ekrana sığmıyor."); return; }
                    Bounds = new Rectangle(Math.Max(wa.Left, Math.Min(Left, wa.Right - nw)), Top, nw, Height);
                }
            }
            else if (WindowState == FormWindowState.Normal)
            {
                // kapanınca eski genişliğine döner
                int nw = Math.Max(MinimumSize.Width, Width - Theme.S(PanelW));
                Bounds = new Rectangle(Left, Top, nw, Height);
            }
            S.LimiterPanel = open;
            card.Visible = open;
            UpdatePanelBtn();
            PerformLayout();
            QueueSave();
        }

        // ------------------------------------------------------------ kısa bildirim

        public void Notify(string text) => toast.Show(text, this);

        // ------------------------------------------------------------ ön ayarlar

        void RebuildPresets(string select)
        {
            updating = true;
            presetNames = new[] { Core.Custom }.Concat(Core.Presets.Select(p => p.Name)).Concat(S.CustomPresets.Keys).ToArray();
            presetDd.Items = presetNames.ToList();
            presetDd.Separators = new System.Collections.Generic.List<int> { 1 };
            if (S.CustomPresets.Count > 0) presetDd.Separators.Add(1 + Core.Presets.Length);
            int idx = Array.IndexOf(presetNames, select);
            presetDd.SelectedIndex = idx >= 0 ? idx : 0;
            if (idx < 0) S.Preset = Core.Custom;
            updating = false;
            UpdateDelBtn();
        }

        void UpdateDelBtn() => delBtn.Enabled = S.CustomPresets.ContainsKey(S.Preset ?? "");

        void SelectPreset(string name)
        {
            int i = Array.IndexOf(presetNames, name);
            if (i >= 0) { presetDd.SelectedIndex = i; OnPresetSelected(i); }
        }

        void OnPresetSelected(int idx)
        {
            if (updating || idx < 0) return;
            string name = presetNames[idx];
            S.Preset = name;
            UpdateDelBtn();
            if (name == Core.Custom) { QueueSave(); return; }
            var gains = S.FindPreset(name);
            updating = true;
            for (int i = 0; i < bands.Length; i++) bands[i].Value = gains[i];
            updating = false;
            S.Gains = gains.ToArray();
            Changed();
        }

        void OnSavePreset()
        {
            using (var dlg = new InputDialog("Ön ayarı kaydet", "Geçerli ayar için bir ad girin.",
                                             S.CustomPresets.ContainsKey(S.Preset ?? "") ? S.Preset : ""))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Value.Length == 0) return;
                string name = dlg.Value;
                if (Core.Presets.Any(p => p.Name == name) || name == Core.Custom)
                {
                    Notify("Bu ad hazır bir ön ayara ait, başka bir ad seçin.");
                    return;
                }
                S.CustomPresets[name] = S.Gains.ToArray();
                S.Preset = name;
                RebuildPresets(name);
                QueueSave();
                Notify($"“{name}” kaydedildi");
            }
        }

        void OnDeletePreset()
        {
            string name = S.Preset;
            if (name == null || !S.CustomPresets.Remove(name)) return;
            S.Preset = Core.Custom;
            RebuildPresets(Core.Custom);
            QueueSave();
            Notify($"“{name}” silindi");
        }

        // ------------------------------------------------------------ değişiklikler

        void OnSlider(int idx, double v)
        {
            if (idx < 0)
            {
                if (v == S.Preamp) return;
                S.Preamp = v;
            }
            else
            {
                if (v == S.Gains[idx]) return;
                S.Gains[idx] = v;
                if (!updating && S.Preset != Core.Custom)
                {
                    var preset = S.FindPreset(S.Preset);
                    if (preset == null || !preset.SequenceEqual(S.Gains))
                    {
                        S.Preset = Core.Custom;
                        presetDd.SelectedIndex = 0;
                        UpdateDelBtn();
                    }
                }
            }
            if (!updating) Changed();
            else RefreshLabels();
        }

        void Changed(bool curveToo = true)
        {
            RefreshLabels();
            if (curveToo) curve.Invalidate();
            // ortak bellek üzerinden anında: ses kesilmez, ek süreç yok
            if (S.Enabled && !engine.Apply(S)) CheckEngine();
            QueueSave();
        }

        void RefreshLabels() => UpdateStatus();

        void UpdateStatus()
        {
            string txt;
            if (!S.Enabled) txt = "Ekolayzer kapalı – ses değiştirilmeden çalınıyor.";
            else if (!engineOk) txt = "Ses motoruna ulaşılamıyor; menüden “Ses motorunu onar”ı seçin.";
            else if (device.Name != null && device.Supported && !device.Registered)
                txt = $"Ses motoru “{device.Name}” aygıtında henüz etkin değil; menüden “Ses motorunu onar”ı seçin.";
            else
            {
                txt = device.Name != null && !device.Supported
                    ? $"Etkin – “{device.Name}” bir sanal aygıt; ekolayzer sesin çıktığı donanım aygıtında uygulanıyor."
                    : "Etkin – tüm uygulamaların ses çıkışına uygulanıyor.";
                txt += "  Limiter tavanı " + Core.FmtDb(S.Limiter["ceiling"]) + ".";
            }
            if (status.Text != txt) status.Text = txt;
            tips.SetToolTip(status, txt);
        }

        bool NeedsRepair => !engineOk || (device.Name != null && device.Supported && !device.Registered);

        void CheckEngine()
        {
            engineOk = engine.Open;
            device = Engine.DefaultDevice();
            UpdateStatus();
        }

        // ------------------------------------------------------------ ölçüm

        double Now => clock.Elapsed.TotalSeconds;

        void StartMeters(bool fast)
        {
            meterTimer.Interval = fast ? 1000 / MeterFps : 250;
            meterTimer.Start();
        }

        void OnMeterTick(object sender, EventArgs e)
        {
            double now = Now;
            var data = S.Enabled ? engine.Poll() : null;
            bool fast = meterTimer.Interval < 100;
            bool silent = data == null || Math.Max(data.Value.L, data.Value.R) < 1e-5f && data.Value.Gr < 1e-5f;
            if (silent)
            {
                if (quietSince < 0) quietSince = now;
                else if (fast && now - quietSince > 2 && MetersEmpty())
                {
                    StartMeters(false);  // sessizlikte kare döngüsü durur; ses gelince geri hızlanır
                    return;
                }
                if (!fast) return;
            }
            else
            {
                quietSince = -1;
                if (!fast) StartMeters(true);
            }
            outMeter.Push(data == null ? null : new double[] { data.Value.L, data.Value.R });
            if (S.LimiterPanel) grMeter.Push(data == null ? null : new double[] { data.Value.Gr });
            if (data != null)
            {
                double db = Meter.ToDb(Math.Max(data.Value.L, data.Value.R));
                if (db >= peakHold || now - peakHoldT > 1) { peakHold = db; peakHoldT = now; }
            }
            if (now - labelT > 0.1)  // sayısal etiketler 10 Hz
            {
                labelT = now;
                double h = now - peakHoldT > 1.5 ? -120 : peakHold;
                outMeter.SetTopText(h > -60 ? h.ToString("+0.0;-0.0", Core.Inv) : "−∞");
                if (S.LimiterPanel) grMeter.SetTopText(grMeter.Holds[0].ToString("0.0", Core.Inv));
            }
        }

        bool MetersEmpty() =>
            outMeter.Vals.Concat(outMeter.Holds).Max() < 0.05 &&
            (!S.LimiterPanel || grMeter.Vals.Concat(grMeter.Holds).Max() < 0.05);

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateTimers();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateTimers();
        }

        void UpdateTimers()
        {
            bool on = Visible && WindowState != FormWindowState.Minimized;
            if (on) { StartMeters(true); statusTimer.Start(); }
            else { meterTimer.Stop(); statusTimer.Stop(); }
        }

        // ------------------------------------------------------------ kaydetme

        void QueueSave()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        void DoSave()
        {
            try { S.Save(); }
            catch (Exception ex) { Notify("Ayarlar kaydedilemedi: " + ex.Message); }
        }

        public void Flush()
        {
            if (saveTimer.Enabled) { saveTimer.Stop(); DoSave(); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Flush();
            meterTimer.Stop();
            statusTimer.Stop();
            Theme.Changed -= OnThemeChanged;
            engine.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Q) { app.Quit(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        // ------------------------------------------------------------ açma / kapama

        public void SetPower(bool on)
        {
            power.On = on;
            OnPower(on);
        }

        void OnPower(bool on)
        {
            S.Enabled = on;
            Flush();
            DoSave();
            engine.Apply(S);
            curve.Invalidate();
            foreach (var b in bands) { b.Dimmed = !on; b.Invalidate(); }
            preamp.Dimmed = !on; preamp.Invalidate();
            UpdateStatus();
            app.OnEnabledChanged(on);
        }

        // ------------------------------------------------------------ menü

        void ShowMenu()
        {
            var m = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = false, Font = Theme.UiFont };
            m.Items.Add("Güncellemeleri denetle", null, (s, e) => app.CheckUpdates(manual: true));
            if (NeedsRepair) m.Items.Add("Ses motorunu onar", null, (s, e) => Repair());
            m.Items.Add("Hakkında", null, (s, e) => app.About());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Çıkış", null, (s, e) => app.Quit());
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            m.Show(menuBtn, new Point(menuBtn.Width - m.PreferredSize.Width, menuBtn.Height + Theme.S(2)));
        }

        void Repair()
        {
            Notify("Ses motoru onarılıyor…");
            Task.Run(() => Elevation.RunElevated(Core.InstalledExe, "--repair", out _)).ContinueWith(t =>
            {
                CheckEngine();
                engine.Apply(S);
                Notify(t.Result == Elevation.Cancelled ? "Yönetici onayı verilmedi." :
                       NeedsRepair ? "Ses motoru onarılamadı." : "Ses motoru etkin.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void SetUpdateInfo(UpdateInfo info)
        {
            UpdateBtn.Visible = info != null;
            if (info != null)
            {
                UpdateBtn.Label = $"Güncelle ({info.Version})";
                tips.SetToolTip(UpdateBtn, $"Ekolayzer {info.Version} mevcut (yüklü: {info.Current}). Tıklayınca indirilip kurulur.");
            }
            PerformLayout();
        }
    }
}
