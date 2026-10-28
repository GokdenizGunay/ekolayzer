// Kurulum, kaldırma ve onarım (yönetici olarak çalışır). Linux paketindeki postinst/prerm/postrm karşılığı:
//  - dosyalar: %ProgramFiles%\Ekolayzer (Ekolayzer.exe + EkolayzerApo.dll)
//  - ses motoru: APO COM kaydı + ses efekti destekleyen her çıkış aygıtına karıştırma sonrası efekt (MFX/GFX)
//  - ortak bellek dosyası: %ProgramData%\Ekolayzer\engine.bin
//  - oturum açılışında arka plan denetimi (Run anahtarı) ve aygıt onarımı (zamanlanmış görev, SYSTEM)
//  - Başlat menüsü + masaüstü kısayolu, "Uygulamalar"da kaldırma kaydı
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Ekolayzer
{
    sealed class InstallException : Exception
    {
        public int Code;
        public InstallException(string m, int code = 1) : base(m) { Code = code; }
    }

    static class Installer
    {
        const string AppKey = @"SOFTWARE\Ekolayzer";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ekolayzer";
        const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string ApoKey = @"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" + Engine.Clsid;
        const string ClsidKey = @"SOFTWARE\Classes\CLSID\" + Engine.Clsid;
        const string TaskName = "Ekolayzer Ses Motoru";
        const string PkeyEfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";
        const string PkeyMfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},6";
        const string PkeyDisableSysFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
        const string ModeDefault = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
        const string IidApo = "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}";

        public static string DllPath => Path.Combine(Core.InstallDir, "EkolayzerApo.dll");
        static string SelfPath => Assembly.GetExecutingAssembly().Location;

        public static string InstalledVersion()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(AppKey))
                return k?.GetValue("Version") as string;
        }

        public static bool IsInstalled => File.Exists(Core.InstalledExe) && InstalledVersion() != null;

        public static bool RunningFromInstallDir =>
            string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(Core.InstalledExe), StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------ kurulum

        /// <param name="update">Otomatik güncelleme: yalnızca kurulu sürümden yeniyse kurar.</param>
        public static void Install(bool update)
        {
            string old = InstalledVersion();
            if (update && old != null)
            {
                var o = Updater.ParseVersion(old);
                if (o != null && Updater.Compare(Updater.ParseVersion(Core.Version), o) <= 0)
                    throw new InstallException($"Kurulu sürüm ({old}) bu paketle aynı veya daha yeni; güncelleme yapılmadı.", 3);
            }

            Directory.CreateDirectory(Core.InstallDir);
            CleanupOld();
            if (!RunningFromInstallDir) ReplaceFile(Core.InstalledExe, File.ReadAllBytes(SelfPath));
            ReplaceFile(DllPath, ReadResource("EkolayzerApo.dll"));

            EnsureEngineFile();
            RegisterApo();
            RegisterEndpoints();
            RestartAudio();
            CleanupOld();

            // Oturum açılışı: kullanıcının ayarlarını uygula + güncelleme denetimi (Linux'taki autostart)
            using (var k = Registry.LocalMachine.CreateSubKey(RunKey))
                k.SetValue("Ekolayzer", $"\"{Core.InstalledExe}\" --background");
            // Yeni takılan aygıtlar / sürücü güncellemeleri için oturum açılışında onarım
            RunQuiet("schtasks.exe", $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RU SYSTEM /RL HIGHEST " +
                                     $"/TR \"\\\"{Core.InstalledExe}\\\" --repair\"");

            // Kısayollar yalnızca ilk kurulumda (kullanıcı silerse güncellemede geri gelmesin)
            bool fresh = old == null;
            if (fresh)
            {
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Ekolayzer.lnk"));
            }
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Ekolayzer.lnk"));

            using (var k = Registry.LocalMachine.CreateSubKey(AppKey))
            {
                k.SetValue("Version", Core.Version);
                k.SetValue("InstallDir", Core.InstallDir);
            }
            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Ekolayzer");
                k.SetValue("DisplayVersion", Core.Version);
                k.SetValue("Publisher", Core.Author);
                k.SetValue("DisplayIcon", Core.InstalledExe + ",0");
                k.SetValue("InstallLocation", Core.InstallDir);
                k.SetValue("UninstallString", $"\"{Core.InstalledExe}\" --uninstall");
                k.SetValue("QuietUninstallString", $"\"{Core.InstalledExe}\" --uninstall --silent");
                k.SetValue("URLInfoAbout", $"https://github.com/{Core.Repo}");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long size = new[] { Core.InstalledExe, DllPath }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
                k.SetValue("EstimatedSize", (int)(size / 1024) + 1, RegistryValueKind.DWord);
            }
        }

        // ------------------------------------------------------------ onarım (oturum açılışı, SYSTEM)

        public static void Repair()
        {
            if (!File.Exists(DllPath)) return;
            CleanupOld();
            EnsureEngineFile();
            bool changed = RegisterApo();
            changed |= RegisterEndpoints();
            if (changed) RestartAudio();
        }

        // ------------------------------------------------------------ kaldırma

        public static void Uninstall()
        {
            // Açık arayüzleri kapat (dosyalar silinebilsin)
            foreach (var p in Process.GetProcessesByName("Ekolayzer"))
            {
                try
                {
                    if (p.Id != Process.GetCurrentProcess().Id &&
                        string.Equals(p.MainModule.FileName, Core.InstalledExe, StringComparison.OrdinalIgnoreCase))
                    { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }

            UnregisterEndpoints();
            Registry.LocalMachine.DeleteSubKeyTree(ApoKey, false);
            Registry.LocalMachine.DeleteSubKeyTree(ClsidKey, false);
            RestartAudio();  // motor DLL'i ses hizmetinden boşalsın

            using (var k = Registry.LocalMachine.OpenSubKey(RunKey, true))
                k?.DeleteValue("Ekolayzer", false);
            RunQuiet("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"");
            foreach (var dir in new[] { Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonPrograms })
                TryDelete(Path.Combine(Environment.GetFolderPath(dir), "Ekolayzer.lnk"));
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
            Registry.LocalMachine.DeleteSubKeyTree(AppKey, false);

            foreach (var f in new[] { DllPath, Core.InstalledExe })
                DeleteOrSchedule(f);
            CleanupOld();
            TryDeleteDir(Core.InstallDir);
            TryDelete(Core.EnginePath);
            TryDeleteDir(Core.EngineDir);
        }

        // ------------------------------------------------------------ ses motoru kaydı

        static bool SetIfDifferent(RegistryKey k, string name, object value, RegistryValueKind kind)
        {
            var cur = k.GetValue(name);
            if (cur != null && k.GetValueKind(name) == kind && Equals(cur is int i ? (object)i : cur, value)) return false;
            k.SetValue(name, value, kind);
            return true;
        }

        /// <summary>COM sınıfı ve APO kaydı. Bir şey değiştiyse true.</summary>
        static bool RegisterApo()
        {
            bool ch = false;
            using (var k = Registry.LocalMachine.CreateSubKey(ClsidKey))
                ch |= SetIfDifferent(k, "", "Ekolayzer APO", RegistryValueKind.String);
            using (var k = Registry.LocalMachine.CreateSubKey(ClsidKey + @"\InprocServer32"))
            {
                ch |= SetIfDifferent(k, "", DllPath, RegistryValueKind.String);
                ch |= SetIfDifferent(k, "ThreadingModel", "Both", RegistryValueKind.String);
            }
            using (var k = Registry.LocalMachine.CreateSubKey(ApoKey))
            {
                ch |= SetIfDifferent(k, "FriendlyName", "Ekolayzer", RegistryValueKind.String);
                ch |= SetIfDifferent(k, "Copyright", "Copyright (c) 2026 " + Core.Author, RegistryValueKind.String);
                foreach (var (n, v) in new[] {
                    ("MajorVersion", 1), ("MinorVersion", 4), ("Flags", 15),
                    ("MinInputConnections", 1), ("MaxInputConnections", 1),
                    ("MinOutputConnections", 1), ("MaxOutputConnections", 1),
                    ("MaxInstances", unchecked((int)0xffffffff)), ("NumAPOInterfaces", 1) })
                    ch |= SetIfDifferent(k, n, v, RegistryValueKind.DWord);
                ch |= SetIfDifferent(k, "APOInterface0", IidApo, RegistryValueKind.String);
            }
            return ch;
        }

        static IEnumerable<string> RenderEndpoints()
        {
            using (var r = Registry.LocalMachine.OpenSubKey(Engine.RenderKey))
            {
                if (r == null) yield break;
                foreach (var guid in r.GetSubKeyNames())
                {
                    using (var k = r.OpenSubKey(guid))
                    {
                        int state = k?.GetValue("DeviceState") is int s ? s & 0xF : 0;
                        // etkin, devre dışı veya fişi çekili (kulaklık takılınca çalışsın); yok olanlar hariç
                        if ((state & (1 | 2 | 8)) == 0) continue;
                    }
                    yield return guid;
                }
            }
        }

        static bool IsOurs(object v) => string.Equals(v as string, Engine.Clsid, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Ses motorunu ses efekti destekleyen her çıkış aygıtına karıştırma sonrası efekt olarak ekler:
        /// yeni düzende MFX (mod efekti), eski sürücülerde GFX. Yuvadaki özgün efekt (ör. Windows'un ses
        /// geliştirmeleri) silinmez; motor onu içinden çalıştırır. Hiç efekt kaydı olmayan aygıtlar
        /// (ör. Elgato Wave Link'in sanal karıştırıcı girişleri) atlanır: ses iki kez işlenmez,
        /// karıştırıcının çıktığı donanım aygıtında bir kez uygulanır. Bir şey değiştiyse true.
        /// </summary>
        static bool RegisterEndpoints()
        {
            bool changed = false;
            Privileges.Enable("SeBackupPrivilege", "SeRestorePrivilege");
            foreach (var guid in RenderEndpoints())
            {
                string ep = Engine.RenderKey + "\\" + guid;
                using (var fx = Privileges.OpenForWrite(ep + @"\FxProperties"))
                {
                    if (fx == null) continue;
                    using (var backup = Registry.LocalMachine.CreateSubKey(AppKey + @"\Endpoints\" + guid))
                    {
                        changed |= RemoveOldEfx(fx, backup);
                        string slot = Engine.Slot(fx);
                        if (slot == null) continue;
                        var cur = fx.GetValue(slot) as string;
                        if (!IsOurs(cur))
                        {
                            if (cur != null)
                            {
                                fx.SetValue(Engine.PkeyChild, cur, RegistryValueKind.String);  // motor bunu zincirler
                                backup.SetValue("child", cur);
                            }
                            backup.SetValue("slot", slot);
                            fx.SetValue(slot, Engine.Clsid, RegistryValueKind.String);
                            changed = true;
                        }
                        if (slot == Engine.PkeyMfx && fx.GetValue(PkeyMfxModes) == null)
                        {
                            fx.SetValue(PkeyMfxModes, new[] { ModeDefault }, RegistryValueKind.MultiString);
                            backup.SetValue("modes_added", 1, RegistryValueKind.DWord);
                            changed = true;
                        }
                        // "Ses geliştirmeleri: Kapalı" ise efektler (ekolayzer dahil) çalışmaz: aç
                        using (var props = Privileges.OpenForWrite(ep + @"\Properties"))
                            if (props != null && props.GetValue(PkeyDisableSysFx) is int dis && dis != 0)
                            {
                                if (backup.GetValue("sysfx") == null) backup.SetValue("sysfx", dis, RegistryValueKind.DWord);
                                props.SetValue(PkeyDisableSysFx, 0, RegistryValueKind.DWord);
                                changed = true;
                            }
                    }
                }
            }
            return changed;
        }

        /// <summary>Geliştirme sırasındaki EFX kaydını kaldırır.</summary>
        static bool RemoveOldEfx(RegistryKey fx, RegistryKey backup)
        {
            bool ch = false;
            if (IsOurs(fx.GetValue(Engine.PkeyEfx)))
            {
                if (backup.GetValue("efx") is string orig) fx.SetValue(Engine.PkeyEfx, orig, RegistryValueKind.String);
                else fx.DeleteValue(Engine.PkeyEfx, false);
                ch = true;
            }
            if (backup.GetValue("efx_modes_added") is int a && a == 1 && fx.GetValue(Engine.PkeyEfx) == null)
            {
                fx.DeleteValue(PkeyEfxModes, false);
                ch = true;
            }
            backup.DeleteValue("efx", false);
            backup.DeleteValue("efx_modes_added", false);
            return ch;
        }

        static void UnregisterEndpoints()
        {
            Privileges.Enable("SeBackupPrivilege", "SeRestorePrivilege");
            using (var r = Registry.LocalMachine.OpenSubKey(Engine.RenderKey))
            {
                if (r == null) return;
                foreach (var guid in r.GetSubKeyNames())
                {
                    string ep = Engine.RenderKey + "\\" + guid;
                    using (var backup = Registry.LocalMachine.CreateSubKey(AppKey + @"\Endpoints\" + guid))
                    using (var fx = Privileges.OpenForWrite(ep + @"\FxProperties"))
                    {
                        if (fx == null) continue;
                        RemoveOldEfx(fx, backup);
                        foreach (var slot in new[] { Engine.PkeyMfx, Engine.PkeyGfx })
                            if (IsOurs(fx.GetValue(slot)))
                            {
                                // özgün efekti geri koy
                                if (fx.GetValue(Engine.PkeyChild) is string orig) fx.SetValue(slot, orig, RegistryValueKind.String);
                                else fx.DeleteValue(slot, false);
                            }
                        fx.DeleteValue(Engine.PkeyChild, false);
                        if (backup.GetValue("modes_added") is int added && added == 1 && fx.GetValue(Engine.PkeyMfx) == null)
                            fx.DeleteValue(PkeyMfxModes, false);
                        if (backup.GetValue("sysfx") is int sysfx)
                            using (var props = Privileges.OpenForWrite(ep + @"\Properties"))
                                props?.SetValue(PkeyDisableSysFx, sysfx, RegistryValueKind.DWord);
                    }
                }
            }
        }

        static void RestartAudio()
        {
            try
            {
                using (var sc = new ServiceController("Audiosrv"))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }
            }
            catch (Exception)
            {
                RunQuiet("net.exe", "stop audiosrv /y");
                RunQuiet("net.exe", "start audiosrv");
            }
        }

        // ------------------------------------------------------------ ortak bellek dosyası

        static void EnsureEngineFile()
        {
            Directory.CreateDirectory(Core.EngineDir);
            bool valid = false;
            try
            {
                using (var f = new FileStream(Core.EnginePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var head = new byte[8];
                    valid = f.Length >= Engine.FileSize && f.Read(head, 0, 8) == 8 &&
                            BitConverter.ToUInt32(head, 0) == Engine.Magic && BitConverter.ToUInt32(head, 4) == Engine.Layout;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!valid) File.WriteAllBytes(Core.EnginePath, Engine.InitialFile(new Settings()));

            // Arayüz (kullanıcı) ayar yazar, ses motoru (LOCAL SERVICE) seviye yazar
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            void Allow(WellKnownSidType t, FileSystemRights r) =>
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(t, null), r, AccessControlType.Allow));
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
            Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
            Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Read | FileSystemRights.Write);
            Allow(WellKnownSidType.LocalServiceSid, FileSystemRights.Read | FileSystemRights.Write);
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"),  // TÜM UYGULAMA PAKETLERİ
                                                       FileSystemRights.Read | FileSystemRights.Write, AccessControlType.Allow));
            File.SetAccessControl(Core.EnginePath, sec);
        }

        // ------------------------------------------------------------ dosyalar

        static byte[] ReadResource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        /// <summary>Dosyayı yeniler; kullanımdaysa (ses hizmeti DLL'i yüklemiş, arayüz açık) eskisini
        /// yeniden adlandırıp yerine yazar, eski kopya sonra silinir.</summary>
        static void ReplaceFile(string path, byte[] data)
        {
            if (File.Exists(path))
            {
                try { if (File.ReadAllBytes(path).SequenceEqual(data)) return; } catch (IOException) { }
                try { File.Delete(path); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    string old = path + "." + DateTime.Now.Ticks + ".old";
                    File.Move(path, old);
                    MoveFileEx(old, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
                }
            }
            File.WriteAllBytes(path, data);
        }

        static void CleanupOld()
        {
            if (!Directory.Exists(Core.InstallDir)) return;
            foreach (var f in Directory.GetFiles(Core.InstallDir, "*.old")) TryDelete(f);
        }

        static void DeleteOrSchedule(string path)
        {
            if (!File.Exists(path)) return;
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                string old = path + "." + DateTime.Now.Ticks + ".old";
                try { File.Move(path, old); path = old; } catch { }
                MoveFileEx(path, null, 4);
            }
        }

        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
            catch { }
            if (Directory.Exists(path)) MoveFileEx(path, null, 4);
        }

        static void CreateShortcut(string lnk)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var st = sc.GetType();
                void Set(string n, object v) => st.InvokeMember(n, BindingFlags.SetProperty, null, sc, new[] { v });
                Set("TargetPath", Core.InstalledExe);
                Set("WorkingDirectory", Core.InstallDir);
                Set("IconLocation", Core.InstalledExe + ",0");
                Set("Description", "Sistem geneli ekolayzer ve limiter");
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        static void RunQuiet(string exe, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args)
                       { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                }
            }
            catch { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string existing, string newName, int flags);
    }

    /// <summary>Ses aygıtı kayıt anahtarları TrustedInstaller/ses hizmetine aittir; yöneticinin yedekleme/geri
    /// yükleme ayrıcalıklarıyla (REG_OPTION_BACKUP_RESTORE) izinlere dokunmadan yazılır.</summary>
    static class Privileges
    {
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct TOKEN_PRIVILEGES { public int Count; public LUID Luid; public int Attr; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool LookupPrivilegeValue(string sys, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES np, int len, IntPtr prev, IntPtr retLen);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegCreateKeyEx(IntPtr hKey, string subKey, int reserved, string cls, int options, int sam,
                                         IntPtr sa, out SafeRegistryHandle result, out int disposition);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyEx(IntPtr hKey, string subKey, int options, int sam, out SafeRegistryHandle result);

        static readonly IntPtr HKLM = new IntPtr(unchecked((int)0x80000002));

        public static void Enable(params string[] names)
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8 /* ADJUST_PRIVILEGES | QUERY */, out var tok)) return;
            try
            {
                foreach (var n in names)
                {
                    if (!LookupPrivilegeValue(null, n, out var luid)) continue;
                    var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = 2 /* SE_PRIVILEGE_ENABLED */ };
                    AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
            finally { CloseHandle(tok); }
        }

        /// <summary>HKLM altındaki anahtarı yedekleme/geri yükleme ayrıcalığıyla yazılabilir açar.</summary>
        public static RegistryKey OpenForWrite(string subKey, bool create = false)
        {
            SafeRegistryHandle h;
            int rc;
            if (create)
                rc = RegCreateKeyEx(HKLM, subKey, 0, null, 4 /* REG_OPTION_BACKUP_RESTORE */, 0, IntPtr.Zero, out h, out _);
            else
            {
                rc = RegOpenKeyEx(HKLM, subKey, 0, 0x20019 | 0x20006 /* KEY_READ | KEY_WRITE */, out h);
                if (rc != 0)  // izin yoksa ayrıcalıkla aç (yalnızca var olan anahtar)
                {
                    h?.Dispose();
                    using (var probe = Registry.LocalMachine.OpenSubKey(subKey)) if (probe == null) return null;
                    rc = RegCreateKeyEx(HKLM, subKey, 0, null, 4, 0, IntPtr.Zero, out h, out _);
                }
            }
            if (rc != 0) { h?.Dispose(); return null; }
            return RegistryKey.FromHandle(h);
        }
    }
}
