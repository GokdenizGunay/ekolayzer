// Ekolayzer ortak çekirdek: sürüm, bantlar, hazır ayarlar, ayar dosyası ve frekans yanıtı.
// Linux sürümündeki ekolayzer_core.py ile aynı değerler ve aynı ayar dosyası biçimi.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Ekolayzer
{
    sealed class LimiterParam
    {
        public string Key, Title;
        public double Min, Max, Default, Step;
        public bool Log;
        public Func<double, string> Format;
    }

    static class Core
    {
        public const string Version = "1.4.1";
        public const string AppId = "io.github.ekolayzer";
        public const string Repo = "GokdenizGunay/ekolayzer";

        public const double SampleRate = 48000;
        public static readonly int[] BandFreqs = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        public const double BandQ = 1.41;
        public const double GainMin = -12, GainMax = 12;
        public const string Custom = "Özel";

        public static readonly (string Name, double[] Gains)[] Presets =
        {
            ("Düz", new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            ("Bas Güçlendirme", new double[] { 7, 6, 5, 3, 1, 0, 0, 0, 0, 0 }),
            ("Bas Azaltma", new double[] { -6, -5, -4, -2, -1, 0, 0, 0, 0, 0 }),
            ("Tiz Güçlendirme", new double[] { 0, 0, 0, 0, 0, 1, 3, 5, 6, 7 }),
            ("Vokal", new double[] { -3, -2, -1, 1, 4, 5, 4, 2, 0, -1 }),
            ("Rock", new double[] { 5, 4, 2, -1, -2, -1, 2, 4, 5, 5 }),
            ("Pop", new double[] { -1, 1, 3, 4, 3, 0, -1, -1, 1, 2 }),
            ("Caz", new double[] { 3, 2, 1, 2, -1, -1, 0, 1, 3, 4 }),
            ("Klasik", new double[] { 4, 3, 2, 1, 0, 0, 0, 2, 3, 4 }),
            ("Elektronik", new double[] { 6, 5, 1, 0, -2, 1, 0, 2, 5, 6 }),
            ("Akustik", new double[] { 4, 3, 2, 1, 2, 2, 3, 3, 3, 2 }),
            ("Konuşma / Podcast", new double[] { -6, -4, -1, 2, 4, 4, 3, 1, -1, -3 }),
            ("Gece Modu", new double[] { -4, -3, -2, 0, 1, 2, 2, 1, -1, -2 }),
        };

        public static string FmtDb(double v) => Math.Abs(v) >= 0.05 ? v.ToString("+0.0;-0.0", Inv) + " dB" : "0.0 dB";
        static string FmtRatio(double v) => v >= 19.95 ? "∞:1" : v.ToString("0.0", Inv) + ":1";
        static string FmtMs(double v) => v < 100 ? v.ToString("0.0", Inv) + " ms" : Math.Round(v).ToString(Inv) + " ms";
        public static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        // Döner düğmeler (Linux sürümündeki KNOBS + LIMITER_PARAMS)
        public static readonly LimiterParam[] Limiter =
        {
            new LimiterParam { Key = "threshold", Title = "Eşik", Min = -30, Max = 0, Default = -1, Step = 0.1, Format = FmtDb },
            new LimiterParam { Key = "ratio", Title = "Oran", Min = 1, Max = 20, Default = 20, Step = 0.1, Log = true, Format = FmtRatio },
            new LimiterParam { Key = "knee", Title = "Diz", Min = 0, Max = 12, Default = 2, Step = 0.1, Format = v => v.ToString("0.0", Inv) + " dB" },
            new LimiterParam { Key = "attack", Title = "Atak", Min = 0.5, Max = 10, Default = 2, Step = 0.05, Log = true, Format = FmtMs },
            new LimiterParam { Key = "release", Title = "Bırakma", Min = 10, Max = 2000, Default = 120, Step = 1, Log = true, Format = FmtMs },
            new LimiterParam { Key = "ceiling", Title = "Tavan", Min = -12, Max = 0, Default = -0.3, Step = 0.1, Format = FmtDb },
        };

        public static LimiterParam Lim(string key) => Limiter.First(p => p.Key == key);

        public static double Clamp(double v, double lo, double hi) => double.IsNaN(v) ? lo : Math.Max(lo, Math.Min(hi, v));

        // ------------------------------------------------------------ yollar

        public static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ekolayzer");
        public static string SettingsPath => Path.Combine(ConfigDir, "settings.json");
        public static string LocalDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ekolayzer");
        public static string EngineDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ekolayzer");
        public static string EnginePath => Path.Combine(EngineDir, "engine.bin");
        public static string InstallDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ekolayzer");
        public static string InstalledExe => Path.Combine(InstallDir, "Ekolayzer.exe");
    }

    /// <summary>Kullanıcı ayarları (%APPDATA%\Ekolayzer\settings.json; Linux'taki biçimle aynı).</summary>
    sealed class Settings
    {
        public bool Enabled = true;
        public double Preamp;
        public double[] Gains = new double[10];
        public string Preset = "Düz";
        public SortedDictionary<string, double[]> CustomPresets = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
        public Dictionary<string, double> Limiter = Core.Limiter.ToDictionary(p => p.Key, p => p.Default);
        public bool LimiterPanel;
        public string UpdateSkip = "";  // "Tekrar gösterme" ile atlanan sürüm

        static double D(object o, double dflt)
        {
            try { return o == null ? dflt : Convert.ToDouble(o, Core.Inv); }
            catch { return dflt; }
        }

        static double[] Arr(object o, int n)
        {
            var r = new double[n];
            if (o is System.Collections.IEnumerable e && !(o is string))
            {
                int i = 0;
                foreach (var x in e) { if (i >= n) break; r[i++] = Core.Clamp(D(x, 0), Core.GainMin, Core.GainMax); }
            }
            return r;
        }

        public static Settings Load()
        {
            var s = new Settings();
            Dictionary<string, object> d = null;
            try
            {
                d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Core.SettingsPath, Encoding.UTF8))
                    as Dictionary<string, object>;
            }
            catch { }
            if (d == null) return s;
            if (d.TryGetValue("enabled", out var v) && v is bool b) s.Enabled = b;
            if (d.TryGetValue("preamp", out v)) s.Preamp = Core.Clamp(D(v, 0), Core.GainMin, Core.GainMax);
            if (d.TryGetValue("gains", out v)) s.Gains = Arr(v, Core.BandFreqs.Length);
            if (d.TryGetValue("preset", out v) && v is string ps) s.Preset = ps;
            if (d.TryGetValue("custom_presets", out v) && v is Dictionary<string, object> cp)
                foreach (var kv in cp) s.CustomPresets[kv.Key] = Arr(kv.Value, Core.BandFreqs.Length);
            if (d.TryGetValue("limiter", out v) && v is Dictionary<string, object> lim)
                foreach (var p in Core.Limiter)
                    if (lim.TryGetValue(p.Key, out var lv)) s.Limiter[p.Key] = Core.Clamp(D(lv, p.Default), p.Min, p.Max);
            if (d.TryGetValue("limiter_panel", out v) && v is bool lp) s.LimiterPanel = lp;
            if (d.TryGetValue("update_skip", out v) && v is string us) s.UpdateSkip = us;
            return s;
        }

        public void Save()
        {
            var lim = new Dictionary<string, object> { ["enabled"] = true };  // limiter her zaman açık
            foreach (var p in Core.Limiter) lim[p.Key] = Limiter[p.Key];
            var d = new Dictionary<string, object>
            {
                ["enabled"] = Enabled,
                ["preamp"] = Preamp,
                ["gains"] = Gains,
                ["preset"] = Preset,
                ["custom_presets"] = CustomPresets.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                ["limiter"] = lim,
                ["limiter_panel"] = LimiterPanel,
                ["update_skip"] = UpdateSkip ?? "",
            };
            Directory.CreateDirectory(Core.ConfigDir);
            string tmp = Core.SettingsPath + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(d), new UTF8Encoding(false));
            if (File.Exists(Core.SettingsPath)) File.Replace(tmp, Core.SettingsPath, null);
            else File.Move(tmp, Core.SettingsPath);
        }

        public IEnumerable<(string Name, double[] Gains)> AllPresets()
        {
            foreach (var p in Core.Presets) yield return p;
            foreach (var kv in CustomPresets) yield return (kv.Key, kv.Value);
        }

        public double[] FindPreset(string name) => AllPresets().Where(p => p.Name == name).Select(p => p.Gains).FirstOrDefault();
    }

    static class Dsp
    {
        static void PeakingCoeffs(double f0, double q, double gainDb, double fs,
                                  out double b0, out double b1, out double b2, out double a1, out double a2)
        {
            double a = Math.Pow(10, gainDb / 40.0), w0 = 2 * Math.PI * f0 / fs;
            double alpha = Math.Sin(w0) / (2 * q), cw = Math.Cos(w0);
            double a0 = 1 + alpha / a;
            b0 = (1 + alpha * a) / a0; b1 = -2 * cw / a0; b2 = (1 - alpha * a) / a0;
            a1 = -2 * cw / a0; a2 = (1 - alpha / a) / a0;
        }

        /// <summary>Tek bandın verilen frekanstaki kazancı (dB).</summary>
        public static double BandDb(int band, double gain, double freq, double fs = Core.SampleRate)
        {
            if (Math.Abs(gain) < 1e-6) return 0;
            PeakingCoeffs(Core.BandFreqs[band], Core.BandQ, gain, fs, out var b0, out var b1, out var b2, out var a1, out var a2);
            double w = 2 * Math.PI * freq / fs;
            double c1 = Math.Cos(w), s1 = -Math.Sin(w), c2 = Math.Cos(2 * w), s2 = -Math.Sin(2 * w);
            double nr = b0 + b1 * c1 + b2 * c2, ni = b1 * s1 + b2 * s2;
            double dr = 1 + a1 * c1 + a2 * c2, di = a1 * s1 + a2 * s2;
            double mag = Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
            return 20 * Math.Log10(Math.Max(mag, 1e-9));
        }
    }
}
