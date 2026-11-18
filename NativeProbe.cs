using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PointDeviation
{
    /// <summary>
    /// Prova della libreria nativa PdRender (voce 180, Avanzate › Diagnostica › «Prova DirectX»). Verifica se durante il
    /// nostro disegno si può intercettare il contesto DirectX 11 di Revit, primo passo per disegnare i punti con shader
    /// propri (grandezza calcolata dalla scheda video a ogni fotogramma, nessun buffer da rifare allo zoom). Non disegna
    /// nulla: scrive nel registro («[DirectX]») cosa ha trovato, al massimo ogni 5 s.
    /// </summary>
    internal static class NativeProbe
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int PdInit(StringBuilder msg, int cap);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdCaptureBegin();
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int PdCaptureEnd(StringBuilder msg, int cap);

        private static int _state;          // 0 = da provare, 1 = attiva, -1 = non disponibile
        private static bool _capturing;
        private static long _lastLog;

        private static bool Ready()
        {
            if (_state != 0) return _state > 0;
            _state = -1;
            try
            {
                string dir = Path.GetDirectoryName(typeof(NativeProbe).Assembly.Location);
                string path = Path.Combine(dir, "PdRender.dll");
                if (!File.Exists(path)) { Log.Warn("DirectX", "PdRender.dll non trovata in " + dir + " (compilala con Native\\build.bat)"); return false; }
                if (LoadLibrary(path) == IntPtr.Zero) { Log.Warn("DirectX", "PdRender.dll non caricata, errore " + Marshal.GetLastWin32Error()); return false; }
                var sb = new StringBuilder(512);
                int ok = PdInit(sb, sb.Capacity);
                Log.Write("DirectX", "avvio: " + sb);
                _state = ok != 0 ? 1 : -1;
            }
            catch (Exception ex) { Log.Error("DirectX", ex); }
            return _state > 0;
        }

        /// <summary>Prima del nostro disegno: si registra il contesto che Revit usa per le nostre chiamate (ogni 5 s).</summary>
        public static void Begin()
        {
            _capturing = false;
            if (!AdvancedSettings.Current.NativeProbe) return;
            if (Environment.TickCount - _lastLog < 5000) return;
            if (!Ready()) return;
            try { PdCaptureBegin(); _capturing = true; } catch (Exception ex) { Log.Error("DirectX", ex); _state = -1; }
        }

        public static void End()
        {
            if (!_capturing) return;
            _capturing = false;
            _lastLog = Environment.TickCount;
            try
            {
                var sb = new StringBuilder(1024);
                PdCaptureEnd(sb, sb.Capacity);
                Log.Write("DirectX", sb.ToString());
            }
            catch (Exception ex) { Log.Error("DirectX", ex); _state = -1; }
        }
    }
}
