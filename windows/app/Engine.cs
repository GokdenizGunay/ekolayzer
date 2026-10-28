// Ses motoruyla (APO) bağlantı: ortak bellek dosyası üzerinden ayar gönderme ve seviye okuma.
// Düzen apo/ekz_shared.h ile birebir aynıdır.
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace Ekolayzer
{
    unsafe sealed class Engine : IDisposable
    {
        public const uint Magic = 0x315A4B45;  // "EKZ1"
        public const uint Layout = 1;
        public const int FileSize = 4096;
        const int OffSeq = 8, OffParams = 16, OffRate = 128, OffCount = 132, OffAlive = 152, OffSlots = 256;
        const int Slots = 64, SlotSize = 16;

        MemoryMappedFile mmf;
        MemoryMappedViewAccessor view;
        byte* p;
        long nextOpen;

        public bool Open
        {
            get
            {
                if (p != null) return true;
                long now = Environment.TickCount;
                if (now - nextOpen < 0) return false;
                nextOpen = now + 1000;
                TryOpen();
                return p != null;
            }
        }

        void TryOpen()
        {
            try
            {
                var fs = new FileStream(Core.EnginePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < FileSize) { fs.Dispose(); return; }
                mmf = MemoryMappedFile.CreateFromFile(fs, null, FileSize, MemoryMappedFileAccess.ReadWrite, null,
                                                      HandleInheritability.None, false);
                view = mmf.CreateViewAccessor(0, FileSize, MemoryMappedFileAccess.ReadWrite);
                byte* b = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref b);
                if (*(uint*)b != Magic || *(uint*)(b + 4) != Layout) { Dispose(); return; }
                p = b;
            }
            catch (Exception) { Dispose(); }
        }

        public void Dispose()
        {
            if (p != null) { view.SafeMemoryMappedViewHandle.ReleasePointer(); p = null; }
            view?.Dispose(); view = null;
            mmf?.Dispose(); mmf = null;
        }

        /// <summary>Ayarları motora gönderir (motor bir sonraki ses bloğunda uygular; ses kesilmez).</summary>
        public bool Apply(Settings s)
        {
            if (!Open) return false;
            int* seq = (int*)(p + OffSeq);
            Interlocked.Increment(ref *seq);  // tek: yazım sürüyor
            WriteParams(p + OffParams, s);
            Interlocked.Increment(ref *seq);  // çift: tamam
            return true;
        }

        public static void WriteParams(byte* q, Settings s)
        {
            *(uint*)q = s.Enabled ? 1u : 0u;
            float* f = (float*)(q + 4);
            f[0] = (float)s.Preamp;
            for (int i = 0; i < 10; i++) f[1 + i] = (float)s.Gains[i];
            f[11] = (float)s.Limiter["threshold"];
            f[12] = (float)s.Limiter["ratio"];
            f[13] = (float)s.Limiter["knee"];
            f[14] = (float)s.Limiter["attack"];
            f[15] = (float)s.Limiter["release"];
            f[16] = (float)s.Limiter["ceiling"];
        }

        /// <summary>Kurulumun oluşturduğu başlangıç dosyası içeriği.</summary>
        public static byte[] InitialFile(Settings s)
        {
            var b = new byte[FileSize];
            fixed (byte* q = b)
            {
                *(uint*)q = Magic;
                *(uint*)(q + 4) = Layout;
                *(int*)(q + OffSeq) = 2;
                WriteParams(q + OffParams, s);
            }
            return b;
        }

        // ------------------------------------------------------------ seviyeler

        int? last;
        long lastNew;

        /// <summary>Son okumadan bu yana gelen dilimlerin en büyükleri (sol, sağ, kazanç azaltma dB) ya da null.</summary>
        public (float L, float R, float Gr)? Poll()
        {
            if (!Open) return null;
            long now = Environment.TickCount;
            int count = Volatile.Read(ref *(int*)(p + OffCount));
            if (last == null) { last = count; lastNew = now; return null; }
            int n = unchecked(count - last.Value);
            if (n <= 0) return null;
            n = Math.Min(n, Slots);
            float l = 0, r = 0, gr = 0;
            for (int k = count - n; k < count; k++)
            {
                float* sl = (float*)(p + OffSlots + ((uint)k % Slots) * SlotSize);
                l = Math.Max(l, sl[0]); r = Math.Max(r, sl[1]); gr = Math.Max(gr, sl[3]);
            }
            last = count; lastNew = now;
            return (l, r, gr);
        }

        public int Rate => Open ? *(int*)(p + OffRate) : 0;

        // ------------------------------------------------------------ durum

        public const string Clsid = "{249FC38E-317D-4074-9AC1-A639059256EC}";
        public const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        public const string PkeyEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";

        public const string PkeyGfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2";
        public const string PkeyMfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6";
        public const string PkeyChild = "{249fc38e-317d-4074-9ac1-a639059256ec},1";  // zincirlenen özgün efekt

        /// <summary>Aygıtın karıştırma sonrası efekt yuvası: yeni düzende MFX, eski sürücülerde GFX;
        /// hiç efekt kaydı yoksa (sanal aygıt) null.</summary>
        public static string Slot(RegistryKey fx)
        {
            bool Has(int pid) => fx.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}," + pid) != null;
            if (Has(5) || Has(6) || Has(7)) return PkeyMfx;
            if (Has(1) || Has(2)) return PkeyGfx;
            return null;
        }

        /// <summary>Varsayılan çıkış aygıtının adı ve ses motorunun o aygıtta kayıtlı olup olmadığı.</summary>
        public static (string Name, bool Registered, bool Supported) DefaultDevice()
        {
            string id = DefaultRenderId();
            if (id == null) return (null, false, false);
            string guid = id.Substring(id.LastIndexOf('.') + 1);
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(RenderKey + "\\" + guid))
                {
                    string name = k?.OpenSubKey("Properties")?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                    using (var fx = k?.OpenSubKey("FxProperties"))
                    {
                        string slot = fx == null ? null : Slot(fx);
                        if (slot == null) return (name, false, false);
                        return (name, string.Equals(fx.GetValue(slot) as string, Clsid, StringComparison.OrdinalIgnoreCase), true);
                    }
                }
            }
            catch { return (null, false, false); }
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            int Activate(ref Guid iid, int ctx, IntPtr p, out IntPtr o);
            int OpenPropertyStore(int access, out IntPtr store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        public static string DefaultRenderId()
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var dev) != 0 || dev == null) return null;
                return dev.GetId(out var id) == 0 ? id : null;
            }
            catch { return null; }
        }
    }
}
