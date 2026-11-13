using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;

namespace PointDeviation
{
    // =====================================================================================
    //  Disegno ibrido.
    //  La nuvola esiste in Revit come nuvola nativa del nostro motore (gestione: Gestisci collegamenti,
    //  Visibilità/Grafica, nascondi, cancella), ma il motore consegna a Revit solo il contorno.
    //  I punti li disegna il plugin (DirectContext3D) dove l'elemento nativo è visibile, con livello di
    //  dettaglio per vista, punti ingranditi, colori istantanei, trasparenza e X-ray.
    // =====================================================================================

    /// <summary>Punti caricati di un blocco (immutabile: il caricatore ne crea uno nuovo e lo sostituisce).</summary>
    internal sealed class TileCpu
    {
        public int Count;
        public float[] Pos;      // 3 per punto, piedi, relativi al centro della nuvola (coordinate originali)
        public byte[] Rgb;       // 3 per punto
        public float[] Dist;     // distanza con segno dal modello (piedi, NaN = nessuna superficie), null se non calcolata
        public int DistKey;      // analisi + posizione per cui valgono le distanze
        public int DistCount;    // punti per cui le distanze sono calcolate
        public sbyte[] Nrm;      // normale (3 per punto, *127), null se non calcolata
        public int NrmCount;     // punti per cui sono state calcolate le normali
    }

    /// <summary>Un pezzo di buffer grafico: al massimo 16k punti (o 16k tetraedri), sorgente fino a SrcEnd.</summary>
    internal sealed class GpuChunk
    {
        public VertexBuffer Vb;
        public int Points;
        public int SrcStart, SrcEnd;  // tratto della sorgente (punti del nodo in ordine) da cui vengono i punti del pezzo
        public int Oct = -1;          // ottante del blocco a cui appartengono i punti (-1 = tutti)
    }

    /// <summary>
    /// Buffer grafici di un blocco per un certo stile. Si costruiscono a pezzi: prima pochi punti (subito, per tutta la vista),
    /// poi si aggiungono gli altri senza rifare quelli già pronti.
    /// </summary>
    internal sealed class TileGpu : IDisposable
    {
        public readonly List<GpuChunk> Chunks = new List<GpuChunk>();
        public bool Points;            // true = punti di un pixel, false = punti grandi (tetraedri)
        public double Radius;          // raggio dei punti grandi (piedi, come ottaedro equivalente)
        public int RadiusN;            // punti disegnati per cui è stato calcolato il raggio
        public int FullCount;          // octree: punti dell'intero nodo; > 0 = raggio progressivo (vedi BuildRange)
        public readonly int[] OctEnd = new int[8]; // fine del tratto di sorgente dell'ultimo pezzo di ogni ottante
        public int StyleVersion, GeoVersion, ColorKey;
        public int Built;              // punti della sorgente già esaminati
        public bool Highlight;         // nuvola illuminata dal pannello Visibilità
        public long LastDraw;

        public void Dispose()
        {
            foreach (GpuChunk c in Chunks) try { c.Vb.Dispose(); } catch { }
            Chunks.Clear();
            Built = 0;
            Array.Clear(OctEnd, 0, OctEnd.Length);
        }
    }

    /// <summary>Come colorare e disegnare le nuvole di un progetto in questo momento (immutabile, con versione).</summary>
    internal sealed class Style
    {
        private static int _next;

        public readonly int Version = Interlocked.Increment(ref _next);
        public DisplayMode Mode;
        public ColorProfile Profile;
        public bool Invert;
        public MissingMode Missing;
        public bool Visible = true;
        public int Brightness, Contrast, Transparency, XrayContrast, PointScale;
        public Quality Quality;
        public ClipSet Clip;
        public double ZMin, ZRange = 1;
        public Dictionary<string, int> CloudColors;
        public AnalysisSnapshot Analysis;
        public bool DebugLevels;      // diagnostica: colori per livello dell'octree
        public double[] Zone;         // zona evidenziata (vedi ZoneHighlight), null = nessuna
        public double ZoneThrFt;

        /// <summary>Zona evidenziata attiva (serve l'analisi: si colorano i punti lontani dal modello).</summary>
        public bool ZoneOn { get { return Zone != null && Analysis != null; } }
        /// <summary>Servono le distanze dei punti: modalità discrepanze o zona evidenziata.</summary>
        public bool NeedsDistances { get { return Analysis != null && (Mode == DisplayMode.Discrepanze || Zone != null); } }

        /// <summary>
        /// Punto della zona evidenziata (dentro l'ingombro e lontano dal modello): viola con un filo di chiaroscuro dalla
        /// scansione. false = punto fuori dalla zona, si colora secondo la modalità.
        /// </summary>
        public bool ZoneColor(TileCpu c, int i, double x, double y, double z, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            double[] zb = Zone;
            if (zb == null || Analysis == null) return false;
            if (x < zb[0] || y < zb[1] || z < zb[2] || x > zb[3] || y > zb[4] || z > zb[5]) return false;
            if (c.Dist == null || i >= c.DistCount) return false;
            float d = c.Dist[i];
            if (!float.IsNaN(d) && Math.Abs(d) <= ZoneThrFt) return false;
            int lum = (c.Rgb[i * 3] * 3 + c.Rgb[i * 3 + 1] * 6 + c.Rgb[i * 3 + 2]) / 10;
            double f = 0.7 + 0.3 * lum / 255.0;
            r = (byte)(176 * f); g = (byte)(64 * f); b = (byte)(230 * f);
            return true;
        }

        private const int LutSize = 4096;
        private byte[] _lut;
        private bool[] _lutOk;
        private double _lutMin, _lutMax;
        private bool _lutAbs;

        public bool NeedsTransparentPass { get { return Transparency > 0 || Mode == DisplayMode.XRay; } }

        /// <summary>Trasparenza del disegno (0 = opaco, 1 = invisibile), applicata a tutto il buffer.</summary>
        public double DrawTransparency
        {
            get
            {
                // i punti si sovrappongono (in media 3-4 strati per pixel), quindi la trasparenza percepita
                // è circa t^3: la si corregge perché il cursore risponda in modo uniforme su tutta la scala
                double s = Transparency / 100.0;
                double t = s <= 0 ? 0 : Math.Pow(s, 1.0 / 3.0);
                // X-ray: trasparenza di base dal contrasto, che si combina con quella scelta
                if (Mode == DisplayMode.XRay) t = 1.0 - (1.0 - t) * (XrayContrast * 0.009);
                return Math.Max(0, Math.Min(0.985, t));
            }
        }
        public bool NeedsNormals { get { return Mode == DisplayMode.Normali; } }

        public static Style From(DeviationSettings s, AnalysisSnapshot analysis, double zmin, double zmax)
        {
            var st = new Style
            {
                Mode = s.Display,
                Profile = s.Profile,
                Invert = s.InvertSign,
                Missing = s.Missing,
                Visible = s.Visible,
                Brightness = s.Brightness,
                Contrast = s.Contrast,
                Transparency = Math.Max(0, Math.Min(100, s.Transparency)),
                XrayContrast = Math.Max(1, Math.Min(100, s.XrayContrast)),
                PointScale = Math.Max(0, Math.Min(400, s.PointScale)),
                Quality = s.Quality,
                Clip = ClipSet.From(s.Clips, s.ClipInvert),
                ZMin = zmin,
                ZRange = Math.Max(1e-6, zmax - zmin),
                CloudColors = new Dictionary<string, int>(s.CloudColors ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase),
                Analysis = analysis,
                DebugLevels = CloudDebug.Levels,
                Zone = ZoneHighlight.Box,
                ZoneThrFt = ZoneHighlight.ThresholdFt
            };
            // analisi chiesta ma non disponibile: colori della scansione
            if (st.Mode == DisplayMode.Discrepanze && analysis == null) st.Mode = DisplayMode.Rgb;
            st.BuildLut();
            return st;
        }

        private void BuildLut()
        {
            _lut = new byte[LutSize * 3];
            _lutOk = new bool[LutSize];
            Profile.GetRange(out _lutMin, out _lutMax);
            _lutAbs = Profile.Absolute;
            for (int i = 0; i < LutSize; i++)
            {
                double v = _lutMin + (_lutMax - _lutMin) * (i + 0.5) / LutSize;
                byte r, g, b;
                _lutOk[i] = Profile.TryColor(v, out r, out g, out b);
                _lut[i * 3] = r; _lut[i * 3 + 1] = g; _lut[i * 3 + 2] = b;
            }
        }

        private bool Lut(double mm, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            double v = _lutAbs ? Math.Abs(mm) : mm;
            if (v < _lutMin - 1e-9 || v > _lutMax + 1e-9) return false;
            int i = (int)((v - _lutMin) / (_lutMax - _lutMin) * LutSize);
            if (i < 0) i = 0; else if (i >= LutSize) i = LutSize - 1;
            if (!_lutOk[i]) return false;
            r = _lut[i * 3]; g = _lut[i * 3 + 1]; b = _lut[i * 3 + 2];
            return true;
        }

        /// <summary>Colore della nuvola illuminata: il suo colore con un filo di chiaroscuro dalla scansione.</summary>
        public static void HighlightColor(TileCpu c, int i, int layerArgb, out byte r, out byte g, out byte b, out byte t)
        {
            System.Drawing.Color k = System.Drawing.Color.FromArgb(layerArgb);
            int lum = (c.Rgb[i * 3] * 3 + c.Rgb[i * 3 + 1] * 6 + c.Rgb[i * 3 + 2]) / 10;
            double f = 0.75 + 0.25 * lum / 255.0;
            r = (byte)(k.R * f); g = (byte)(k.G * f); b = (byte)(k.B * f);
            t = 0;
        }

        /// <summary>Colore e trasparenza (0 = opaco, 255 = invisibile) del punto i. false = punto da non disegnare.</summary>
        public bool Color(TileCpu c, int i, double mz, int layerArgb, out byte r, out byte g, out byte b, out byte t)
        {
            t = 0; // la trasparenza si applica a tutto il buffer (DrawTransparency)
            r = g = b = 150;
            switch (Mode)
            {
                case DisplayMode.Rgb:
                    r = c.Rgb[i * 3]; g = c.Rgb[i * 3 + 1]; b = c.Rgb[i * 3 + 2];
                    ColorScale.Adjust(ref r, ref g, ref b, Brightness, Contrast);
                    return true;

                case DisplayMode.Quota:
                    ColorScale.Elevation((mz - ZMin) / ZRange, out r, out g, out b);
                    ColorScale.Adjust(ref r, ref g, ref b, Brightness, Contrast);
                    return true;

                case DisplayMode.Normali:
                    if (c.Nrm != null && i < c.NrmCount)
                    {
                        r = (byte)(55 + Math.Abs((int)c.Nrm[i * 3]) * 200 / 127);
                        g = (byte)(55 + Math.Abs((int)c.Nrm[i * 3 + 1]) * 200 / 127);
                        b = (byte)(55 + Math.Abs((int)c.Nrm[i * 3 + 2]) * 200 / 127);
                    }
                    ColorScale.Adjust(ref r, ref g, ref b, Brightness, Contrast);
                    return true;

                case DisplayMode.Nuvola:
                {
                    System.Drawing.Color k = System.Drawing.Color.FromArgb(layerArgb);
                    // un filo di chiaroscuro dalla scansione, per leggere le forme anche con un colore unico
                    int lum = (c.Rgb[i * 3] * 3 + c.Rgb[i * 3 + 1] * 6 + c.Rgb[i * 3 + 2]) / 10;
                    double f = 0.65 + 0.35 * lum / 255.0;
                    r = (byte)(k.R * f); g = (byte)(k.G * f); b = (byte)(k.B * f);
                    ColorScale.Adjust(ref r, ref g, ref b, Brightness, Contrast);
                    return true;
                }

                case DisplayMode.XRay:
                {
                    int lum = (c.Rgb[i * 3] * 3 + c.Rgb[i * 3 + 1] * 6 + c.Rgb[i * 3 + 2]) / 10;
                    byte gray = (byte)(70 + lum * 110 / 255);
                    r = g = b = gray;
                    return true;
                }

                default:
                {
                    float dk = c.Dist != null && i < c.Dist.Length ? c.Dist[i] : float.NaN;
                    double mm = float.IsNaN(dk) ? double.NaN : (Invert ? -dk : dk) * 304.8;
                    if (Profile.IsSelection)
                    {
                        PointState st = Profile.Evaluate(mm, out r, out g, out b);
                        return st != PointState.Nascosto;
                    }
                    if (!double.IsNaN(mm) && Lut(mm, out r, out g, out b)) return true;
                    if (Missing == MissingMode.Nascondi || Profile.HideMissing) return false;
                    // con una zona evidenziata (viola) gli altri punti senza modello vanno in grigio: altrimenti, essendo
                    // anch'essi viola, la zona selezionata non si distinguerebbe
                    if (Missing == MissingMode.Evidenzia && !ZoneOn) { r = ColorScale.MissingRgb[0]; g = ColorScale.MissingRgb[1]; b = ColorScale.MissingRgb[2]; }
                    else { r = g = b = 150; }
                    return true;
                }
            }
        }
    }

    /// <summary>Una nuvola del progetto: l'istanza nativa, i dati della libreria, i blocchi caricati e i loro buffer.</summary>
    internal sealed class CloudLayer : IDisposable
    {
        private const double FtPerM = 1.0 / 0.3048;

        public readonly ElementId InstanceId;
        public readonly string Folder;
        public readonly CloudInfo Info;
        public readonly TileEntry[] Tiles;
        public readonly string PointsPath;
        public readonly double OffX, OffY, OffZ;     // centro della nuvola (piedi, coordinate originali)

        public double[] T = Placement.Identity();     // posizionamento: originali (piedi) -> Revit
        public int GeoVersion = 1;                    // cresce quando la nuvola viene spostata

        internal readonly TileCpu[] Cpu;
        private readonly TileGpu[] _gpu;              // punti di un pixel
        private readonly TileGpu[] _gpuO;             // punti grandi (ottaedri), raggio fisso dalla densità del blocco
        private readonly byte[] _mode;                // 1 = si disegnano i punti grandi
        internal readonly Baked[] BakedP, BakedO;     // vertici preparati in background, pronti da copiare nella scheda video
        internal readonly int[] BakePendP, BakePendO; // 1 = preparazione in coda
        internal volatile int CurrentStyleVersion;
        private readonly byte[] _covered;             // octree: ottanti in cui un figlio è già disegnabile (bit)
        private readonly double[] _ocx, _ocy, _ocz;   // octree: centro del cubo del nodo (locali, piedi)
        private readonly int[] _want;                 // punti desiderati per blocco (massimo tra le viste recenti)
        private readonly long[] _wantTick;
        internal readonly int[] Pending;              // 1 = richiesta di caricamento in coda

        // geometria dei blocchi in coordinate locali (piedi)
        private readonly double[] _area;              // superficie stimata del blocco
        private readonly double[] _cx, _cy, _cz, _rad; // centro e raggio (locali, piedi)

        // octree (formato 2)
        public readonly bool IsOctree;
        private readonly double[] _nodeSpacing;       // distanza tra i punti al livello del nodo (piedi)
        private readonly double[] _effSpacing;        // distanza effettiva col dettaglio disegnato sotto (ultimo fotogramma)
        private readonly int[] _firstChild, _childCount;
        private int[] _heap = new int[256];
        private int[] _order = new int[256];          // nodi scelti, dal più importante (ordine di caricamento)
        // raggio progressivo dei punti grandi: i primi punti dell'ordine a strati coprono di più, ma al massimo così
        // (era 2,5: quando di un blocco si disegnava solo l'inizio si vedevano rombi enormi)
        private const double ProgressiveCap = 1.6;
        private const double SpacingFactor = 0.75;     // distanza reale tra i punti / cella della griglia (vedi costruttore)
        private ViewCam _lastCam;
        private double _capTarget = 2.8;               // distanza a schermo scelta (qualità × dimensione), senza gli aumenti per il limite di punti                      // octree: camera dell'ultimo fotogramma (per la vista precedente mostrata in attesa)
        // blocchi in coda di lettura chiesti in un fotogramma: a vista ferma i fotogrammi arrivano al massimo ogni 150 ms, e
        // con 24 la lettura si fermava a circa 160 blocchi al secondo (vista da 432 blocchi, 292 da leggere: quasi 2 s)
        private const int LoaderFeed = 64;
        public bool Moving;                            // camera in movimento in questo fotogramma (impostato dal server)
        private double _stableTarget;                  // distanza tra i punti a schermo usata finora (pixel)
        private byte[] _wantedOct = new byte[0];       // octree: ottanti con un figlio scelto (pronto o no)
        private bool[] _coarse = new bool[0];          // octree: blocco molto più rado del necessario a schermo
        private bool[] _wasRefined = new bool[0];
        // cambio atomico: blocchi dell'ultima vista completa, ridisegnati finché la nuova non è pronta
        private int[] _shownWant = new int[0];
        private byte[] _shownCov = new byte[0];
        private bool[] _shownSkip = new bool[0];
        private int[] _shownList = new int[64];
        private int _shownN;
        private long _pendingSince = -1;
        private const double SwapReady = 0.97;   // frazione dei punti della nuova vista che deve essere pronta
        private const long SwapTimeoutMs = 1200; // oltre, si passa comunque alla nuova vista (con i ripieghi)
        // Blocco «molto più rado del necessario»: dove il figlio non è ancora pronto si mostra a punti piccoli invece che
        // con i suoi punti grandi. Era 2 (un livello sopra): zoomando in fretta i blocchi del livello sopra passavano
        // subito ai punti piccoli e la zona sembrava sparire per un attimo (registro del 12/11 11:08). Con 4 i punti
        // grandi del livello sopra restano finché arriva il figlio (al massimo il doppio di quelli giusti, raramente il
        // quadruplo); oltre si passa ai punti piccoli, per non mostrare rombi enormi.
        private const double CoarseFactor = 4.0;
        private bool[] _viewNow = new bool[0];
        private double[] _pxNow = new double[0];        // piedi per pixel sul nodo nell'ultimo fotogramma (0 = non in vista)
        // un nodo con i punti a meno di 1,2 px l'uno dall'altro a schermo si disegna a punti di un pixel (effetto foto);
        // oltre, a punti grandi
        private const double PixelSwitch = 1.2;

        /// <summary>Il nodo, con n punti disegnati, è abbastanza fitto a schermo per i punti di un pixel?</summary>
        private bool PixelFine(int i, int n)
        {
            double px = i < _pxNow.Length ? _pxNow[i] : 0;
            if (px <= 0) return false;
            return OwnSpacing(i, n) / px <= PixelSwitch;
        }         // octree: nodo scelto per la vista nell'ultimo fotogramma (priorità di preparazione)
        private const double PrefetchDensity = 1.0;    // precaricamento: distanza tra i punti rispetto a quella della vista (era 0,75: con la densità dei punti di un pixel (151) bastava la stessa)
        private int[] _pfWant = new int[0];             // precaricamento: punti voluti per i blocchi da preparare in anticipo
        private int[] _pfOrder = new int[0];            // precaricamento: blocchi in ordine di importanza
        private int _pfN;
        private int[] _parent = new int[0];             // octree: padre di ogni nodo (-1 = radice)
        private bool[] _okStyle = new bool[0];          // octree: il nodo o un suo antenato è già disegnato con lo stile attuale
        private bool[] _skipStale = new bool[0];        // octree: nodo ancora con lo stile vecchio, coperto da un antenato aggiornato      // blocchi suddivisi nell'ultimo fotogramma (isteresi)
        private int _orderN;
        private double[] _heapKey = new double[256];

        public CloudLayer(ElementId instanceId, string folder)
        {
            InstanceId = instanceId;
            Folder = folder;
            Info = CloudInfo.Load(folder);
            Tiles = TileEntry.Read(Path.Combine(folder, CloudInfo.IndexName));
            PointsPath = Path.Combine(folder, CloudInfo.PointsName);
            OffX = Info.CenterM[0] * FtPerM; OffY = Info.CenterM[1] * FtPerM; OffZ = Info.CenterM[2] * FtPerM;

            int n = Tiles.Length;
            Cpu = new TileCpu[n];
            _gpu = new TileGpu[n];
            _gpuO = new TileGpu[n];
            _mode = new byte[n];
            BakedP = new Baked[n]; BakedO = new Baked[n];
            _wasRefined = new bool[n];
            _wantedOct = new byte[n];
            _pfWant = new int[n]; _pfOrder = new int[64];
            _shownWant = new int[n]; _shownCov = new byte[n]; _shownSkip = new bool[n];
            _coarse = new bool[n];
            BakePendP = new int[n]; BakePendO = new int[n];
            _viewNow = new bool[n]; _pxNow = new double[n];
            _covered = new byte[n];
            _ocx = new double[n]; _ocy = new double[n]; _ocz = new double[n];
            _want = new int[n];
            _wantTick = new long[n];
            Pending = new int[n];
            _area = new double[n]; _cx = new double[n]; _cy = new double[n]; _cz = new double[n]; _rad = new double[n];
            for (int i = 0; i < n; i++)
            {
                TileEntry t = Tiles[i];
                double dx = (t.MaxX - t.MinX) * FtPerM, dy = (t.MaxY - t.MinY) * FtPerM, dz = (t.MaxZ - t.MinZ) * FtPerM;
                double[] s = { dx, dy, dz };
                Array.Sort(s);
                _area[i] = Math.Max(1e-4, s[1] * s[2]);
                _cx[i] = (t.MinX + t.MaxX) * 0.5 * FtPerM; _cy[i] = (t.MinY + t.MaxY) * 0.5 * FtPerM; _cz[i] = (t.MinZ + t.MaxZ) * 0.5 * FtPerM;
                _rad[i] = 0.5 * Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            IsOctree = Info.IsOctree && n > 0;
            if (IsOctree)
            {
                int g = Info.OctreeGrid > 0 ? Info.OctreeGrid : OctreeBuilder.NodeGrid;
                _nodeSpacing = new double[n]; _effSpacing = new double[n];
                _firstChild = new int[n]; _childCount = new int[n];
                _parent = new int[n]; _okStyle = new bool[n]; _skipStale = new bool[n];
                for (int i = 0; i < n; i++) _parent[i] = -1;
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < Tiles[i].ChildCount; k++)
                        if (Tiles[i].FirstChild + k < n) _parent[Tiles[i].FirstChild + k] = i;
                for (int i = 0; i < n; i++)
                {
                    TileEntry t = Tiles[i];
                    _firstChild[i] = t.FirstChild;
                    _childCount[i] = t.ChildCount;
                    // distanza reale tra i punti di un nodo: circa 0,75 × la cella della griglia. Con un punto per cella i
                    // punti di celle vicine stanno più stretti della cella (sulla nuvola di lavoro: 1,9 volte i punti per
                    // livello di un octree Potree con la stessa cella alla radice; stima superficie/punti 0,56-0,66 della
                    // cella). Usare la cella intera faceva scegliere livelli troppo fitti e punti grandi troppo larghi:
                    // circa il doppio dei punti necessari per la stessa resa a schermo.
                    double sp = t.CubeSize * FtPerM / g * SpacingFactor;
                    // nodo ridotto dal tetto di punti (indice versione 3): più rado della cella
                    if (t.Spacing > 0) sp = Math.Max(sp, t.Spacing * FtPerM * SpacingFactor);
                    // foglia: tiene tutti i punti della sua zona, quindi può essere più fitta della griglia
                    if (_childCount[i] == 0 && t.Count > 0) sp = Math.Min(sp, Math.Sqrt(_area[i] / t.Count));
                    _nodeSpacing[i] = _effSpacing[i] = sp;
                    double h = t.CubeSize * 0.5;
                    _ocx[i] = (t.CubeX + h) * FtPerM; _ocy[i] = (t.CubeY + h) * FtPerM; _ocz[i] = (t.CubeZ + h) * FtPerM;
                }
            }
        }

        public string Name { get { return Info.Name; } }

        // distanze salvate su disco per l'analisi attuale (vedi DistCache)
        private DistCache _distCache;
        private AnalysisSnapshot _distCacheFor;
        private readonly object _distCacheLock = new object();

        public DistCache GetDistCache(AnalysisSnapshot an)
        {
            if (an == null || !IsOctree) return null;
            lock (_distCacheLock)
            {
                if (_distCacheFor == an) return _distCache;
                if (_distCache != null) _distCache.Close();
                _distCacheFor = an;
                _distCache = DistCache.Open(this, an);
                return _distCache;
            }
        }

        /// <summary>Distanza tra i punti disegnati in un blocco (piedi): octree = livello più fitto scelto, altrimenti densità del blocco.</summary>
        public double DrawSpacing(int tile, int n)
        {
            if (!IsOctree) return Spacing(tile, n);
            double e = _effSpacing[tile];
            int cnt = Tiles[tile].Count;
            return n > 0 && n < cnt ? e * Math.Sqrt((double)cnt / n) : e;
        }

        /// <summary>Distanza media tra i punti di un blocco con n punti caricati (piedi).</summary>
        public double Spacing(int tile, int n)
        {
            return Math.Sqrt(_area[tile] / Math.Max(1, n));
        }

        public void ToModel(double lx, double ly, double lz, out double mx, out double my, out double mz)
        {
            double x = lx + OffX, y = ly + OffY, z = lz + OffZ;
            double[] p = T;
            mx = p[0] * x + p[1] * y + p[2] * z + p[9];
            my = p[3] * x + p[4] * y + p[5] * z + p[10];
            mz = p[6] * x + p[7] * y + p[8] * z + p[11];
        }

        /// <summary>Ingombro in coordinate Revit.</summary>
        public void ModelBounds(out XYZ min, out XYZ max)
        {
            double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
            foreach (TileEntry t in Tiles)
            {
                for (int k = 0; k < 8; k++)
                {
                    double x, y, z;
                    ToModel(((k & 1) == 0 ? t.MinX : t.MaxX) * FtPerM, ((k & 2) == 0 ? t.MinY : t.MaxY) * FtPerM,
                        ((k & 4) == 0 ? t.MinZ : t.MaxZ) * FtPerM, out x, out y, out z);
                    mn[0] = Math.Min(mn[0], x); mn[1] = Math.Min(mn[1], y); mn[2] = Math.Min(mn[2], z);
                    mx[0] = Math.Max(mx[0], x); mx[1] = Math.Max(mx[1], y); mx[2] = Math.Max(mx[2], z);
                }
            }
            if (Tiles.Length == 0) { mn[0] = mn[1] = mn[2] = 0; mx[0] = mx[1] = mx[2] = 1; }
            min = new XYZ(mn[0], mn[1], mn[2]);
            max = new XYZ(mx[0], mx[1], mx[2]);
        }

        /// <summary>Distanze valide per l'analisi e la posizione attuali.</summary>
        public int DistKey(AnalysisSnapshot an)
        {
            return an == null ? 0 : (an.Version * 397) ^ GeoVersion;
        }

        /// <summary>La nuvola nativa è visibile in questa vista? (nascondi elemento/categoria, isola, nuvole spente, scaricata)</summary>
        public bool VisibleIn(View view)
        {
            string r = HiddenReason(view);
            // registro a ogni cambio (per vista), per verificare che gli interruttori di Revit vengano letti
            long key = view.Id.Value;
            string old;
            if (!_lastHidden.TryGetValue(key, out old) || old != r)
            {
                _lastHidden[key] = r;
                Redraw.Request(); // cambio di visibilità: si ridisegna subito
                if (old != null || r != null || _lastHidden.Count > 1)
                    Log.Write("Visibilità", "«" + Name + "» in «" + view.Name + "»: " + (r == null ? "visibile" : "nascosta (" + r + ")"));
            }
            return r == null;
        }
        private readonly Dictionary<long, string> _lastHidden = new Dictionary<long, string>();

        /// <summary>Perché la nuvola non si disegna in questa vista (null = si disegna).</summary>
        public string HiddenReason(View view)
        {
            try
            {
                Document doc = view.Document;
                Element e = doc.GetElement(InstanceId);
                if (e == null) return "elemento non trovato";
                if (!NativeClouds.IsLoaded(Folder)) return "nuvola scaricata (Gestisci collegamenti)";
                if (e.IsHidden(view)) return "elemento nascosto nella vista";
                if (e.Category != null && view.GetCategoryHidden(e.Category.Id)) return "categoria Nuvole di punti spenta nella vista";
                if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_PointClouds))) return "categoria Nuvole di punti spenta nella vista";
                if (PointCloudsHiddenInView(view)) return "nuvole di punti spente in Visibilità/Grafica";
                if (InstanceHiddenByOverrides(view, InstanceId)) return "nuvola spenta nella scheda Nuvole di punti di Visibilità/Grafica";
                if (view.IsTemporaryHideIsolateActive() &&
                    !view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, InstanceId)) return "nascosta da Nascondi/Isola temporaneo";
                return null;
            }
            catch (Exception ex) { return "errore: " + ex.Message; }
        }

        // Visibilità/Grafica, scheda Nuvole di punti: l'interruttore generale e le spunte per nuvola.
        // Si leggono per riflessione perché i nomi dell'API cambiano tra le versioni di Revit.
        private static System.Reflection.PropertyInfo _pcHidden;
        private static System.Reflection.MethodInfo _pcOverrides, _pcScanSettings;
        private static bool _pcInit;

        private static void InitPcReflection()
        {
            if (_pcInit) return;
            _pcInit = true;
            try
            {
                _pcHidden = typeof(View).GetProperty("ArePointCloudsHidden");
                _pcOverrides = typeof(View).GetMethod("GetPointCloudOverrides", Type.EmptyTypes);
                if (_pcOverrides != null)
                    _pcScanSettings = _pcOverrides.ReturnType.GetMethod("GetPointCloudScanOverrideSettings", new[] { typeof(ElementId) });
                Log.Write("Visibilità", string.Format("API: ArePointCloudsHidden {0}, GetPointCloudOverrides {1}, GetPointCloudScanOverrideSettings {2}",
                    _pcHidden != null, _pcOverrides != null, _pcScanSettings != null));
            }
            catch (Exception ex) { Log.Error("Visibilità (API)", ex); }
        }

        /// <summary>Diagnostica: stato di tutti gli interruttori di visibilità per questa nuvola in questa vista.</summary>
        public string VisibilityDiag(View view)
        {
            try
            {
                Element e = view.Document.GetElement(InstanceId);
                string cat = e != null && e.Category != null ? e.Category.Name + " (" + e.Category.Id.Value + ")" : "nessuna";
                return string.Format("categoria {0} nascosta={1}, OST_PointClouds nascosta={2}, ArePointCloudsHidden={3}, spunta nuvola spenta={4}, elemento nascosto={5}, caricata={6}",
                    cat, e != null && e.Category != null && view.GetCategoryHidden(e.Category.Id),
                    view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_PointClouds)),
                    PointCloudsHiddenInView(view), InstanceHiddenByOverrides(view, InstanceId),
                    e != null && e.IsHidden(view), NativeClouds.IsLoaded(Folder)) + DeepDiag(view);
            }
            catch (Exception ex) { return "errore: " + ex.Message; }
        }

        /// <summary>
        /// Diagnostica approfondita delle impostazioni «Nuvole di punti» di Visibilità/Grafica: vista (id, modello di
        /// vista), vista riletta dal documento, tutte le proprietà della vista con «PointCloud» nel nome e tutte le
        /// proprietà delle impostazioni della nuvola. Serve a capire perché le spunte di V/G non risultano.
        /// </summary>
        private string DeepDiag(View view)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                Document doc = view.Document;
                sb.Append(" | vista id ").Append(view.Id.Value).Append(" tipo ").Append(view.GetType().Name);
                ElementId tpl = view.ViewTemplateId;
                if (tpl != null && tpl != ElementId.InvalidElementId)
                {
                    View tv = doc.GetElement(tpl) as View;
                    sb.Append(", modello di vista «").Append(tv != null ? tv.Name : "?").Append("»");
                    if (tv != null && _pcHidden != null) try { sb.Append(" (nuvole spente nel modello=").Append(_pcHidden.GetValue(tv, null)).Append(")"); } catch { }
                }
                else sb.Append(", nessun modello di vista");
                View fresh = doc.GetElement(view.Id) as View;
                if (fresh != null && _pcHidden != null) try { sb.Append(", riletta: ArePointCloudsHidden=").Append(_pcHidden.GetValue(fresh, null)); } catch { }
                foreach (System.Reflection.PropertyInfo pi in typeof(View).GetProperties())
                    if (pi.Name.IndexOf("PointCloud", StringComparison.OrdinalIgnoreCase) >= 0 && pi.GetIndexParameters().Length == 0)
                        try { sb.Append(", ").Append(pi.Name).Append('=').Append(pi.GetValue(view, null)); } catch { }
                if (_pcOverrides != null && _pcScanSettings != null)
                {
                    object ov = _pcOverrides.Invoke(view, null);
                    object st = ov != null ? _pcScanSettings.Invoke(ov, new object[] { InstanceId }) : null;
                    if (st != null)
                    {
                        foreach (System.Reflection.PropertyInfo pi in st.GetType().GetProperties())
                            if (pi.GetIndexParameters().Length == 0)
                                try { sb.Append(", nuvola.").Append(pi.Name).Append('=').Append(pi.GetValue(st, null)); } catch { }
                    }
                    else sb.Append(", impostazioni della nuvola: nessuna");
                    // le righe della scheda Nuvole di punti mostrano il nome del file, cioè il tipo: si prova anche il tipo
                    Element inst = doc.GetElement(InstanceId);
                    ElementId typeId = inst != null ? inst.GetTypeId() : ElementId.InvalidElementId;
                    if (ov != null && typeId != ElementId.InvalidElementId)
                        try
                        {
                            object ts = _pcScanSettings.Invoke(ov, new object[] { typeId });
                            System.Reflection.PropertyInfo tv = ts != null ? ts.GetType().GetProperty("Visible") : null;
                            sb.Append(", tipo ").Append(typeId.Value).Append(".Visible=").Append(tv != null ? tv.GetValue(ts, null) : "?");
                        }
                        catch (Exception ex) { sb.Append(", tipo: ").Append(ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
                }
            }
            catch (Exception ex) { sb.Append(" | errore diagnostica: ").Append(ex.Message); }
            return sb.ToString();
        }

        private static bool PointCloudsHiddenInView(View view)
        {
            InitPcReflection();
            try { return _pcHidden != null && (bool)_pcHidden.GetValue(view, null); }
            catch { return false; }
        }

        /// <summary>
        /// Riaccende in Visibilità/Grafica «Mostra nuvole di punti in questa vista» e la spunta delle nuvole indicate
        /// (da chiamare dentro una transazione). Per riflessione, come la lettura.
        /// </summary>
        internal static void ShowInViewSettings(View view, IList<ElementId> ids)
        {
            InitPcReflection();
            try
            {
                if (_pcHidden != null && _pcHidden.CanWrite && (bool)_pcHidden.GetValue(view, null)) _pcHidden.SetValue(view, false, null);
            }
            catch (Exception ex) { Log.Warn("Visibilità", "impossibile riaccendere le nuvole nella vista: " + ex.Message); }
            if (_pcOverrides == null || _pcScanSettings == null) return;
            try
            {
                object ov = _pcOverrides.Invoke(view, null);
                if (ov == null) return;
                foreach (ElementId id in ids)
                {
                    object st = _pcScanSettings.Invoke(ov, new object[] { id });
                    if (st == null) continue;
                    System.Reflection.PropertyInfo vis = st.GetType().GetProperty("Visible");
                    if (vis == null || !vis.CanWrite || (bool)vis.GetValue(st, null)) continue;
                    vis.SetValue(st, true, null);
                    System.Reflection.MethodInfo set = ov.GetType().GetMethod("SetPointCloudScanOverrideSettings", new[] { typeof(ElementId), st.GetType() });
                    if (set != null) set.Invoke(ov, new object[] { id, st });
                    else Log.Warn("Visibilità", "SetPointCloudScanOverrideSettings non disponibile");
                }
            }
            catch (Exception ex) { Log.Warn("Visibilità", "impossibile riaccendere la spunta della nuvola: " + ex.Message); }
        }

        private static bool InstanceHiddenByOverrides(View view, ElementId id)
        {
            InitPcReflection();
            if (_pcOverrides == null || _pcScanSettings == null) return false;
            try
            {
                object ov = _pcOverrides.Invoke(view, null);
                if (ov == null) return false;
                object st = _pcScanSettings.Invoke(ov, new object[] { id });
                if (st == null) return false;
                System.Reflection.PropertyInfo vis = st.GetType().GetProperty("Visible");
                return vis != null && !(bool)vis.GetValue(st, null);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ livello di dettaglio

        /// <summary>Punti per pixel² da disegnare secondo la qualità.</summary>
        public static double PointsPerPixel(Quality q)
        {
            return q == Quality.Fine ? 4.0 : q == Quality.Media ? 1.0 : 2.0;
        }

        /// <summary>Punti desiderati per ogni blocco in questa vista (0 = fuori vista) e dimensione del pixel sul blocco.</summary>
        public void Desired(ViewCam cam, Style st, int[] outWant, double[] outPx, long budget)
        {
            if (IsOctree) { DesiredOctree(cam, st, outWant, outPx, budget); return; }
            double dpp = PointsPerPixel(st.Quality);
            for (int i = 0; i < Tiles.Length; i++)
            {
                double cx, cy, cz;
                ToModel(_cx[i], _cy[i], _cz[i], out cx, out cy, out cz);
                double px;
                if (!cam.PixelSize(cx, cy, cz, _rad[i], out px)) { outWant[i] = 0; outPx[i] = 0; continue; }
                double want = _area[i] / (px * px) * dpp;
                outWant[i] = (int)Math.Min(Tiles[i].Count, Math.Max(want, Math.Min(Tiles[i].Count, 256)));
                outPx[i] = px;
            }
        }

        /// <summary>
        /// Octree: si parte dalla radice e si scende nei figli dove sullo schermo i punti del livello sono più radi
        /// di quanto chiede la qualità, prima le zone più grandi a schermo, fino al limite di punti.
        /// Ogni nodo scelto si disegna intero (i suoi punti si sommano a quelli degli antenati).
        /// </summary>
        private void DesiredOctree(ViewCam cam, Style st, int[] outWant, double[] outPx, long budget)
        {
            int n = Tiles.Length;
            StatBudgetHit = false;
            // pixel tra due punti: con i punti grandi ogni punto copre più pixel, quindi ne servono molti meno
            // «Dimensione punti» (%) agisce sulla distanza a schermo, non sul raggio: sotto il 100% i punti sono più
            // piccoli e più fitti (la superficie resta coperta), sopra il 100% più grandi e più radi. Prima cambiava solo
            // il raggio con la stessa densità: rimpicciolendo i punti comparivano i buchi. Dal registro dell'11/11 la
            // scheda video ha margine (44 M triangoli senza rallentare il fotogramma), quindi i punti in più si reggono.
            // Modo automatico «effetto foto» (voce 151): la densità si sceglie come per i punti di un pixel (circa 2 punti
            // per pixel², 0,7 px tra i punti con qualità Normale), quindi dove la nuvola è abbastanza fitta si vedono punti
            // di un pixel uno accanto all'altro, come un'immagine. Solo dove la nuvola è più rada dello schermo (da vicino,
            // vegetazione, bordi a fuoco) si usano i punti grandi, con la grandezza massima data da «Dimensione punti»
            // (100% = circa 5 px di diametro). Prima la densità era 2,8 px con punti grandi ovunque: superfici granulose e
            // piene di buchi bianchi (immagini del concorrente e prova a 1 px dell'utente, 13/11).
            double target = 1.0 / Math.Sqrt(PointsPerPixel(st.Quality));
            _lastCam = cam;
            _capTarget = st.PointScale > 0
                ? Math.Max(1.0, (st.Quality == Quality.Fine ? 2.0 : st.Quality == Quality.Media ? 3.6 : 2.8) * st.PointScale / 100.0)
                : target;
            _focus = !AdvancedSettings.Current.UniformDetail;

            double px0;
            if (!NodeVisible(cam, 0, out px0))
            {
                Array.Clear(outWant, 0, n); Array.Clear(outPx, 0, n); _orderN = 0;
                Log.Write("LOD", "«" + Name + "»: la nuvola è fuori dall'inquadratura della vista");
                FrameStats.OutOfFrame++;
                return;
            }

            // Precaricamento: la stessa visita con l'inquadratura allargata (blocchi appena fuori dai bordi, per le
            // rotazioni e gli spostamenti) e un livello più fine (per lo zoom). Questi blocchi non si disegnano: si
            // leggono e si preparano nella scheda video quando c'è tempo, così al movimento successivo sono già pronti.
            double pfMargin = AdvancedSettings.Current.Effective.PrefetchMargin;
            if (st.PointScale > 0 && pfMargin > 0)
            {
                // densità poco più fine di quella della vista (era 0,55: quasi il doppio dei punti, in un'area 2,6 volte la
                // finestra); ora fascia stretta attorno all'inquadratura, come i visualizzatori che caricano solo ciò che si vede
                SelectOctree(cam.Widened(pfMargin), (_stableTarget > 0 ? Math.Max(target, _stableTarget) : target) * PrefetchDensity, budget, outWant, outPx);
                if (_pfOrder.Length < _orderN) _pfOrder = new int[_order.Length];
                Array.Copy(_order, _pfOrder, _orderN);
                _pfN = _orderN;
                Array.Copy(outWant, _pfWant, n);
            }
            else _pfN = 0;

            // Se con la densità chiesta i punti superano il limite, non ci si ferma a metà della visita (resterebbero
            // blocchi scelti a caso tra quelli alla stessa distanza: scacchiera), ma si rarefà in modo uniforme tutta la
            // vista, aumentando la distanza tra i punti finché il totale rientra nel limite.
            //
            // La distanza scelta è stabile nel tempo: mentre la camera si muove resta quella dell'ultimo fotogramma (la
            // dimensione dei punti non salta a ogni scatto della rotella; per un attimo si può superare il limite), da
            // ferma si avvicina a quella giusta al massimo del 25% per fotogramma.
            double baseTarget = target;
            long total;
            if (Moving && _stableTarget > 0)
            {
                target = Math.Max(baseTarget, _stableTarget);
                total = SelectOctree(cam, target, budget * 3, outWant, outPx);
                StatBudgetHit = total > budget;
            }
            else
            {
                total = SelectOctree(cam, target, budget * 3, outWant, outPx);
                for (int iter = 0; iter < 8 && total > budget; iter++)
                {
                    StatBudgetHit = true;
                    target *= Math.Sqrt((double)total / budget) * 1.05;
                    total = SelectOctree(cam, target, budget * 3, outWant, outPx);
                }
                if (_stableTarget > 0)
                {
                    double t = Math.Max(_stableTarget / 1.25, Math.Min(_stableTarget * 1.25, target));
                    t = Math.Max(baseTarget, t);
                    if (Math.Abs(t - target) > 1e-6 * target)
                    {
                        target = t;
                        total = SelectOctree(cam, target, budget * 3, outWant, outPx);
                        Redraw.Request(); // si continua ad avvicinarsi nei fotogrammi successivi
                    }
                }
            }
            _stableTarget = target;
            StatTarget = target;

            // memoria per l'isteresi: quali blocchi sono stati suddivisi in questo fotogramma
            for (int i = 0; i < n; i++)
            {
                bool refined = false;
                if (outPx[i] > 0)
                    for (int k = 0; k < _childCount[i] && !refined; k++)
                        if (outPx[_firstChild[i] + k] > 0) refined = true;
                _wasRefined[i] = refined;
            }
            // distanza effettiva tra i punti disegnati: quella del livello più fitto scelto sotto ogni nodo
            for (int i = n - 1; i >= 0; i--)
            {
                if (outPx[i] <= 0) continue;
                double e = _nodeSpacing[i];
                for (int k = 0; k < _childCount[i]; k++)
                {
                    int c = _firstChild[i] + k;
                    if (outPx[c] > 0) e = Math.Min(e, _effSpacing[c]);
                }
                _effSpacing[i] = e;
            }
        }

        /// <summary>Distanza tra i punti a schermo usata nell'ultimo fotogramma (pixel).</summary>
        public double StatTarget;

        /// <summary>
        /// Visita dell'octree per una distanza tra i punti a schermo (target, pixel): si scende nei figli finché i punti
        /// del livello a schermo sono più radi del target, prima le zone più importanti. Restituisce i punti scelti.
        /// </summary>
        private long _lastActual;

        /// <summary>
        /// Caricamento a fuoco (come i visualizzatori di nuvole dedicati): pieno dettaglio nella fascia centrale della
        /// finestra (e in profondità lungo la direzione di vista), sempre più rado verso i bordi e fuori dall'inquadratura.
        /// Restituisce il fattore per la distanza tra i punti voluta: 1 al centro, FocusEdge sul bordo, di più oltre.
        /// Senza, tutta la vista aveva lo stesso dettaglio e si leggevano e preparavano anche i bordi (registro del 13/11:
        /// la nuvola «c'è sempre tutta»).
        /// </summary>
        private double FocusFactor(ViewCam cam, int i)
        {
            if (!_focus) return 1.0;
            double cx, cy, cz;
            ToModel(_cx[i], _cy[i], _cz[i], out cx, out cy, out cz);
            double d = cam.CenterDistanceSphere(cx, cy, cz, _rad[i]);
            if (d <= FocusBand) return 1.0;
            double t = (d - FocusBand) / (1.0 - FocusBand);
            return 1.0 + (FocusEdge - 1.0) * t * t;
        }
        private bool _focus;
        // fascia centrale a pieno dettaglio: metà della mezza finestra attorno al centro; sul bordo punti 3 volte più distanti
        // (9 volte meno punti). Era 35% e 4 volte: da lontano la vista risultava troppo rada (utente, 13/11).
        private const double FocusBand = 0.5;
        private const double FocusEdge = 3.0;

        private long SelectOctree(ViewCam cam, double target, long cap, int[] outWant, double[] outPx)
        {
            long actual = 0;
            int n = Tiles.Length;
            cam.BoxCulled = 0;
            Array.Clear(outWant, 0, n);
            Array.Clear(outPx, 0, n);
            _orderN = 0;
            int hn = 0;
            double px;
            if (NodeVisible(cam, 0, out px)) Push(ref hn, 0, Priority(cam, 0, px));
            long total = 0;
            while (hn > 0)
            {
                double key;
                int i = Pop(ref hn, out key);
                if (_orderN == _order.Length) Array.Resize(ref _order, _orderN * 2);
                _order[_orderN++] = i;
                NodeVisible(cam, i, out px);
                outPx[i] = px;
                int cnt = Tiles[i].Count;
                // un blocco più fitto di quanto serve a questa distanza se ne disegna solo la parte necessaria
                // (i punti sono in ordine a strati: l'inizio copre tutto il blocco in modo uniforme)
                double focus = FocusFactor(cam, i);
                if (cnt > 0)
                {
                    double sOwn = _childCount[i] == 0 ? Math.Sqrt(_area[i] / cnt) : _nodeSpacing[i];
                    double sWant = target * focus * px;
                    if (sOwn < sWant) cnt = (int)Math.Max(Math.Min(cnt, 512), cnt * (sOwn / sWant) * (sOwn / sWant));
                }
                outWant[i] = cnt;
                // nel limite conta solo la parte del blocco dentro la finestra: da vicino i blocchi sono più grandi della
                // vista, e contarli interi esauriva il limite costringendo a punti enormi
                double bx, by, bz;
                ToModel(_cx[i], _cy[i], _cz[i], out bx, out by, out bz);
                total += (long)(cnt * cam.VisibleFraction(bx, by, bz, _rad[i]));
                actual += cnt;
                if (total >= cap || actual >= cap * 2) break; // sicurezza: la chiamata successiva userà una distanza più grande
                // isteresi: un blocco già suddiviso resta suddiviso finché i suoi punti a schermo non diventano
                // chiaramente più fitti del necessario. Senza, ai blocchi vicini alla soglia bastava un piccolo zoom per
                // perdere e riprendere i figli: a ogni movimento tornavano per un attimo i punti grandi del livello sopra.
                double ratio = _nodeSpacing[i] / Math.Max(px, 1e-9);
                if (ratio <= (_wasRefined[i] ? target * 0.6 : target) * focus || _childCount[i] == 0) continue;
                for (int k = 0; k < _childCount[i]; k++)
                {
                    int c = _firstChild[i] + k;
                    double pc;
                    if (NodeVisible(cam, c, out pc)) Push(ref hn, c, Priority(cam, c, pc));
                }
            }
            _lastActual = actual;
            // anche i punti effettivamente costruiti (blocchi interi) hanno un tetto, per la memoria video
            return Math.Max(total, actual / 2);
        }

        /// <summary>
        /// Ordine di visita: prima i blocchi con i punti più radi a schermo; a parità (viste parallele: tutti i blocchi
        /// dello stesso livello) prima quelli vicini al centro della finestra, così se si arriva al limite di punti
        /// manca il dettaglio ai bordi e non in zone a caso.
        /// </summary>
        private double Priority(ViewCam cam, int i, double px)
        {
            double cx, cy, cz;
            ToModel(_cx[i], _cy[i], _cz[i], out cx, out cy, out cz);
            double d = Math.Min(1.5, cam.CenterDistance(cx, cy, cz));
            return _nodeSpacing[i] / Math.Max(px, 1e-9) * (1.3 - 0.2 * d);
        }

        public bool StatBudgetHit;

        private bool NodeVisible(ViewCam cam, int i, out double px)
        {
            double cx, cy, cz;
            ToModel(_cx[i], _cy[i], _cz[i], out cx, out cy, out cz);
            return cam.PixelSize(cx, cy, cz, _rad[i], out px);
        }

        // coda con priorità (massimo in cima) per la visita dell'octree
        private void Push(ref int hn, int node, double key)
        {
            if (hn == _heap.Length) { Array.Resize(ref _heap, hn * 2); Array.Resize(ref _heapKey, hn * 2); }
            int i = hn++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_heapKey[p] >= key) break;
                _heap[i] = _heap[p]; _heapKey[i] = _heapKey[p];
                i = p;
            }
            _heap[i] = node; _heapKey[i] = key;
        }

        private int Pop(ref int hn, out double key)
        {
            int top = _heap[0];
            key = _heapKey[0];
            hn--;
            if (hn > 0)
            {
                int node = _heap[hn];
                double k = _heapKey[hn];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1;
                    if (l >= hn) break;
                    int r = l + 1;
                    int m = r < hn && _heapKey[r] > _heapKey[l] ? r : l;
                    if (_heapKey[m] <= k) break;
                    _heap[i] = _heap[m]; _heapKey[i] = _heapKey[m];
                    i = m;
                }
                _heap[i] = node; _heapKey[i] = k;
            }
            return top;
        }

        /// <summary>Registra il fabbisogno di una vista: si tiene il massimo tra le viste disegnate negli ultimi secondi.</summary>
        public void Request(int tile, int n, long now)
        {
            if (n >= _want[tile] || now - _wantTick[tile] > 3000) { _want[tile] = n; _wantTick[tile] = now; }
            else _wantTick[tile] = Math.Max(_wantTick[tile], now - 2500);
        }

        /// <summary>Millisecondi dall'ultima volta che una vista ha chiesto questo blocco.</summary>
        public long IdleMs(int tile) { return Environment.TickCount - _wantTick[tile]; }

        /// <summary>Quanti punti caricare: a gradini (2048, 4096, 8192…) per non rileggere il blocco a ogni piccolo zoom.</summary>
        public int Level(int tile, int n)
        {
            int cnt = Tiles[tile].Count;
            if (n >= cnt) return cnt;
            int l = 2048;
            while (l < n && l < (1 << 29)) l <<= 1;
            return Math.Min(cnt, l);
        }

        private bool ReadyFor(TileCpu c, Style st)
        {
            if (st.NeedsDistances)
            {
                // zona evidenziata in un'altra modalità: servono le distanze, ma i normali vanno controllati comunque
                bool dist = c.Dist != null && c.DistKey == DistKey(st.Analysis) && c.DistCount >= c.Count;
                if (!dist) return false;
                if (st.Mode == DisplayMode.Discrepanze) return true;
            }
            if (st.NeedsNormals) return c.Nrm != null;
            return true;
        }

        private static int ColorKey(TileCpu c, Style st)
        {
            return st.Mode == DisplayMode.Discrepanze ? c.DistKey : (st.NeedsNormals ? 1 : 0) + (st.ZoneOn ? 2 + 4 * c.DistKey : 0);
        }

        private int LayerArgb(Style st)
        {
            int argb;
            return st.CloudColors.TryGetValue(Folder, out argb) ? argb : CloudPalette.For(Folder);
        }

        // ------------------------------------------------------------------ disegno

        /// <summary>
        /// Fase 1: chiede al caricatore i punti mancanti e, dove lo stile è cambiato, ricostruisce subito
        /// una versione leggera del blocco (così tutta la vista cambia colore in un attimo).
        /// </summary>
        public void Prepare(Style st, RenderBudget budget, int[] want, double[] px)
        {
            CurrentStyleVersion = st.Version;
            long now = Environment.TickCount;
            int argb = LayerArgb(st);
            // modalità discrepanze: le distanze di tutta la nuvola si calcolano (e si salvano) in background
            if (IsOctree && st.Mode == DisplayMode.Discrepanze && st.Analysis != null) DistPrefetch.Ensure(this, st.Analysis);

            // fuori vista (o modo non usato) da un po': si libera la memoria
            PerfValues perf = AdvancedSettings.Current.Effective;
            long gpuKeep = perf.GpuKeepSeconds * 1000L, memKeep = perf.MemKeepSeconds * 1000L;
            for (int i = 0; i < Tiles.Length; i++)
            {
                TileGpu g = _gpu[i], o = _gpuO[i];
                // solo i blocchi non scelti per questa vista: dopo una pausa (vista ferma, nessun fotogramma per più del
                // tempo impostato) si liberava tutto, e al primo movimento la nuvola ripartiva dai livelli più grossolani
                if (want[i] > 0 || (IsOctree && _shownWant.Length == Tiles.Length && _shownWant[i] > 0)) continue;
                if (g != null && now - g.LastDraw > gpuKeep) { g.Dispose(); _gpu[i] = null; }
                if (o != null && now - o.LastDraw > gpuKeep) { o.Dispose(); _gpuO[i] = null; }
                if (want[i] <= 0 && _gpu[i] == null && _gpuO[i] == null && Cpu[i] != null && now - _wantTick[i] > memKeep && Volatile.Read(ref Pending[i]) == 0) Cpu[i] = null;
            }

            // blocchi in vista, dal più importante (octree: più grande a schermo) al meno importante
            int count = IsOctree ? _orderN : Tiles.Length;
            if (IsOctree)
            {
                Array.Clear(_viewNow, 0, _viewNow.Length); Array.Clear(_pxNow, 0, _pxNow.Length);
                // prima del giro: i padri guardano già i pixel dei figli (CoveredOctants, PixelFine)
                for (int oi = 0; oi < count; oi++) { int i = _order[oi]; if (want[i] > 0) _pxNow[i] = px[i]; }
            }
            for (int oi = 0; oi < count; oi++)
            {
                int i = IsOctree ? _order[oi] : oi;
                int w = want[i];
                if (w <= 0) continue;
                if (IsOctree) _viewNow[i] = true;
                Request(i, w, now);
                int level = Level(i, _want[i]);

                TileCpu cpu = Volatile.Read(ref Cpu[i]);
                bool ready = cpu != null && ReadyFor(cpu, st);
                bool needData = cpu == null || cpu.Count < level || !ready
                    || (st.NeedsNormals && cpu.NrmCount < cpu.Count * 0.6);
                // coda limitata (LoaderFeed): si chiedono i blocchi in ordine di importanza, al fotogramma dopo si rivaluta
                // con la vista aggiornata; i blocchi usciti dalla vista da più di 5 s si scartano alla lettura
                if (needData && CloudLoader.Queued < LoaderFeed) CloudLoader.Enqueue(this, i, Math.Max(level, cpu != null ? cpu.Count : 0), st);
                if (IsOctree && st.PointScale > 0)
                {
                    // maschere aggiornate anche per i blocchi in attesa (distanze o normali): si disegnano con quello che
                    // avevano, e con maschere di fotogrammi vecchi mostravano punti grandi dove il figlio era già pronto
                    _wantedOct[i] = WantedOctants(i, want);
                    _covered[i] = CoveredOctants(i, want);
                    // blocco molto più rado di quanto serve a schermo: dove manca ancora il figlio si mostra a punti
                    // piccoli invece che con rombi grandi
                    _coarse[i] = px[i] > 0 && _nodeSpacing[i] / px[i] > CoarseFactor * Math.Max(StatTarget, 1.0);
                }
                if (!ready) continue; // si continua a disegnare quello che c'era finché arrivano distanze o normali

                int drawN = Math.Min(cpu.Count, w);
                int key = ColorKey(cpu, st);
                bool hl = Highlighted;

                if (IsOctree && st.PointScale > 0)
                {
                    // octree: per ogni ottante del blocco, punti di un pixel dove il figlio è già disegnato (la grana la dà lui),
                    // punti grandi dove no, così nessuna zona resta vuota mentre i figli arrivano
                    // ottanti in cui un figlio è scelto per questa vista: lì il padre si disegna sempre a punti piccoli, anche
                    // se il figlio non è ancora pronto. Così i punti grandi compaiono solo dove sono la grana definitiva e
                    // non si vedono più gonfiarsi a ogni movimento: al massimo per un attimo la zona è più rada.
                    byte cov = _wantedOct[i];
                    // raggio di base dal livello del nodo; il raggio di ogni punto dipende poi dalla sua posizione nell'ordine
                    // a strati (vedi BuildRange), così il buffer vale per qualunque zoom e non va mai ricostruito
                    double radius = RadiusKey(i, px[i]);
                    // i punti grandi servono sempre: coprono la zona da soli finché i figli non sono pronti, e un figlio
                    // «copre» il suo ottante nel padre solo quando ha i suoi punti grandi completi
                    if (_raw == 0) CalibrateRaw();
                    if (_raw == 1) { EnsureNode(i, radius, cpu, drawN, key, hl, st, argb, budget, now); continue; }
                    EnsureGpu(_gpuO, i, true, radius, cpu, drawN, key, hl, st, argb, budget, now);
                    if (cov != 0 && Tiles[i].ChildMask != 0) EnsureGpu(_gpu, i, false, 0, cpu, drawN, key, hl, st, argb, budget, now);
                    continue;
                }

                bool octa = WantOcta(i, drawN, px[i], st, want);
                _mode[i] = (byte)(octa ? 1 : 0);
                double r1 = octa ? 0.75 * OwnSpacing(i, drawN) * st.PointScale / 100.0 : 0;
                EnsureGpu(octa ? _gpuO : _gpu, i, octa, r1, cpu, drawN, key, hl, st, argb, budget, now);
            }

            // precaricamento (vedi DesiredOctree): dopo i blocchi in vista, con code corte per non rubare loro tempo
            if (IsOctree && st.PointScale > 0 && _raw == 1)
                for (int oi = 0; oi < _pfN; oi++)
                {
                    int i = _pfOrder[oi];
                    int w = _pfWant[i];
                    if (w <= 0 || want[i] > 0) continue;
                    Request(i, w, now);
                    // tenuti pronti: non si liberano finché restano nel precaricamento
                    if (_gpu[i] != null) _gpu[i].LastDraw = now;
                    if (_gpuO[i] != null) _gpuO[i].LastDraw = now;
                    int level = Level(i, w);
                    TileCpu cpu = Volatile.Read(ref Cpu[i]);
                    bool ready = cpu != null && ReadyFor(cpu, st);
                    if ((cpu == null || cpu.Count < level || !ready) && CloudLoader.Queued < 6)
                        CloudLoader.Enqueue(this, i, Math.Max(level, cpu != null ? cpu.Count : 0), st);
                    if (!ready || CloudBaker.Queued >= 16) continue;
                    double radius = RadiusKey(i, 0);
                    // in movimento si preparano in background ma non si copiano: il tempo del fotogramma va alla vista
                    EnsureNode(i, radius, cpu, Math.Min(cpu.Count, w), ColorKey(cpu, st), Highlighted, st, argb, budget, now, budget.Moving, true);
                }
        }

        /// <summary>
        /// Octree: raggio di base dei punti grandi di un nodo (piedi), dalla distanza tra i punti del suo livello. La
        /// dimensione scelta dall'utente agisce sulla densità (vedi DesiredOctree), così i punti si toccano sempre.
        /// </summary>
        private double NodeRadius(int i) { return 0.85 * _nodeSpacing[i]; }

        /// <summary>
        /// Raggio massimo dei punti grandi di un nodo in questa vista (piedi), che è anche la chiave per decidere se il
        /// buffer va rifatto: il raggio del nodo con il gonfiamento progressivo, ma mai più di 0,85 × la distanza a schermo
        /// voluta (circa 5 px di diametro al 100%, 3,4 al 70%). Dove la nuvola è più rada di quanto si vede (da vicino,
        /// vegetazione, zone lontane dalle stazioni) i punti restano piccoli e si vedono i vuoti veri tra un punto e
        /// l'altro, invece di rombi di 20 px che impastano tutto (registro e immagini del 12/11: staccionata a doghe che
        /// sembrava fatta di tronchi uniti). Cambiando zoom il buffer si rifà a scatti del 35%.
        /// px = piedi per pixel sul nodo (0 = da calcolare con la camera dell'ultimo fotogramma).
        /// </summary>
        private double RadiusKey(int i, double px)
        {
            double full = NodeRadius(i) * ProgressiveCap;
            if (px <= 0 && (_lastCam == null || !NodeVisible(_lastCam, i, out px))) return full;
            if (px <= 0) return full;
            // Il massimo va a scatti di 2 (diametro tra 0,7 e 1,4 volte quello nominale): con scatti del 35% e la distanza
            // reale (che sale quando si tocca il limite di punti) quasi ogni zoom rifaceva tutti i blocchi vicini: registro
            // del 12/11, fino a 45 M punti preparati e 27 M copiati in 5 s per 2-10 M mostrati.
            // con il caricamento a fuoco verso i bordi i punti sono più radi: lì possono essere anche più grandi, così le
            // superfici restano leggibili come macchie di colore invece di puntini sparsi (immagine del concorrente, 13/11)
            double cap = 0.85 * Math.Max(_capTarget, 1.0) * px * (_lastCam != null ? FocusFactor(_lastCam, i) : 1.0);
            cap = Math.Pow(2.0, Math.Round(Math.Log(cap / 1e-3, 2.0))) * 1e-3;
            // Il massimo serve solo dove il nodo è più rado di quanto si vede (foglie da vicino, zone senza figli): lì i
            // punti si allargavano a 20 px. Un nodo abbastanza fitto per questa vista tiene il suo raggio di sempre, che non
            // dipende dallo zoom: prima ogni raddoppio dello zoom rifaceva tutti i blocchi in vista (registro 12/11 10:59:
            // 10-24 M punti preparati in 5 s per 0,5-4 M mostrati, attese delle viste nuove fino a 1,3 s).
            return NodeRadius(i) <= cap ? full : cap;
        }

        /// <summary>
        /// Il buffer di un nodo con raggio «have» va ancora bene per il raggio voluto «want»? Foglie: entro il 35% (sono il
        /// dettaglio definitivo, il massimo a schermo conta). Nodi con figli: entro il doppio. Un nodo con figli è più rado
        /// del necessario solo per poco (finché arrivano i figli, poi si disegna a punti piccoli dove ci sono), e con il
        /// massimo a scatti di 2 ogni raddoppio dello zoom lo faceva rifare: registro del 12/11 11:09, fino a 14 M punti
        /// preparati in 5 s senza nessun blocco letto, mentre la vista nuova aspettava.
        /// </summary>
        private bool RadiusOk(int i, double have, double want)
        {
            if (Tiles[i].ChildMask == 0) return Math.Abs(have - want) <= want * 0.35 + 1e-12;
            return have <= want * 2.01 && have >= want / 2.01;
        }

        /// <summary>
        /// Diagnostica (riga «[Lavoro]»): perché si prepara un nodo. 0 = nessun buffer (nuovo o liberato), 1 = stile o
        /// posizione cambiati, 2 = raggio dei punti cambiato, 3 = più punti caricati, 4 = altro (versione mancante).
        /// Letto dal thread di preparazione: valori del fotogramma più recente, solo per il registro.
        /// </summary>
        internal int BakeReason(BakeJob j)
        {
            TileGpu g = j.Octa ? _gpuO[j.Node] : _gpu[j.Node];
            if (g == null) return 0;
            if (g.StyleVersion != j.Style.Version || g.GeoVersion != GeoVersion || g.ColorKey != j.Key || g.Highlight != j.Highlight) return 1;
            if (j.Octa && !RadiusOk(j.Node, g.Radius, j.Radius)) return 2;
            if (g.Built < j.UpTo) return 3;
            return 4;
        }

        /// <summary>
        /// Chiamata dal thread di lettura appena un blocco è in memoria: la preparazione dei vertici parte subito, invece di
        /// aspettare il fotogramma successivo (a vista ferma i fotogrammi arrivano ogni 150 ms e ogni passaggio
        /// lettura → preparazione → copia ne costava uno). Stessi parametri di EnsureNode.
        /// </summary>
        internal void BakeAfterLoad(int i, Style st)
        {
            if (!IsOctree || st == null || st.PointScale <= 0 || _raw != 1) return;
            if (st.Version != CurrentStyleVersion || IdleMs(i) > 2000 || CloudBaker.Queued >= 96) return;
            TileCpu cpu = Volatile.Read(ref Cpu[i]);
            if (cpu == null || !ReadyFor(cpu, st)) return;
            int key = ColorKey(cpu, st);
            bool hl = Highlighted;
            double radius = RadiusKey(i, 0);
            int prio = i < _viewNow.Length && _viewNow[i] ? 0 : 1;
            if (PixelFine(i, cpu.Count))
            {
                // nodo fitto a schermo: solo la versione a punti di un pixel (vedi EnsureNode)
                TileGpu gp = _gpu[i];
                if (gp != null && gp.StyleVersion == st.Version && gp.GeoVersion == GeoVersion && gp.ColorKey == key && gp.Highlight == hl
                    && gp.Built >= cpu.Count) return;
                Baked bp = Volatile.Read(ref BakedP[i]);
                if (bp != null && bp.StyleVersion == st.Version && bp.GeoVersion == GeoVersion && bp.ColorKey == key && bp.Highlight == hl
                    && bp.Built >= cpu.Count) return;
                CloudBaker.Enqueue(new BakeJob
                {
                    Layer = this, Node = i, Octa = false, WithPoints = false, Radius = radius, UpTo = cpu.Count,
                    Key = key, Highlight = hl, Style = st, Argb = LayerArgb(st), Prio = prio
                });
                return;
            }
            // già pronto per tutti i punti caricati (nella scheda video o preparato in attesa di copia): niente da fare
            TileGpu g = _gpuO[i];
            if (g != null && g.StyleVersion == st.Version && g.GeoVersion == GeoVersion && g.ColorKey == key && g.Highlight == hl
                && RadiusOk(i, g.Radius, radius) && g.Built >= cpu.Count) return;
            Baked b = Volatile.Read(ref BakedO[i]);
            if (b != null && b.StyleVersion == st.Version && b.GeoVersion == GeoVersion && b.ColorKey == key && b.Highlight == hl
                && b.Built >= cpu.Count) return;
            CloudBaker.Enqueue(new BakeJob
            {
                Layer = this, Node = i, Octa = true, WithPoints = true, Radius = radius, UpTo = cpu.Count,
                Key = key, Highlight = hl, Style = st, Argb = LayerArgb(st), Prio = prio
            });
        }

        /// <summary>Costruisce (in versione leggera) il buffer del blocco se manca o non vale più per lo stile attuale.</summary>
        private void EnsureGpu(TileGpu[] arr, int i, bool octa, double radius, TileCpu cpu, int drawN, int key, bool hl,
            Style st, int argb, RenderBudget budget, long now)
        {
            if (_raw == 0) CalibrateRaw();
            TileGpu gpu = arr[i];
            bool stale = gpu == null || gpu.StyleVersion != st.Version || gpu.GeoVersion != GeoVersion
                || gpu.ColorKey != key || gpu.Highlight != hl
                || (octa && !RadiusOk(i, gpu.Radius, radius));

            // octree con scrittura diretta: i vertici si preparano in background (calcolo di posizioni, colori, ottanti) e
            // qui si copiano soltanto nella scheda video, che costa pochissimo. Il fotogramma non aspetta mai il calcolo.
            if (IsOctree && _raw == 1)
            {
                int need = Math.Min(drawN, cpu.Count);
                if (!stale && gpu.Built >= need) return;
                Baked[] barr = octa ? BakedO : BakedP;
                Baked bk = Volatile.Read(ref barr[i]);
                if (bk != null && bk.StyleVersion == st.Version && bk.GeoVersion == GeoVersion && bk.ColorKey == key && bk.Highlight == hl
                    && bk.Points == !octa && RadiusOk(i, bk.Radius, radius) && bk.Built >= need)
                {
                    if (!budget.CoarseTime) { budget.MoreWork = true; return; }
                    var usw = System.Diagnostics.Stopwatch.StartNew();
                    TileGpu up = Upload(bk);
                    CloudBaker.UploadTicks += usw.ElapsedTicks;
                    CloudBaker.UploadPoints += bk.Built;
                    up.LastDraw = now;
                    if (gpu != null) gpu.Dispose();
                    arr[i] = up;
                    Volatile.Write(ref barr[i], null);
                    return;
                }
                // da preparare: intanto resta quello che c'era (anche se di uno stile o di una densità precedente)
                if (CloudBaker.Queued < 48)
                    CloudBaker.Enqueue(new BakeJob
                    {
                        // si preparano tutti i punti caricati del nodo, non solo quelli che servono ora: zoomando i punti
                        // voluti cambiano di continuo e preparare solo il necessario costringeva a rifare il nodo ogni volta
                        // (registro: 13 milioni di punti preparati per 3 milioni disegnati)
                        Layer = this, Node = i, Octa = octa, Radius = radius, UpTo = cpu.Count,
                        Key = key, Highlight = hl, Style = st, Argb = argb
                    });
                return;
            }

            if (!stale) return;
            if (!budget.CoarseTime) { budget.MoreWork = true; return; }
            var ng = new TileGpu
            {
                StyleVersion = st.Version, GeoVersion = GeoVersion, ColorKey = key, Points = !octa, Highlight = hl,
                Radius = radius, RadiusN = drawN, FullCount = octa && IsOctree ? Tiles[i].Count : 0
            };
            // octree: i blocchi sono piccoli e si costruiscono interi (con la scrittura diretta bastano pochi millisecondi);
            // un blocco costruito a metà si vedrebbe come un riquadro più rado dei vicini
            int coarse = IsOctree ? drawN : Math.Min(drawN, Math.Max(512, drawN / 8));
            BuildRange(cpu, ng, coarse, st, argb, null, IsOctree ? i : -1);
            if (gpu != null) gpu.Dispose();
            ng.LastDraw = now;
            arr[i] = ng;
            if (coarse < drawN) budget.MoreWork = true;
        }

        /// <summary>
        /// Octree con scrittura diretta: tiene pronte nella scheda video le due versioni del nodo (punti grandi e, se il
        /// nodo ha figli, punti piccoli), preparate insieme in un solo passaggio in background.
        /// </summary>
        private void EnsureNode(int i, double radius, TileCpu cpu, int drawN, int key, bool hl, Style st, int argb, RenderBudget budget, long now,
            bool noUpload = false, bool prefetch = false)
        {
            int need = Math.Min(drawN, cpu.Count);
            // versione a punti di un pixel per tutti i nodi (anche le foglie): è quella che si disegna dove il nodo è
            // abbastanza fitto a schermo (vedi PixelFine)
            bool withPts = true;
            // Nodo fitto a schermo: si disegna solo a punti di un pixel, quindi si prepara solo quella versione (16 byte per
            // punto invece di 16 + 64). I punti grandi si preparano quando servono (zoom avanti). Registro del 13/11 17:10:
            // dal 25 al 50% della preparazione era per punti grandi poi non disegnati.
            if (PixelFine(i, need))
            {
                int fp = UseBaked(_gpu, BakedP, i, false, radius, need, key, hl, st, budget, now, noUpload);
                if (fp == 2 && (CloudBaker.Queued < 48 || (!prefetch && CloudBaker.QueuedUrgent < 48)))
                    CloudBaker.Enqueue(new BakeJob
                    {
                        Layer = this, Node = i, Octa = false, WithPoints = false, Radius = radius, UpTo = cpu.Count,
                        Key = key, Highlight = hl, Style = st, Argb = argb, Prio = prefetch ? 1 : 0
                    });
                return;
            }
            int so = UseBaked(_gpuO, BakedO, i, true, radius, need, key, hl, st, budget, now, noUpload);
            int sp = withPts ? UseBaked(_gpu, BakedP, i, false, radius, need, key, hl, st, budget, now, noUpload) : 0;
            if (so != 2 && sp != 2) return;
            // Solo il raggio è cambiato (il nodo si disegna già, con punti un po' più grandi o più piccoli del giusto):
            // registro del 13/11, 168 M punti preparati su 251 M erano per questo motivo, in coda insieme ai blocchi che
            // mancavano ancora. Ora si rifanno solo a camera ferma e dopo tutto il resto.
            TileGpu g = _gpuO[i];
            bool radiusOnly = g != null && g.Chunks.Count > 0 && g.StyleVersion == st.Version && g.GeoVersion == GeoVersion
                && g.ColorKey == key && g.Highlight == hl && g.Built >= need && !RadiusOk(i, g.Radius, radius)
                && (!withPts || _gpu[i] != null);
            if (radiusOnly && budget.Moving) return;
            int prio = prefetch ? 1 : radiusOnly ? 2 : 0;
            if (CloudBaker.Queued < 48 || (prio == 0 && CloudBaker.QueuedUrgent < 48))
                CloudBaker.Enqueue(new BakeJob
                {
                    // tutti i punti caricati del nodo, così zoomando non va rifatto (vedi MODIFICHE 97)
                    Layer = this, Node = i, Octa = true, WithPoints = withPts, Radius = radius, UpTo = cpu.Count,
                    Key = key, Highlight = hl, Style = st, Argb = argb, Prio = prio
                });
        }

        /// <summary>0 = buffer già valido, 1 = pronto ma si copia al prossimo fotogramma, 2 = da preparare.</summary>
        private int UseBaked(TileGpu[] arr, Baked[] barr, int i, bool octa, double radius, int need, int key, bool hl,
            Style st, RenderBudget budget, long now, bool noUpload = false)
        {
            TileGpu gpu = arr[i];
            bool stale = gpu == null || gpu.StyleVersion != st.Version || gpu.GeoVersion != GeoVersion
                || gpu.ColorKey != key || gpu.Highlight != hl
                || (octa && !RadiusOk(i, gpu.Radius, radius));
            if (!stale && gpu.Built >= need) return 0;
            Baked bk = Volatile.Read(ref barr[i]);
            if (bk == null || bk.StyleVersion != st.Version || bk.GeoVersion != GeoVersion || bk.ColorKey != key || bk.Highlight != hl
                || bk.Points == octa || (octa && !RadiusOk(i, bk.Radius, radius)) || bk.Built < need)
                return 2;
            if (noUpload || !budget.CoarseTime) { budget.MoreWork = true; return 1; }
            var usw = System.Diagnostics.Stopwatch.StartNew();
            TileGpu up = Upload(bk);
            CloudBaker.UploadTicks += usw.ElapsedTicks;
            CloudBaker.UploadPoints += bk.Built;
            up.LastDraw = now;
            if (gpu != null) gpu.Dispose();
            arr[i] = up;
            Volatile.Write(ref barr[i], null);
            return 0;
        }

        /// <summary>Ottanti del nodo in cui il figlio è scelto per questa vista (pronto o no).</summary>
        private byte WantedOctants(int i, int[] want)
        {
            int mask = Tiles[i].ChildMask, c = _firstChild[i], res = 0;
            for (int o = 0; o < 8; o++)
            {
                if ((mask & (1 << o)) == 0) continue;
                int ch = c++;
                if (want[ch] > 0) res |= 1 << o;
            }
            return (byte)res;
        }

        /// <summary>Ottanti del nodo in cui il figlio è scelto per questa vista e ha già qualcosa da disegnare.</summary>
        private byte CoveredOctants(int i, int[] want)
        {
            int mask = Tiles[i].ChildMask, c = _firstChild[i], res = 0;
            for (int o = 0; o < 8; o++)
            {
                if ((mask & (1 << o)) == 0) continue;
                int ch = c++;
                if (want[ch] <= 0) continue;
                // il figlio copre il suo ottante quando ha almeno metà dei punti che servono: grazie all'ordine a strati e al
                // raggio progressivo anche una parte copre tutta la zona. Pretendere il 100% faceva tornare i punti grandi
                // del padre a ogni piccolo movimento della camera (il numero di punti voluti cambia di poco, il figlio
                // risultava «incompleto» finché non si aggiungevano i punti mancanti).
                TileCpu cc = Volatile.Read(ref Cpu[ch]);
                if (cc == null) continue;
                int need = Math.Min(cc.Count, want[ch]);
                TileGpu b = _gpuO[ch];
                // e deve avere già lo stile attuale: cambiando modo (RGB -> discrepanze) un figlio ancora con i colori
                // vecchi non copre il padre aggiornato, così non si vedono riquadri dei due stili mescolati
                if (b != null && b.Chunks.Count > 0 && b.StyleVersion == CurrentStyleVersion
                    && (b.Built >= need / 2 || b.Built >= cc.Count)) res |= 1 << o;
                else
                {
                    // figlio fitto a schermo: si disegna a punti di un pixel e può non avere i punti grandi
                    TileGpu pb = _gpu[ch];
                    if (pb != null && pb.Chunks.Count > 0 && pb.StyleVersion == CurrentStyleVersion && PixelFine(ch, need)
                        && (pb.Built >= need / 2 || pb.Built >= cc.Count)) res |= 1 << o;
                }
            }
            return (byte)res;
        }

        /// <summary>Distanza tra i punti del blocco stesso (piedi), con n punti disegnati.</summary>
        private double OwnSpacing(int tile, int n)
        {
            if (!IsOctree) return Spacing(tile, n);
            int cnt = Tiles[tile].Count;
            double sp = _nodeSpacing[tile];
            return n > 0 && n < cnt ? sp * Math.Sqrt((double)cnt / n) : sp;
        }

        /// <summary>Qualche figlio scelto per questa vista è già disegnabile? (allora il dettaglio fine lo danno i figli)</summary>
        private bool ChildDrawable(int i, int[] want)
        {
            if (!IsOctree) return false;
            for (int k = 0; k < _childCount[i]; k++)
            {
                int c = _firstChild[i] + k;
                if (want[c] <= 0) continue;
                TileGpu g = _mode[c] == 1 ? _gpuO[c] : _gpu[c];
                if (g != null && g.Chunks.Count > 0) return true;
            }
            return false;
        }

        /// <summary>Fase 2: aggiunge punti ai blocchi già pronti finché c'è tempo in questo fotogramma.</summary>
        public void Refine(Style st, RenderBudget budget, int[] want)
        {
            if (IsOctree && _raw == 1) return; // i blocchi si preparano interi in background (EnsureGpu)
            int argb = LayerArgb(st);
            bool both = IsOctree && st.PointScale > 0;
            int count = IsOctree ? _orderN : Tiles.Length;
            for (int oi = 0; oi < count; oi++)
            {
                int i = IsOctree ? _order[oi] : oi;
                int w = want[i];
                if (w <= 0) continue;
                TileCpu cpu = Volatile.Read(ref Cpu[i]);
                if (cpu == null || !ReadyFor(cpu, st)) continue;
                int drawN = Math.Min(cpu.Count, w);
                for (int m = 0; m < 2; m++)
                {
                    TileGpu gpu = both ? (m == 0 ? _gpuO[i] : _gpu[i]) : (m == 0 ? (_mode[i] == 1 ? _gpuO[i] : _gpu[i]) : null);
                    if (gpu == null || gpu.StyleVersion != st.Version || gpu.GeoVersion != GeoVersion || gpu.Highlight != Highlighted) continue;
                    if (gpu.ColorKey != ColorKey(cpu, st) || gpu.Built >= drawN) continue;
                    if (!budget.RefineTime) { budget.MoreWork = true; return; }
                    BuildRange(cpu, gpu, drawN, st, argb, budget, IsOctree ? i : -1);
                    if (gpu.Built < drawN) budget.MoreWork = true;
                }
            }
        }

        /// <summary>Fase 3: disegna i blocchi in vista, fino al numero di punti voluto.</summary>
        public void DrawAll(Style st, int[] want)
        {
            long now = Environment.TickCount;
            EffectInstance effect = EffectFor(st, Highlighted);
            int nodes = 0, wanted = 0, missing = 0, notLoaded = 0, notBuilt = 0;
            long pts = 0;
            // Cambio di stile (per esempio accendendo l'analisi): un nodo che ha ancora i colori vecchi non si disegna se
            // un suo antenato ha già quelli nuovi; lo sostituisce l'antenato finché non è pronto. Così la vista passa
            // al nuovo stile tutta insieme, prima rada e poi sempre più fitta, invece che a riquadri.
            if (IsOctree && st.PointScale > 0)
                for (int oi = 0; oi < _orderN; oi++)
                {
                    int i = _order[oi];
                    if (want[i] <= 0) { _okStyle[i] = false; _skipStale[i] = false; continue; }
                    TileGpu g0 = _gpuO[i], g1 = _gpu[i];
                    bool cur = (g0 != null && g0.Chunks.Count > 0 && g0.StyleVersion == st.Version)
                        || (g1 != null && g1.Chunks.Count > 0 && g1.StyleVersion == st.Version);
                    int p = _parent[i];
                    bool anc = p >= 0 && want[p] > 0 && _okStyle[p];
                    _okStyle[i] = cur || anc;
                    _skipStale[i] = !cur && anc;
                }
            if (IsOctree && st.PointScale > 0) { DrawOctreeAtomic(st, want, effect, now); return; }
            for (int i = 0; i < Tiles.Length; i++)
            {
                if (want[i] <= 0) continue;
                wanted++;
                if (IsOctree && st.PointScale > 0)
                {
                    TileGpu gp = _gpu[i], go = _gpuO[i];
                    // un blocco scelto tiene pronte entrambe le versioni anche se in questo momento ne usa una sola:
                    // prima i punti grandi di un padre coperto dai figli venivano liberati dopo 15 s, e allontanando lo
                    // zoom il padre restava per un po' con i soli puntini, poi i punti grandi tornavano tutti insieme
                    if (gp != null) gp.LastDraw = now;
                    if (go != null) go.LastDraw = now;
                    bool hp = gp != null && gp.Chunks.Count > 0, ho = go != null && go.Chunks.Count > 0;
                    if (Volatile.Read(ref Cpu[i]) == null) notLoaded++;
                    else if (go == null || go.StyleVersion != st.Version || go.Built < Math.Min(Cpu[i].Count, want[i])
                        || (_wantedOct[i] != 0 && !hp)) notBuilt++;
                    if (!hp && !ho) { missing++; continue; }
                    if (_skipStale[i]) continue;
                    // Ottanti con il figlio già pronto: punti piccoli (la grana la dà il figlio). Ottanti con il figlio scelto
                    // ma non ancora pronto: resta la versione stabile di prima, i punti grandi del blocco, che al massimo
                    // sono il doppio di quelli giusti (un livello sopra); solo per i blocchi molto più radi del necessario
                    // si usano i punti piccoli, per non mostrare rombi enormi. Ottanti senza figli scelti: punti grandi.
                    // Se una versione manca ancora si usa l'altra, meglio che un buco.
                    int cov = _covered[i] | (_coarse[i] ? _wantedOct[i] : 0);
                    int pixMask = hp ? (ho ? cov : 0xFF) : 0;
                    int bigMask = ho ? (0xFF & ~pixMask) : 0;
                    if (pixMask != 0) { gp.LastDraw = now; int dn = Draw(gp, want[i], effect, pixMask); pts += dn; FrameStats.SmallPts += dn; }
                    if (bigMask != 0) { go.LastDraw = now; int dn = Draw(go, want[i], effect, bigMask); pts += dn; FrameStats.BigPts += dn; }
                    nodes++;
                    continue;
                }
                TileGpu gpu = _mode[i] == 1 ? _gpuO[i] : _gpu[i];
                // il modo scelto non è ancora pronto: intanto si disegna l'altro, se c'è
                if (gpu == null || gpu.Chunks.Count == 0) gpu = _mode[i] == 1 ? _gpu[i] : _gpuO[i];
                if (gpu == null || gpu.Chunks.Count == 0) continue;
                gpu.LastDraw = now;
                {
                    int dn = Draw(gpu, want[i], effect, 0xFF); pts += dn;
                    if (gpu.Points) FrameStats.SmallPts += dn; else FrameStats.BigPts += dn;
                }
                nodes++;
            }
            StatPoints = pts; StatNodes = nodes; StatWanted = wanted; StatTick = now; StatMissing = missing; StatNotLoaded = notLoaded; StatNotBuilt = notBuilt;
            if (now - _lastStatLog > 5000)
            {
                _lastStatLog = now;
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                long bp = Interlocked.Exchange(ref CloudBaker.BakePoints, 0), bt = Interlocked.Exchange(ref CloudBaker.BakeTicks, 0);
                long up = CloudBaker.UploadPoints, ut = CloudBaker.UploadTicks;
                CloudBaker.UploadPoints = 0; CloudBaker.UploadTicks = 0;
                Log.Debug("Stato", string.Format("«{0}»: blocchi scelti {1}, disegnati {2}, da caricare {3}, da costruire {4}, punti {5:N0}, distanza punti {6:F1} px{7}",
                    Name, wanted, nodes, notLoaded, notBuilt, pts, StatTarget, StatBudgetHit ? ", limite raggiunto" : ""));
                LogWork(bp, bt * f, up, ut * f);
            }
        }

        /// <summary>
        /// Octree, cambio atomico: si controlla quanta parte della nuova vista è pronta (punti con lo stile attuale nella
        /// scheda video). Se lo è quasi tutta si passa alla nuova vista; altrimenti si ridisegnano esattamente i blocchi
        /// dell'ultima vista completa, che restano corretti nello spazio (con lo zoom si ingrandiscono come
        /// un'immagine), e si cambia tutto insieme appena la nuova è pronta. Oltre un tempo massimo si passa comunque
        /// alla nuova vista con i ripieghi di sempre, per non restare indietro durante un movimento lungo.
        /// </summary>
        private void DrawOctreeAtomic(Style st, int[] want, EffectInstance effect, long now)
        {
            int wanted = 0, missing = 0, notLoaded = 0, notBuilt = 0;
            long needPts = 0, readyPts = 0;
            for (int i = 0; i < Tiles.Length; i++)
            {
                if (want[i] <= 0) continue;
                wanted++;
                TileCpu cpu = Volatile.Read(ref Cpu[i]);
                TileGpu go = _gpuO[i], gp = _gpu[i];
                int need = cpu != null ? Math.Min(cpu.Count, want[i]) : want[i];
                needPts += need;
                if (cpu == null) { notLoaded++; continue; }
                bool hp = gp != null && gp.Chunks.Count > 0;
                bool okO = go != null && go.Chunks.Count > 0 && go.StyleVersion == st.Version && go.Built >= need;
                bool okP = _wantedOct[i] == 0 || (hp && gp.StyleVersion == st.Version);
                // nodo fitto a schermo: basta la versione a punti di un pixel
                bool ok = PixelFine(i, need) ? hp && gp.StyleVersion == st.Version && gp.Built >= need : okO && okP;
                if (ok) readyPts += need; else notBuilt++;
                if (!hp && (go == null || go.Chunks.Count == 0)) missing++;
            }

            // Due modi (Impostazioni avanzate › Prestazioni):
            // - graduale (predefinito): si disegna sempre la vista nuova; dove un blocco non è ancora pronto resta il
            //   livello sopra (punti grandi nel suo ottante), quindi il dettaglio arriva zona per zona mentre si zooma;
            // - «vista nuova solo quando è pronta» (cambio atomico, voce 106): resta l'ultima vista completa, ingrandita
            //   come un'immagine, e si passa a quella nuova tutta insieme. Dal registro del 12/11 11:08 l'attesa arrivava
            //   a 1,1-1,3 s e il cambio si vedeva come uno scatto.
            bool hold = AdvancedSettings.Current.HoldViewUntilReady;
            bool complete = needPts == 0 || readyPts >= needPts * SwapReady;
            bool adopt = !hold || _shownN == 0 || complete
                || (_pendingSince >= 0 && now - _pendingSince > SwapTimeoutMs);
            int nodes = 0;
            long pts = 0;
            // diagnostica: quanto ci vuole perché la vista nuova sia pronta (graduale) o mostrata (atomico)
            if (complete || (hold && adopt))
            {
                if (_pendingSince >= 0) FrameStats.ViewAdopted(now - _pendingSince, !complete);
                _pendingSince = -1;
            }
            else
            {
                if (_pendingSince < 0) _pendingSince = now;
                // si ricontrolla ai fotogrammi successivi (senza fine se un blocco non arriva mai)
                if (now - _pendingSince < 5000) Redraw.Request();
            }
            if (adopt)
            {
                for (int k = 0; k < _shownN; k++) { int j = _shownList[k]; _shownWant[j] = 0; }
                _shownN = 0;
                for (int i = 0; i < Tiles.Length; i++)
                {
                    if (want[i] <= 0) continue;
                    int cov = _covered[i] | (_coarse[i] ? _wantedOct[i] : 0);
                    if (_shownN == _shownList.Length) Array.Resize(ref _shownList, _shownN * 2);
                    _shownList[_shownN++] = i;
                    _shownWant[i] = want[i]; _shownCov[i] = (byte)cov; _shownSkip[i] = _skipStale[i];
                }
            }

            // si disegna l'insieme mostrato (appena aggiornato, o quello dell'ultima vista completa)
            for (int k = 0; k < _shownN; k++)
            {
                int i = _shownList[k];
                TileGpu gp = _gpu[i], go = _gpuO[i];
                if (gp != null) gp.LastDraw = now;
                if (go != null) go.LastDraw = now;
                bool hp = gp != null && gp.Chunks.Count > 0, ho = go != null && go.Chunks.Count > 0;
                if (!hp && !ho) continue;
                if (_shownSkip[i]) continue;
                int cov = _shownCov[i];
                // effetto foto: nodo fitto a schermo → tutto a punti di un pixel
                if (hp && gp.StyleVersion == st.Version && PixelFine(i, _shownWant[i])) cov = 0xFF;
                // vista precedente mostrata in attesa della nuova: con lo zoom i suoi punti grandi si ingrandiscono come
                // un'immagine (da 208 a 7 ft di ampiezza: 28 volte). Dove ora sono molto più grandi del necessario si usano
                // i punti piccoli dello stesso blocco: per un attimo la zona è più rada invece che coperta da rombi enormi
                // (stessa regola dei blocchi molto più radi del necessario nella vista nuova, vedi _coarse)
                double pxNow;
                if (!adopt && hp && _lastCam != null && NodeVisible(_lastCam, i, out pxNow) && pxNow > 0
                    && _nodeSpacing[i] / pxNow > CoarseFactor * Math.Max(StatTarget, 1.0)) cov = 0xFF;
                int pixMask = hp ? (ho ? cov : 0xFF) : 0;
                int bigMask = ho ? (0xFF & ~pixMask) : 0;
                if (pixMask != 0) { int dn = Draw(gp, _shownWant[i], effect, pixMask); pts += dn; FrameStats.SmallPts += dn; }
                if (bigMask != 0) { int dn = Draw(go, _shownWant[i], effect, bigMask); pts += dn; FrameStats.BigPts += dn; }
                nodes++;
            }

            StatPoints = pts; StatNodes = nodes; StatWanted = wanted; StatTick = now; StatMissing = missing; StatNotLoaded = notLoaded; StatNotBuilt = notBuilt;
            if (now - _lastStatLog > 5000)
            {
                _lastStatLog = now;
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                long bp = Interlocked.Exchange(ref CloudBaker.BakePoints, 0), bt = Interlocked.Exchange(ref CloudBaker.BakeTicks, 0);
                long up = CloudBaker.UploadPoints, ut = CloudBaker.UploadTicks;
                CloudBaker.UploadPoints = 0; CloudBaker.UploadTicks = 0;
                Log.Debug("Stato", string.Format("«{0}»: blocchi scelti {1}, disegnati {2}, da caricare {3}, da costruire {4}, punti {5:N0}, distanza punti {6:F1} px{7}; nuova vista pronta {8:F0}%{9}",
                    Name, wanted, nodes, notLoaded, notBuilt, pts, StatTarget, StatBudgetHit ? ", limite raggiunto" : "",
                    needPts > 0 ? 100.0 * readyPts / needPts : 100, adopt ? (hold ? "" : " (graduale)") : " (si mostra la vista precedente)")
                    + (_lastCam != null && _lastCam.HasBox ? string.Format(_lastCam.PlanRange ? "; intervallo di vista della pianta: {0} blocchi esclusi" : _lastCam.SectionRange ? "; campo della sezione: {0} blocchi esclusi" : "; riquadro di sezione: {0} blocchi esclusi", _lastCam.BoxCulled) : ""));
                LogWork(bp, bt * f, up, ut * f);
            }
        }

        /// <summary>
        /// Preparazione e copia sono comuni a tutte le nuvole: una riga a parte (prima finivano nella riga della nuvola
        /// che scriveva per prima, e le altre risultavano sempre a zero).
        /// </summary>
        private static void LogWork(long bp, double bms, long up, double ums)
        {
            long rb = Interlocked.Exchange(ref CloudLoader.ReadBlocks, 0), rp = Interlocked.Exchange(ref CloudLoader.ReadPoints, 0);
            double rms = Interlocked.Exchange(ref CloudLoader.ReadTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (bp == 0 && up == 0 && rb == 0) return;
            long[] why = CloudBaker.ReasonPoints;
            long w0 = Interlocked.Exchange(ref why[0], 0), w1 = Interlocked.Exchange(ref why[1], 0), w2 = Interlocked.Exchange(ref why[2], 0),
                w3 = Interlocked.Exchange(ref why[3], 0), w4 = Interlocked.Exchange(ref why[4], 0), w5 = Interlocked.Exchange(ref why[5], 0);
            Log.Debug("Lavoro", string.Format("tutte le nuvole: letti {4:N0} blocchi ({5:N0} punti) in {6:F0} ms (somma dei thread), preparati {0:N0} punti in {1:F0} ms (somma dei thread), copiati {2:N0} punti in {3:F0} ms; in coda: lettura {7}, preparazione {8}; preparati perché: senza buffer {9:N0}, stile {10:N0}, raggio {11:N0}, più punti {12:N0}, altro {13:N0}, precaricamento {14:N0}",
                bp, bms, up, ums, rb, rp, rms, CloudLoader.Queued, CloudBaker.Queued, w0, w1, w2, w3, w4, w5));
        }

        // diagnostica (pannello Gestisci visibilità): ultimo fotogramma disegnato
        public long StatPoints;
        public int StatNodes, StatWanted, StatMissing, StatNotLoaded, StatNotBuilt;
        private long _lastStatLog;
        public long StatTick;

        /// <summary>Blocchi in memoria, in coda di lettura e illeggibili.</summary>
        public void LoadStats(out int loaded, out int pending, out int failed)
        {
            loaded = pending = failed = 0;
            for (int i = 0; i < Tiles.Length; i++)
            {
                if (Cpu[i] != null) loaded++;
                int p = Volatile.Read(ref Pending[i]);
                if (p == 1) pending++; else if (p == 2) failed++;
            }
        }

        /// <summary>
        /// Punti grandi (ottaedri) dove a schermo i punti del blocco si staccano (oltre ~1,5 pixel) e nessun figlio
        /// aggiunge già dettaglio: come la dimensione adattiva di Potree, la superficie resta piena.
        /// </summary>
        private bool WantOcta(int tile, int n, double px, Style st, int[] want)
        {
            if (st.PointScale <= 0 || n <= 0) return false;
            // octree: i blocchi «di confine» (nessun figlio ancora disegnato) a punti grandi con raggio dal proprio livello,
            // così coprono tutta la loro zona; appena arrivano i figli il blocco passa ai punti di un pixel (versione già
            // pronta in memoria) e la grana la danno i figli. Come la dimensione adattiva di Potree, senza punti enormi
            // dei livelli alti sopra il dettaglio fine.
            if (IsOctree) return !ChildDrawable(tile, want);
            if (px <= 0) return false;
            if (ChildDrawable(tile, want)) return false;
            double spacingPx = OwnSpacing(tile, n) / px * st.PointScale / 100.0;
            bool was = _mode[tile] == 1;
            return spacingPx > (was ? 1.3 : 1.6);
        }

        private static VertexFormat _format;
        private static EffectInstance _effect;
        private static IndexBuffer _pointIb, _octaIb;
        private const int PointChunk = 16384;
        // Punti grandi = tetraedri regolari (4 vertici, 4 triangoli) invece di ottaedri (6 vertici, 8 triangoli): da
        // qualunque direzione coprono una superficie mai nulla, e con il raggio × 1,225 coprono almeno quanto l'ottaedro
        // (area proiettata minima 0,94 r² contro 1,42 r², verificata in Python su 20.000 direzioni). Metà triangoli per la
        // scheda video e un terzo di dati in meno da preparare e copiare (registro del 13/11: il disegno dei punti grandi
        // era la parte più lunga del fotogramma).
        private const int BigVerts = 4, BigTris = 4;
        private const int OctaChunk = 65532 / BigVerts;
        private static readonly int[] OctaFaces = { 0, 1, 2, 0, 3, 1, 0, 2, 3, 1, 3, 2 };
        private const double TetraScale = 1.225;                       // raggio del tetraedro rispetto all'ottaedro equivalente
        private static readonly float TetraK = (float)(TetraScale / Math.Sqrt(3.0)); // componente dei vertici (±k, ±k, ±k)

        /// <summary>Indici uguali per tutti i pezzi: si creano una volta sola e si condividono.</summary>
        private static void EnsureShared()
        {
            if (_format == null) _format = new VertexFormat(VertexFormatBits.PositionColored);
            if (_effect == null) _effect = new EffectInstance(VertexFormatBits.PositionColored);
            if (_pointIb == null)
            {
                var ib = new IndexBuffer(PointChunk * IndexPoint.GetSizeInShortInts());
                ib.Map(PointChunk * IndexPoint.GetSizeInShortInts());
                IndexStreamPoint s = ib.GetIndexStreamPoint();
                for (int j = 0; j < PointChunk; j++) s.AddPoint(new IndexPoint(j));
                ib.Unmap();
                _pointIb = ib;
            }
            if (_octaIb == null)
            {
                int tris = OctaChunk * BigTris;
                var ib = new IndexBuffer(tris * IndexTriangle.GetSizeInShortInts());
                ib.Map(tris * IndexTriangle.GetSizeInShortInts());
                IndexStreamTriangle s = ib.GetIndexStreamTriangle();
                for (int j = 0; j < OctaChunk; j++)
                {
                    int o = j * BigVerts;
                    for (int f = 0; f < BigTris; f++) s.AddTriangle(new IndexTriangle(o + OctaFaces[f * 3], o + OctaFaces[f * 3 + 1], o + OctaFaces[f * 3 + 2]));
                }
                ib.Unmap();
                _octaIb = ib;
            }
        }

        /// <summary>Aggiunge al blocco i punti della sorgente da gpu.Built a end (si ferma prima se il tempo è finito).</summary>
        // punti scelti per i pezzi in costruzione, uno per ottante del nodo (solo thread di Revit)
        private const int BucketCap = OctaChunk * BigVerts > PointChunk ? OctaChunk * BigVerts : PointChunk;
        private static readonly float[][] _bx = NewBuckets(), _by = NewBuckets(), _bz = NewBuckets();
        private static readonly byte[][] _bc = NewColorBuckets();
        private static readonly int[] _bnv = new int[8], _bpts = new int[8];

        private static float[][] NewBuckets() { var r = new float[8][]; for (int i = 0; i < 8; i++) r[i] = new float[BucketCap]; return r; }
        private static byte[][] NewColorBuckets() { var r = new byte[8][]; for (int i = 0; i < 8; i++) r[i] = new byte[BucketCap * 4]; return r; }

        /// <param name="node">octree: indice del nodo (i punti si dividono per ottante), -1 = un solo gruppo</param>
        private void BuildRange(TileCpu cpu, TileGpu gpu, int end, Style st, int layerArgb, RenderBudget budget, int node = -1)
        {
            EnsureShared();
            int per = gpu.Points ? PointChunk : OctaChunk;
            int vpp = gpu.Points ? 1 : BigVerts;
            float r0 = (float)gpu.Radius;
            // octree: raggio di base del nodo; gpu.Radius è il massimo (vedi RadiusKey)
            double rb = node >= 0 && IsOctree ? NodeRadius(node) : gpu.Radius;
            bool split = node >= 0;
            double cx = split ? _ocx[node] : 0, cy = split ? _ocy[node] : 0, cz = split ? _ocz[node] : 0;
            for (int o = 0; o < 8; o++) { _bnv[o] = 0; _bpts[o] = 0; }
            int k = gpu.Built;
            end = Math.Min(end, cpu.Count);
            for (; k < end; k++)
            {
                if (budget != null && (k & 4095) == 0 && k > gpu.Built && !budget.RefineTime) break;
                float lx = cpu.Pos[k * 3], ly = cpu.Pos[k * 3 + 1], lz = cpu.Pos[k * 3 + 2];
                double x, y, z;
                ToModel(lx, ly, lz, out x, out y, out z);
                if (st.Clip != null && !st.Clip.Contains(x, y, z)) continue;
                byte r, g, b, t;
                if (gpu.Highlight) Style.HighlightColor(cpu, k, layerArgb, out r, out g, out b, out t);
                else if (st.ZoneColor(cpu, k, x, y, z, out r, out g, out b)) t = 0;
                else if (st.DebugLevels && split) { CloudDebug.LevelColor(Tiles[node].Level, gpu.Points, out r, out g, out b); t = 0; }
                else if (!st.Color(cpu, k, z, layerArgb, out r, out g, out b, out t)) continue;
                int oc = split ? (lx >= cx ? 1 : 0) | (ly >= cy ? 2 : 0) | (lz >= cz ? 4 : 0) : 0;
                float fx = (float)x, fy = (float)y, fz = (float)z;
                if (vpp == 1) Put(oc, fx, fy, fz, r, g, b, t);
                else
                {
                    // raggio progressivo: i punti sono in ordine a strati, quindi disegnandone i primi k la loro distanza è
                    // circa quella del nodo intero × √(totale/k). Il k-esimo punto prende il raggio adatto a quella densità:
                    // i primi grandi, gli ultimi piccoli. Qualunque prefisso si disegni, la superficie resta coperta.
                    if (gpu.FullCount > 0)
                        r0 = (float)Math.Min(rb * Math.Min(ProgressiveCap, Math.Sqrt((double)gpu.FullCount / (k + 1))), gpu.Radius);
                    float q = r0 * TetraK;
                    Put(oc, fx + q, fy + q, fz + q, r, g, b, t);
                    Put(oc, fx + q, fy - q, fz - q, r, g, b, t);
                    Put(oc, fx - q, fy + q, fz - q, r, g, b, t);
                    Put(oc, fx - q, fy - q, fz + q, r, g, b, t);
                }
                if (++_bpts[oc] >= per) Flush(gpu, oc, k + 1, split);
            }
            for (int o = 0; o < 8; o++) Flush(gpu, o, k, split);
            gpu.Built = Math.Max(gpu.Built, k);
        }

        private static void Flush(TileGpu gpu, int oc, int srcEnd, bool split)
        {
            if (_bpts[oc] == 0) return;
            VertexBuffer vb = MakeBuffer(oc, _bnv[oc]);
            gpu.Chunks.Add(new GpuChunk { Vb = vb, Points = _bpts[oc], SrcStart = gpu.OctEnd[oc], SrcEnd = srcEnd, Oct = split ? oc : -1 });
            gpu.OctEnd[oc] = srcEnd;
            _bnv[oc] = 0; _bpts[oc] = 0;
        }

        private static void Put(int oc, float x, float y, float z, byte r, byte g, byte b, byte t)
        {
            int nv = _bnv[oc]++;
            _bx[oc][nv] = x; _by[oc][nv] = y; _bz[oc][nv] = z;
            byte[] bc = _bc[oc];
            int o = nv * 4;
            bc[o] = r; bc[o + 1] = g; bc[o + 2] = b; bc[o + 3] = t;
        }

        // ------------------------------------------------------------------ preparazione in background

        /// <summary>Copia nella scheda video i vertici già preparati (solo memcpy: pochissimo tempo nel fotogramma).</summary>
        private static TileGpu Upload(Baked bk)
        {
            EnsureShared();
            var g = new TileGpu
            {
                StyleVersion = bk.StyleVersion, GeoVersion = bk.GeoVersion, ColorKey = bk.ColorKey, Points = bk.Points,
                Highlight = bk.Highlight, Radius = bk.Radius, FullCount = bk.FullCount
            };
            int fl = VertexPositionColored.GetSizeInFloats();
            foreach (BakedChunk c in bk.Chunks)
            {
                int floats = c.Verts * fl;
                var vb = new VertexBuffer(floats);
                vb.Map(floats);
                System.Runtime.InteropServices.Marshal.Copy(c.Data, 0, vb.GetMappedHandle(), c.Verts * _stride);
                vb.Unmap();
                g.Chunks.Add(new GpuChunk { Vb = vb, Points = c.Points, SrcStart = c.SrcStart, SrcEnd = c.SrcEnd, Oct = c.Oct });
            }
            g.Built = bk.Built;
            return g;
        }

        /// <summary>
        /// Prepara (su un thread di lavoro) i vertici di un nodo: stessa logica di BuildRange, ma scritta in memoria
        /// normale con la disposizione dei byte del buffer grafico, pronta per essere copiata.
        /// </summary>
        internal unsafe Baked Bake(BakeJob j)
        {
            TileCpu cpu = Volatile.Read(ref Cpu[j.Node]);
            if (cpu == null) return null;
            Style st = j.Style;
            int node = j.Node;
            bool octa = j.Octa;
            int per = octa ? OctaChunk : PointChunk, vpp = octa ? BigVerts : 1, stride = _stride;
            int oR = _offR, oG = _offG, oB = _offB, oA = _offA;
            bool op = _alphaOpacity;
            int upTo = Math.Min(j.UpTo, cpu.Count);
            int full = Tiles[node].Count;
            var bk = new Baked
            {
                StyleVersion = st.Version, GeoVersion = GeoVersion, ColorKey = j.Key, Highlight = j.Highlight, Points = !octa,
                Radius = j.Radius, FullCount = octa ? full : 0
            };
            // Buffer di lavoro riusati dal thread (prima: fino a 8 da 1 MB nuovi a ogni nodo, memoria «large object» di
            // .NET che obbliga a raccolte complete della memoria di Revit). I dati restano copiati in BakeFlush.
            if (_tsOcta == null) { _tsOcta = new byte[8][]; _tsPts = new byte[8][]; }
            var buf = octa ? _tsOcta : _tsPts;
            var nv = new int[8]; var np = new int[8]; var start = new int[8];
            // versione a punti piccoli preparata nello stesso passaggio (posizioni e colori si calcolano una volta sola)
            bool both = octa && j.WithPoints;
            Baked bp = both ? new Baked
            {
                StyleVersion = st.Version, GeoVersion = GeoVersion, ColorKey = j.Key, Highlight = j.Highlight, Points = true, Radius = j.Radius
            } : null;
            var pbuf = both ? _tsPts : null;
            var pnv = new int[8]; var pnp = new int[8]; var pstart = new int[8];
            double cx = _ocx[node], cy = _ocy[node], cz = _ocz[node];
            double rb = NodeRadius(node);   // raggio di base; j.Radius è il massimo in questa vista (vedi RadiusKey)
            for (int k = 0; k < upTo; k++)
            {
                float lx = cpu.Pos[k * 3], ly = cpu.Pos[k * 3 + 1], lz = cpu.Pos[k * 3 + 2];
                double x, y, z;
                ToModel(lx, ly, lz, out x, out y, out z);
                if (st.Clip != null && !st.Clip.Contains(x, y, z)) continue;
                byte r, g, b, t;
                if (j.Highlight) Style.HighlightColor(cpu, k, j.Argb, out r, out g, out b, out t);
                else if (st.ZoneColor(cpu, k, x, y, z, out r, out g, out b)) t = 0;
                else if (st.DebugLevels) { CloudDebug.LevelColor(Tiles[node].Level, !octa, out r, out g, out b); t = 0; }
                else if (!st.Color(cpu, k, z, j.Argb, out r, out g, out b, out t)) continue;
                int oc = (lx >= cx ? 1 : 0) | (ly >= cy ? 2 : 0) | (lz >= cz ? 4 : 0);
                if (buf[oc] == null) buf[oc] = new byte[per * vpp * stride];
                byte a = op ? (byte)(255 - t) : t;
                float fx = (float)x, fy = (float)y, fz = (float)z;
                if (both)
                {
                    if (pbuf[oc] == null) pbuf[oc] = new byte[PointChunk * stride];
                    fixed (byte* q0 = pbuf[oc]) Vert(q0, ref pnv[oc], stride, fx, fy, fz, oR, oG, oB, oA, r, g, b, a);
                    if (++pnp[oc] >= PointChunk) BakeFlush(bp, pbuf, pnv, pnp, pstart, oc, k + 1, stride);
                }
                fixed (byte* p0 = buf[oc])
                {
                    if (vpp == 1) Vert(p0, ref nv[oc], stride, fx, fy, fz, oR, oG, oB, oA, r, g, b, a);
                    else
                    {
                        float r0 = (float)Math.Min(rb * Math.Min(ProgressiveCap, Math.Sqrt((double)full / (k + 1))), j.Radius);
                        float q = r0 * TetraK;
                        Vert(p0, ref nv[oc], stride, fx + q, fy + q, fz + q, oR, oG, oB, oA, r, g, b, a);
                        Vert(p0, ref nv[oc], stride, fx + q, fy - q, fz - q, oR, oG, oB, oA, r, g, b, a);
                        Vert(p0, ref nv[oc], stride, fx - q, fy + q, fz - q, oR, oG, oB, oA, r, g, b, a);
                        Vert(p0, ref nv[oc], stride, fx - q, fy - q, fz + q, oR, oG, oB, oA, r, g, b, a);
                    }
                }
                if (++np[oc] >= per) { BakeFlush(bk, buf, nv, np, start, oc, k + 1, stride); }
            }
            for (int o = 0; o < 8; o++) BakeFlush(bk, buf, nv, np, start, o, upTo, stride);
            bk.Built = upTo;
            if (both)
            {
                for (int o = 0; o < 8; o++) BakeFlush(bp, pbuf, pnv, pnp, pstart, o, upTo, stride);
                bp.Built = upTo;
                bk.PointsTwin = bp;
            }
            return bk;
        }

        [ThreadStatic] private static byte[][] _tsOcta, _tsPts;

        private static unsafe void Vert(byte* p0, ref int nv, int stride, float x, float y, float z, int oR, int oG, int oB, int oA,
            byte r, byte g, byte b, byte a)
        {
            byte* p = p0 + nv * stride;
            *(float*)p = x; *(float*)(p + 4) = y; *(float*)(p + 8) = z;
            for (int q = 12; q < stride; q++) p[q] = 0;
            p[oR] = r; p[oG] = g; p[oB] = b;
            if (oA >= 0) p[oA] = a;
            nv++;
        }

        private static void BakeFlush(Baked bk, byte[][] buf, int[] nv, int[] np, int[] start, int oc, int srcEnd, int stride)
        {
            if (np[oc] == 0) return;
            var data = new byte[nv[oc] * stride];
            Buffer.BlockCopy(buf[oc], 0, data, 0, data.Length);
            bk.Chunks.Add(new BakedChunk { Data = data, Verts = nv[oc], Points = np[oc], Oct = oc, SrcStart = start[oc], SrcEnd = srcEnd });
            start[oc] = srcEnd;
            nv[oc] = 0; np[oc] = 0;
        }

        // ------------------------------------------------------------------ scrittura veloce dei vertici
        // Creare per ogni vertice gli oggetti XYZ / ColorWithTransparency / VertexPositionColored dell'API costa molto
        // (oggetti nativi con finalizzatore: milioni per nuvola). Dal 2021 il buffer mappato si può scrivere direttamente
        // (GetMappedHandle). La disposizione dei byte del colore si ricava una volta scrivendo un vertice di prova con l'API.

        private static int _raw;                 // 0 = da verificare, 1 = scrittura diretta, -1 = solo API
        private static int _stride, _offR, _offG, _offB, _offA;
        private static bool _alphaOpacity;

        private static unsafe void CalibrateRaw()
        {
            _raw = -1;
            try
            {
                int fl = VertexPositionColored.GetSizeInFloats();
                if (fl < 4) return;
                int stride = fl * 4;
                var bytes = new byte[stride];
                using (var vb = new VertexBuffer(fl))
                {
                    vb.Map(fl);
                    vb.GetVertexStreamPositionColored().AddVertex(new VertexPositionColored(new XYZ(1.5, 2.5, 3.5), new ColorWithTransparency(11, 22, 33, 44)));
                    IntPtr h = vb.GetMappedHandle();
                    if (h != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Copy(h, bytes, 0, stride);
                    vb.Unmap();
                }
                if (BitConverter.ToSingle(bytes, 0) != 1.5f || BitConverter.ToSingle(bytes, 4) != 2.5f || BitConverter.ToSingle(bytes, 8) != 3.5f)
                {
                    Log.Write("Buffer", "scrittura diretta non disponibile (posizione non riconosciuta): uso l'API");
                    return;
                }
                int oR = -1, oG = -1, oB = -1, oA = -1;
                bool opacity = false;
                for (int i = 12; i < stride; i++)
                {
                    if (bytes[i] == 11 && oR < 0) oR = i;
                    else if (bytes[i] == 22 && oG < 0) oG = i;
                    else if (bytes[i] == 33 && oB < 0) oB = i;
                    else if (bytes[i] == 44 && oA < 0) { oA = i; opacity = false; }
                    else if (bytes[i] == 211 && oA < 0) { oA = i; opacity = true; }
                }
                if (oR < 0 || oG < 0 || oB < 0)
                {
                    Log.Write("Buffer", "scrittura diretta non disponibile (colore non riconosciuto: " + BitConverter.ToString(bytes) + "): uso l'API");
                    return;
                }
                _stride = stride; _offR = oR; _offG = oG; _offB = oB; _offA = oA; _alphaOpacity = opacity;
                _raw = 1;
                Log.Write("Buffer", string.Format("scrittura diretta attiva: {0} byte per vertice, colore R{1} G{2} B{3} A{4}{5}", stride, oR, oG, oB, oA, opacity ? " (opacità)" : ""));
            }
            catch (Exception ex)
            {
                _raw = -1;
                Log.Error("Buffer (verifica scrittura diretta)", ex);
            }
        }

        private static unsafe VertexBuffer MakeBuffer(int oc, int nv)
        {
            float[] bxs = _bx[oc], bys = _by[oc], bzs = _bz[oc];
            byte[] bcs = _bc[oc];
            if (_raw == 0) CalibrateRaw();
            int fl = nv * VertexPositionColored.GetSizeInFloats();
            var vb = new VertexBuffer(fl);
            vb.Map(fl);
            if (_raw == 1)
            {
                byte* p = (byte*)vb.GetMappedHandle().ToPointer();
                int stride = _stride, oR = _offR, oG = _offG, oB = _offB, oA = _offA;
                bool op = _alphaOpacity;
                for (int i = 0; i < nv; i++, p += stride)
                {
                    *(float*)p = bxs[i]; *(float*)(p + 4) = bys[i]; *(float*)(p + 8) = bzs[i];
                    for (int q = 12; q < stride; q++) p[q] = 0;
                    int o = i * 4;
                    p[oR] = bcs[o]; p[oG] = bcs[o + 1]; p[oB] = bcs[o + 2];
                    if (oA >= 0) p[oA] = op ? (byte)(255 - bcs[o + 3]) : bcs[o + 3];
                }
            }
            else
            {
                var list = new List<VertexPositionColored>(nv);
                for (int i = 0; i < nv; i++)
                {
                    int o = i * 4;
                    list.Add(new VertexPositionColored(new XYZ(bxs[i], bys[i], bzs[i]), new ColorWithTransparency(bcs[o], bcs[o + 1], bcs[o + 2], bcs[o + 3])));
                }
                vb.GetVertexStreamPositionColored().AddVertices(list);
                foreach (VertexPositionColored v in list) { var d = v as IDisposable; if (d != null) d.Dispose(); }
            }
            vb.Unmap();
            return vb;
        }

        /// <summary>Nuvola illuminata dal pannello Visibilità (mouse sopra il nome)?</summary>
        public bool Highlighted { get { return string.Equals(CloudHighlight.Folder, Folder, StringComparison.OrdinalIgnoreCase); } }

        private static EffectInstance _effectT;
        private static double _effectTValue = -1;

        /// <summary>Effetto di disegno: opaco, oppure con la trasparenza dello stile.</summary>
        private static EffectInstance EffectFor(Style st, bool highlighted)
        {
            EnsureShared();
            double t = highlighted || !st.NeedsTransparentPass ? 0 : st.DrawTransparency;
            if (t <= 0) return _effect;
            if (_effectT == null) _effectT = new EffectInstance(VertexFormatBits.PositionColored);
            if (Math.Abs(_effectTValue - t) > 1e-6) { _effectT.SetTransparency(t); _effectTValue = t; }
            return _effectT;
        }

        /// <summary>
        /// Disegna i primi «want» punti del nodo (in ordine a strati). Ogni pezzo sa da quale tratto della sorgente vengono
        /// i suoi punti: i pezzi dei vari ottanti si riempiono a ritmi diversi, quindi ognuno si valuta per conto suo.
        /// </summary>
        private static int Draw(TileGpu gpu, int want, EffectInstance effect, int octMask)
        {
            int drawn = 0;
            foreach (GpuChunk c in gpu.Chunks)
            {
                if (c.Oct >= 0 && (octMask & (1 << c.Oct)) == 0) continue;
                if (c.SrcStart >= want) continue;
                int n = c.Points;
                int span = c.SrcEnd - c.SrcStart;
                // pezzo a cavallo del limite: solo la sua parte iniziale (l'ordine a strati la rende uniforme)
                if (c.SrcEnd > want && span > 0) n = Math.Max(1, (int)((long)c.Points * (want - c.SrcStart) / span));
                drawn += n;
                if (gpu.Points)
                    DrawContext.FlushBuffer(c.Vb, c.Points, _pointIb, n * IndexPoint.GetSizeInShortInts(),
                        _format, effect, PrimitiveType.PointList, 0, n);
                else
                    DrawContext.FlushBuffer(c.Vb, c.Points * BigVerts, _octaIb, n * BigTris * IndexTriangle.GetSizeInShortInts(),
                        _format, effect, PrimitiveType.TriangleList, 0, n * BigTris);
            }
            return drawn;
        }

        // ------------------------------------------------------------------ selezione di un punto (Misura punti)

        /// <summary>Punto caricato più vicino al raggio (entro tol), il più vicino all'osservatore. t = profondità lungo dir.</summary>
        public bool Pick(XYZ origin, XYZ dir, double tol, Style st, ref double bestT, ref XYZ best)
        {
            bool found = false;
            double tol2 = tol * tol;
            for (int i = 0; i < Tiles.Length; i++)
            {
                TileCpu c = Volatile.Read(ref Cpu[i]);
                if (c == null) continue;
                for (int k = 0; k < c.Count; k++)
                {
                    double x, y, z;
                    ToModel(c.Pos[k * 3], c.Pos[k * 3 + 1], c.Pos[k * 3 + 2], out x, out y, out z);
                    double vx = x - origin.X, vy = y - origin.Y, vz = z - origin.Z;
                    double t = vx * dir.X + vy * dir.Y + vz * dir.Z;
                    double px = vx - t * dir.X, py = vy - t * dir.Y, pz = vz - t * dir.Z;
                    if (px * px + py * py + pz * pz > tol2) continue;
                    if (t >= bestT) continue;
                    if (st != null && st.Clip != null && !st.Clip.Contains(x, y, z)) continue;
                    bestT = t;
                    best = new XYZ(x, y, z);
                    found = true;
                }
            }
            return found;
        }

        /// <summary>Libera i buffer grafici (da chiamare sul thread di Revit).</summary>
        public void DropGpu()
        {
            for (int i = 0; i < _gpu.Length; i++)
            {
                if (_gpuO[i] != null) { _gpuO[i].Dispose(); _gpuO[i] = null; }
                if (_gpu[i] != null) { _gpu[i].Dispose(); _gpu[i] = null; }
            }
        }

        public void Dispose()
        {
            DropGpu();
            for (int i = 0; i < Cpu.Length; i++) Cpu[i] = null;
        }
    }

    /// <summary>
    /// Diagnostica: colora i punti per livello dell'octree (punti grandi a colori pieni, punti di un pixel più chiari),
    /// per vedere quali blocchi si stanno disegnando e dove.
    /// </summary>
    internal static class CloudDebug
    {
        public static volatile bool Levels;

        private static readonly byte[,] Palette =
        {
            { 230, 25, 75 }, { 245, 130, 48 }, { 255, 225, 25 }, { 60, 180, 75 }, { 66, 212, 244 },
            { 67, 99, 216 }, { 145, 30, 180 }, { 240, 50, 230 }, { 128, 128, 0 }, { 0, 128, 128 }, { 128, 0, 0 }, { 0, 0, 117 }
        };

        public static void LevelColor(int level, bool pixelPoints, out byte r, out byte g, out byte b)
        {
            int i = Math.Max(0, level) % Palette.GetLength(0);
            r = Palette[i, 0]; g = Palette[i, 1]; b = Palette[i, 2];
            if (pixelPoints) { r = (byte)((r + 255) / 2); g = (byte)((g + 255) / 2); b = (byte)((b + 255) / 2); }
        }
    }

    /// <summary>Nuvola da illuminare del suo colore (mouse sopra il nome nel pannello Visibilità).</summary>
    internal static class CloudHighlight
    {
        private static volatile string _folder;
        public static string Folder { get { return _folder; } }

        public static void Set(string folder)
        {
            if (string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase)) return;
            _folder = folder;
            Redraw.Request();
        }
    }

    /// <summary>Vertici di un nodo preparati in background, nella disposizione del buffer grafico.</summary>
    internal sealed class Baked
    {
        public int StyleVersion, GeoVersion, ColorKey, Built, FullCount;
        public bool Points, Highlight;
        public double Radius;
        public Baked PointsTwin;   // versione a punti piccoli preparata insieme (solo per i job WithPoints)
        public readonly List<BakedChunk> Chunks = new List<BakedChunk>();
    }

    internal sealed class BakedChunk
    {
        public byte[] Data;
        public int Verts, Points, Oct, SrcStart, SrcEnd;
    }

    internal sealed class BakeJob
    {
        public CloudLayer Layer;
        public int Node, UpTo, Key, Argb;
        public bool Octa, Highlight, WithPoints;   // WithPoints: insieme ai punti grandi si preparano anche quelli piccoli
        public double Radius;
        public Style Style;
        public int Prio;          // 0 = blocco in vista senza buffer o con più punti, 1 = precaricamento, 2 = solo raggio cambiato
    }

    /// <summary>
    /// Preparazione dei vertici in background (2-3 thread): il calcolo pesante esce dal fotogramma di Revit, che deve solo
    /// copiare i dati pronti nella scheda video. Le richieste di uno stile superato si scartano.
    /// </summary>
    internal static class CloudBaker
    {
        // tre code per priorità (vedi BakeJob.Prio): prima i blocchi che mancano nella vista, poi il precaricamento, per
        // ultimi i blocchi già disegnati da rifare solo per il raggio. Con una coda sola i blocchi mancanti aspettavano
        // dietro a quelli da rifare (registro del 13/11: fino a 48 in coda).
        private static readonly ConcurrentQueue<BakeJob>[] _queues = { new ConcurrentQueue<BakeJob>(), new ConcurrentQueue<BakeJob>(), new ConcurrentQueue<BakeJob>() };
        private static readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private static Thread[] _threads;
        private static int _busy;

        public static int Queued { get { return _queues[0].Count + _queues[1].Count + _queues[2].Count; } }
        public static int QueuedUrgent { get { return _queues[0].Count; } }
        public static bool Busy { get { return Volatile.Read(ref _busy) > 0 || Queued > 0; } }

        private static bool TryTake(out BakeJob j)
        {
            for (int p = 0; p < _queues.Length; p++) if (_queues[p].TryDequeue(out j)) return true;
            j = null;
            return false;
        }
        public static long BakeTicks, BakePoints;         // diagnostica: tempo e punti preparati
        public static long UploadTicks, UploadPoints;     // diagnostica: tempo e punti copiati nella scheda video
        public static readonly long[] ReasonPoints = new long[6]; // diagnostica: punti preparati per motivo (vedi BakeReason)

        public static void Enqueue(BakeJob j)
        {
            int[] pend = j.Octa ? j.Layer.BakePendO : j.Layer.BakePendP;
            if (Interlocked.CompareExchange(ref pend[j.Node], 1, 0) != 0) return;
            _queues[Math.Max(0, Math.Min(2, j.Prio))].Enqueue(j);
            if (_threads == null)
            {
                // preparatori: tanti quanti i processori lasciando liberi quelli di Revit (con 16 processori: 12)
                int n = AdvancedSettings.Current.Effective.BakerThreads;
                var t = new Thread[n];
                for (int i = 0; i < n; i++)
                {
                    t[i] = new Thread(Loop) { IsBackground = true, Name = "PointDeviation baker " + (i + 1), Priority = ThreadPriority.BelowNormal };
                    t[i].Start();
                }
                _threads = t;
            }
            _signal.Release();
        }

        private static void Loop()
        {
            while (true)
            {
                BakeJob j;
                if (!TryTake(out j)) { _signal.Wait(500); continue; }
                Interlocked.Increment(ref _busy);
                bool done = false;
                Baked[] dest = j.Octa ? j.Layer.BakedO : j.Layer.BakedP;
                int[] pend = j.Octa ? j.Layer.BakePendO : j.Layer.BakePendP;
                try
                {
                    // stile cambiato nel frattempo: la richiesta non serve più
                    // nodo uscito dalla vista mentre era in coda (rotazione veloce): non si prepara
                    if (j.Style.Version == j.Layer.CurrentStyleVersion && j.Layer.IdleMs(j.Node) < 2000)
                    {
                        int why = j.Prio == 1 ? 5 : j.Layer.BakeReason(j);   // 5 = precaricamento
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        Baked bk = j.Layer.Bake(j);
                        if (bk != null)
                        {
                            Interlocked.Add(ref BakeTicks, sw.ElapsedTicks);
                            Interlocked.Add(ref BakePoints, bk.Built);
                            Interlocked.Add(ref ReasonPoints[why], bk.Built);
                            if (bk.PointsTwin != null) { Volatile.Write(ref j.Layer.BakedP[j.Node], bk.PointsTwin); bk.PointsTwin = null; }
                            Volatile.Write(ref dest[j.Node], bk);
                            done = true;
                        }
                    }
                }
                catch (Exception ex) { Log.Error("Preparazione blocco " + j.Node, ex); }
                finally
                {
                    Interlocked.Exchange(ref pend[j.Node], 0);
                    Interlocked.Decrement(ref _busy);
                    if (done) Redraw.Request();
                }
            }
        }
    }

    /// <summary>Colori automatici delle nuvole (modalità «Nuvola»), stabili per cartella.</summary>
    internal static class CloudPalette
    {
        private static readonly int[] Colors =
        {
            System.Drawing.Color.FromArgb(230, 70, 110).ToArgb(), System.Drawing.Color.FromArgb(40, 140, 230).ToArgb(),
            System.Drawing.Color.FromArgb(60, 180, 90).ToArgb(), System.Drawing.Color.FromArgb(240, 160, 30).ToArgb(),
            System.Drawing.Color.FromArgb(150, 90, 220).ToArgb(), System.Drawing.Color.FromArgb(20, 180, 180).ToArgb(),
            System.Drawing.Color.FromArgb(200, 110, 60).ToArgb(), System.Drawing.Color.FromArgb(120, 120, 120).ToArgb()
        };

        public static int For(string folder)
        {
            int h = 0;
            foreach (char ch in (folder ?? "").ToLowerInvariant()) h = h * 31 + ch;
            return Colors[(h & 0x7FFFFFFF) % Colors.Length];
        }
    }

    /// <summary>Tempo a disposizione in un fotogramma per costruire buffer grafici.</summary>
    internal sealed class RenderBudget
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public bool MoreWork;
        /// <summary>La vista si sta muovendo (orbita, zoom): niente lavoro pesante, si disegna quello che c'è.</summary>
        public bool Moving;
        /// <summary>Versioni leggere dopo un cambio di stile: tutta la vista deve aggiornarsi subito.</summary>
        /// Le copie nella scheda video si fanno solo sul thread di disegno di Revit: in movimento al massimo 20 ms per
        /// fotogramma (prima 50: a ogni fotogramma la navigazione si fermava per copiare), da fermi 120 ms (prima 250).
        public bool CoarseTime { get { return _sw.ElapsedMilliseconds < (Moving ? _perf.UploadMovingMs : _perf.UploadStillMs); } }
        private readonly PerfValues _perf = AdvancedSettings.Current.Effective;
        /// <summary>Aggiunta di dettaglio: poco per volta e mai mentre ci si muove.</summary>
        public bool RefineTime { get { return !Moving && _sw.ElapsedMilliseconds < 70; } }
    }

    /// <summary>
    /// Zona inquadrata di ogni vista aperta: angoli di zoom (coordinate di modello) e larghezza della finestra in pixel.
    /// Si legge dall'interfaccia di Revit quando è a riposo (Idling) e si usa durante il disegno.
    /// </summary>
    internal static class ZoomCache
    {
        public sealed class Entry
        {
            public XYZ C0, C1;
            public int WidthPx;
        }

        private static readonly ConcurrentDictionary<long, Entry> _map = new ConcurrentDictionary<long, Entry>();

        public static Entry Get(View v)
        {
            Entry e;
            return v != null && _map.TryGetValue(v.Id.Value, out e) ? e : null;
        }

        /// <summary>Aggiorna le viste aperte; true se qualcosa è cambiato (zoom o spostamento).</summary>
        public static bool Update(Autodesk.Revit.UI.UIDocument uidoc)
        {
            bool changed = false;
            foreach (Autodesk.Revit.UI.UIView uiv in uidoc.GetOpenUIViews())
            {
                IList<XYZ> c = uiv.GetZoomCorners();
                Autodesk.Revit.DB.Rectangle r = uiv.GetWindowRectangle();
                var e = new Entry { C0 = c[0], C1 = c[1], WidthPx = Math.Max(1, r.Right - r.Left) };
                Entry old;
                long key = uiv.ViewId.Value;
                if (!_map.TryGetValue(key, out old) || !old.C0.IsAlmostEqualTo(e.C0, 1e-6) || !old.C1.IsAlmostEqualTo(e.C1, 1e-6) || old.WidthPx != e.WidthPx)
                {
                    _map[key] = e;
                    changed = true;
                }
            }
            return changed;
        }
    }

    /// <summary>
    /// Camera della vista per il livello di dettaglio: dimensione di un pixel (piedi) alla distanza di un punto,
    /// e se un blocco è fuori dall'inquadratura. Direzioni dalla vista, zona inquadrata dagli angoli di zoom.
    /// </summary>
    internal sealed class ViewCam
    {
        public XYZ Pos, Dir, Up, Right;
        public bool Perspective;
        public double HalfW, HalfH, Target;
        public double PxAtTarget;
        public bool Valid;

        public double OffU, OffV;      // prospettiva: centro della finestra rispetto all'asse (alla distanza del bersaglio)
        public double Margin = 1.0;    // allargamento dell'inquadratura (precaricamento: blocchi appena fuori dai bordi)

        // Riquadro di sezione della vista 3D (null = nessuno): i blocchi interamente fuori non si scelgono, non si leggono
        // e non si preparano. Prima si caricavano comunque (Revit poi non li mostrava): lavoro per punti che non si vedono.
        private double[] _box;         // origine, assi X Y Z (3 ciascuno), min, max nelle coordinate del riquadro
        public int BoxCulled;          // diagnostica: blocchi esclusi dal riquadro nell'ultima scelta
        public bool HasBox { get { return _box != null; } }

        private void SetBox(View view)
        {
            _box = null;
            PlanRange = false; SectionRange = false;
            var vp = view as ViewPlan;
            if (vp != null) { SetPlanRange(vp); return; }
            if (view is ViewSection) { SetSectionRange(view); return; }
            var v3 = view as View3D;
            if (v3 == null || !v3.IsSectionBoxActive) return;
            BoundingBoxXYZ b = v3.GetSectionBox();
            if (b == null) return;
            Transform t = b.Transform ?? Transform.Identity;
            XYZ o = t.Origin, x = t.BasisX.Normalize(), y = t.BasisY.Normalize(), z = t.BasisZ.Normalize();
            _box = new[] { o.X, o.Y, o.Z, x.X, x.Y, x.Z, y.X, y.Y, y.Z, z.X, z.Y, z.Z, b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z };
        }

        // Piante: intervallo di vista. Prima si sceglievano i blocchi di tutta l'altezza della nuvola (tutti i piani uno
        // sopra l'altro, 7-11 M punti e 100-160 ms di disegno per fotogramma) anche se Revit mostra solo la fascia tra
        // il piano di taglio e la profondità di vista. Si escludono i blocchi interamente fuori da quella fascia
        // (pianta: dalla profondità di vista al piano di taglio; pianta del controsoffitto: dal taglio al limite
        // superiore). Livelli «illimitato» o «livello sotto»: nessun limite da quella parte.
        private void SetPlanRange(ViewPlan vp)
        {
            PlanViewRange r = vp.GetViewRange();
            if (r == null) return;
            Document doc = vp.Document;
            bool ceiling = vp.ViewType == ViewType.CeilingPlan;
            double cut = PlaneZ(doc, vp, r, PlanViewPlane.CutPlane);
            if (double.IsNaN(cut)) return;
            double lo, hi;
            if (ceiling) { lo = cut; hi = PlaneZ(doc, vp, r, PlanViewPlane.TopClipPlane); if (double.IsNaN(hi)) hi = 1e30; }
            else
            {
                hi = cut;
                lo = PlaneZ(doc, vp, r, PlanViewPlane.ViewDepthPlane);
                double bottom = PlaneZ(doc, vp, r, PlanViewPlane.BottomClipPlane);
                if (double.IsNaN(lo) || (!double.IsNaN(bottom) && bottom < lo)) lo = bottom;
                if (double.IsNaN(lo)) lo = -1e30;
            }
            if (hi <= lo) return;
            _box = new[] { 0.0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, -1e30, -1e30, lo, 1e30, 1e30, hi };
            PlanRange = true;
        }

        public bool PlanRange;         // il ritaglio viene dall'intervallo di vista di una pianta
        public bool SectionRange;      // il ritaglio viene dal campo di una sezione o di un prospetto

        // Sezioni e prospetti: Revit mostra solo cio' che sta nel loro campo, cioe' oltre il piano della sezione (verso la
        // scena), entro il limite lontano se attivo ed entro la regione di ritaglio se attiva. Si escludono i blocchi
        // interamente fuori. Assi del ritaglio: destra, alto e direzione verso la scena (-ViewDirection, che in Revit
        // punta verso chi guarda), origine sul piano della vista. Il piano vicino vale solo per le sezioni e i dettagli:
        // nei prospetti il segno sta fuori dall'edificio e non serve.
        private void SetSectionRange(View view)
        {
            XYZ o = view.Origin, x = view.RightDirection.Normalize(), y = view.UpDirection.Normalize(), z = view.ViewDirection.Normalize().Negate();
            // origine: quella del ritaglio della vista, che sta sul piano della sezione o del prospetto
            BoundingBoxXYZ cb0 = view.CropBox;
            if (cb0 != null && cb0.Transform != null) o = cb0.Transform.Origin;
            bool section = view.ViewType == ViewType.Section || view.ViewType == ViewType.Detail;
            double xmin = -1e30, xmax = 1e30, ymin = -1e30, ymax = 1e30, zmin = section ? 0 : -1e30, zmax = 1e30;
            Parameter fc = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
            Parameter fo = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
            if (fc != null && fc.AsInteger() != 0 && fo != null && fo.AsDouble() > 0) zmax = fo.AsDouble();
            if (view.CropBoxActive)
            {
                BoundingBoxXYZ b = view.CropBox;
                if (b != null)
                {
                    Transform t = b.Transform ?? Transform.Identity;
                    xmin = ymin = 1e30; xmax = ymax = -1e30;
                    for (int k = 0; k < 8; k++)
                    {
                        XYZ c = t.OfPoint(new XYZ((k & 1) == 0 ? b.Min.X : b.Max.X, (k & 2) == 0 ? b.Min.Y : b.Max.Y, (k & 4) == 0 ? b.Min.Z : b.Max.Z)) - o;
                        double u = c.DotProduct(x), v = c.DotProduct(y);
                        xmin = Math.Min(xmin, u); xmax = Math.Max(xmax, u); ymin = Math.Min(ymin, v); ymax = Math.Max(ymax, v);
                    }
                }
            }
            if (zmin <= -1e30 && zmax >= 1e30 && xmin <= -1e30) return;
            _box = new[] { o.X, o.Y, o.Z, x.X, x.Y, x.Z, y.X, y.Y, y.Z, z.X, z.Y, z.Z, xmin, ymin, zmin, xmax, ymax, zmax };
            SectionRange = true;
        }

        private static double PlaneZ(Document doc, ViewPlan vp, PlanViewRange r, PlanViewPlane plane)
        {
            ElementId id = r.GetLevelId(plane);
            if (id == null || id == ElementId.InvalidElementId || id == PlanViewRange.Unlimited || id == PlanViewRange.LevelBelow)
                return double.NaN;
            Level lv = id == PlanViewRange.Current ? vp.GenLevel : doc.GetElement(id) as Level;
            if (lv == null) return double.NaN;
            return lv.ProjectElevation + r.GetOffset(plane);
        }

        /// <summary>La sfera (centro, raggio) tocca il riquadro di sezione? (sempre vero senza riquadro)</summary>
        public bool InBox(double px, double py, double pz, double radius)
        {
            double[] b = _box;
            if (b == null) return true;
            double dx = px - b[0], dy = py - b[1], dz = pz - b[2];
            double lx = dx * b[3] + dy * b[4] + dz * b[5];
            double ly = dx * b[6] + dy * b[7] + dz * b[8];
            double lz = dx * b[9] + dy * b[10] + dz * b[11];
            return lx >= b[12] - radius && lx <= b[15] + radius && ly >= b[13] - radius && ly <= b[16] + radius
                && lz >= b[14] - radius && lz <= b[17] + radius;
        }

        /// <summary>Copia con l'inquadratura allargata di un fattore.</summary>
        public ViewCam Widened(double f)
        {
            var c = (ViewCam)MemberwiseClone();
            c.Margin = Margin * f;
            return c;
        }

        public static ViewCam From(Camera c, View view)
        {
            ViewCam v = FromCamera(c, view);
            try { v.SetBox(view); } catch { }
            return v;
        }

        private static ViewCam FromCamera(Camera c, View view)
        {
            // camera di DirectContext3D: posizione, direzioni e ampiezza esatte di questo fotogramma
            try
            {
                var v = new ViewCam();
                v.Dir = c.ViewDirection.Normalize();
                v.Up = c.UpDirection.Normalize();
                var v3 = view as View3D;
                v.Perspective = v3 != null && v3.IsPerspective;
                // il verso delle direzioni della camera non è documentato: lo si allinea a quello della vista
                XYZ fwd, up;
                if (v3 != null) { ViewOrientation3D o = v3.GetOrientation(); fwd = o.ForwardDirection; up = o.UpDirection; }
                else { fwd = view.ViewDirection.Negate(); up = view.UpDirection; }
                if (v.Dir.DotProduct(fwd) < 0) v.Dir = v.Dir.Negate();
                if (v.Up.DotProduct(up) < 0) v.Up = v.Up.Negate();
                v.Right = v.Dir.CrossProduct(v.Up).Normalize();
                double hx = Math.Abs(c.HorizontalExtent), vx = Math.Abs(c.VerticalExtent);
                ZoomCache.Entry z = ZoomCache.Get(view);
                int widthPx = z != null ? z.WidthPx : 1600;
                if (hx > 1e-9 && vx > 1e-9)
                {
                    v.HalfW = hx * 0.5; v.HalfH = vx * 0.5;
                    v.PxAtTarget = Math.Max(1e-6, hx / widthPx);
                    v.Target = Math.Max(1e-3, Math.Abs(c.TargetDistance));
                    if (v.Perspective)
                    {
                        v.Pos = c.EyePosition;
                        v.OffU = c.HorizontalOffset; v.OffV = c.VerticalOffset;
                    }
                    else
                    {
                        // vista parallela: centro della zona inquadrata. Gli angoli di zoom della finestra lo danno senza
                        // ambiguità (aggiornati a riposo); la camera serve finché non ci sono o durante uno zoom.
                        XYZ camCenter = c.EyePosition + v.Right * c.HorizontalOffset + v.Up * c.VerticalOffset;
                        if (z != null)
                        {
                            XYZ mid = (z.C0 + z.C1) / 2.0;
                            double w = Math.Abs((z.C1 - z.C0).DotProduct(v.Right));
                            // angoli di zoom coerenti con l'ampiezza attuale (nessuno zoom in corso): si usano quelli
                            if (Math.Abs(w - hx) < hx * 0.05) v.Pos = mid;
                            else v.Pos = camCenter;
                            LogCamera(view, mid, camCenter, w, hx);
                        }
                        else v.Pos = camCenter;
                    }
                    v.Valid = true;
                    return v;
                }
            }
            catch { }
            return FromView(c, view);
        }

        private static long _lastCamLog;

        /// <summary>Diagnostica: confronto tra il centro dagli angoli di zoom e quello dalla camera (al massimo ogni 10 s).</summary>
        private static void LogCamera(View view, XYZ zoomMid, XYZ camCenter, double zoomW, double camW)
        {
            long now = Environment.TickCount;
            if (now - _lastCamLog < 10000) return;
            _lastCamLog = now;
            Log.Debug("Camera", string.Format("«{0}»: centro da zoom {1:F1},{2:F1},{3:F1}  da camera {4:F1},{5:F1},{6:F1}  (distanza {7:F2} ft); ampiezza zoom {8:F1} camera {9:F1} ft",
                view.Name, zoomMid.X, zoomMid.Y, zoomMid.Z, camCenter.X, camCenter.Y, camCenter.Z,
                (zoomMid - camCenter).GetLength(), zoomW, camW));
        }

        /// <summary>Riserva: direzioni dalla vista e zona inquadrata dagli angoli di zoom della finestra.</summary>
        private static ViewCam FromView(Camera c, View view)
        {
            var v = new ViewCam();
            try
            {
                var v3 = view as View3D;
                if (v3 != null)
                {
                    ViewOrientation3D o = v3.GetOrientation();
                    v.Dir = o.ForwardDirection.Normalize();
                    v.Up = o.UpDirection.Normalize();
                    v.Pos = o.EyePosition;
                    v.Perspective = v3.IsPerspective;
                }
                else
                {
                    v.Dir = view.ViewDirection.Negate().Normalize();
                    v.Up = view.UpDirection.Normalize();
                    v.Pos = view.Origin;
                    v.Perspective = false;
                }
                v.Right = v.Dir.CrossProduct(v.Up).Normalize();

                ZoomCache.Entry z = ZoomCache.Get(view);
                if (v.Perspective)
                {
                    int px = z != null ? z.WidthPx : 1600;
                    v.Target = Math.Max(1e-3, Math.Abs(c.TargetDistance));
                    v.PxAtTarget = Math.Max(1e-6, Math.Abs(c.HorizontalExtent) / px);
                    v.HalfW = Math.Abs(c.HorizontalExtent) * 0.5;
                    v.HalfH = Math.Abs(c.VerticalExtent) * 0.5;
                    v.Valid = true;
                }
                else if (z != null)
                {
                    XYZ mid = (z.C0 + z.C1) / 2.0;
                    XYZ diag = z.C1 - z.C0;
                    double w = Math.Abs(diag.DotProduct(v.Right)), h = Math.Abs(diag.DotProduct(v.Up));
                    v.Pos = mid;
                    v.HalfW = w * 0.5; v.HalfH = h * 0.5;
                    v.PxAtTarget = Math.Max(1e-6, w / z.WidthPx);
                    v.Valid = true;
                }
            }
            catch { v.Valid = false; }
            if (!v.Valid) Log.Warn("Camera", "camera della vista non disponibile: nessun ritaglio per inquadratura");
            return v;
        }

        /// <summary>
        /// Frazione (0..1) di una sfera che cade dentro la finestra, approssimata con il quadrato che la contiene.
        /// Serve a contare nel limite di punti solo la parte di un blocco che si vede davvero.
        /// </summary>
        public double VisibleFraction(double x, double y, double z, double radius)
        {
            if (!Valid || radius <= 0) return 1;
            double dx = x - Pos.X, dy = y - Pos.Y, dz = z - Pos.Z;
            double u = dx * Right.X + dy * Right.Y + dz * Right.Z;
            double v = dx * Up.X + dy * Up.Y + dz * Up.Z;
            double w = HalfW, h = HalfH;
            if (Perspective)
            {
                double depth = dx * Dir.X + dy * Dir.Y + dz * Dir.Z;
                double s = Math.Max(0.1, depth) / Target;
                u -= OffU * s; v -= OffV * s; w *= s; h *= s;
            }
            double ou = Math.Max(0, Math.Min(u + radius, w) - Math.Max(u - radius, -w));
            double ov = Math.Max(0, Math.Min(v + radius, h) - Math.Max(v - radius, -h));
            return Math.Max(0.02, Math.Min(1, ou * ov / (4 * radius * radius)));
        }

        /// <summary>Distanza dal centro dello schermo, in frazioni di mezza finestra (0 = centro, 1 = bordo).</summary>
        /// <summary>
        /// Distanza a schermo del punto più vicino al centro di una sfera (centro, raggio) dal centro della finestra: 0 se
        /// la sfera tocca il centro, 1 sul bordo della finestra (si misura come CenterDistance).
        /// </summary>
        public double CenterDistanceSphere(double x, double y, double z, double radius)
        {
            if (!Valid) return 0;
            double dx = x - Pos.X, dy = y - Pos.Y, dz = z - Pos.Z;
            double u = dx * Right.X + dy * Right.Y + dz * Right.Z;
            double v = dx * Up.X + dy * Up.Y + dz * Up.Z;
            double s = 1;
            if (Perspective)
            {
                double depth = dx * Dir.X + dy * Dir.Y + dz * Dir.Z;
                s = Math.Max(0.1, depth) / Target;
                u -= OffU * s; v -= OffV * s;
            }
            return Math.Max(Math.Max(0, Math.Abs(u) - radius) / (HalfW * s), Math.Max(0, Math.Abs(v) - radius) / (HalfH * s));
        }

        public double CenterDistance(double x, double y, double z)
        {
            if (!Valid) return 0;
            double dx = x - Pos.X, dy = y - Pos.Y, dz = z - Pos.Z;
            double u = dx * Right.X + dy * Right.Y + dz * Right.Z;
            double v = dx * Up.X + dy * Up.Y + dz * Up.Z;
            double s = 1;
            if (Perspective)
            {
                double depth = dx * Dir.X + dy * Dir.Y + dz * Dir.Z;
                s = Math.Max(0.1, depth) / Target;
                u -= OffU * s; v -= OffV * s;
            }
            return Math.Max(Math.Abs(u) / (HalfW * s), Math.Abs(v) / (HalfH * s));
        }

        /// <summary>Dimensione di un pixel alla posizione data; false se il blocco è fuori inquadratura.</summary>
        public bool PixelSize(double x, double y, double z, double radius, out double px)
        {
            px = 0.05;
            if (!Valid) return true;
            if (_box != null && !InBox(x, y, z, radius)) { BoxCulled++; return false; }
            double dx = x - Pos.X, dy = y - Pos.Y, dz = z - Pos.Z;
            double depth = dx * Dir.X + dy * Dir.Y + dz * Dir.Z;
            double u = dx * Right.X + dy * Right.Y + dz * Right.Z;
            double v = dx * Up.X + dy * Up.Y + dz * Up.Z;
            if (Perspective)
            {
                if (depth < -radius) return false;
                double d = Math.Max(radius * 0.25, Math.Max(0.1, depth));
                double scale = d / Target;
                u -= OffU * scale; v -= OffV * scale;
                // margine abbondante: un blocco tagliato per errore lascia un buco ben visibile
                if (Math.Abs(u) > HalfW * scale * 1.3 * Margin + radius * 1.5 || Math.Abs(v) > HalfH * scale * 1.3 * Margin + radius * 1.5) return false;
                px = PxAtTarget * scale;
            }
            else
            {
                if (Math.Abs(u) > HalfW * 1.15 * Margin + radius || Math.Abs(v) > HalfH * 1.15 * Margin + radius) return false;
                px = PxAtTarget;
            }
            px = Math.Max(px, 1e-5);
            return true;
        }
    }

    /// <summary>Richiesta di ridisegno della vista, eseguita da Revit nell'evento Idling.</summary>
    internal static class Redraw
    {
        private static int _needed;
        private static long _lastRequest;
        public static void Request() { Interlocked.Exchange(ref _needed, 1); Interlocked.Exchange(ref _lastRequest, Environment.TickCount); }
        /// <summary>Millisecondi dall'ultima richiesta di ridisegno.</summary>
        public static long SinceLastMs { get { return Environment.TickCount - Interlocked.Read(ref _lastRequest); } }
        public static bool Take() { return Interlocked.Exchange(ref _needed, 0) == 1; }
        public static bool Pending { get { return Volatile.Read(ref _needed) == 1; } }
    }

    // =====================================================================================
    //  Caricamento in background: legge i blocchi dalla libreria, calcola distanze e normali.
    //  Non tocca mai l'API di Revit (non è thread-safe): i buffer grafici li costruisce il disegno.
    // =====================================================================================

    internal static class CloudLoader
    {
        private sealed class Job
        {
            public CloudLayer Layer;
            public int Tile, Target;
            public AnalysisSnapshot Analysis;
            public int DistKey;
            public bool Normals;
            public bool NeedDist;
            public Style St;
        }

        private static readonly ConcurrentQueue<Job> _queue = new ConcurrentQueue<Job>();
        private static readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private static Thread[] _threads;

        public static void Enqueue(CloudLayer layer, int tile, int target, Style st)
        {
            if (Interlocked.CompareExchange(ref layer.Pending[tile], 1, 0) != 0) return;
            _queue.Enqueue(new Job
            {
                Layer = layer, Tile = tile, Target = target,
                Analysis = st.NeedsDistances ? st.Analysis : null,
                DistKey = layer.DistKey(st.Analysis),
                NeedDist = st.NeedsDistances,
                Normals = st.NeedsNormals,
                St = st
            });
            EnsureThreads();
            _signal.Release();
        }

        /// <summary>Più lettori in parallelo: mentre uno legge dal disco un altro decodifica o calcola distanze.</summary>
        private static void EnsureThreads()
        {
            if (_threads != null) return;
            // lettori: con molti processori anche la lettura (e le distanze già salvate) va in parallelo
            int n = AdvancedSettings.Current.Effective.LoaderThreads;
            var t = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                t[i] = new Thread(Loop) { IsBackground = true, Name = "PointDeviation loader " + (i + 1), Priority = ThreadPriority.BelowNormal };
                t[i].Start();
            }
            _threads = t;
        }

        private static int _busy;

        /// <summary>Blocchi in coda (non ancora in lettura).</summary>
        public static int Queued { get { return _queue.Count; } }

        /// <summary>Ci sono blocchi in coda o in lettura?</summary>
        public static bool Busy { get { return Volatile.Read(ref _busy) > 0 || !_queue.IsEmpty; } }

        public static long ReadTicks, ReadBlocks, ReadPoints;   // diagnostica: blocchi letti, punti nuovi e tempo (somma dei thread)

        private static void Loop()
        {
            while (true)
            {
                Job j;
                if (!_queue.TryDequeue(out j)) { _signal.Wait(500); continue; }
                Interlocked.Increment(ref _busy);
                bool ok = false, changed = false;
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    changed = Process(j);
                    if (changed)
                    {
                        Interlocked.Add(ref ReadTicks, sw.ElapsedTicks);
                        Interlocked.Increment(ref ReadBlocks);
                        // preparazione subito, senza aspettare il fotogramma successivo (vedi BakeAfterLoad)
                        j.Layer.BakeAfterLoad(j.Tile, j.St);
                    }
                    ok = true;
                }
                catch (Exception ex) { Log.Error("Lettura blocco " + j.Tile + " di " + j.Layer.Name, ex); }
                finally
                {
                    // un blocco che non si riesce a leggere resta segnato (2) e non viene richiesto all'infinito
                    Interlocked.Exchange(ref j.Layer.Pending[j.Tile], ok ? 0 : 2);
                    Interlocked.Decrement(ref _busy);
                    if (changed) Redraw.Request();
                }
            }
        }

        private static readonly ParallelOptions _par = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

        [ThreadStatic] private static byte[] _tsRaw;

        private static bool Process(Job j)
        {
            CloudLayer L = j.Layer;
            // richiesta vecchia (la vista si è spostata altrove da tempo): non serve più
            // (5 s: con la coda più lunga, dopo una rotazione veloce restavano da leggere blocchi non più in vista)
            if (L.IdleMs(j.Tile) > 5000) { Redraw.Request(); return false; }
            TileEntry t = L.Tiles[j.Tile];
            TileCpu old = Volatile.Read(ref L.Cpu[j.Tile]);
            int have = old != null ? old.Count : 0;
            int target = Math.Min(t.Count, Math.Max(have, j.Target));

            var c = new TileCpu { Count = target };
            c.Pos = new float[target * 3];
            c.Rgb = new byte[target * 3];
            if (old != null)
            {
                Array.Copy(old.Pos, c.Pos, have * 3);
                Array.Copy(old.Rgb, c.Rgb, have * 3);
            }

            if (target > have)
            {
                int R = L.Info.RecordSize;
                bool fileNormals = L.Info.HasNormals;
                if (fileNormals)
                {
                    c.Nrm = new sbyte[target * 3];
                    if (old != null && old.Nrm != null) Array.Copy(old.Nrm, c.Nrm, Math.Min(old.Nrm.Length, have * 3));
                }
                double sc = L.Info.Scale / 0.3048;
                // buffer di lettura riusato dal thread (serve solo qui: i dati vanno nei vettori del blocco)
                int rawLen = (target - have) * R;
                if (_tsRaw == null || _tsRaw.Length < rawLen) _tsRaw = new byte[Math.Max(rawLen, 1 << 16)];
                byte[] raw = _tsRaw;
                using (var fs = new FileStream(L.PointsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                {
                    fs.Position = (t.FirstRecord + have) * R;
                    int got = LasSource.ReadFully(fs, raw, rawLen) / R;
                    target = have + got;
                    c.Count = target;
                    for (int k = 0; k < got; k++)
                    {
                        int o = k * R, w = (have + k) * 3;
                        c.Pos[w] = (float)(BitConverter.ToInt32(raw, o) * sc);
                        c.Pos[w + 1] = (float)(BitConverter.ToInt32(raw, o + 4) * sc);
                        c.Pos[w + 2] = (float)(BitConverter.ToInt32(raw, o + 8) * sc);
                        c.Rgb[w] = raw[o + 12]; c.Rgb[w + 1] = raw[o + 13]; c.Rgb[w + 2] = raw[o + 14];
                        if (fileNormals) { c.Nrm[w] = (sbyte)raw[o + 15]; c.Nrm[w + 1] = (sbyte)raw[o + 16]; c.Nrm[w + 2] = (sbyte)raw[o + 17]; }
                    }
                    if (fileNormals) c.NrmCount = target;
                    Interlocked.Add(ref ReadPoints, got);
                }
            }

            // distanze dal modello: quelle già calcolate per la stessa analisi si conservano sempre
            // (anche mostrando RGB), così riaccendendo l'analisi non si ricalcola nulla
            if (old != null && old.Dist != null && old.DistKey == j.DistKey)
            {
                c.Dist = new float[c.Count];
                c.DistCount = Math.Min(old.DistCount, c.Count);
                Array.Copy(old.Dist, c.Dist, c.DistCount);
                c.DistKey = old.DistKey;
            }
            if (j.NeedDist && j.Analysis != null)
            {
                if (c.Dist == null || c.DistKey != j.DistKey) { c.Dist = new float[c.Count]; c.DistCount = 0; c.DistKey = j.DistKey; }
                // prima quelle già salvate su disco (stessa analisi in un altro momento, o calcolate in background)
                DistCache dc = L.GetDistCache(j.Analysis);
                if (dc != null) c.DistCount = dc.Read(j.Tile, t.FirstRecord, c.DistCount, c.Count, c.Dist);
                int calcFrom = c.DistCount;
                TriangleBvh bvh = j.Analysis.Bvh;
                double range = j.Analysis.SearchFt;
                float[] dist = c.Dist;
                int m = c.Count - calcFrom;
                if (m > 0)
                {
                    var xyz = new double[m * 3];
                    for (int k = 0; k < m; k++)
                    {
                        int s3 = (calcFrom + k) * 3;
                        L.ToModel(c.Pos[s3], c.Pos[s3 + 1], c.Pos[s3 + 2], out xyz[k * 3], out xyz[k * 3 + 1], out xyz[k * 3 + 2]);
                    }
                    DistCompute.Run(bvh, range, xyz, dist, calcFrom, _par, ThreadPriority.BelowNormal);
                }
                c.DistCount = c.Count;
                if (dc != null && calcFrom < c.Count) dc.Write(j.Tile, t.FirstRecord, calcFrom, c.Count, c.Dist);
            }

            // normali: dal file (formato 2), altrimenti calcolate e ricalcolate quando i punti sono cresciuti molto
            if (L.Info.HasNormals)
            {
                if (c.Nrm == null && old != null && old.Nrm != null) { c.Nrm = old.Nrm; c.NrmCount = Math.Min(old.NrmCount, c.Count); }
            }
            else if (j.Normals)
            {
                if (old != null && old.Nrm != null && old.NrmCount >= c.Count * 0.6)
                {
                    c.Nrm = new sbyte[c.Count * 3];
                    Array.Copy(old.Nrm, c.Nrm, Math.Min(old.Nrm.Length, c.Nrm.Length));
                    c.NrmCount = Math.Min(old.NrmCount, c.Count);
                }
                else
                {
                    c.Nrm = Normals.Compute(c.Pos, c.Count, L.Spacing(j.Tile, c.Count));
                    c.NrmCount = c.Count;
                }
            }
            else if (old != null && old.Nrm != null)
            {
                c.Nrm = old.Nrm.Length >= c.Count * 3 ? old.Nrm : null;
                c.NrmCount = c.Nrm != null ? Math.Min(old.NrmCount, c.Count) : 0;
            }

            Volatile.Write(ref L.Cpu[j.Tile], c);
            return true;
        }
    }

    /// <summary>Normali stimate dai punti vicini (piano medio dei vicini in una griglia), per la modalità Normali.</summary>
    internal static class Normals
    {
        public static sbyte[] Compute(float[] pos, int n, double spacing)
        {
            var res = new sbyte[n * 3];
            if (n < 4) return res;
            double cell = Math.Max(1e-4, spacing * 3.0);
            var grid = new Dictionary<long, List<int>>();
            Func<double, long> q = v => (long)Math.Floor(v / cell);
            for (int i = 0; i < n; i++)
            {
                long key = Key(q(pos[i * 3]), q(pos[i * 3 + 1]), q(pos[i * 3 + 2]));
                List<int> l;
                if (!grid.TryGetValue(key, out l)) grid[key] = l = new List<int>(8);
                l.Add(i);
            }

            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
            {
                long gx = q(pos[i * 3]), gy = q(pos[i * 3 + 1]), gz = q(pos[i * 3 + 2]);
                double sx = 0, sy = 0, sz = 0; int cnt = 0;
                double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
                for (int a = -1; a <= 1; a++)
                    for (int b = -1; b <= 1; b++)
                        for (int c = -1; c <= 1; c++)
                        {
                            List<int> l;
                            if (!grid.TryGetValue(Key(gx + a, gy + b, gz + c), out l)) continue;
                            foreach (int k in l)
                            {
                                if (cnt >= 48) break;
                                double x = pos[k * 3], y = pos[k * 3 + 1], z = pos[k * 3 + 2];
                                sx += x; sy += y; sz += z;
                                xx += x * x; xy += x * y; xz += x * z; yy += y * y; yz += y * z; zz += z * z;
                                cnt++;
                            }
                        }
                if (cnt < 3) { res[i * 3 + 2] = 127; return; }
                double mx = sx / cnt, my = sy / cnt, mz = sz / cnt;
                double cxx = xx / cnt - mx * mx, cxy = xy / cnt - mx * my, cxz = xz / cnt - mx * mz;
                double cyy = yy / cnt - my * my, cyz = yz / cnt - my * mz, czz = zz / cnt - mz * mz;
                double nx, ny, nz;
                SmallestEigenvector(cxx, cxy, cxz, cyy, cyz, czz, out nx, out ny, out nz);
                if (nz < 0) { nx = -nx; ny = -ny; nz = -nz; }
                res[i * 3] = (sbyte)Math.Round(nx * 127); res[i * 3 + 1] = (sbyte)Math.Round(ny * 127); res[i * 3 + 2] = (sbyte)Math.Round(nz * 127);
            });
            return res;
        }

        private static long Key(long x, long y, long z)
        {
            return ((x & 0x1FFFFF) << 42) | ((y & 0x1FFFFF) << 21) | (z & 0x1FFFFF);
        }

        /// <summary>Autovettore del minimo autovalore di una matrice simmetrica 3x3 (Jacobi).</summary>
        internal static void SmallestEigenvector(double a00, double a01, double a02, double a11, double a12, double a22,
            out double x, out double y, out double z)
        {
            double[,] a = { { a00, a01, a02 }, { a01, a11, a12 }, { a02, a12, a22 } };
            double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 12; sweep++)
            {
                double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (off < 1e-14) break;
                for (int p = 0; p < 2; p++)
                    for (int r = p + 1; r < 3; r++)
                    {
                        if (Math.Abs(a[p, r]) < 1e-18) continue;
                        double theta = (a[r, r] - a[p, p]) / (2 * a[p, r]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akr = a[k, r];
                            a[k, p] = c * akp - s * akr; a[k, r] = s * akp + c * akr;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], ark = a[r, k];
                            a[p, k] = c * apk - s * ark; a[r, k] = s * apk + c * ark;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkr = v[k, r];
                            v[k, p] = c * vkp - s * vkr; v[k, r] = s * vkp + c * vkr;
                        }
                    }
            }
            int m = 0;
            if (a[1, 1] < a[m, m]) m = 1;
            if (a[2, 2] < a[m, m]) m = 2;
            x = v[0, m]; y = v[1, m]; z = v[2, m];
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-12) { x = 0; y = 0; z = 1; return; }
            x /= len; y /= len; z /= len;
        }
    }
}
