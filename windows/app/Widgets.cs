// Ekolayzer arayüz bileşenleri: tema, dikey kaydırıcı, seviye göstergesi, döner düğme, yanıt eğrisi,
// anahtar, simge düğmesi, açılır liste ve kısa bildirim. Linux (GTK3) sürümündeki görünümün karşılığı;
// hepsi kendi çizilir, ekran ölçeğine (DPI) ve açık/koyu temaya uyar.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Ekolayzer
{
    static class Theme
    {
        public static bool Dark;
        public static Color Bg, Fg, Card, Accent, AccentFg, Border;
        public static float Scale = 1f;
        public static Font UiFont, Caption, CaptionBold, Heading, Title;
        public static FontFamily IconFamily;
        public static event Action Changed;

        public static int S(float v) => (int)Math.Round(v * Scale);

        public static void Init(float dpi)
        {
            Scale = dpi / 96f;
            UiFont = new Font("Segoe UI", 9f);
            Caption = new Font("Segoe UI", 7.6f);
            CaptionBold = new Font("Segoe UI Semibold", 7.6f);
            Heading = new Font("Segoe UI Semibold", 9.5f);
            Title = new Font("Segoe UI Semibold", 12f);
            using (var fonts = new InstalledFontCollection())
                IconFamily = fonts.Families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons")
                             ?? fonts.Families.FirstOrDefault(f => f.Name == "Segoe MDL2 Assets")
                             ?? FontFamily.GenericSansSerif;
            Load();
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                {
                    bool d = Dark; var a = Accent;
                    Load();
                    if (d != Dark || a != Accent) Changed?.Invoke();
                }
            };
        }

        static void Load()
        {
            int light = 1;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = k?.GetValue("AppsUseLightTheme") is int v ? v : 1;
            }
            catch { }
            Dark = light == 0;
            Color acc = Color.FromArgb(0x35, 0x84, 0xE4);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                    if (k?.GetValue("AccentColor") is int abgr)
                        acc = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            }
            catch { }
            if (Dark)
            {
                Bg = Color.FromArgb(0x24, 0x24, 0x24);
                Fg = Color.FromArgb(0xF2, 0xF2, 0xF2);
                Card = Color.FromArgb(0x2E, 0x2E, 0x2E);
                Border = Color.FromArgb(0x3C, 0x3C, 0x3C);
                acc = Lighten(acc, 0.25f);
            }
            else
            {
                Bg = Color.FromArgb(0xFA, 0xFA, 0xFA);
                Fg = Color.FromArgb(0x1F, 0x1F, 0x1F);
                Card = Color.White;
                Border = Color.FromArgb(0xDE, 0xDE, 0xDE);
            }
            Accent = acc;
            AccentFg = (acc.R * 0.299 + acc.G * 0.587 + acc.B * 0.114) > 160 ? Color.Black : Color.White;
        }

        static Color Lighten(Color c, float t) =>
            Color.FromArgb((int)(c.R + (255 - c.R) * t), (int)(c.G + (255 - c.G) * t), (int)(c.B + (255 - c.B) * t));

        public static Color A(Color c, double alpha) => Color.FromArgb((int)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))), c);

        /// <summary>Saydam katmanın arka plan üzerindeki opak karşılığı (metin çizimi için).</summary>
        public static Color Mix(Color fg, Color bg, double a) =>
            Color.FromArgb((int)(bg.R + (fg.R - bg.R) * a), (int)(bg.G + (fg.G - bg.G) * a), (int)(bg.B + (fg.B - bg.B) * a));

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
            if (rad <= 0.5f) { p.AddRectangle(r); return p; }
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r,
                                TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter)
            => TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        public static void Glyph(Graphics g, string glyph, float size, Color c, RectangleF r)
        {
            using (var f = new Font(IconFamily, size, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(glyph, f, b, r, sf);
            }
        }
    }

    /// <summary>Çift arabellekli, kendi çizen temel denetim.</summary>
    class Painted : Control
    {
        public Painted()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected Color Surface
        {
            get
            {
                for (Control c = Parent; c != null; c = c.Parent)
                    if (c.BackColor.A == 255) return c.BackColor;
                return Theme.Bg;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Surface);
        }
    }

    /// <summary>Başlık, değer etiketi ve dikey kaydırıcıdan oluşan bant sütunu (-12..+12 dB).</summary>
    sealed class SliderColumn : Painted
    {
        public string Title;
        public double Min = Core.GainMin, Max = Core.GainMax;
        double value;
        public event Action<double> ValueChanged;
        bool dragging, hover;
        public bool Dimmed;

        public SliderColumn(string title, double v)
        {
            Title = title; value = v;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public double Value
        {
            get => value;
            set => SetValue(value, false);
        }

        void SetValue(double v, bool notify)
        {
            v = Math.Round(Core.Clamp(v, Min, Max) * 2) / 2;  // 0,5 dB adım
            if (Math.Abs(v - value) < 1e-9) return;
            value = v;
            Invalidate();
            if (notify) ValueChanged?.Invoke(v);
        }

        int TopH => Theme.S(20);
        int BottomH => Theme.S(20);
        Rectangle Track
        {
            get
            {
                int pad = Theme.S(11);
                return new Rectangle(Width / 2 - Theme.S(2), TopH + pad, Theme.S(4), Height - TopH - BottomH - 2 * pad);
            }
        }

        float YOf(double v) { var t = Track; return (float)(t.Bottom - (v - Min) / (Max - Min) * t.Height); }
        double VOf(int y) { var t = Track; return Min + (t.Bottom - y) * (Max - Min) / Math.Max(1, t.Height); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var surf = Surface;
            Theme.Text(g, value.ToString("+0.0;-0.0;+0.0", Core.Inv), Theme.Caption, Theme.Mix(Theme.Fg, surf, 0.75),
                       new Rectangle(0, 0, Width, TopH));
            Theme.Text(g, Title, Theme.CaptionBold, Theme.Fg, new Rectangle(0, Height - BottomH, Width, BottomH));

            var t = Track;
            using (var p = Theme.Round(t, t.Width / 2f))
            using (var b = new SolidBrush(Theme.A(Theme.Fg, 0.15)))
                g.FillPath(b, p);
            float y0 = YOf(0), yv = YOf(value);
            var acc = Dimmed ? Theme.A(Theme.Fg, 0.35) : Theme.Accent;
            using (var b = new SolidBrush(acc))
            {
                var fill = RectangleF.FromLTRB(t.X, Math.Min(y0, yv), t.Right, Math.Max(y0, yv));
                if (fill.Height > 0.5f) using (var p = Theme.Round(fill, t.Width / 2f)) g.FillPath(b, p);
            }
            using (var pen = new Pen(Theme.A(Theme.Fg, 0.35), 1))
                g.DrawLine(pen, t.X - Theme.S(5), (int)y0 + 0.5f, t.Right + Theme.S(5), (int)y0 + 0.5f);

            float r = Theme.S(hover || dragging || Focused ? 9 : 8);
            var knob = new RectangleF(Width / 2f - r, yv - r, 2 * r, 2 * r);
            using (var sh = new SolidBrush(Theme.A(Color.Black, Theme.Dark ? 0.5 : 0.18)))
                g.FillEllipse(sh, RectangleF.Inflate(new RectangleF(knob.X, knob.Y + 1, knob.Width, knob.Height), 0.5f, 0.5f));
            using (var b = new SolidBrush(Theme.Dark ? Color.FromArgb(0xE8, 0xE8, 0xE8) : Color.White))
                g.FillEllipse(b, knob);
            using (var pen = new Pen(Focused ? acc : Theme.A(Color.Black, 0.15), Focused ? 2 : 1))
                g.DrawEllipse(pen, knob);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks == 2) { SetValue(0, true); return; }  // çift tıklama: 0 dB
            dragging = true;
            Capture = true;
            SetValue(VOf(e.Y), true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) SetValue(VOf(e.Y), true);
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            SetValue(value + Math.Sign(e.Delta) * 0.5, true);
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }

        protected override bool IsInputKey(Keys k) => k == Keys.Up || k == Keys.Down || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up: SetValue(value + 0.5, true); break;
                case Keys.Down: SetValue(value - 0.5, true); break;
                case Keys.PageUp: SetValue(value + 3, true); break;
                case Keys.PageDown: SetValue(value - 3, true); break;
                case Keys.Home: case Keys.Back: case Keys.Delete: SetValue(0, true); break;
                default: return;
            }
            e.Handled = true;
        }
    }

    /// <summary>Dikey tepe göstergesi. level: stereo çıkış (0..-60 dBFS) yeşil→sarı→kırmızı;
    /// gr: limiterin kazanç azaltması (0..24 dB), yukarıdan aşağı dolar.</summary>
    sealed class Meter : Painted
    {
        readonly bool level;
        readonly int bars;
        readonly double span;
        readonly int[] marks;
        public readonly double[] Vals, Holds;
        readonly double[] holdT;
        double lastT;
        const double FallDbS = 48, HoldS = 1.0;
        public string TopText = "", Caption = "";

        public Meter(bool levelMode)
        {
            level = levelMode;
            bars = level ? 2 : 1;
            span = level ? 60 : 24;
            marks = level ? new[] { 0, 6, 12, 24, 36, 48, 60 } : new[] { 0, 3, 6, 9, 12, 18, 24 };
            Vals = new double[bars]; Holds = new double[bars]; holdT = new double[bars];
            lastT = Now();
        }

        static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        public int PreferredWidth => Theme.S(bars * 9 + (bars - 1) * 3 + 26);

        public static double ToDb(double v) => v > 1e-6 ? 20 * Math.Log10(v) : -120;

        /// <summary>level: doğrusal tepe (sol, sağ); gr: (dB). null = veri yok.</summary>
        public void Push(double[] values)
        {
            double now = Now(), dt = Math.Min(now - lastT, 0.1);
            lastT = now;
            bool dirty = false;
            for (int i = 0; i < bars; i++)
            {
                double v = values == null ? 0 : level ? Math.Max(0, span + Math.Max(ToDb(values[i]), -span)) : Math.Min(Math.Max(values[0], 0), span);
                double cur = Vals[i];
                double nv = v >= cur ? v : Math.Max(v, cur - FallDbS * dt);
                double hold = Holds[i];
                if (v >= hold || now - holdT[i] > HoldS) { Holds[i] = v; holdT[i] = now; }
                // yalnızca görünür bir değişiklik varsa yeniden çiz (sessizlikte hiç çizim yok)
                if (Math.Abs(nv - cur) > 0.05 || Math.Abs(Holds[i] - hold) > 0.05) dirty = true;
                Vals[i] = nv;
            }
            if (dirty) Invalidate(MeterRect());
        }

        int TopH => Theme.S(20);
        Rectangle MeterRect() => new Rectangle(0, TopH, Width, Height - TopH - Theme.S(20));

        public void SetTopText(string s)
        {
            if (s == TopText) return;
            TopText = s;
            Invalidate(new Rectangle(0, 0, Width, TopH));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var surf = Surface;
            int barW = Theme.S(9), gap = Theme.S(3);
            int barsW = bars * barW + (bars - 1) * gap;
            int x0 = (Width - (barsW + Theme.S(26))) / 2;
            Theme.Text(g, TopText, Theme.Caption, Theme.Mix(Theme.Fg, surf, 0.75), new Rectangle(0, 0, Width, TopH));
            Theme.Text(g, Caption, Theme.CaptionBold, Theme.Fg, new Rectangle(0, Height - Theme.S(20), Width, Theme.S(20)));

            var mr = MeterRect();
            int top = mr.Y + Theme.S(7), bh = mr.Height - Theme.S(13);
            float Y(double depth) => top + (float)(Math.Min(Math.Max(depth, 0), span) / span * bh);

            using (var f = new Font("Segoe UI", 6.5f))
                foreach (var mk in marks)
                {
                    float y = Y(mk);
                    Theme.Text(g, mk.ToString(), f, Theme.Mix(Theme.Fg, surf, 0.6),
                               new Rectangle(x0 + barsW + Theme.S(6), (int)y - Theme.S(7), Theme.S(22), Theme.S(14)),
                               TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    using (var b = new SolidBrush(Theme.A(Theme.Fg, 0.25)))
                        g.FillRectangle(b, x0 + barsW + 1, (int)Math.Round(y), Theme.S(4), 1);
                }

            for (int i = 0; i < bars; i++)
            {
                int x = x0 + i * (barW + gap);
                using (var b = new SolidBrush(Theme.A(Theme.Fg, 0.10)))
                    g.FillRectangle(b, x, top, barW, bh);
                double v = Vals[i], holdDepth;
                if (level)
                {
                    foreach (var (lo, hi, c) in new[] { (12.0, 60.0, Color.FromArgb(51, 209, 82)),
                                                       (3.0, 12.0, Color.FromArgb(245, 212, 46)),
                                                       (0.0, 3.0, Color.FromArgb(224, 28, 36)) })
                    {
                        double depth = span - v;
                        if (depth >= hi) continue;
                        float y1 = Y(Math.Max(depth, lo)), y2 = Y(hi);
                        using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y1, barW, y2 - y1);
                    }
                    holdDepth = span - Holds[i];
                }
                else
                {
                    foreach (var (lo, hi, c) in new[] { (0.0, 6.0, Color.FromArgb(247, 153, 38)),
                                                       (6.0, 24.0, Color.FromArgb(224, 64, 36)) })
                    {
                        if (v <= lo) continue;
                        float y1 = Y(lo), y2 = Y(Math.Min(v, hi));
                        using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y1, barW, y2 - y1);
                    }
                    holdDepth = Holds[i];
                }
                if ((level && Holds[i] > 0.5) || (!level && Holds[i] > 0.1))
                    using (var b = new SolidBrush(Theme.A(Theme.Fg, 0.85)))
                        g.FillRectangle(b, x, Y(holdDepth) - 1, barW, 2);
            }
        }
    }

    /// <summary>Döner düğme: dikey sürükleme (Shift ile ince ayar), tekerlek, ok tuşları, çift tıkla varsayılan.</summary>
    sealed class Knob : Painted
    {
        readonly LimiterParam prm;
        double value, dragStart;
        int dragY;
        bool dragging;
        public event Action<double> ValueChanged;

        public Knob(LimiterParam p, double v)
        {
            prm = p; value = v;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.SizeNS;
        }

        public double Value => value;
        public static Size Preferred => new Size(Theme.S(80), Theme.S(46 + 36));

        double ToNorm(double v) => prm.Log ? Math.Log(v / prm.Min) / Math.Log(prm.Max / prm.Min) : (v - prm.Min) / (prm.Max - prm.Min);

        double FromNorm(double n)
        {
            n = Math.Min(Math.Max(n, 0), 1);
            double v = prm.Log ? prm.Min * Math.Pow(prm.Max / prm.Min, n) : prm.Min + n * (prm.Max - prm.Min);
            v = Math.Round(v / prm.Step) * prm.Step;
            return Math.Min(Math.Max(v, prm.Min), prm.Max);
        }

        public void SetValue(double v, bool notify = true)
        {
            v = Math.Min(Math.Max(v, prm.Min), prm.Max);
            if (Math.Abs(v - value) < 1e-9) return;
            value = v;
            Invalidate();
            if (notify) ValueChanged?.Invoke(v);
        }

        void Nudge(double steps, bool fine)
        {
            double v = FromNorm(ToNorm(value) + steps * (fine ? 0.005 : 0.025));
            if (Math.Abs(v - value) < 1e-9 && steps != 0) v = value + (steps > 0 ? prm.Step : -prm.Step);  // yuvarlamada takılma
            SetValue(Math.Round(v / prm.Step) * prm.Step);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var surf = Surface;
            int th = Theme.S(18), size = Theme.S(46);
            Theme.Text(g, prm.Title, Theme.CaptionBold, Theme.Fg, new Rectangle(0, 0, Width, th));
            Theme.Text(g, prm.Format(value), Theme.Caption, Theme.Mix(Theme.Fg, surf, 0.75),
                       new Rectangle(0, th + size, Width, Height - th - size));

            float cx = Width / 2f, cy = th + size / 2f, rad = size / 2f - Theme.S(4);
            float a0 = 135, sweep = 270, a = (float)(sweep * ToNorm(value));
            var arc = new RectangleF(cx - rad, cy - rad, 2 * rad, 2 * rad);
            using (var pen = new Pen(Theme.A(Theme.Fg, 0.15), Theme.S(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, arc, a0, sweep);
            if (a > 0.5f)
                using (var pen = new Pen(Theme.Accent, Theme.S(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, arc, a0, a);
            float ir = rad - Theme.S(6);
            using (var b = new SolidBrush(Theme.A(Theme.Fg, 0.08)))
                g.FillEllipse(b, cx - ir, cy - ir, 2 * ir, 2 * ir);
            double ang = (a0 + a) * Math.PI / 180;
            using (var pen = new Pen(Theme.A(Theme.Fg, 0.85), Theme.S(2.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, cx + (float)Math.Cos(ang) * (rad - Theme.S(14)), cy + (float)Math.Sin(ang) * (rad - Theme.S(14)),
                           cx + (float)Math.Cos(ang) * (rad - Theme.S(6)), cy + (float)Math.Sin(ang) * (rad - Theme.S(6)));
            if (Focused)
                using (var pen = new Pen(Theme.A(Theme.Accent, 0.6), 1))
                    g.DrawEllipse(pen, cx - rad - 2.5f, cy - rad - 2.5f, 2 * rad + 5, 2 * rad + 5);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks == 2) { SetValue(prm.Default); return; }
            dragging = true; Capture = true;
            dragY = e.Y; dragStart = ToNorm(value);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            bool fine = (ModifierKeys & Keys.Shift) != 0;
            SetValue(FromNorm(dragStart - (e.Y - dragY) / (fine ? 800.0 : 160.0) / Theme.Scale));
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Nudge(Math.Sign(e.Delta), (ModifierKeys & Keys.Shift) != 0);
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) => Invalidate();
        protected override void OnLostFocus(EventArgs e) => Invalidate();
        protected override bool IsInputKey(Keys k) => k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool fine = e.Shift;
            switch (e.KeyCode)
            {
                case Keys.Up: case Keys.Right: Nudge(1, fine); break;
                case Keys.Down: case Keys.Left: Nudge(-1, fine); break;
                case Keys.Home: case Keys.Back: case Keys.Delete: SetValue(prm.Default); break;
                default: return;
            }
            e.Handled = true;
        }
    }

    /// <summary>Toplam frekans yanıtı eğrisi (bant yanıtları önbelleğe alınır).</summary>
    sealed class ResponseView : Painted
    {
        const double FMin = 20, FMax = 20000, DbRange = 15;
        readonly Func<Settings> get;
        double[] freqs = new double[0];
        readonly Dictionary<(int, double), double[]> cache = new Dictionary<(int, double), double[]>();

        public ResponseView(Func<Settings> get) { this.get = get; }

        float X(double f, int w) => (float)(w * Math.Log10(f / FMin) / Math.Log10(FMax / FMin));
        float Y(double db, int h) => (float)(h / 2.0 - db / DbRange * (h / 2.0 - Theme.S(6)));

        double[] Band(int i, double gain)
        {
            if (!cache.TryGetValue((i, gain), out var c))
            {
                c = freqs.Select(f => Dsp.BandDb(i, gain, f)).ToArray();
                if (cache.Count > 400) cache.Clear();
                cache[(i, gain)] = c;
            }
            return c;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Surface);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var p = Theme.Round(r, Theme.S(8)))
            using (var b = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.Border))
            {
                e.Graphics.FillPath(b, p);
                e.Graphics.DrawPath(pen, p);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int w = Width, h = Height;
            var s = get();
            using (var p = Theme.Round(new RectangleF(1, 1, w - 2, h - 2), Theme.S(8))) g.SetClip(p);
            using (var pen = new Pen(Theme.A(Theme.Fg, 0.12)))
            {
                foreach (var f in Core.BandFreqs) { float x = (int)X(f, w) + 0.5f; g.DrawLine(pen, x, 0, x, h); }
                foreach (var db in new[] { -12, -6, 6, 12 }) { float y = (int)Y(db, h) + 0.5f; g.DrawLine(pen, 0, y, w, y); }
            }
            float y0 = (int)Y(0, h) + 0.5f;
            using (var pen = new Pen(Theme.A(Theme.Fg, 0.35))) g.DrawLine(pen, 0, y0, w, y0);

            int steps = Math.Max(w / 3, 50);
            if (freqs.Length != steps + 1)
            {
                freqs = Enumerable.Range(0, steps + 1).Select(i => FMin * Math.Pow(FMax / FMin, i / (double)steps)).ToArray();
                cache.Clear();
            }
            var total = Enumerable.Repeat(s.Preamp, freqs.Length).ToArray();
            for (int i = 0; i < s.Gains.Length; i++)
                if (Math.Abs(s.Gains[i]) > 1e-6)
                {
                    var c = Band(i, s.Gains[i]);
                    for (int k = 0; k < total.Length; k++) total[k] += c[k];
                }
            var pts = freqs.Select((f, k) => new PointF(X(f, w), Y(Math.Max(-DbRange, Math.Min(DbRange, total[k])), h))).ToArray();
            double alpha = s.Enabled ? 1.0 : 0.35;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath())
            {
                path.AddLine(pts[0].X, y0, pts[0].X, pts[0].Y);
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1].X, pts[pts.Length - 1].Y, pts[pts.Length - 1].X, y0);
                path.CloseFigure();
                using (var b = new SolidBrush(Theme.A(Theme.Accent, 0.18 * alpha))) g.FillPath(b, path);
            }
            using (var pen = new Pen(Theme.A(Theme.Accent, alpha), Theme.S(2.5f)) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);
            g.ResetClip();
        }
    }

    /// <summary>Windows 11 tarzı aç/kapat anahtarı.</summary>
    sealed class ToggleSwitch : Painted
    {
        bool on, hover;
        public event Action<bool> Toggled;

        public ToggleSwitch(bool v)
        {
            on = v;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Size = new Size(Theme.S(44), Theme.S(24));
        }

        public bool On { get => on; set { on = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(Theme.S(2), Theme.S(2), Width - Theme.S(4) - 1, Height - Theme.S(4) - 1);
            using (var p = Theme.Round(r, r.Height / 2))
            {
                if (on) using (var b = new SolidBrush(hover ? Theme.Mix(Theme.Accent, Theme.Fg, 0.9) : Theme.Accent)) g.FillPath(b, p);
                else using (var pen = new Pen(Theme.A(Theme.Fg, hover ? 0.75 : 0.6), Theme.S(1.2f))) g.DrawPath(pen, p);
            }
            float d = r.Height - Theme.S(on ? 6 : 8);
            float x = on ? r.Right - Theme.S(3) - d : r.X + Theme.S(4);
            using (var b = new SolidBrush(on ? Theme.AccentFg : Theme.A(Theme.Fg, 0.75)))
                g.FillEllipse(b, x, r.Y + (r.Height - d) / 2, d, d);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Theme.Fg, 1) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        void Flip() { on = !on; Invalidate(); Toggled?.Invoke(on); }
        protected override void OnMouseClick(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Flip(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Flip(); e.Handled = true; } }
    }

    /// <summary>Düz simge düğmesi (Segoe Fluent/MDL2 simgesi); isteğe bağlı metin ve vurgu (hap) biçimi.</summary>
    class IconButton : Painted
    {
        public string Glyph, Label;
        public bool Pill;
        bool hover, down;

        public IconButton(string glyph, string label = null)
        {
            Glyph = glyph; Label = label;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Size = new Size(Theme.S(32), Theme.S(32));
        }

        public int PreferredWidth
        {
            get
            {
                if (string.IsNullOrEmpty(Label)) return Theme.S(32);
                return Theme.S(Pill ? 44 : 36) + TextRenderer.MeasureText(Label, Theme.UiFont).Width;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fg = Enabled ? Theme.Fg : Theme.A(Theme.Fg, 0.35);
            using (var p = Theme.Round(r, Pill ? r.Height / 2 : Theme.S(6)))
            {
                if (Pill)
                {
                    using (var b = new SolidBrush(down ? Theme.Mix(Theme.Accent, Color.Black, 0.85) : hover ? Theme.Mix(Theme.Accent, Color.White, 0.9) : Theme.Accent))
                        g.FillPath(b, p);
                    fg = Theme.AccentFg;
                }
                else if (Enabled && (hover || down))
                    using (var b = new SolidBrush(Theme.A(Theme.Fg, down ? 0.14 : 0.08))) g.FillPath(b, p);
            }
            if (string.IsNullOrEmpty(Label))
                Theme.Glyph(g, Glyph, Theme.S(16), fg, new RectangleF(0, 0, Width, Height));
            else
            {
                int gx = Theme.S(Pill ? 14 : 8);
                Theme.Glyph(g, Glyph, Theme.S(14), fg, new RectangleF(gx, 0, Theme.S(16), Height));
                Theme.Text(g, Label, Theme.UiFont, fg, new Rectangle(gx + Theme.S(22), 0, Width - gx - Theme.S(22), Height),
                           TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Theme.A(Theme.Fg, 0.6), 1) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; } }
    }

    /// <summary>Açılır liste görünümlü düğme; tıklanınca temaya uygun menü açar.</summary>
    sealed class DropDown : IconButton
    {
        public List<string> Items = new List<string>();
        public List<int> Separators = new List<int>();
        int index = -1;
        public event Action<int> SelectedChanged;

        public DropDown() : base("") { Height = Theme.S(32); }

        public int SelectedIndex
        {
            get => index;
            set { index = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var p = Theme.Round(r, Theme.S(6)))
            using (var b = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Focused ? Theme.Accent : Theme.Border))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            string text = index >= 0 && index < Items.Count ? Items[index] : "";
            Theme.Text(g, text, Theme.UiFont, Theme.Fg, new Rectangle(Theme.S(10), 0, Width - Theme.S(40), Height),
                       TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Theme.Glyph(g, "", Theme.S(11), Theme.A(Theme.Fg, 0.7), new RectangleF(Width - Theme.S(30), 0, Theme.S(24), Height));
        }

        protected override void OnClick(EventArgs e)
        {
            Focus();
            var menu = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true, Font = Theme.UiFont };
            for (int i = 0; i < Items.Count; i++)
            {
                if (Separators.Contains(i)) menu.Items.Add(new ToolStripSeparator());
                int idx = i;
                var it = new ToolStripMenuItem(Items[i]) { Checked = i == index };
                it.Click += (s, a) => { index = idx; Invalidate(); SelectedChanged?.Invoke(idx); };
                menu.Items.Add(it);
            }
            menu.MinimumSize = new Size(Width, 0);
            menu.Closed += (s, a) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(this, new Point(0, Height + Theme.S(2)));
        }
    }

    /// <summary>Açık/koyu temaya uyan menü çizimi.</summary>
    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new Colors()) { RoundedEdges = true; }

        sealed class Colors : ProfessionalColorTable
        {
            Color Menu => Theme.Dark ? Color.FromArgb(0x2C, 0x2C, 0x2C) : Color.FromArgb(0xF9, 0xF9, 0xF9);
            Color Hi => Theme.A(Theme.Fg, 0.09);
            public override Color ToolStripDropDownBackground => Menu;
            public override Color ImageMarginGradientBegin => Menu;
            public override Color ImageMarginGradientMiddle => Menu;
            public override Color ImageMarginGradientEnd => Menu;
            public override Color MenuBorder => Theme.Border;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => Theme.Mix(Theme.Fg, Menu, 0.09);
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Border;
            public override Color CheckBackground => Color.Transparent;
            public override Color CheckSelectedBackground => Color.Transparent;
            public override Color CheckPressedBackground => Color.Transparent;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Fg : Theme.A(Theme.Fg, 0.4);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Theme.Glyph(e.Graphics, "", Theme.S(12), Theme.Accent, e.ImageRectangle);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Fg;
            base.OnRenderArrow(e);
        }
    }

    /// <summary>libadwaita "toast" karşılığı: üstte birkaç saniye görünen kısa bildirim.</summary>
    sealed class Toast : Painted
    {
        readonly Timer timer = new Timer { Interval = 3000 };
        string text = "";

        public Toast()
        {
            Visible = false;
            timer.Tick += (s, e) => { timer.Stop(); Visible = false; };
        }

        public void Show(string t, Control host)
        {
            text = t;
            var sz = TextRenderer.MeasureText(t, Theme.UiFont);
            Size = new Size(Math.Min(sz.Width + Theme.S(36), host.ClientSize.Width - Theme.S(32)), sz.Height + Theme.S(16));
            Location = new Point((host.ClientSize.Width - Width) / 2, Theme.S(10));
            using (var p = Theme.Round(new RectangleF(0, 0, Width, Height), Height / 2f)) Region = new Region(p);
            Visible = true;
            BringToFront();
            Invalidate();
            timer.Stop();
            timer.Start();
        }

        protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Theme.Mix(Theme.Fg, Theme.Bg, 0.88));

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Text(e.Graphics, text, Theme.UiFont, Theme.Bg, ClientRectangle,
                       TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
