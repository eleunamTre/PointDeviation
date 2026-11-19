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
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetTest(int on, float[] viewProj, float[] cubePos);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetCameraInfo(float[] e, float[] d, float[] r, float target, float halfW, int persp);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int PdDump(StringBuilder msg, int cap);
        private static float[] F3(XYZ p) { return new[] { (float)p.X, (float)p.Y, (float)p.Z }; }

        private static readonly float[] _vp = new float[16];
        private static float[] _cube;

        /// <summary>
        /// Voce 189: matrice dal modello allo schermo per lo shader di prova, dalla camera del fotogramma (stessa usata dal
        /// motore). Convenzione: vettore riga per matrice, riga per riga (row_major nello shader). Prospettiva: ampiezza
        /// HalfW/HalfH alla distanza Target; parallela: ampiezza HalfW/HalfH attorno a Pos.
        /// </summary>
        private static void SendCamera(ViewCam cam, XYZ cubeAt)
        {
            if (cam == null || !cam.Valid) return;
            XYZ R = cam.Right, U = cam.Up, D = cam.Dir, E = cam.Pos;
            double er = E.DotProduct(R), eu = E.DotProduct(U), ed = E.DotProduct(D);
            var m = new double[16];
            if (cam.Perspective)
            {
                double sx = cam.Target / cam.HalfW, sy = cam.Target / cam.HalfH;
                double ox = -cam.OffU / cam.HalfW, oy = -cam.OffV / cam.HalfH;
                double n = 0.5, f = 100000.0, A = f / (f - n), B = -n * f / (f - n);
                // colonne: x, y, z, w
                double[] cx = { R.X * sx + D.X * ox, R.Y * sx + D.Y * ox, R.Z * sx + D.Z * ox, -er * sx - ed * ox };
                double[] cy = { U.X * sy + D.X * oy, U.Y * sy + D.Y * oy, U.Z * sy + D.Z * oy, -eu * sy - ed * oy };
                double[] cz = { D.X * A, D.Y * A, D.Z * A, -ed * A + B };
                double[] cw = { D.X, D.Y, D.Z, -ed };
                for (int r = 0; r < 4; r++) { m[r * 4] = cx[r]; m[r * 4 + 1] = cy[r]; m[r * 4 + 2] = cz[r]; m[r * 4 + 3] = cw[r]; }
            }
            else
            {
                double sx = 1.0 / cam.HalfW, sy = 1.0 / cam.HalfH;
                double[] cx = { R.X * sx, R.Y * sx, R.Z * sx, -er * sx };
                double[] cy = { U.X * sy, U.Y * sy, U.Z * sy, -eu * sy };
                for (int r = 0; r < 4; r++) { m[r * 4] = cx[r]; m[r * 4 + 1] = cy[r]; m[r * 4 + 2] = 0; m[r * 4 + 3] = 0; }
                m[14] = 0.5; m[15] = 1;
            }
            for (int i = 0; i < 16; i++) _vp[i] = (float)m[i];
            if (_cube == null)
            {
                // voce 192: cubo fisso nel centro dell'ingombro della nuvola (prima nel punto guardato al primo fotogramma, che
                // nelle viste parallele poteva essere la posizione della camera, fuori dal modello)
                XYZ p = cubeAt ?? (cam.Perspective ? E + D * cam.Target : E);
                _cube = new[] { (float)p.X, (float)p.Y, (float)p.Z };
                Log.Write("DirectX", string.Format("cubo di prova in {0:F1}, {1:F1}, {2:F1} piedi", p.X, p.Y, p.Z));
            }
            PdSetTest(1, _vp, _cube);
            // voce 190: dati della camera per riconoscere la matrice di Revit nei suoi buffer
            PdSetCameraInfo(F3(E), F3(D), F3(R), (float)cam.Target, (float)cam.HalfW, cam.Perspective ? 1 : 0);
        }

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
        private static XYZ _mPos;

        private static void RebuildMarker(XYZ p)
        {
            int fl = 3 * VertexPositionColored.GetSizeInFloats();
            var vb = new VertexBuffer(fl);
            vb.Map(fl);
            var c = new ColorWithTransparency(0, 0, 0, 255);
            vb.GetVertexStreamPositionColored().AddVertices(new List<VertexPositionColored>
            {
                new VertexPositionColored(p, c), new VertexPositionColored(p, c), new VertexPositionColored(p, c)
            });
            vb.Unmap();
            if (_mVb != null) _mVb.Dispose();
            _mVb = vb;
            _mPos = p;
        }

        /// <summary>Durante il nostro disegno: segnale e (ogni 5 s) cattura delle chiamate fatte dentro il nostro disegno.</summary>
        public static void Begin(ViewCam cam, XYZ cubeAt)
        {
            _capturing = false;
            if (!AdvancedSettings.Current.NativeProbe) return;
            if (!Ready()) return;
            try
            {
                if (Environment.TickCount - _lastLog >= 5000) { PdCaptureBegin(); _capturing = true; }
                SendCamera(cam, cubeAt);
                DrawMarker();
                _frames++;
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
            // voce 187: il segnale si sposta davanti alla camera (prima era nell'origine, forse scartato prima del disegno)
            try
            {
                Camera cam = DrawContext.GetCamera();
                if (cam != null)
                {
                    XYZ p = cam.EyePosition + cam.ViewDirection.Normalize() * 10.0;
                    if (_mPos == null || !_mPos.IsAlmostEqualTo(p, 1.0)) { RebuildMarker(p); }
                }
            }
            catch { }
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
                var rb = new StringBuilder(4096);
                PdReport(rb, rb.Capacity);
                Log.Write("DirectX", string.Format("{0} fotogrammi nostri; segnale: {1}; dentro il nostro disegno: {2}", _frames, rb, sb));
                var db = new StringBuilder(8192);
                if (PdDump(db, db.Capacity) != 0) Log.Write("DirectX", "buffer di Revit: " + db);
                _frames = 0;
            }
            catch (Exception ex) { Log.Error("DirectX", ex); _state = -1; }
        }
    }
}
