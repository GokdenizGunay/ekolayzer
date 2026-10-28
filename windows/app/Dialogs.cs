// İleti pencereleri: Windows'un yerel görev iletişim kutusu (TaskDialog; "Tekrar gösterme" onay kutusu,
// ilerleme çubuğu, bağlantılar) ve ön ayar adı için küçük bir giriş penceresi.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ekolayzer
{
    sealed class TaskDlg
    {
        public const int IdOk = 1, IdCancel = 2;

        public string Title = "Ekolayzer", Heading, Body, Verification, Footer;
        public bool VerificationChecked, Links, ProgressBar, Marquee, Locked;
        public readonly List<(int Id, string Text)> Buttons = new List<(int, string)>();
        public int CommonButtons, DefaultButton;
        public IntPtr Owner;
        public IntPtr Hwnd { get; private set; }
        public event Action<TaskDlg> Created;
        public Action<string> LinkClicked;
        bool allowClose;

        // ------------------------------------------------------------ yerel API

        delegate int Callback(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, IntPtr refData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        struct Config
        {
            public uint cbSize;
            public IntPtr hwndParent, hInstance;
            public int dwFlags, dwCommonButtons;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszWindowTitle;
            public IntPtr hMainIcon;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszMainInstruction, pszContent;
            public uint cButtons;
            public IntPtr pButtons;
            public int nDefaultButton;
            public uint cRadioButtons;
            public IntPtr pRadioButtons;
            public int nDefaultRadioButton;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszVerificationText, pszExpandedInformation,
                                                           pszExpandedControlText, pszCollapsedControlText;
            public IntPtr hFooterIcon;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszFooter;
            public Callback pfCallback;
            public IntPtr lpCallbackData;
            public uint cxWidth;
        }

        [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
        static extern int TaskDialogIndirect(ref Config c, out int button, out int radio, out bool verification);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        const int WM_USER = 0x400;
        const int TDM_CLICK_BUTTON = WM_USER + 102, TDM_SET_MARQUEE_PROGRESS_BAR = WM_USER + 103,
                  TDM_SET_PROGRESS_BAR_POS = WM_USER + 106, TDM_SET_PROGRESS_BAR_MARQUEE = WM_USER + 107,
                  TDM_SET_ELEMENT_TEXT = WM_USER + 108, TDM_ENABLE_BUTTON = WM_USER + 111;

        static Icon appIcon;
        public static Icon AppIcon
        {
            get
            {
                if (appIcon == null)
                    using (var s = typeof(TaskDlg).Assembly.GetManifestResourceStream("ekolayzer.ico"))
                        appIcon = new Icon(s, new Size(32, 32));
                return appIcon;
            }
        }

        /// <summary>Pencereyi gösterir ve basılan düğmenin kimliğini döndürür (kapatılırsa IdCancel).</summary>
        public int Show()
        {
            var handles = new List<IntPtr>();
            IntPtr btns = IntPtr.Zero;
            int size = Marshal.SizeOf(typeof(int)) + IntPtr.Size;
            Callback cb = OnCallback;
            try
            {
                if (Buttons.Count > 0)
                {
                    btns = Marshal.AllocHGlobal(size * Buttons.Count);
                    for (int i = 0; i < Buttons.Count; i++)
                    {
                        var s = Marshal.StringToHGlobalUni(Buttons[i].Text);
                        handles.Add(s);
                        Marshal.WriteInt32(btns, i * size, Buttons[i].Id);
                        Marshal.WriteIntPtr(btns, i * size + 4, s);
                    }
                }
                int flags = 0x2 /* USE_HICON_MAIN */ | 0x8 /* ALLOW_CANCELLATION */ | 0x1000000 /* SIZE_TO_CONTENT */;
                if (Links) flags |= 0x1;
                if (VerificationChecked) flags |= 0x100;
                if (ProgressBar) flags |= Marquee ? 0x400 : 0x200;
                if (Owner != IntPtr.Zero) flags |= 0x1000;  // sahibinin ortasında
                var c = new Config
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(Config)),
                    hwndParent = Owner,
                    dwFlags = flags,
                    dwCommonButtons = Buttons.Count == 0 && CommonButtons == 0 ? 1 : CommonButtons,
                    pszWindowTitle = Title,
                    hMainIcon = AppIcon.Handle,
                    pszMainInstruction = Heading,
                    pszContent = Body,
                    cButtons = (uint)Buttons.Count,
                    pButtons = btns,
                    nDefaultButton = DefaultButton,
                    pszVerificationText = Verification,
                    pszFooter = Footer,
                    pfCallback = cb,
                };
                int hr = TaskDialogIndirect(ref c, out int button, out _, out bool ver);
                GC.KeepAlive(cb);
                if (hr != 0) return IdCancel;
                VerificationChecked = ver;
                return button;
            }
            finally
            {
                foreach (var h in handles) Marshal.FreeHGlobal(h);
                if (btns != IntPtr.Zero) Marshal.FreeHGlobal(btns);
                Hwnd = IntPtr.Zero;
            }
        }

        int OnCallback(IntPtr hwnd, int msg, IntPtr w, IntPtr l, IntPtr data)
        {
            switch (msg)
            {
                case 0:  // TDN_CREATED
                    Hwnd = hwnd;
                    if (ProgressBar && Marquee) SendMessage(hwnd, TDM_SET_PROGRESS_BAR_MARQUEE, (IntPtr)1, IntPtr.Zero);
                    if (Locked) SendMessage(hwnd, TDM_ENABLE_BUTTON, (IntPtr)IdCancel, IntPtr.Zero);
                    SetForegroundWindow(hwnd);  // arka planda açılan popup da öne gelsin
                    Created?.Invoke(this);
                    break;
                case 2:  // TDN_BUTTON_CLICKED
                    if (Locked && !allowClose) return 1;  // iş bitmeden kapatılamaz
                    break;
                case 3:  // TDN_HYPERLINK_CLICKED
                    LinkClicked?.Invoke(Marshal.PtrToStringUni(l));
                    break;
            }
            return 0;
        }

        // Başka iş parçacığından da çağrılabilir (SendMessage pencerenin iş parçacığında işlenir).
        public void SetProgress(double fraction)
        {
            if (Hwnd != IntPtr.Zero) SendMessage(Hwnd, TDM_SET_PROGRESS_BAR_POS, (IntPtr)(int)(fraction * 100), IntPtr.Zero);
        }

        public void SetMarquee()
        {
            if (Hwnd == IntPtr.Zero) return;
            SendMessage(Hwnd, TDM_SET_MARQUEE_PROGRESS_BAR, (IntPtr)1, IntPtr.Zero);
            SendMessage(Hwnd, TDM_SET_PROGRESS_BAR_MARQUEE, (IntPtr)1, IntPtr.Zero);
        }

        public void SetBody(string text)
        {
            if (Hwnd == IntPtr.Zero) return;
            var s = Marshal.StringToHGlobalUni(text);
            try { SendMessage(Hwnd, TDM_SET_ELEMENT_TEXT, IntPtr.Zero /* TDE_CONTENT */, s); }
            finally { Marshal.FreeHGlobal(s); }
        }

        public void Close(int id = IdCancel)
        {
            allowClose = true;
            if (Hwnd != IntPtr.Zero) SendMessage(Hwnd, TDM_CLICK_BUTTON, (IntPtr)id, IntPtr.Zero);
        }

        public static void Open(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
    }

    /// <summary>Ön ayar adı penceresi (temaya uyar).</summary>
    sealed class InputDialog : Form
    {
        readonly TextBox box;

        public string Value => box.Text.Trim();

        public InputDialog(string heading, string body, string value)
        {
            Text = "Ekolayzer";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.UiFont;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            int w = Theme.S(360), pad = Theme.S(20);
            ClientSize = new Size(w, Theme.S(170));
            var h = new Label { Text = heading, Font = Theme.Title, AutoSize = false, Location = new Point(pad, pad),
                                Size = new Size(w - 2 * pad, Theme.S(26)), ForeColor = Theme.Fg };
            var b = new Label { Text = body, AutoSize = false, Location = new Point(pad, pad + Theme.S(30)),
                                Size = new Size(w - 2 * pad, Theme.S(20)), ForeColor = Theme.Mix(Theme.Fg, Theme.Bg, 0.75) };
            box = new TextBox { Text = value ?? "", Location = new Point(pad, pad + Theme.S(56)), Width = w - 2 * pad,
                                BackColor = Theme.Card, ForeColor = Theme.Fg, BorderStyle = BorderStyle.FixedSingle };
            var save = new Button { Text = "Kaydet", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat,
                                    BackColor = Theme.Accent, ForeColor = Theme.AccentFg, Size = new Size(Theme.S(96), Theme.S(30)) };
            save.FlatAppearance.BorderSize = 0;
            var cancel = new Button { Text = "Vazgeç", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat,
                                      BackColor = Theme.Card, ForeColor = Theme.Fg, Size = new Size(Theme.S(96), Theme.S(30)) };
            cancel.FlatAppearance.BorderColor = Theme.Border;
            save.Location = new Point(w - pad - save.Width, ClientSize.Height - pad - save.Height);
            cancel.Location = new Point(save.Left - Theme.S(8) - cancel.Width, save.Top);
            Controls.AddRange(new Control[] { h, b, box, cancel, save });
            AcceptButton = save;
            CancelButton = cancel;
            Shown += (s, e) => { box.Focus(); box.SelectAll(); };
            HandleCreated += (s, e) => Native.DarkTitleBar(Handle, Theme.Dark);
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

        public static void DarkTitleBar(IntPtr h, bool dark)
        {
            int v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(h, 20, ref v, 4) != 0) DwmSetWindowAttribute(h, 19, ref v, 4);
        }
    }
}
