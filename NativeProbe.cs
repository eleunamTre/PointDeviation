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
        private static extern void PdSetFocus(float[] p);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetAlpha(float a);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetPointAlpha(int on);
        private static bool _ptAlphaMissing;
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetModelMesh(float[] xyz, int tris, double[] origin);
        private static bool _meshMissing;
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetMeshTolerance(int on);
        private static bool _meshTolMissing;
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetFlatBeaconFix(int on);
        private static bool _flatFixMissing;
        private static object _meshSent;
        private static bool _meshAny;
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetCameraInfo(float[] e, float[] d, float[] r, float target, float halfW, int persp);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetView(long viewId);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdViewOff(long viewId);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int PdShaderUpload(int layer, int node, byte[] data, int count);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdShaderFree(int layer, int node);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdShaderAdd(int layer, int node, int count, float spacing, float grow);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdShaderEndFrame(int on, float maxPx, float scale, int wanted);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetLinks(float[] m, int n);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetCloudTransforms(double[] m, int n);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdTraceOn(int on);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int PdTraceTake(byte[] buf, int cap);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int PdVideoMem(out double usedMB, out double budgetMB);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int PdShaderReport(StringBuilder msg, int cap);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int PdShaderReady();
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdSetClip(int on, float[] planes);
        [DllImport("PdRender.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern float PdShaderGpuMs();

        // ---------------------------------------------------------------- voce 201: limite di punti dello shader
        // Il limite segue il tempo che la scheda video impiega davvero a disegnare i punti (query di tempo DirectX). Prima
        // (199-200) il limite era fisso o seguiva il tempo misurato dal plugin, che non comprende il disegno: si arrivava a
        // 7-17 M punti per fotogramma, l'orbita era lenta e Revit saltava fotogrammi (nuvola che spariva e ricompariva).
        private const double GpuTargetMs = 8.0;
        private static double _cap = 4000000;
        private static long _capAdjust;
        private static double _intEma = -1;
        public static int NotReadyFrames;
        public static double GpuMs { get { try { return PdShaderGpuMs(); } catch { return -1; } } }
        /// <summary>Punti da disegnare in questo fotogramma: lo stesso limite adattivo in movimento e da fermi (voce 238).</summary>
        public static long ShaderCap(bool moving)
        {
            long now = Environment.TickCount;
            double g = GpuMs;
            // voce 204: conta anche il fotogramma intero. Senza riquadro di sezione Revit disegna tutto il modello
            // (13 mila chiamate per fotogramma contro 1.200) e scende a 3-4 fotogrammi al secondo anche con i nostri
            // punti a 8 ms: la scheda video e' piena di lavoro di Revit e si lascia spazio a lui.
            double fi = FrameStats.LastMovingIntervalMs;
            _intEma = fi <= 0 ? _intEma : (_intEma < 0 ? fi : _intEma * 0.7 + fi * 0.3);
            if (moving && g > 0 && now - _capAdjust > 250)
            {
                _capAdjust = now;
                // voce 207: la regola della 204 (scendere quando il fotogramma intero e' lento) e' tolta. Registro del 19/11
                // 20:19: senza riquadro i fotogrammi restavano a 120-380 ms anche con soli 0,4 M punti a 1,9 ms di scheda
                // video, quindi la lentezza era tutta di Revit; intanto la nuvola restava bloccata a 0,4 M punti e da
                // lontano quasi non si vedeva. Il limite segue solo il tempo dei nostri punti, con un minimo di 1 M.
                // voce 228: passi piu' piccoli e fascia piu' larga. Registro del 20/11 (trasparenza 42%): con la regola di
                // prima il limite saltava da 2,5 a 8 M in pochi secondi (scheda video 4-14 ms) e la densita' cambiava a
                // ogni zoom. Minimo 3 M: sotto, da lontano la nuvola e' troppo rada.
                if (g > GpuTargetMs * 1.5) _cap *= 0.9;
                else if (g < GpuTargetMs * 0.75) _cap *= 1.05;
                _cap = Math.Max(3000000, Math.Min(8000000, _cap));
            }
            // voce 228: da fermi una volta e mezza il limite in movimento (era il doppio). Il livello di dettaglio si sceglie
            // con questo limite: col doppio, a ogni pausa dello zoom si passava a blocchi piu' fini da caricare (intanto
            // l'antenato, piu' rado) e si vedeva la nuvola rada che poi «andava a fuoco»; con la trasparenza, dove il
            // limite in movimento e' piu' basso, il salto era piu' evidente.
            // voce 238: stesso limite in movimento e da fermi. Con la 228 da fermi era una volta e mezza: diario del 21/11,
            // in movimento si disegnava il 45-49% dei punti chiesti (17 M chiesti, 8 M di limite) e da fermi tutti, e la
            // nuvola cambiava aspetto a ogni pausa («due visualizzazioni»).
            return (long)_cap;
        }
        // voce 239: trasformazioni inverse dei modelli collegati (dal progetto al collegamento), 16 float per riga ciascuna
        public static void SetLinks(float[] m, int n) { if (_state > 0) try { PdSetLinks(m, n); } catch { } }
        /// <summary>Voce 251: trasformazioni delle istanze delle nostre nuvole (dalla nuvola al progetto), per il faro.</summary>
        public static void SetCloudTransforms(double[] m, int n) { if (_state > 0) try { PdSetCloudTransforms(m, n); } catch { } }
        // voce 234: diario dei fotogrammi (vedi FrameDiary)
        public static bool Loaded { get { return _state > 0; } }
        public static void TraceOn(bool on) { if (_state > 0) try { PdTraceOn(on ? 1 : 0); } catch { } }
        public static int TraceTake(byte[] buf) { if (_state <= 0) return 0; try { return PdTraceTake(buf, buf.Length); } catch { return 0; } }
        public static bool VideoMem(out double used, out double budget)
        {
            used = budget = 0;
            if (_state <= 0) return false;
            try { return PdVideoMem(out used, out budget) != 0; } catch { return false; }
        }
        /// <summary>Voce 229: frazione dei punti chiesti disegnata nell'ultimo fotogramma (1 = tutti).</summary>
        public static double LastScale = 1;
        public static string CapNote { get { return string.Format("limite punti in movimento {0:F1} M, scheda video {1:F1} ms per fotogramma (obiettivo {2:F0} ms), fotogramma intero in movimento {4:F0} ms; punti chiesti disegnati nell'ultimo fotogramma {5:P0}; fotogrammi con la libreria non pronta {3}", _cap / 1e6, GpuMs, GpuTargetMs, NotReadyFrames, _intEma, LastScale); } }

        // ---------------------------------------------------------------- voce 194: disegno con shader
        /// <summary>Passaggio di Revit in cui si disegna (voce 199), scritto nel registro con il resoconto DirectX.</summary>
        public static string PassNote = "";

        /// <summary>Disegno con shader acceso nelle impostazioni (implica l'aggancio a DirectX).</summary>
        public static bool ShaderWanted { get { return AdvancedSettings.Current.NativeShader; } }
        /// <summary>La libreria è pronta a disegnare: dispositivo di Revit e sua matrice trovati.</summary>
        public static bool ShaderReady
        {
            get { try { return ShaderWanted && _state > 0 && PdShaderReady() != 0; } catch { return false; } }
        }
        public static bool Upload(int layer, int node, byte[] data, int count)
        {
            try { return PdShaderUpload(layer, node, data, count) != 0; } catch (Exception ex) { Log.Error("DirectX", ex); return false; }
        }
        /// <summary>
        /// Voce 272: mesh del modello (dall'analisi del documento della vista) per la profondità disegnata dalla libreria
        /// nelle immagini intermedie. Si invia solo quando cambia; null la toglie.
        /// </summary>
        public static void SetModelMesh(TriangleBvh bvh)
        {
            if (bvh != null && bvh.DrawVerts == null) bvh = null;
            SetModelMesh(bvh, bvh != null ? bvh.DrawVerts : null, bvh != null ? bvh.DrawOrigin : null, "dell'analisi");
        }
        /// <summary>Voce 278: mesh qualsiasi (owner = oggetto che la identifica: si invia solo quando cambia).</summary>
        public static void SetModelMesh(object owner, float[] verts, double[] origin, string what)
        {
            if (_meshMissing || _state <= 0) return;
            if (verts == null || origin == null) owner = null;
            if (ReferenceEquals(owner, _meshSent) && (owner != null || !_meshAny)) return;
            try
            {
                if (owner != null) PdSetModelMesh(verts, verts.Length / 9, origin);
                else PdSetModelMesh(null, 0, null);
                _meshSent = owner; _meshAny = owner != null;
                Log.Write("DirectX", owner != null ? string.Format("mesh del modello per la profondità in movimento ({0}): {1:N0} triangoli (voce 272, 278)", what, verts.Length / 9) : "mesh del modello per la profondità tolta (voce 272)");
            }
            catch (EntryPointNotFoundException) { _meshMissing = true; Log.Write("DirectX", "PdRender.dll senza PdSetModelMesh: ricompilare la libreria nativa (voce 272)"); }
            catch (Exception ex) { Log.Error("DirectX", ex); }
        }
        /// <summary>Voce 200: piani di ritaglio della vista (6 x nx, ny, nz, d) o null.</summary>
        public static void SetClip(float[] planes)
        {
            try { PdSetClip(planes != null ? 1 : 0, planes ?? new float[24]); } catch { }
        }
        public static void Free(int layer, int node) { try { PdShaderFree(layer, node); } catch { } }
        /// <summary>Voce 223: il plugin non disegna piu' in questa vista; se e' quella della libreria, elenco vuoto.</summary>
        public static void ViewOff(long viewId) { if (_state > 0) try { PdViewOff(viewId); } catch { } }
        public static void Add(int layer, int node, int count, float spacing, float grow = 1f) { try { PdShaderAdd(layer, node, count, spacing, grow); } catch { } }
        /// <summary>Fine del fotogramma: l'elenco dei blocchi passa al disegno. Raggio massimo come «Dimensione punti» (100% ≈ 5 px).</summary>
        public static void EndFrame(Style st, bool on)
        {
            try
            {
                // voce 197: raggio massimo come il motore di sempre (100% = circa 5 px di diametro); con la 194 era 1,6 volte
                // tanto e i punti risultavano piu' grandi del normale
                float maxPx = (float)Math.Max(1.0, 2.5 * Math.Max(st.PointScale, 20) / 100.0);
                // voce 217: trasparenza e X-ray come nel disegno di sempre (stessa correzione per i punti sovrapposti)
                // voce 231: con la trasparenza la nuvola sembra piena dove molti punti si sovrappongono nello stesso pixel
                // (da lontano, VistaIniziale): disegnandone una frazione in movimento (LastScale) la sovrapposizione cala e
                // la nuvola sembra piu' rada e chiara, poi da ferma «si riempie». Si compensa l'opacita' del singolo punto:
                // con n strati l'opacita' e' 1-(1-a)^n, con f*n strati serve a' = 1-(1-a)^(1/f).
                double a = st.NeedsTransparentPass ? 1.0 - st.DrawTransparency : 1.0;
                if (a < 1 && LastScale > 0 && LastScale < 1) a = 1 - Math.Pow(1 - a, 1 / LastScale);
                PdSetAlpha((float)Math.Min(1.0, a));
                // voce 259: punti dei link trasparenti (opacità per punto); con una libreria vecchia si va avanti senza
                if (!_ptAlphaMissing)
                    try { PdSetPointAlpha(st.PointAlpha ? 1 : 0); }
                    catch (EntryPointNotFoundException) { _ptAlphaMissing = true; Log.Write("DirectX", "PdRender.dll senza PdSetPointAlpha: ricompilare la libreria nativa (voce 259)"); }
                // voce 274: tolleranza sulle superfici della mesh
                if (!_meshTolMissing)
                    try { PdSetMeshTolerance(AdvancedSettings.Current.NoMeshTolerance ? 0 : 1); }
                    catch (EntryPointNotFoundException) { _meshTolMissing = true; Log.Write("DirectX", "PdRender.dll senza PdSetMeshTolerance: ricompilare la libreria nativa (voce 274)"); }
                // voce 282: profondita' del fotogramma completo in pianta e sezione
                if (!_flatFixMissing)
                    try { PdSetFlatBeaconFix(AdvancedSettings.Current.NoFlatBeaconFix ? 0 : 1); }
                    catch (EntryPointNotFoundException) { _flatFixMissing = true; Log.Write("DirectX", "PdRender.dll senza PdSetFlatBeaconFix: ricompilare la libreria nativa (voce 282)"); }
                PdShaderEndFrame(on ? 1 : 0, maxPx, 0.5f, ShaderWanted ? 1 : 0);
            }
            catch (Exception ex) { Log.Error("DirectX", ex); }
        }
        private static bool _shaderWasOn;

        private static float[] F3(XYZ p) { return new[] { (float)p.X, (float)p.Y, (float)p.Z }; }

        /// <summary>
        /// Camera del fotogramma alla libreria: serve a riconoscere la matrice di Revit nei suoi buffer (voce 190) e il
        /// punto guardato per scartare le matrici dei tasselli (voce 216, prima il centro della nuvola del cubo di prova).
        /// </summary>
        private static void SendCamera(ViewCam cam)
        {
            if (cam == null || !cam.Valid) return;
            XYZ R = cam.Right, D = cam.Dir, E = cam.Pos;
            PdSetFocus(F3(E + D * cam.Target));
            PdSetCameraInfo(F3(E), F3(D), F3(R), (float)cam.Target, (float)cam.HalfW, cam.Perspective ? 1 : 0);
        }

        private static int _state;          // 0 = da provare, 1 = attiva, -1 = non disponibile
        private static bool _capturing;
        private static long _lastLog;

        /// <summary>
        /// Voce 206: disegno con shader acceso e libreria caricata. In questo modo si vede solo la nuvola dello shader:
        /// niente buffer di DirectContext3D (ne' preparati ne' disegnati). Se la libreria manca si torna al disegno di sempre.
        /// </summary>
        public static bool ShaderMode { get { return ShaderWanted && Ready(); } }

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
        public static void Begin(ViewCam cam, long viewId)
        {
            _capturing = false;
            // voce 218: opzione spenta a Revit aperto. Va fatto prima dell'uscita qui sotto: con «Prova DirectX» spenta
            // Begin usciva subito, la libreria continuava a disegnare l'elenco vecchio con i blocchi gia' liberati dal
            // disegno di sempre e Revit si chiudeva.
            if (_shaderWasOn && !ShaderWanted && _state > 0)
            {
                try { PdShaderEndFrame(0, 1, 0.5f, 0); } catch (Exception ex) { Log.Error("DirectX", ex); }
            }
            _shaderWasOn = ShaderWanted && _state > 0;
            if (!AdvancedSettings.Current.NativeProbe && !AdvancedSettings.Current.NativeShader) return;
            if (!Ready()) return;
            _shaderWasOn = ShaderWanted;
            try
            {
                if (Environment.TickCount - _lastLog >= 5000) { PdCaptureBegin(); _capturing = true; }
                PdSetView(viewId);
                SendCamera(cam);
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
                if (AdvancedSettings.Current.NativeShader)
                {
                    var shb = new StringBuilder(8192);
                    PdShaderReport(shb, shb.Capacity);
                    Log.Write("DirectX", shb.ToString() + "; " + PassNote + "; " + CapNote);
                    NotReadyFrames = 0;
                }
                _frames = 0;
            }
            catch (Exception ex) { Log.Error("DirectX", ex); _state = -1; }
        }
    }
}
