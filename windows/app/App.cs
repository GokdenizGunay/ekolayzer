// Uygulama denetleyicisi: tek örnek, ana pencere, bildirim alanı simgesi (ekolayzer açıkken),
// güncelleme popup'ı (ana pencere olmasa da), sol alttaki güncelleme düğmesi ve hakkında penceresi.
// Linux sürümündeki EqApp + UpdateFlow karşılığı.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ekolayzer
{
    sealed class App : ApplicationContext
    {
        const int RLater = 100, RUpdate = 101, RPage = 102, ROk = 103, RRestart = 104;

        public MainForm Win;
        UpdateInfo updateInfo;  // bilinen en yeni sürüm (sol alttaki düğme için)
        bool updateBusy, checking;
        readonly SynchronizationContext ui;
        readonly Mutex mutex;
        readonly NotifyIcon tray;

        public App(Mutex mutex)
        {
            this.mutex = mutex;
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            // Bildirim alanı simgesi: ekolayzer açıkken görünür
            var menu = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = false, Font = Theme.UiFont };
            var open = new ToolStripMenuItem("Ekolayzer'i aç", null, (s, e) => ShowMain());
            open.Font = new System.Drawing.Font(Theme.UiFont, System.Drawing.FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add("Ekolayzeri kapat", null, (s, e) => SetEnabled(false));
            menu.Items.Add("Güncellemeleri denetle", null, (s, e) => CheckUpdates(manual: true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Çıkış", null, (s, e) => Quit());
            using (var st = typeof(App).Assembly.GetManifestResourceStream("ekolayzer.ico"))
                tray = new NotifyIcon
                {
                    Icon = new System.Drawing.Icon(st, SystemInformation.SmallIconSize),
                    Text = "Ekolayzer – etkin",
                    ContextMenuStrip = menu,
                };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
            tray.Visible = Settings.Load().Enabled;
        }

        /// <summary>Oturum açılışı: pencere açmadan arka planda kal (simge) ve güncellemeyi denetle.</summary>
        public void StartResident()
        {
            CheckUpdates(retry: true);
        }

        public bool Enabled => Win != null ? Win.S.Enabled : Settings.Load().Enabled;

        /// <summary>Simgedeki menüden aç/kapat.</summary>
        void SetEnabled(bool on)
        {
            if (Win != null) { Win.SetPower(on); return; }
            var s = Settings.Load();
            s.Enabled = on;
            try { s.Save(); } catch { }
            using (var e = new Engine()) e.Apply(s);
            OnEnabledChanged(on);
        }

        /// <summary>Ekolayzer açıldı/kapandı: simge yalnızca açıkken görünür.</summary>
        public void OnEnabledChanged(bool on)
        {
            tray.Visible = on;
            MaybeExit();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        /// <summary>Pencere kapanınca arka planda kalan süreç belleğini geri versin.</summary>
        static void Trim()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }

        public void Quit()
        {
            Win?.Close();
            tray.Visible = false;
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }

        public void Post(Action a) => ui.Post(_ => a(), null);

        // ------------------------------------------------------------ ana pencere

        public void ShowMain()
        {
            bool first = Win == null || Win.IsDisposed;
            if (first)
            {
                Win = new MainForm(this);
                Win.FormClosed += (s, e) => { Win = null; MaybeExit(); Trim(); };
                Win.SetUpdateInfo(updateInfo);
                Win.Show();
            }
            if (Win.WindowState == FormWindowState.Minimized) Win.WindowState = FormWindowState.Normal;
            Win.Activate();
            if (first) CheckUpdates();
        }

        void MaybeExit()
        {
            // simge görünürken (ekolayzer açık) arka planda kalır
            if (Win == null && !updateBusy && !checking && !tray.Visible) ExitThread();
        }

        IntPtr Owner => Win != null && Win.Visible ? Win.Handle : IntPtr.Zero;

        // ------------------------------------------------------------ güncelleme

        public void CheckUpdates(bool manual = false, bool retry = false)
        {
            if (updateBusy || checking) return;
            checking = true;
            Task.Run(() =>
            {
                for (int i = 0; ; i++)
                {
                    try { return (Updater.Check(), (Exception)null); }
                    catch (Exception e)
                    {
                        // oturum açılışında ağ henüz hazır olmayabilir
                        if (!retry || i >= 3 || !(e is NetworkException)) return ((UpdateInfo)null, e);
                        Thread.Sleep(15000);
                    }
                }
            }).ContinueWith(t =>
            {
                checking = false;
                if (Win == null) Trim();
                var (info, err) = t.Result;
                if (info != null)
                {
                    SetUpdateInfo(info);
                    // "Tekrar gösterme" denmiş sürüm için açılışta popup yok; menüden denetimde her zaman göster
                    if (manual || Skipped() != info.Version) ShowUpdate(info);
                }
                else if (manual && Win != null)
                    Win.Notify(err != null ? "Güncelleme sunucusuna ulaşılamadı." : $"Ekolayzer güncel ({Core.Version}).");
                else if (manual)
                    tray.ShowBalloonTip(3000, "Ekolayzer", err != null ? "Güncelleme sunucusuna ulaşılamadı." : $"Ekolayzer güncel ({Core.Version}).", ToolTipIcon.Info);
                MaybeExit();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void SetUpdateInfo(UpdateInfo info)
        {
            updateInfo = info;
            Win?.SetUpdateInfo(info);
        }

        public void ShowUpdate(UpdateInfo info)
        {
            if (updateBusy) return;
            updateBusy = true;  // ana pencere yokken de popup kapanana kadar uygulama açık kalsın
            Post(() => UpdatePopup(info));
        }

        void UpdatePopup(UpdateInfo info)
        {
            string body = $"Ekolayzer {info.Version} yayımlandı. Yüklü sürüm: {info.Current}.";
            if (info.Summary.Count > 0)
                body += "\n\nYenilikler\n" + string.Join("\n", info.Summary.Select(x => "• " + x));
            var d = new TaskDlg
            {
                Heading = "Ekolayzer güncellemesi var",
                Body = body,
                Verification = "Tekrar gösterme",
                Owner = Owner,
            };
            d.Buttons.Add((RLater, "Sonra"));
            if (info.AssetUrl != null) { d.Buttons.Add((RUpdate, "Güncelle")); d.DefaultButton = RUpdate; }
            else { d.Buttons.Add((RPage, "Sürüm sayfasını aç")); d.DefaultButton = RPage; }
            int r = d.Show();
            if (r == RUpdate) { RunUpdate(info); return; }
            if (d.VerificationChecked) SkipVersion(info.Version);
            if (r == RPage) TaskDlg.Open(info.Page);
            UpdateFinished();
        }

        /// <summary>Sol alttaki düğme: sormadan doğrudan indir ve kur.</summary>
        public void StartUpdate()
        {
            if (updateBusy || updateInfo == null) return;
            if (updateInfo.AssetUrl == null) { TaskDlg.Open(updateInfo.Page); return; }
            updateBusy = true;
            RunUpdate(updateInfo);
        }

        void RunUpdate(UpdateInfo info)
        {
            bool ok = false;
            string msg = null;
            var d = new TaskDlg
            {
                Heading = "Ekolayzer güncelleniyor",
                Body = $"Ekolayzer {info.Version} indiriliyor…",
                ProgressBar = true,
                Locked = true,  // iş bitmeden kapatılamaz
                CommonButtons = 8,
                Owner = Owner,
            };
            d.Created += dlg => Task.Run(() =>
            {
                try
                {
                    string path = Updater.Download(info, f => dlg.SetProgress(f));
                    dlg.SetBody("Kuruluyor… Açılan Kullanıcı Hesabı Denetimi penceresinde onay ver.");
                    dlg.SetMarquee();
                    (ok, msg) = Updater.Install(path, info.Sha256);
                }
                catch (Exception e) when (e is NetworkException || e is InvalidDataException || e is IOException || e is UnauthorizedAccessException)
                {
                    msg = "Kurulum dosyası indirilemedi: " + e.Message;
                }
                dlg.Close();
            });
            d.Show();

            bool guiOpen = Win != null && Win.Visible;
            var res = new TaskDlg { Owner = Owner };
            if (ok)
            {
                SetUpdateInfo(null);
                res.Heading = "Güncelleme tamamlandı";
                res.Body = $"Ekolayzer {info.Version} kuruldu; ses motoru yeni sürümle çalışıyor.";
                if (guiOpen)
                {
                    res.Body += "\n\nYeni arayüzü kullanmak için uygulamayı yeniden başlat.";
                    res.Buttons.Add((RLater, "Sonra"));
                    res.Buttons.Add((RRestart, "Yeniden başlat"));
                    res.DefaultButton = RRestart;
                }
                else res.Buttons.Add((ROk, "Tamam"));
            }
            else
            {
                res.Heading = "Güncelleme kurulamadı";
                res.Body = msg;
                res.Buttons.Add((RPage, "Sürüm sayfasını aç"));
                res.Buttons.Add((ROk, "Tamam"));
                res.DefaultButton = ROk;
            }
            int r = res.Show();
            // pencere açık değilse arka plandaki eski sürüm kendiliğinden yenisiyle değişsin
            if (r == RRestart || (ok && !guiOpen)) { Restart(); return; }
            if (r == RPage) TaskDlg.Open(info.Page);
            UpdateFinished();
        }

        string Skipped() => Win != null ? Win.S.UpdateSkip : Settings.Load().UpdateSkip;

        void SkipVersion(string v)
        {
            if (Win != null) { Win.S.UpdateSkip = v; Win.S.Save(); return; }
            var s = Settings.Load();
            s.UpdateSkip = v;
            try { s.Save(); } catch { }
        }

        void UpdateFinished()
        {
            updateBusy = false;
            MaybeExit();
        }

        void Restart()
        {
            Win?.Flush();
            try { mutex.ReleaseMutex(); } catch { }
            bool window = Win != null;
            Win?.Dispose();
            string exe = File.Exists(Core.InstalledExe) ? Core.InstalledExe : Application.ExecutablePath;
            Process.Start(new ProcessStartInfo(exe, window ? "" : "--background") { UseShellExecute = false });
            ExitThread();
        }

        // ------------------------------------------------------------ hakkında

        public void About()
        {
            var d = new TaskDlg
            {
                Heading = "Ekolayzer " + Core.Version,
                Body = "Sistem geneli 10 bantlı ekolayzer ve limiter.\nTüm uygulamaların ses çıkışına uygulanır.\n\n" +
                       $"© 2026 {Core.Author}\nLisans: GNU GPL 3.0 veya üstü\n\n" +
                       $"<a href=\"https://github.com/{Core.Repo}\">GitHub'daki proje sayfası</a>",
                Links = true,
                Owner = Owner,
                LinkClicked = TaskDlg.Open,
                CommonButtons = 0x20,  // Kapat
            };
            d.Show();
        }
    }
}
