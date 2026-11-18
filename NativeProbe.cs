using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;

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
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int PdReport(StringBuilder msg, int cap);

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

        // Segnale (voce 181): un oggetto invisibile (1237 triangoli tutti con i tre vertici nello stesso punto) disegnato a
        // ogni fotogramma. La libreria lo riconosce dal numero di indici quando Revit lo manda alla scheda video: è il
        // momento in cui Revit disegna il nostro contenuto, con la sua destinazione e il suo buffer di profondità.
        private const int MarkerTris = 1237;
        private static VertexBuffer _mVb;
        private static IndexBuffer _mIb;
        private static VertexFormat _mFormat;
        private static EffectInstance _mEffect;
        private static int _frames;

        /// <summary>Durante il nostro disegno: segnale e (ogni 5 s) cattura delle chiamate fatte dentro il nostro disegno.</summary>
        public static void Begin()
        {
            _capturing = false;
            if (!AdvancedSettings.Current.NativeProbe) return;
            if (!Ready()) return;
            try
            {
                DrawMarker();
                _frames++;
                if (Environment.TickCount - _lastLog >= 5000) { PdCaptureBegin(); _capturing = true; }
            }
            catch (Exception ex) { Log.Error("DirectX", ex); _state = -1; }
        }

        private static void DrawMarker()
        {
            if (_mVb == null)
            {
                _mFormat = new VertexFormat(VertexFormatBits.PositionColored);
                _mEffect = new EffectInstance(VertexFormatBits.PositionColored);
                int fl = 3 * VertexPositionColored.GetSizeInFloats();
                var vb = new VertexBuffer(fl);
                vb.Map(fl);
                var c = new ColorWithTransparency(0, 0, 0, 255);
                vb.GetVertexStreamPositionColored().AddVertices(new List<VertexPositionColored>
                {
                    new VertexPositionColored(XYZ.Zero, c), new VertexPositionColored(XYZ.Zero, c), new VertexPositionColored(XYZ.Zero, c)
                });
                vb.Unmap();
                int sh = MarkerTris * IndexTriangle.GetSizeInShortInts();
                var ib = new IndexBuffer(sh);
                ib.Map(sh);
                IndexStreamTriangle st = ib.GetIndexStreamTriangle();
                for (int i = 0; i < MarkerTris; i++) st.AddTriangle(new IndexTriangle(0, 1, 2));
                ib.Unmap();
                _mVb = vb; _mIb = ib;
            }
            DrawContext.FlushBuffer(_mVb, 3, _mIb, MarkerTris * IndexTriangle.GetSizeInShortInts(), _mFormat, _mEffect,
                PrimitiveType.TriangleList, 0, MarkerTris);
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
                var rb = new StringBuilder(1024);
                PdReport(rb, rb.Capacity);
                Log.Write("DirectX", string.Format("{0} fotogrammi nostri; segnale: {1}; dentro il nostro disegno: {2}", _frames, rb, sb));
                _frames = 0;
            }
            catch (Exception ex) { Log.Error("DirectX", ex); _state = -1; }
        }
    }
}
