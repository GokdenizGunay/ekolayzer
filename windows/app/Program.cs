// Ekolayzer giriş noktası. Tek dosya hem uygulama hem kurulumdur:
//   (bağımsız)          kurulu değilse/başka yerden açıldıysa kurulum penceresi, kuruluysa arayüz
//   --background         oturum açılışı: kullanıcının ayarlarını motora uygula, bildirim alanında
//                        simgeyle (ekolayzer açıkken) arka planda kal, güncellemeyi denetle
//   --install [--silent] [--update] [--result=dosya]   kur (gerekirse UAC ile yükseltir)
//   --uninstall [--silent]                             kaldır
//   --repair                                           ses motoru kaydını onar (SYSTEM / yönetici)
//   --apply-update <dosya> --sha256=<özet>             indirilen güncellemeyi doğrulayıp kur (yönetici)
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ekolayzer
{
    static class Program
    {
        const string MutexName = @"Local\io.github.ekolayzer";
        const string ShowEventName = @"Local\io.github.ekolayzer.show";

        [STAThread]
        static int Main(string[] args)
        {
            bool Has(string a) => args.Contains(a);
            string resultFile = args.FirstOrDefault(a => a.StartsWith("--result="))?.Substring(9);
            try
            {
                if (Has("--repair"))
                {
                    if (!Elevation.IsAdmin()) return 5;
                    Installer.Repair();
                    return 0;
                }
                int ai = Array.IndexOf(args, "--apply-update");
                if (ai >= 0 && ai + 1 < args.Length)
                {
                    if (!Elevation.IsAdmin()) return 5;
                    string sha = args.FirstOrDefault(a => a.StartsWith("--sha256="))?.Substring(9);
                    return Elevation.ApplyUpdate(args[ai + 1], sha, resultFile);
                }
                if (Has("--install")) return Has("--silent") ? InstallSilent(Has("--update"), resultFile) : SetupUi(false);
                if (Has("--uninstall")) return Has("--silent") ? UninstallSilent(resultFile) : SetupUi(true);
                if (Has("--background")) return Background();
                if (!Installer.RunningFromInstallDir && !Has("--no-install")) return SetupUi(false);
                return Gui(false);
            }
            catch (Exception e) when (Has("--silent") || Has("--repair") || Has("--background") || Has("--apply-update"))
            {
                WriteResult(resultFile, e.Message);
                return 1;
            }
        }

        static void WriteResult(string path, string msg)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.WriteAllText(path, msg ?? ""); } catch { }
        }

        // ------------------------------------------------------------ sessiz kurulum / kaldırma (yönetici)

        static int InstallSilent(bool update, string result)
        {
            if (!Elevation.IsAdmin())
            {
                int c = Elevation.RunElevated(Application.ExecutablePath, string.Join(" ",
                    Environment.GetCommandLineArgs().Skip(1).Select(a => "\"" + a + "\"")), out var err);
                if (err != null) WriteResult(result, err);
                return c;
            }
            try
            {
                Installer.Install(update);
                WriteResult(result, "");
                return 0;
            }
            catch (InstallException e) { WriteResult(result, e.Message); return e.Code; }
        }

        static int UninstallSilent(string result)
        {
            if (!Elevation.IsAdmin())
            {
                int c = Elevation.RunElevated(Application.ExecutablePath, "--uninstall --silent" + (result != null ? $" \"--result={result}\"" : ""), out var err);
                if (err != null) WriteResult(result, err);
                return c;
            }
            Installer.Uninstall();
            WriteResult(result, "");
            return 0;
        }

        // ------------------------------------------------------------ kurulum penceresi

        static int SetupUi(bool uninstall)
        {
            InitUi();
            string self = Application.ExecutablePath;
            if (uninstall && Installer.RunningFromInstallDir)
            {
                // kurulu dosya silinebilsin diye geçici bir kopyadan çalış
                string tmp = Path.Combine(Path.GetTempPath(), "ekolayzer-kaldir-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(self, tmp);
                Process.Start(new ProcessStartInfo(tmp, "--uninstall") { UseShellExecute = false });
                return 0;
            }

            string installed = Installer.InstalledVersion();
            if (uninstall)
            {
                var q = new TaskDlg { Heading = "Ekolayzer kaldırılsın mı?",
                                      Body = "Ses motoru tüm çıkış aygıtlarından kaldırılır; ses birkaç saniye kesilir.\nKişisel ayarların korunur." };
                q.Buttons.Add((10, "Kaldır"));
                q.Buttons.Add((TaskDlg.IdCancel, "Vazgeç"));
                if (q.Show() != 10) { SelfDelete(); return 1; }
            }
            else
            {
                var q = new TaskDlg { Heading = installed == null ? $"Ekolayzer {Core.Version} kurulsun mu?" : $"Ekolayzer {Core.Version}",
                                      Footer = $"© 2026 {Core.Author} · GPL-3.0" };
                q.Body = "Sistem geneli 10 bantlı ekolayzer ve limiter. Tüm uygulamaların ses çıkışına uygulanır.\n\n";
                if (installed == null) q.Body += "Kurulum sırasında ses birkaç saniye kesilir.";
                else
                {
                    var cmp = Updater.Compare(Updater.ParseVersion(Core.Version), Updater.ParseVersion(installed) ?? new[] { 0 });
                    q.Body += cmp > 0 ? $"Kurulu sürüm {installed}; {Core.Version} sürümüne yükseltilecek. Ayarların korunur."
                            : cmp == 0 ? "Bu sürüm zaten kurulu. Yeniden kurmak ses motoru kaydını da onarır."
                            : $"Daha yeni bir sürüm ({installed}) kurulu. Bu eski sürüm kurulursa onun yerini alır.";
                }
                q.Buttons.Add((10, installed == null ? "Kur" : "Yeniden kur"));
                q.Buttons.Add((TaskDlg.IdCancel, "Vazgeç"));
                if (installed != null && Updater.Compare(Updater.ParseVersion(Core.Version), Updater.ParseVersion(installed) ?? new[] { 0 }) > 0)
                    q.Buttons[0] = (10, "Yükselt");
                if (q.Show() != 10) return 1;
            }

            string result = Path.Combine(Path.GetTempPath(), "ekolayzer-sonuc-" + Process.GetCurrentProcess().Id + ".txt");
            int code = -1;
            string err = null;
            var p = new TaskDlg
            {
                Heading = uninstall ? "Ekolayzer kaldırılıyor" : "Ekolayzer kuruluyor",
                Body = "Açılan Kullanıcı Hesabı Denetimi penceresinde onay ver…",
                ProgressBar = true, Marquee = true, Locked = true, CommonButtons = 8,
            };
            p.Created += d => Task.Run(() =>
            {
                code = Elevation.RunElevated(self, (uninstall ? "--uninstall" : "--install") + $" --silent \"--result={result}\"", out err);
                d.Close();
            });
            p.Show();
            string msg = File.Exists(result) ? File.ReadAllText(result).Trim() : err;
            try { File.Delete(result); } catch { }

            var r = new TaskDlg();
            if (code == Elevation.Cancelled)
            {
                r.Heading = "Yönetici onayı verilmedi";
                r.Body = uninstall ? "Ekolayzer kaldırılmadı." : "Ekolayzer kurulmadı.";
                r.Show();
                if (uninstall) SelfDelete();
                return 1;
            }
            if (code != 0)
            {
                r.Heading = uninstall ? "Kaldırma başarısız" : "Kurulum başarısız";
                r.Body = string.IsNullOrEmpty(msg) ? $"Çıkış kodu {code}." : msg;
                r.Show();
                if (uninstall) SelfDelete();
                return code;
            }
            if (uninstall)
            {
                r.Heading = "Ekolayzer kaldırıldı";
                r.Body = "Ses artık ekolayzerden geçmiyor.";
                r.Show();
                SelfDelete();
                return 0;
            }
            r.Heading = "Ekolayzer kuruldu";
            r.Body = "Ses motoru çalışıyor: tüm uygulamaların sesi ekolayzerden geçiyor.\n" +
                     "Masaüstüne ve Başlat menüsüne kısayol eklendi. Oturum açılışında bildirim alanında çalışır ve güncellemeleri denetler.";
            r.Buttons.Add((10, "Ekolayzer'i aç"));
            r.Buttons.Add((TaskDlg.IdCancel, "Kapat"));
            r.DefaultButton = 10;
            if (r.Show() == 10) Process.Start(new ProcessStartInfo(Core.InstalledExe) { UseShellExecute = false });
            return 0;
        }

        /// <summary>Kaldırma için kullanılan geçici kopya kendini birkaç saniye sonra siler.</summary>
        static void SelfDelete()
        {
            string self = Application.ExecutablePath;
            if (!Path.GetFileName(self).StartsWith("ekolayzer-kaldir-")) return;
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\"")
                          { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        // ------------------------------------------------------------ oturum açılışı

        static int Background()
        {
            // Bu kullanıcının son ayarları motorda geçerli olsun (çok kullanıcılı bilgisayarlar için)
            if (File.Exists(Core.SettingsPath))
                using (var e = new Engine()) e.Apply(Settings.Load());

            if (Mutex.TryOpenExisting(MutexName, out var open))
            {
                open.Dispose();
                return 0;  // uygulama zaten açık
            }
            return Gui(true);
        }

        // ------------------------------------------------------------ arayüz

        static void InitUi()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Init(g.DpiX);
        }

        static int Gui(bool background)
        {
            var mutex = new Mutex(true, MutexName, out bool created);
            if (!created)
            {
                // zaten açık: o örneğin penceresini öne getir
                if (!background)
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                return 0;
            }
            InitUi();
            var app = new App(mutex);
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() => { while (show.WaitOne()) app.Post(app.ShowMain); }) { IsBackground = true }.Start();
            if (background) app.StartResident();
            else app.ShowMain();
            Application.Run(app);
            GC.KeepAlive(mutex);
            return 0;
        }
    }
}
