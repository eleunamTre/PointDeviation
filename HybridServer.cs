using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace PointDeviation
{
    /// <summary>
    /// Server DirectContext3D: per ogni vista disegna le nuvole del progetto che Revit considera visibili
    /// (elemento nativo non nascosto, categoria accesa, nuvola caricata). Il livello di dettaglio dipende
    /// dalla camera della vista; il caricamento avviene in background e quando arriva qualcosa di nuovo
    /// la vista si ridisegna da sola (evento Idling).
    /// </summary>
    internal sealed class HybridServer : IDirectContext3DServer
    {
        public static readonly Guid ServerGuid = new Guid("5c1f2b8e-7a41-4d3b-8e55-0b6a9d2c4f18");
        private static HybridServer _instance;

        // ridisegni chiesti da noi: mai più di uno ogni tanto, perché Revit ridisegna tutta la vista (modello compreso)
        private const int MinRefreshMs = 150;
        private const int MinRefreshFlatMs = 700;
        private const int ZoomSettleMs = 150;
        private static long _lastRefresh, _zoomTick, _lastVisLog;
        private static bool _zoomDirty;

        public static void Register()
        {
            if (_instance != null) return;
            var svc = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
            if (svc == null) return;
            _instance = new HybridServer();
            svc.AddServer(_instance);
            IList<Guid> ids = svc.GetActiveServerIds();
            ids.Add(ServerGuid);
            svc.SetActiveServers(ids);
        }

        public static void Unregister()
        {
            try
            {
                var svc = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
                if (svc != null && _instance != null) svc.RemoveServer(ServerGuid);
            }
            catch { }
            _instance = null;
        }

        /// <summary>
        /// Idling: ridisegna la vista attiva quando ci sono novità (blocchi caricati, dettaglio da aggiungere, zoom cambiato),
        /// con un ritmo limitato. Dopo uno zoom aspetta che la vista sia ferma prima di ricalcolare il dettaglio.
        /// </summary>
        public static void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                var app = sender as UIApplication;
                if (app == null || app.ActiveUIDocument == null) return;
                DocumentData d = DeviationController.GetData(app.ActiveUIDocument.Document);
                if (d == null || d.Layers.Count == 0) { Redraw.Take(); return; }

                long now = Environment.TickCount;
                if (ZoomCache.Update(app.ActiveUIDocument)) { _zoomDirty = true; _zoomTick = now; }

                // Piante, sezioni e prospetti: RefreshActiveView ridisegna tutta la vista, anche griglie, simboli e
                // annotazioni, che si vedono lampeggiare (registro 13/11 17:59: «a ogni zoom si ricaricano tutti gli
                // elementi»). Lì niente ridisegno extra a fine zoom (Revit ha appena ridisegnato da solo; se manca
                // dettaglio lo chiede il disegno) e i ridisegni per i blocchi in arrivo al massimo ogni 700 ms.
                bool flat = !(app.ActiveUIDocument.ActiveView is View3D);
                bool refresh = false;
                if (_zoomDirty && now - _zoomTick >= ZoomSettleMs)
                {
                    _zoomDirty = false;
                    if (!flat) refresh = true;
                }
                if (Redraw.Pending && now - _lastRefresh >= (flat ? MinRefreshFlatMs : MinRefreshMs)) { Redraw.Take(); refresh = true; }
                if (refresh)
                {
                    _lastRefresh = now;
                    app.ActiveUIDocument.RefreshActiveView();
                }
                // lavoro in sospeso (o appena concluso: il caricatore può finire dopo questo controllo):
                // Revit ci richiama subito invece che dopo una lunga pausa
                if (_zoomDirty || Redraw.Pending || CloudLoader.Busy || CloudBaker.Busy || Redraw.SinceLastMs < 3000) e.SetRaiseWithoutDelay();
            }
            catch { }
        }

        private static readonly Dictionary<long, double[]> _lastCam = new Dictionary<long, double[]>();
        private static readonly Dictionary<long, long> _camTick = new Dictionary<long, long>();

        /// <summary>La camera di questa vista è cambiata negli ultimi istanti?</summary>
        private static bool CameraMoving(View view, ViewCam cam)
        {
            if (cam == null || !cam.Valid || cam.Pos == null) return false;
            long id = view.Id.Value;
            double[] sig = { cam.Pos.X, cam.Pos.Y, cam.Pos.Z, cam.Dir.X, cam.Dir.Y, cam.Dir.Z, cam.HalfW, cam.HalfH, cam.PxAtTarget };
            long now = Environment.TickCount;
            double[] old;
            bool changed = !_lastCam.TryGetValue(id, out old);
            if (!changed)
                for (int i = 0; i < sig.Length; i++)
                    if (Math.Abs(sig[i] - old[i]) > 1e-5 * Math.Max(1.0, Math.Abs(old[i]))) { changed = true; break; }
            _lastCam[id] = sig;
            if (changed)
            {
                bool first = old == null;
                _camTick[id] = first ? now - 10000 : now;
                if (!first) Redraw.Request(); // a camera ferma si riprende ad aggiungere dettaglio
            }
            long t;
            return _camTick.TryGetValue(id, out t) && now - t < 300;
        }

        private static DocumentData Data(View view)
        {
            try { return view == null ? null : DeviationController.GetData(view.Document); }
            catch { return null; }
        }

        public Guid GetServerId() { return ServerGuid; }
        public ExternalServiceId GetServiceId() { return ExternalServices.BuiltInExternalServices.DirectContext3DService; }
        public string GetName() { return "Point Deviation"; }
        public string GetVendorId() { return "OPEN"; }
        public string GetDescription() { return "Point cloud display"; }
        public string GetApplicationId() { return ""; }
        public string GetSourceId() { return ""; }
        public bool UsesHandles() { return false; }

        public bool UseInTransparentPass(View view)
        {
            DocumentData d = Data(view);
            return d != null && d.Style != null && d.Style.NeedsTransparentPass;
        }

        public bool CanExecute(View view)
        {
            try
            {
                if (view is ViewSheet || view is ViewSchedule) return false;
                // prova del disegno fatto da Revit (voce 178): il plugin non disegna
                if (AdvancedSettings.Current.NativeDisplay) return false;
                // anteprime (editor tipo, famiglie): vista del progetto non aperta in nessuna finestra
                if (!ZoomCache.IsOpen(view)) return false;
                DocumentData d = Data(view);
                if (d == null || d.Style == null || !d.Style.Visible || d.Layers.Count == 0) return false;
                long now = Environment.TickCount;
                if (now - _lastVisLog > 10000)
                {
                    _lastVisLog = now;
                    foreach (CloudLayer l in d.Layers) Log.Debug("Visibilità", "«" + l.Name + "» in «" + view.Name + "»: " + l.VisibilityDiag(view));
                }
                foreach (CloudLayer l in d.Layers) if (l.VisibleIn(view)) return true;
                foreach (CloudLayer l in d.Layers) Log.Debug("Visibilità", "«" + l.Name + "» non disegnata in «" + view.Name + "»: " + l.HiddenReason(view));
                return false;
            }
            catch { return false; }
        }

        public Outline GetBoundingBox(View view)
        {
            try
            {
                DocumentData d = Data(view);
                if (d != null && d.Layers.Count > 0)
                {
                    XYZ mn = null, mx = null;
                    foreach (CloudLayer l in d.Layers)
                    {
                        XYZ a, b;
                        l.ModelBounds(out a, out b);
                        mn = mn == null ? a : new XYZ(Math.Min(mn.X, a.X), Math.Min(mn.Y, a.Y), Math.Min(mn.Z, a.Z));
                        mx = mx == null ? b : new XYZ(Math.Max(mx.X, b.X), Math.Max(mx.Y, b.Y), Math.Max(mx.Z, b.Z));
                    }
                    return new Outline(mn, mx);
                }
            }
            catch { }
            return new Outline(XYZ.Zero, new XYZ(0.001, 0.001, 0.001));
        }

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            try
            {
                DocumentData d = Data(view);
                if (d == null || d.Style == null) return;
                Style st = d.Style;

                // un solo passaggio per fotogramma: quello trasparente se serve la trasparenza, altrimenti quello opaco.
                // Voce 199: con il disegno con shader sempre il passaggio opaco. In quello trasparente Revit disegna in
                // un'immagine a parte che mostra solo dove ci sono materiali trasparenti (nuvola visibile solo nei vetri).
                bool transparentPass = st.NeedsTransparentPass && !NativeProbe.ShaderWanted;
                if (DrawContext.IsTransparentPass() != transparentPass) return;
                NativeProbe.PassNote = "passaggio " + (transparentPass ? "trasparente" : "opaco") + ", trasparenza del profilo " + st.Transparency + "%" + (st.Mode == DisplayMode.XRay ? " (raggi X)" : "");

                long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
                ViewCam cam = ViewCam.From(DrawContext.GetCamera(), view);
                var budget = new RenderBudget { Moving = CameraMoving(view, cam) };
                FrameStats.Begin(budget.Moving);

                // livello di dettaglio: punti desiderati per blocco, poi limite complessivo della qualità scelta
                // con i punti grandi (4 vertici l'uno) bastano molti meno punti
                // limite dei punti di un pixel anche con i punti grandi: dalla voce 151 la densità è quella dei punti di un
                // pixel e i punti grandi restano solo dove la nuvola è rada
                long limit = st.Quality == Quality.Fine ? 12000000 : st.Quality == Quality.Media ? 3000000 : 6000000;
                limit = limit * AdvancedSettings.Current.Effective.BudgetPercent / 100;
                // il limite si adatta alla velocità reale di disegno (vedi DrawGovernor)
                limit = (long)(limit * DrawGovernor.Scale);
                // voce 200: con il disegno con shader ogni punto e' un quadrato (4 vertici, 2 triangoli) e il tempo misurato
                // qui non comprende il lavoro della scheda video: con il limite di sempre si arrivava a 17 M punti per
                // fotogramma, Revit rallentava, toglieva elementi del modello e la nuvola spariva e ricompariva.
                // voce 206: con lo shader acceso si vede solo la nuvola dello shader, anche finche' la libreria non e' pronta
                bool shaderMode = NativeProbe.ShaderMode;
                bool shader = shaderMode && NativeProbe.ShaderReady;
                if (shaderMode && !shader) NativeProbe.NotReadyFrames++;
                // voce 201: limite che segue il tempo della scheda video (vedi NativeProbe.ShaderCap)
                long shaderCap = shaderMode ? NativeProbe.ShaderCap(budget.Moving) : 0;
                if (shaderMode) limit = shaderCap;
                var wants = new List<int[]>();
                var pxs = new List<double[]>();
                var visible = new List<CloudLayer>();
                long total = 0;
                foreach (CloudLayer l in d.Layers)
                {
                    if (!l.VisibleIn(view)) continue;
                    var w = new int[l.Tiles.Length];
                    var px = new double[l.Tiles.Length];
                    l.Moving = budget.Moving;
                    l.Desired(cam, st, w, px, limit);
                    // l'octree rispetta il limite da solo (con la parte visibile dei blocchi e una distanza stabile tra i
                    // punti): la riduzione proporzionale qui sotto accorciava tutti i blocchi in modo diverso a ogni
                    // fotogramma e lasciava solo i primi punti, quelli più grandi (punti che si gonfiano in movimento)
                    if (!l.IsOctree) foreach (int x in w) total += x;
                    wants.Add(w); pxs.Add(px);
                    visible.Add(l);
                }
                if (total > limit)
                {
                    double f = (double)limit / total;
                    foreach (int[] w in wants)
                        for (int i = 0; i < w.Length; i++) if (w[i] > 0) w[i] = Math.Max(64, (int)(w[i] * f));
                }

                // prima tutta la vista con lo stile giusto (versioni leggere), poi il dettaglio, poi il disegno
                long f1 = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int k = 0; k < visible.Count; k++) visible[k].Prepare(st, budget, wants[k], pxs[k], shaderMode);
                if (!shaderMode) for (int k = 0; k < visible.Count; k++) visible[k].Refine(st, budget, wants[k]);
                long f2 = System.Diagnostics.Stopwatch.GetTimestamp();
                XYZ cubeAt = null;
                if ((AdvancedSettings.Current.NativeProbe || AdvancedSettings.Current.NativeShader) && visible.Count > 0)
                {
                    XYZ bmin, bmax; visible[0].ModelBounds(out bmin, out bmax);
                    cubeAt = (bmin + bmax) / 2.0;
                }
                NativeProbe.Begin(cam, cubeAt, view.Id.Value);
                try
                {
                    // voce 194: con il disegno con shader acceso e la libreria pronta, i punti li disegna la libreria al
                    // segnale; altrimenti (o finché non è pronta) il disegno di sempre
                    if (shader)
                    {
                        long uploadBudget = budget.Moving ? 1500000 : 6000000;
                        long drawBudget = shaderCap;
                        for (int k = 0; k < visible.Count; k++) visible[k].ShaderDraw(st, wants[k], ref uploadBudget, ref drawBudget);
                        NativeProbe.SetClip(cam.ClipPlanes());
                        NativeProbe.EndFrame(st, true);
                        if (uploadBudget <= 0) Redraw.Request();
                    }
                    else
                    {
                        if (NativeProbe.ShaderWanted) NativeProbe.EndFrame(st, false);
                        // libreria non ancora pronta (matrice di Revit da trovare): si riprova, ma non all'infinito
                        if (shaderMode) { if (NativeProbe.NotReadyFrames < 60) Redraw.Request(); }
                        else for (int k = 0; k < visible.Count; k++) visible[k].DrawAll(st, wants[k]);
                    }
                }
                finally { NativeProbe.End(); }
                long f3 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool hit = false;
                foreach (CloudLayer vl in visible) hit |= vl.StatBudgetHit;
                if (!shader) DrawGovernor.Sample(budget.Moving, (f3 - f2) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, FrameStats.BigPts, FrameStats.SmallPts, hit);
                FrameStats.End(f1 - f0, f2 - f1, f3 - f2);
                if (budget.MoreWork) Redraw.Request();
            }
            catch (Exception ex)
            {
                // un errore di disegno non deve mai far cadere Revit
                Log.Error("Disegno", ex);
            }
        }
    }

    /// <summary>
    /// Diagnostica della navigazione (riga «[Fotogrammi]» ogni 5 s, livello Debug): fotogrammi al secondo in
    /// movimento (intervallo tra due disegni: comprende Revit e la scheda video), nostro tempo per fotogramma diviso in
    /// scelta dei blocchi / preparazione e copie / disegno, attesa prima di mostrare una nuova vista, punti grandi e
    /// piccoli dell'ultimo fotogramma, raccolte della memoria di .NET. Serve a capire dove va il tempo quando la
    /// navigazione è lenta: nel nostro codice, nella scheda video (troppi triangoli) o nell'arrivo dei dati.
    /// </summary>
    /// <summary>
    /// Limite di punti che segue la velocità reale del disegno. Il disegno di un milione di triangoli è costato da circa 1 a
    /// circa 15 ms secondo la sessione di Revit (altri programmi aperti, batteria, scheda scelta da Windows): con un limite
    /// fisso nei giorni lenti la navigazione scendeva a 5-7 fotogrammi al secondo. Si misura il tempo di disegno dei
    /// fotogrammi in movimento e si sposta il limite a piccoli scatti (5%) per stare vicino all'obiettivo; sale solo se il
    /// limite era davvero raggiunto. Disattivabile in Avanzate («Limite di punti fisso»).
    /// </summary>
    internal static class DrawGovernor
    {
        public const double TargetMs = 12.0;     // tempo di disegno obiettivo per fotogramma in movimento
        private const double Min = 0.7, Max = 1.25;
        private static double _scale = 1.0, _ema = -1, _msPerM;
        private static long _lastAdjust, _lastHit;
        private static int _over, _under;

        public static double Scale { get { return AdvancedSettings.Current.FixedBudget ? 1.0 : _scale; } }

        public static void Sample(bool moving, double drawMs, long big, long small, bool limitHit)
        {
            long now = Environment.TickCount;
            if (limitHit) _lastHit = now;
            if (!moving || AdvancedSettings.Current.FixedBudget) return;
            double tri = big * 4.0 + small * 0.25;      // peso dei punti: i grandi hanno 4 triangoli, quelli piccoli un vertice
            if (tri < 200000) return;                   // pochi punti: il tempo misurato non dice nulla sulla scheda video
            // un fotogramma isolato molto lento (cambio di vista, copie in corso) non deve spostare la media: registro del
            // 17/11, tre fotogrammi da 13-23 ms in dieci secondi portarono il limite al 35% per tutta la giornata
            double cl = Math.Min(drawMs, 2.5 * Math.Max(_ema, TargetMs));
            _ema = _ema < 0 ? cl : _ema * 0.8 + cl * 0.2;
            _msPerM = _msPerM <= 0 ? drawMs * 1e6 / tri : _msPerM * 0.8 + (drawMs * 1e6 / tri) * 0.2;
            if (now - _lastAdjust < 400) return;
            _lastAdjust = now;
            double s = _scale;
            // scende solo se il tempo resta alto per almeno 2 s, a piccoli passi; risale appena c'e' margine (subito se il
            // limite era raggiunto, altrimenti dopo 5 s) cosi' non resta mai bloccato in basso
            if (_ema > TargetMs * 1.25) { _under = 0; if (++_over >= 5) { s *= 0.95; _over = 0; } }
            else
            {
                _over = 0;
                if (_ema < TargetMs * 0.8)
                {
                    _under++;
                    if (now - _lastHit < 5000) s *= 1.05;
                    else if (_under >= 12) { s *= 1.05; _under = 0; }
                }
                else _under = 0;
            }
            s = Math.Max(Min, Math.Min(Max, s));
            _scale = Math.Round(s * 20.0) / 20.0;
        }

        public static string Describe()
        {
            if (AdvancedSettings.Current.FixedBudget) return "; limite di punti fisso";
            return _ema < 0 ? "" : string.Format("; limite di punti adattivo {0:F0}% (disegno in movimento {1:F1} ms, {2:F1} ms per milione di punti pesati)", _scale * 100, _ema, _msPerM);
        }
    }

    internal static class FrameStats
    {
        private static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static long _winStart = -1, _lastFrame = -1;
        private static int _frames, _moving, _intervals;
        private static double _intSum, _intMax, _ourSum, _ourMax, _lodSum, _prepSum, _drawSum;
        private static int _views, _forced;
        private static double _waitSum, _waitMax;
        private static int _gc0, _gc1, _gc2;
        private static bool _hwLogged;
        private static bool _curMoving;
        public static long BigPts, SmallPts;      // punti dell'ultimo fotogramma (somma delle nuvole)
        private static long _lastBig, _lastSmall;
        public static int OutOfFrame;

        /// <summary>Intervallo tra gli ultimi due fotogrammi in movimento (ms): comprende il lavoro di Revit.</summary>
        public static double LastMovingIntervalMs = -1;

        public static void Begin(bool moving)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!_hwLogged)
            {
                _hwLogged = true;
                PerfValues v = AdvancedSettings.Current.Effective;
                Log.Write("Versione", BuildInfo.Describe());
                Log.Write("Hardware", string.Format("{0}; profilo {1}: {2} thread di preparazione, {3} di lettura, limite punti {4}%, precaricamento {5}; {6}",
                    Hardware.Describe(), AdvancedSettings.Current.ResolvedProfile, v.BakerThreads, v.LoaderThreads, v.BudgetPercent,
                    v.PrefetchMargin > 0 ? v.PrefetchMargin.ToString("0.0") : "spento", Hardware.GraphicsDiag()));
            }
            if (_winStart < 0) { _winStart = now; _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2); }
            // intervallo tra due fotogrammi consecutivi in movimento: è il tempo che si percepisce navigando
            if (moving && _curMoving && _lastFrame >= 0)
            {
                double ms = (now - _lastFrame) * TickMs;
                if (ms < 2000) { _intervals++; _intSum += ms; if (ms > _intMax) _intMax = ms; LastMovingIntervalMs = ms; }
            }
            _lastFrame = now;
            _curMoving = moving;
            _frames++;
            if (moving) _moving++;
            if (BigPts + SmallPts > 0) { _lastBig = BigPts; _lastSmall = SmallPts; }
            BigPts = 0; SmallPts = 0;
        }

        public static void End(long lodTicks, long prepTicks, long drawTicks)
        {
            double lod = lodTicks * TickMs, prep = prepTicks * TickMs, draw = drawTicks * TickMs, our = lod + prep + draw;
            _lodSum += lod; _prepSum += prep; _drawSum += draw; _ourSum += our;
            if (our > _ourMax) _ourMax = our;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if ((now - _winStart) * TickMs < 5000) return;
            int g0 = GC.CollectionCount(0) - _gc0, g1 = GC.CollectionCount(1) - _gc1, g2 = GC.CollectionCount(2) - _gc2;
            long big = BigPts + SmallPts > 0 ? BigPts : _lastBig, small = BigPts + SmallPts > 0 ? SmallPts : _lastSmall;
            Log.Debug("Fotogrammi", string.Format(
                "{0} fotogrammi ({1} in movimento){2}; nostro tempo per fotogramma medio {3:F1} ms (scelta blocchi {4:F1}, preparazione e copie {5:F1}, disegno {6:F1}), massimo {7:F0} ms; " +
                "nuove viste {8} (attesa media {9:F0} ms, massima {10:F0} ms, mostrate incomplete {11}); ultimo fotogramma {12:N0} punti grandi (4 triangoli l'uno) e {13:N0} piccoli; " +
                "raccolte memoria gen0/gen1/gen2 {14}/{15}/{16}{17}",
                _frames, _moving,
                _intervals > 0 ? string.Format(", in movimento un fotogramma ogni {0:F0} ms (≈ {1:F1} al secondo), il più lento {2:F0} ms", _intSum / _intervals, 1000.0 / Math.Max(1, _intSum / _intervals), _intMax) : "",
                _ourSum / Math.Max(1, _frames), _lodSum / Math.Max(1, _frames), _prepSum / Math.Max(1, _frames), _drawSum / Math.Max(1, _frames), _ourMax,
                _views, _views > 0 ? _waitSum / _views : 0, _waitMax, _forced, big, small, g0, g1, g2,
                OutOfFrame > 0 ? string.Format("; «fuori dall'inquadratura» {0} volte", OutOfFrame) : "") + DrawGovernor.Describe());
            _winStart = now; _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _frames = _moving = _intervals = 0; _intSum = _intMax = _ourSum = _ourMax = _lodSum = _prepSum = _drawSum = 0;
            _views = _forced = 0; _waitSum = _waitMax = 0; OutOfFrame = 0;
        }

        /// <summary>Una nuova vista è stata mostrata dopo «ms» di attesa; forced = a tempo scaduto, non ancora completa.</summary>
        public static void ViewAdopted(long ms, bool forced)
        {
            _views++; _waitSum += ms; if (ms > _waitMax) _waitMax = ms;
            if (forced) _forced++;
        }
    }
}
