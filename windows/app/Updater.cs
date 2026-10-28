// Güncelleme denetimi: GitHub'daki son sürümü bulur, kurulum dosyasını indirip SHA-256 ile doğrular
// ve UAC onayıyla kurar. Linux sürümündeki ekolayzer_update.py ile aynı akış.
// Bu sınıf Windows Forms kullanmaz; oturum açılışındaki arka plan denetimi güncelleme yoksa
// arayüz kitaplıklarını hiç yüklemeden biter.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Ekolayzer
{
    sealed class UpdateInfo
    {
        public string Version, Current, Name, Page, AssetUrl, AssetName, Sha256;
        public long Size;
        public List<string> Summary = new List<string>();
    }

    sealed class NetworkException : Exception
    {
        public NetworkException(string m, Exception inner = null) : base(m, inner) { }
    }

    static class Updater
    {
        // Test için EKOLAYZER_UPDATE_URL ile başka bir adres verilebilir.
        public static string ApiUrl =>
            Environment.GetEnvironmentVariable("EKOLAYZER_UPDATE_URL") ?? $"https://api.github.com/repos/{Core.Repo}/releases/latest";
        static string UserAgent => "ekolayzer-windows/" + Core.Version;
        static readonly Regex AssetRe = new Regex(@"^ekolayzer-setup-[\w.~+-]+\.exe$", RegexOptions.IgnoreCase);

        static Updater()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288 /* TLS 1.3 */;
        }

        public static int[] ParseVersion(string text)
        {
            var m = Regex.Match(text ?? "", @"^\s*v?(\d+(?:\.\d+)*)");
            return m.Success ? m.Groups[1].Value.Split('.').Select(int.Parse).ToArray() : null;
        }

        public static int Compare(int[] a, int[] b)
        {
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        static List<string> Summary(string body, int limit = 6)
        {
            return (body ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- "))
                .Select(l => Regex.Replace(l.Substring(2).Trim(), @"\*\*|`", "")).Take(limit).ToList();
        }

        static HttpWebRequest Request(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Accept = "application/vnd.github+json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return req;
        }

        /// <summary>Yüklü sürümden yeni bir sürüm varsa bilgisini, yoksa null döndürür.</summary>
        public static UpdateInfo Check(int timeoutMs = 10000)
        {
            Dictionary<string, object> rel;
            try
            {
                using (var r = (HttpWebResponse)Request(ApiUrl, timeoutMs).GetResponse())
                using (var sr = new StreamReader(r.GetResponseStream()))
                    rel = new JavaScriptSerializer { MaxJsonLength = 8 << 20 }.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
            }
            catch (WebException e) when (e.Response is HttpWebResponse hr && hr.StatusCode == HttpStatusCode.NotFound)
            {
                return null;  // henüz sürüm yok
            }
            catch (Exception e) when (e is WebException || e is IOException || e is ArgumentException || e is InvalidOperationException)
            {
                throw new NetworkException(e.Message, e);
            }
            if (rel == null) return null;
            string S(string k) => rel.TryGetValue(k, out var v) ? v as string : null;
            if (rel.TryGetValue("draft", out var dr) && dr is bool d1 && d1) return null;
            if (rel.TryGetValue("prerelease", out var pr) && pr is bool p1 && p1) return null;

            string tag = S("tag_name") ?? "";
            var nv = ParseVersion(tag);
            var cv = ParseVersion(Core.Version);
            if (nv == null || cv == null || Compare(nv, cv) <= 0) return null;

            Dictionary<string, object> asset = null;
            if (rel.TryGetValue("assets", out var av) && av is object[] assets)
                asset = assets.OfType<Dictionary<string, object>>()
                    .FirstOrDefault(a => a.TryGetValue("name", out var n) && n is string ns && AssetRe.IsMatch(ns));
            string sha = null, aname = null, aurl = null;
            long size = 0;
            if (asset != null)
            {
                aname = asset["name"] as string;
                aurl = asset.TryGetValue("browser_download_url", out var u) ? u as string : null;
                size = asset.TryGetValue("size", out var sz) ? Convert.ToInt64(sz) : 0;
                string digest = asset.TryGetValue("digest", out var dg) ? dg as string ?? "" : "";
                if (digest.StartsWith("sha256:")) sha = digest.Substring(7).ToLowerInvariant();
                else  // sürüm notlarındaki "<özet>  <dosya>" satırı
                {
                    var m = Regex.Match(S("body") ?? "", @"([0-9a-fA-F]{64})\s+" + Regex.Escape(aname));
                    sha = m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
                }
            }
            return new UpdateInfo
            {
                Version = string.Join(".", nv),
                Current = Core.Version,
                Name = S("name") ?? tag,
                Page = S("html_url") ?? $"https://github.com/{Core.Repo}/releases",
                Summary = Summary(S("body")),
                AssetUrl = sha != null ? aurl : null,
                AssetName = aname,
                Size = size,
                Sha256 = sha,
            };
        }

        public static string CacheDir => Path.Combine(Core.LocalDir, "updates");

        /// <summary>Kurulum dosyasını indirir, SHA-256 özetini doğrular ve yolunu döndürür.</summary>
        public static string Download(UpdateInfo info, Action<double> progress = null)
        {
            Directory.CreateDirectory(CacheDir);
            string path = Path.Combine(CacheDir, Path.GetFileName(info.AssetName));
            string tmp = path + ".part";
            string hex;
            try
            {
                var req = Request(info.AssetUrl, 30000);
                req.Accept = "application/octet-stream";
                using (var r = req.GetResponse())
                using (var src = r.GetResponseStream())
                using (var f = File.Create(tmp))
                using (var sha = SHA256.Create())
                {
                    long total = r.ContentLength > 0 ? r.ContentLength : info.Size, done = 0;
                    var buf = new byte[65536];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        f.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        done += n;
                        if (total > 0) progress?.Invoke((double)done / total);
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception e) when (e is WebException || e is IOException)
            {
                throw new NetworkException(e.Message, e);
            }
            if (hex != info.Sha256)
            {
                File.Delete(tmp);
                throw new InvalidDataException("İndirilen dosyanın SHA-256 özeti eşleşmiyor; güncelleme iptal edildi.");
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        /// <summary>Yönetici onayıyla (UAC) kurar. (başarılı mı, ileti) döndürür.</summary>
        public static (bool Ok, string Message) Install(string path, string sha256)
        {
            string result = Path.Combine(CacheDir, "result.txt");
            try { File.Delete(result); } catch { }
            // Kullanıcı klasöründeki dosya doğrudan yönetici olarak başlatılmaz (Windows buna izin vermeyebilir):
            // kurulu Ekolayzer yönetici olarak açılır, dosyayı korumalı klasöre kopyalayıp özetini yeniden doğrular.
            string helper = File.Exists(Core.InstalledExe) ? Core.InstalledExe : System.Windows.Forms.Application.ExecutablePath;
            int code = Elevation.RunElevated(helper, $"--apply-update \"{path}\" --sha256={sha256} \"--result={result}\"", out string err);
            if (code == Elevation.Cancelled) return (false, "Yönetici onayı verilmedi; güncelleme kurulmadı.");
            if (code < 0) return (false, err);
            string msg = File.Exists(result) ? File.ReadAllText(result).Trim() : "";
            if (code == 0)
            {
                try { File.Delete(path); } catch { }
                return (true, "");
            }
            return (false, msg.Length > 0 ? msg : $"Kurulum başarısız oldu (çıkış kodu {code}).");
        }
    }

    static class Elevation
    {
        public static string Sha256(string path)
        {
            using (var f = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Yönetici tarafı (Linux'taki ekolayzer-update-install karşılığı): indirilen kurulumu
        /// yalnızca yöneticinin yazabildiği klasöre kopyalar, SHA-256 özetini doğrular ve çalıştırır.</summary>
        public static int ApplyUpdate(string src, string sha256, string result)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "Ekolayzer");
            Directory.CreateDirectory(dir);
            string copy = Path.Combine(dir, "ekolayzer-setup.exe");
            try
            {
                File.Copy(src, copy, true);
                if (string.IsNullOrEmpty(sha256) || Sha256(copy) != sha256.ToLowerInvariant())
                {
                    File.WriteAllText(result, "Kurulum dosyasının SHA-256 özeti eşleşmiyor; güncelleme iptal edildi.");
                    return 4;
                }
                using (var p = Process.Start(new ProcessStartInfo(copy, $"--install --silent --update \"--result={result}\"")
                                             { UseShellExecute = false, CreateNoWindow = true }))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            finally
            {
                try { File.Delete(copy); } catch { }
            }
        }

        public const int Cancelled = -2;

        /// <summary>Programı UAC ile yönetici olarak çalıştırıp bitmesini bekler; çıkış kodunu döndürür.</summary>
        public static int RunElevated(string exe, string args, out string error)
        {
            // ShellExecute (UAC) iş havuzu (MTA) iş parçacığında "yol bulunamadı" ile başarısız olabilir:
            // her zaman ayrı bir STA iş parçacığından çağır.
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
            {
                int code = -1;
                string err = null;
                var t = new System.Threading.Thread(() => code = RunElevated(exe, args, out err));
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start();
                t.Join();
                error = err;
                return code;
            }
            error = null;
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args)
                       { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe) }))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                return Cancelled;  // kullanıcı UAC penceresinde "Hayır" dedi
            }
            catch (Exception e)
            {
                error = e.Message;
                return -1;
            }
        }

        public static bool IsAdmin()
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
