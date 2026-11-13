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

                bool refresh = false;
                if (_zoomDirty && now - _zoomTick >= ZoomSettleMs) { _zoomDirty = false; refresh = true; }
                if (Redraw.Pending && now - _lastRefresh >= MinRefreshMs) { Redraw.Take(); refresh = true; }
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

                // un solo passaggio per fotogramma: quello trasparente se serve la trasparenza, altrimenti quello opaco
                if (DrawContext.IsTransparentPass() != st.NeedsTransparentPass) return;

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
                for (int k = 0; k < visible.Count; k++) visible[k].Prepare(st, budget, wants[k], pxs[k]);
                for (int k = 0; k < visible.Count; k++) visible[k].Refine(st, budget, wants[k]);
                long f2 = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int k = 0; k < visible.Count; k++) visible[k].DrawAll(st, wants[k]);
                long f3 = System.Diagnostics.Stopwatch.GetTimestamp();
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

        public static void Begin(bool moving)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!_hwLogged)
            {
                _hwLogged = true;
                PerfValues v = AdvancedSettings.Current.Effective;
                Log.Write("Hardware", string.Format("{0}; profilo {1}: {2} thread di preparazione, {3} di lettura, limite punti {4}%, precaricamento {5}; {6}",
                    Hardware.Describe(), AdvancedSettings.Current.ResolvedProfile, v.BakerThreads, v.LoaderThreads, v.BudgetPercent,
                    v.PrefetchMargin > 0 ? v.PrefetchMargin.ToString("0.0") : "spento", Hardware.GraphicsDiag()));
            }
            if (_winStart < 0) { _winStart = now; _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2); }
            // intervallo tra due fotogrammi consecutivi in movimento: è il tempo che si percepisce navigando
            if (moving && _curMoving && _lastFrame >= 0)
            {
                double ms = (now - _lastFrame) * TickMs;
                if (ms < 2000) { _intervals++; _intSum += ms; if (ms > _intMax) _intMax = ms; }
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
                OutOfFrame > 0 ? string.Format("; «fuori dall'inquadratura» {0} volte", OutOfFrame) : ""));
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
