using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PointDeviation
{
    internal sealed class WindowOwner : System.Windows.Forms.IWin32Window
    {
        public WindowOwner(IntPtr handle) { Handle = handle; }
        public IntPtr Handle { get; private set; }
    }

    /// <summary>
    /// Coordina i dati di ogni progetto aperto (m_docData), le finestre, il server di rendering e l'ExternalEvent.
    /// Tutto gira sul thread principale di Revit.
    ///
    /// I progetti si identificano con una chiave stringa e non con l'oggetto Document: non si fa alcuna
    /// ipotesi sul fatto che Revit restituisca sempre la stessa istanza wrapper per lo stesso file.
    /// </summary>
    internal static class DeviationController
    {
        private const string UnsavedPrefix = "unsaved:";

        internal static readonly object Sync = new object();
        internal static Job PendingJob = Job.None;
        internal static DeviationSettings PendingSettings;
        internal static string PendingKey;

        /// <summary>Un DocumentData per ogni progetto aperto (famiglie e link esclusi), per chiave.</summary>
        private static readonly Dictionary<string, DocumentData> m_docData = new Dictionary<string, DocumentData>();

        private static DocumentData _active;
        private static readonly DeviationSettings _noDocSettings = new DeviationSettings();

        // cache della chiave: serve solo a risparmiare calcoli quando Revit passa la stessa istanza (confronto per riferimento,
        // senza mai accedere all'oggetto); se le istanze cambiano, la chiave viene semplicemente ricalcolata
        private static Document _keyCacheDoc;
        private static string _keyCache;

        // chiave del progetto prima di un "salva con nome", per rinominarla dopo il salvataggio
        private static string _savingAsFrom;

        private static DeviationForm _form;
        private static LegendForm _legend;
        private static DisplayForm _appearance;
        private static LibraryForm _library;
        private static ExternalEvent _event;
        private static DeviationHandler _handler;
        private static ExternalEvent _extractEvent;
        private static ExtractHandler _extractHandler;
        private static IntPtr _mainWindow = IntPtr.Zero;
        internal static IntPtr MainWindow { get { return _mainWindow; } }

        /// <summary>Scatta quando cambia il progetto attivo: le finestre aperte ricaricano i propri valori.</summary>
        internal static event Action ActiveChanged;

        /// <summary>Impostazioni del progetto attivo (quelle che le finestre e i pulsanti modificano).</summary>
        public static DeviationSettings Current
        {
            get { return _active != null ? _active.Settings : _noDocSettings; }
        }

        public static DocumentData ActiveData { get { return _active; } }

        // ------------------------------------------------------------------ chiave del documento

        private static bool IsTracked(Document doc)
        {
            if (doc == null) return false;
            try { return !doc.IsLinked && !doc.IsFamilyDocument; }
            catch { return false; }
        }

        private static string SafePath(Document doc)
        {
            try { return doc.PathName; }
            catch { return null; }
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc.Title; }
            catch { return null; }
        }

        /// <summary>Percorso completo normalizzato se il file è salvato, altrimenti "unsaved:" + titolo.</summary>
        private static string ComputeKey(Document doc)
        {
            string pathKey = ProjectStore.KeyFor(SafePath(doc));
            if (pathKey != null) return pathKey;
            return UnsavedPrefix + (SafeTitle(doc) ?? "").ToLowerInvariant();
        }

        internal static string KeyOf(Document doc)
        {
            if (doc == null) return null;
            if (ReferenceEquals(doc, _keyCacheDoc) && _keyCache != null) return _keyCache;

            string key = ComputeKey(doc);
            _keyCacheDoc = doc;
            _keyCache = key;
            return key;
        }

        private static void InvalidateKeyCache()
        {
            _keyCacheDoc = null;
            _keyCache = null;
        }

        /// <summary>Chiave sotto cui salvare le impostazioni su disco: i progetti mai salvati non ne hanno.</summary>
        private static string PersistKey(string key)
        {
            return key == null || key.StartsWith(UnsavedPrefix, StringComparison.Ordinal) ? null : key;
        }

        // ------------------------------------------------------------------ dizionario per documento

        internal static DocumentData GetData(Document doc)
        {
            if (doc == null) return null;
            string key = KeyOf(doc);
            DocumentData d;
            return key != null && m_docData.TryGetValue(key, out d) ? d : null;
        }

        /// <summary>Restituisce i dati del documento, creandoli (con le impostazioni salvate) se mancano.</summary>
        internal static DocumentData GetOrCreate(Document doc)
        {
            if (!IsTracked(doc)) return null;

            string key = KeyOf(doc);
            DocumentData d;
            if (m_docData.TryGetValue(key, out d)) return d;

            d = new DocumentData(key);
            string persist = PersistKey(key);
            if (persist != null)
            {
                StoredSettings stored = ProjectStore.Load(persist);
                if (stored != null) stored.ApplyTo(d.Settings);
            }
            m_docData[key] = d;
            return d;
        }

        // ------------------------------------------------------------------ eventi del ciclo di vita

        internal static void OnDocumentCreated(Document doc)
        {
            GetOrCreate(doc);
        }

        internal static void OnDocumentOpened(Document doc)
        {
            GetOrCreate(doc);
        }

        /// <summary>Prima del salvataggio con nome: le impostazioni passano al nuovo percorso e si ricorda la vecchia chiave.</summary>
        internal static void OnDocumentSavingAs(Document doc, string newPath)
        {
            DocumentData d = GetOrCreate(doc);
            if (d == null) return;

            _savingAsFrom = d.Key;
            ProjectStore.Save(ProjectStore.KeyFor(newPath), d.Settings);
        }

        /// <summary>Dopo il salvataggio con nome: il progetto ha un nuovo percorso, quindi una nuova chiave.</summary>
        internal static void OnDocumentSavedAs(Document doc)
        {
            string from = _savingAsFrom;
            _savingAsFrom = null;
            InvalidateKeyCache();
            if (from == null || !IsTracked(doc)) return;

            DocumentData d;
            if (!m_docData.TryGetValue(from, out d)) return;

            string newKey = ComputeKey(doc);
            if (newKey == null || newKey == from || m_docData.ContainsKey(newKey)) return;

            m_docData.Remove(from);
            d.Key = newKey;
            m_docData[newKey] = d;
            AnalysisSnapshot snap = AnalysisRegistry.Get(from);
            if (snap != null) { AnalysisRegistry.Remove(from); AnalysisRegistry.Set(newKey, snap); }
        }

        internal static void OnDocumentClosing(Document doc)
        {
            DocumentData d = GetData(doc);
            InvalidateKeyCache();
            if (d == null) return;

            ProjectStore.Save(PersistKey(d.Key), d.Settings);
            m_docData.Remove(d.Key);

            bool wasActive = ReferenceEquals(d, _active);
            if (wasActive) _active = null;
            d.Dispose();
            if (wasActive) NotifyActiveChanged();
        }

        /// <summary>true mentre il plugin stesso inserisce, sostituisce o toglie la nuvola: quei cambi non vanno reinterpretati.</summary>
        internal static bool OwnChange;

        /// <summary>
        /// Nuvole inserite, cancellate (anche con Annulla), spostate o ruotate in Revit: si riallineano le nuvole
        /// del progetto. Solo lettura sul documento, quindi si può fare qui dentro l'evento.
        /// </summary>
        internal static void OnDocumentChanged(Document doc, ICollection<ElementId> added, ICollection<ElementId> deleted, ICollection<ElementId> modified)
        {
            if (OwnChange) return;
            DocumentData d = GetData(doc);
            if (d == null) return;

            // Visibilità/Grafica modifica la vista (non la nuvola): Revit non sempre ridisegna gli elementi del plugin,
            // e una nuvola appena spenta restava a schermo fino al movimento successivo. Si chiede un ridisegno.
            if (d.Layers.Count > 0 && modified.Any(id => doc.GetElement(id) is View))
            {
                Redraw.Request();
                Log.Debug("Visibilità", "vista modificata: ridisegno richiesto");
            }

            bool relevant = deleted.Any(id => d.Layers.Exists(l => l.InstanceId == id))
                || modified.Any(id => d.Layers.Exists(l => l.InstanceId == id))
                || added.Any(id => doc.GetElement(id) is PointCloudInstance);
            if (!relevant) return;

            int before = d.Layers.Count;
            if (!d.SyncLayers(doc)) return;

            if (d.Layers.Count < before && d.Layers.Count == 0)
            {
                // tolta l'ultima nuvola: si dimentica anche l'analisi
                AnalysisRegistry.Remove(d.Key);
                DistPrefetch.Stop();
                d.SetDistances(null);
                d.Cloud = null;
                d.Settings.Display = d.Settings.LastViewMode;
            }
            d.RebuildStyle(d.Settings);
            if (ReferenceEquals(d, _active)) { App.SyncRibbon(d.Settings); NotifySettingsChanged(); }
        }

        private static VisibilityForm _visibility;

        public static void ShowVisibility()
        {
            if (_visibility != null && !_visibility.IsDisposed)
            {
                _visibility.Show();
                _visibility.Activate();
                _visibility.Reload();
                return;
            }
            _visibility = new VisibilityForm();
            _visibility.FormClosed += (s, e) => { _visibility = null; };
            _visibility.Show(new WindowOwner(_mainWindow));
        }

        private static ClipManagerForm _clips;

        public static void ShowClipManager()
        {
            if (_clips != null && !_clips.IsDisposed)
            {
                _clips.Show();
                _clips.Activate();
                return;
            }
            _clips = new ClipManagerForm();
            _clips.FormClosed += (s, e) => { _clips = null; };
            _clips.Show(new WindowOwner(_mainWindow));
        }

        internal static void OnViewActivated(UIApplication app, Document doc)
        {
            DocumentData d = GetOrCreate(doc);
            if (d == null) return;

            // prima attivazione del progetto: si leggono le nuvole del plugin presenti nel modello
            if (!d.AutoLoadTried)
            {
                d.AutoLoadTried = true;
                d.SyncLayers(doc);
                if (d.Settings.Display == DisplayMode.Discrepanze && AnalysisRegistry.Get(d.Key) == null)
                    d.Settings.Display = d.Settings.LastViewMode;
            }
            if (d.Style == null) d.RebuildStyle(d.Settings);

            SetActive(doc);
        }

        internal static void SetActive(Document doc)
        {
            DocumentData d = GetOrCreate(doc);
            if (ReferenceEquals(d, _active)) return;
            _active = d;
            NotifyActiveChanged();
        }

        /// <summary>Le impostazioni sono cambiate fuori dalle finestre (es. dalla barra): le finestre ricaricano i valori.</summary>
        internal static void NotifySettingsChanged()
        {
            Action h = SettingsChanged;
            if (h != null) h();
            RefreshLegend();
        }

        /// <summary>Scatta quando le impostazioni del progetto attivo cambiano fuori dalla finestra che le mostra.</summary>
        internal static event Action SettingsChanged;

        private static void NotifyActiveChanged()
        {
            RefreshLegend();
            Action h = ActiveChanged;
            if (h != null) h();
            App.SyncRibbon(Current);
        }

        // ------------------------------------------------------------------ inizializzazione e finestre

        /// <summary>All'avvio di Revit (ApplicationInitialized): eventi esterni e server di disegno, così le nuvole
        /// presenti nei progetti si caricano da sole all'apertura, senza dover premere un pulsante.</summary>
        public static void InitAtStartup()
        {
            try { ApiQueue.Init(); } catch { }
            try { HybridServer.Register(); } catch { }
            if (_event == null)
            {
                _handler = new DeviationHandler();
                _event = ExternalEvent.Create(_handler);
            }
            if (_extractEvent == null)
            {
                _extractHandler = new ExtractHandler();
                _extractEvent = ExternalEvent.Create(_extractHandler);
            }
        }

        /// <summary>Da chiamare da qualunque comando: crea evento e server una sola volta e allinea il progetto attivo.</summary>
        private static UIApplication _uiapp;

        public static void Init(UIApplication app)
        {
            _uiapp = app;
            _mainWindow = app.MainWindowHandle;
            ApiQueue.Init();
            ConversionQueue.InitUi();

            if (_event == null)
            {
                _handler = new DeviationHandler();
                _event = ExternalEvent.Create(_handler);
            }

            if (_extractEvent == null)
            {
                _extractHandler = new ExtractHandler();
                _extractEvent = ExternalEvent.Create(_extractHandler);
            }

            UIDocument ui = app.ActiveUIDocument;
            if (ui != null) SetActive(ui.Document);
        }

        public static void ShowWindow(UIApplication app)
        {
            Init(app);

            if (_form != null && !_form.IsDisposed)
            {
                _form.Show();
                _form.Activate();
                return;
            }

            _form = new DeviationForm();
            _handler.Form = _form;
            _form.FormClosed += (s, e) =>
            {
                _form = null;
                if (_handler != null) _handler.Form = null;
            };
            _form.Show(new WindowOwner(_mainWindow));
        }

        public static void ShowLibrary()
        {
            if (_uiapp != null) ShowLibrary(_uiapp);
        }

        public static void ShowLibrary(UIApplication app)
        {
            Init(app);

            if (_library != null && !_library.IsDisposed)
            {
                _library.Show();
                _library.Activate();
                return;
            }

            _library = new LibraryForm();
            _handler.Library = _library;
            _library.FormClosed += (s, e) =>
            {
                _library = null;
                if (_handler != null) _handler.Library = null;
            };
            _library.Show(new WindowOwner(_mainWindow));
        }

        /// <summary>Inserisce nel progetto attivo una nuvola della libreria, con la modalità di posizionamento scelta.</summary>
        public static void InsertFromLibrary(string folder, PlacementMode mode)
        {
            Current.LasPath = folder;
            Current.PendingPlacement = mode;
            Apply(Job.Insert);
        }

        /// <summary>Chiede a Revit di estrarre i punti di una nuvola ReCap (sul thread API).</summary>
        public static bool RequestExtract(ExtractRequest r)
        {
            if (_extractEvent == null || _extractHandler.Busy) return false;
            _extractHandler.Request = r;
            _extractEvent.Raise();
            return true;
        }

        private static ProfileManagerForm _profiles;

        public static void ShowProfiles()
        {
            if (_profiles != null && !_profiles.IsDisposed)
            {
                _profiles.Show();
                _profiles.Activate();
                return;
            }
            _profiles = new ProfileManagerForm();
            _profiles.FormClosed += (s, e) => { _profiles = null; };
            _profiles.Show(new WindowOwner(_mainWindow));
        }

        private static AdvancedForm _advanced;

        /// <summary>
        /// Dopo l'analisi: finestra di avanzamento che calcola subito le distanze di tutte le nuvole. Se sono già tutte
        /// salvate (stessa analisi di prima) non compare.
        /// </summary>
        public static void ShowAnalysisProgress(List<CloudLayer> layers, AnalysisSnapshot an)
        {
            if (layers == null || layers.Count == 0 || an == null) return;
            long missing = 0;
            foreach (CloudLayer l in layers)
            {
                DistCache dc = l.GetDistCache(an);
                if (dc == null) { missing = 1; break; }
                for (int i = 0; i < l.Tiles.Length; i++) missing += l.Tiles[i].Count - dc.Done(i);
            }
            if (missing <= 0) { Log.Write("Analisi", "distanze già salvate per questa analisi: nessun calcolo"); return; }
            using (var f = new AnalysisProgressForm(layers, an))
            {
                IntPtr owner = _mainWindow != IntPtr.Zero ? _mainWindow : System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                f.ShowDialog(new WindowOwner(owner));
            }
        }

        public static void ShowAdvanced(UIApplication app)
        {
            Init(app);
            if (_advanced != null && !_advanced.IsDisposed) { _advanced.Show(); _advanced.Activate(); return; }
            _advanced = new AdvancedForm();
            _advanced.FormClosed += (s, e) => { _advanced = null; };
            _advanced.Show(new WindowOwner(_mainWindow));
        }

        public static void ShowAppearance(UIApplication app)
        {
            Init(app);

            if (_appearance != null && !_appearance.IsDisposed)
            {
                _appearance.Show();
                _appearance.Activate();
                return;
            }

            _appearance = new DisplayForm();
            _appearance.FormClosed += (s, e) => { _appearance = null; };
            _appearance.Show(new WindowOwner(_mainWindow));
        }

        /// <summary>Mostra o chiude la legenda. Restituisce true se ora è visibile.</summary>
        public static bool ToggleLegend()
        {
            if (_legend != null && !_legend.IsDisposed)
            {
                _legend.Close();
                return false;
            }

            _legend = new LegendForm();
            _legend.FormClosed += (s, e) => { _legend = null; };
            _legend.Show(new WindowOwner(_mainWindow));
            return true;
        }

        public static void RefreshLegend()
        {
            LegendForm l = _legend;
            if (l != null && !l.IsDisposed) l.Invalidate();
        }

        // ------------------------------------------------------------------ lavori per il thread API

        /// <summary>Accoda un lavoro usando una copia delle impostazioni del progetto attivo.</summary>
        public static void Apply(Job job)
        {
            Raise(job, Current.Clone());
        }

        /// <summary>Accoda un lavoro per il thread API di Revit. Più richieste ravvicinate vengono fuse nella più impegnativa.</summary>
        public static void Raise(Job job, DeviationSettings settings)
        {
            if (_event == null) return;
            lock (Sync)
            {
                if (job > PendingJob) PendingJob = job;
                PendingSettings = settings;
                PendingKey = _active != null ? _active.Key : null;
            }
            _event.Raise();
        }

        public static void Shutdown()
        {
            HybridServer.Unregister();

            foreach (KeyValuePair<string, DocumentData> kv in m_docData)
            {
                try { ProjectStore.Save(PersistKey(kv.Key), kv.Value.Settings); } catch { }
                try { kv.Value.Dispose(); } catch { }
            }
            m_docData.Clear();
            _active = null;
            InvalidateKeyCache();
        }
    }

    internal sealed class DeviationHandler : IExternalEventHandler
    {
        public DeviationForm Form;
        public LibraryForm Library;

        private void Status(string text)
        {
            DeviationForm f = Form;
            if (f != null && !f.IsDisposed) f.SetStatus(text);
            LibraryForm l = Library;
            if (l != null && !l.IsDisposed) l.SetStatus(text);
        }

        private void Stats(string text)
        {
            DeviationForm f = Form;
            if (f != null && !f.IsDisposed) f.SetStats(text);
        }

        public string GetName() { return "PointDeviation handler"; }

        public void Execute(UIApplication app)
        {
            Job job;
            DeviationSettings s;
            string pendingKey;
            lock (DeviationController.Sync)
            {
                job = DeviationController.PendingJob;
                s = DeviationController.PendingSettings;
                pendingKey = DeviationController.PendingKey;
                DeviationController.PendingJob = Job.None;
            }
            if (job == Job.None || s == null) return;

            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc != null ? uidoc.Document : null;
            DocumentData data = DeviationController.GetOrCreate(doc);
            if (data == null)
            {
                Status(L.T("Nessun progetto attivo."));
                return;
            }

            // se nel frattempo l'utente ha cambiato progetto, la richiesta non vale più per quello attivo
            if (pendingKey != null && pendingKey != data.Key) return;

            var sw = Stopwatch.StartNew();
            try
            {
                data.SyncLayers(doc);

                if (job == Job.Insert)
                {
                    if (!CloudInfo.IsCloudFolder(s.LasPath))
                    {
                        Status(L.T("Nuvola della libreria non trovata."));
                        return;
                    }
                    CloudInfo info = CloudInfo.Load(s.LasPath);
                    string note;
                    double[] pl = RevitCloudOps.ComputePlacement(doc, info, s.PendingPlacement, data.Settings.LastPlacement, out note);
                    data.Settings.LastPlacement = pl;

                    NativeSync.Insert(doc, s.LasPath, info, pl);
                    data.SyncLayers(doc);

                    // nuova nuvola: l'analisi precedente non vale per lei, si riparte dai colori della scansione
                    AnalysisRegistry.Remove(data.Key);
                    DistPrefetch.Stop();
                    data.SetDistances(null);
                    data.Cloud = null;
                    data.ComputedMm = 0;
                    if (data.Settings.Display == DisplayMode.Discrepanze) data.Settings.Display = data.Settings.LastViewMode;
                    s.Display = data.Settings.Display;

                    data.LoadInfo = string.Format(L.T("«{0}» inserita. {1}"), info.Name,
                        (L.T("Posizionamento: ") + Placement.Describe(s.PendingPlacement) + ". " + note).Trim());
                    job = Job.Recolor;
                }

                if (job == Job.Clear)
                {
                    NativeSync.RemoveAll(doc, data);
                    data.SyncLayers(doc);
                    AnalysisRegistry.Remove(data.Key);
                    DistPrefetch.Stop();
                    data.SetDistances(null);
                    data.Cloud = null;
                    Status(L.T("Nuvola rimossa."));
                    Stats("");
                    data.RebuildStyle(data.Settings);
                    uidoc.RefreshActiveView();
                    return;
                }

                if (!data.HasCloud)
                {
                    Status(L.T("Inserisci prima una nuvola dalla libreria."));
                    data.RebuildStyle(s);
                    return;
                }

                bool needDist = s.Display == DisplayMode.Discrepanze;
                if (job == Job.Recolor && needDist && (AnalysisRegistry.Get(data.Key) == null || s.MaxRangeMm > data.ComputedMm + 1e-9))
                    job = Job.Recompute;

                if (job == Job.Recompute && !Compute(uidoc, data, s))
                {
                    // analisi non riuscita: si torna alla visualizzazione di prima
                    s.Display = data.Settings.Display = data.Settings.LastViewMode;
                }

                data.RebuildStyle(s);
                if (s.Display == DisplayMode.Discrepanze) Stats(data.ComputeStats(s.Profile, s.InvertSign));

                string extra = (s.Display == DisplayMode.Rgb && !data.HasRgb) ? L.T("\nLa nuvola non contiene colori RGB: uso la quota.") : "";
                Status(string.Format(L.T("{0}\nAggiornato in {1:F1} s.{2}"), data.LoadInfo, sw.Elapsed.TotalSeconds, extra).Trim());

                App.SyncRibbon(data.Settings);
                uidoc.RefreshActiveView();
                DeviationController.RefreshLegend();
            }
            catch (Exception ex)
            {
                Status(L.T("Errore: ") + ex.Message);
            }
        }

        private bool Compute(UIDocument uidoc, DocumentData data, DeviationSettings s)
        {
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;
            double searchFt = s.MaxRangeFt;

            Status(L.T("Estrazione della geometria del modello..."));
            ICollection<ElementId> sel = null;
            if (s.SelectionOnly)
            {
                // si usa la selezione memorizzata con "Analizza selezione"; in mancanza, quella corrente (che viene memorizzata)
                sel = data.SelectionIds;
                if (sel == null || sel.Count == 0)
                {
                    ICollection<ElementId> now = uidoc.Selection.GetElementIds();
                    if (now.Count > 0) data.SelectionIds = new List<ElementId>(now);
                    sel = data.SelectionIds;
                }
                if (sel == null || sel.Count == 0)
                {
                    Status(L.T("Seleziona almeno un elemento del modello, oppure scegli \"vista attiva\"."));
                    return false;
                }
            }

            int elems, tris, links;
            Outline limit = data.GetOutline(searchFt);
            TriangleBvh bvh = ModelMeshExtractor.Build(doc, view, sel, limit, s.IncludeLinks, out elems, out tris, out links);
            if (bvh == null)
            {
                Status(L.T("Nessuna geometria di modello trovata nella zona della nuvola (controlla traslazione, vista attiva o selezione)."));
                return false;
            }

            Log.Write("Analisi", string.Format("modello: {0:N0} elementi, {1:N0} triangoli, suddivisi in {2:N0} (lato massimo {3:F2} m); distanza massima {4:F0} mm; {5} processori",
                elems, bvh.OriginalTriangles, bvh.TriangleCount, bvh.SplitEdgeFt * 0.3048, searchFt * 304.8, Environment.ProcessorCount));
            Status(string.Format(L.T("Calcolo distanze: {0:N0} elementi{1}, {2:N0} triangoli..."),
                elems, links > 0 ? " (" + links + L.T(" link)") : "", tris));

            // versione unica anche tra sessioni diverse
            data.AnalysisVersion = Math.Max(data.AnalysisVersion + 1, (int)((DateTime.UtcNow - new DateTime(2026, 1, 1)).TotalSeconds));
            AnalysisRegistry.Set(data.Key, new AnalysisSnapshot { Bvh = bvh, SearchFt = searchFt, Version = data.AnalysisVersion, Signature = bvh.Signature() });

            // statistiche su un campione uniforme di tutte le nuvole; i punti disegnati calcolano le distanze da soli
            data.Cloud = data.BuildSample(Math.Max(100000, Math.Min(s.MaxPoints, 800000)));
            if (data.Cloud != null) data.SetDistances(data.ComputeDistances(bvh, searchFt));
            data.ComputedMm = s.MaxRangeMm;

            // distanze di tutti i punti subito (con avanzamento), così la navigazione è fluida dall'inizio
            Status(L.T("Calcolo delle distanze dal modello..."));
            DeviationController.ShowAnalysisProgress(data.Layers.FindAll(l => l.IsOctree), AnalysisRegistry.Get(data.Key));
            return true;
        }
    }

    /// <summary>Richiesta di estrazione di una nuvola ReCap, compilata dalla finestra libreria.</summary>
    internal sealed class ExtractRequest
    {
        public string FilePath;          // file .rcp/.rcs, oppure null per una nuvola già nel progetto
        public bool FromProject;
        public double SpacingM = 0.005;
        public string RawPath;
        public Action<string> Progress;
        /// <summary>Al termine: file grezzo pronto (o null), nome suggerito, errore (o null).</summary>
        public Action<string, string, Exception> Done;
    }

    internal sealed class ExtractHandler : IExternalEventHandler
    {
        public ExtractRequest Request;
        public bool Busy;

        public string GetName() { return "PointDeviation estrazione nuvola"; }

        public void Execute(UIApplication app)
        {
            ExtractRequest r = Request;
            Request = null;
            if (r == null) return;
            Busy = true;
            try
            {
                UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc == null) throw new InvalidOperationException(L.T("Apri un progetto Revit: la lettura dei file ReCap passa da Revit."));
                Document doc = uidoc.Document;

                // Revit restituisce solo i punti che carica per la vista attiva (intervallo di vista, ritaglio):
                // serve una vista 3D che mostri tutta la nuvola
                var v3 = uidoc.ActiveView as View3D;
                if (v3 == null || v3.IsTemplate)
                    throw new InvalidOperationException(L.T("Per leggere una nuvola ReCap apri una vista 3D (Revit fornisce solo i punti della vista attiva: in pianta o in sezione si otterrebbe solo una fetta)."));
                if (v3.IsSectionBoxActive)
                    throw new InvalidOperationException(L.T("Spegni il riquadro di sezione della vista 3D attiva: Revit fornisce solo i punti visibili nella vista."));
                if (v3.CropBoxActive)
                    throw new InvalidOperationException(L.T("Disattiva il ritaglio della vista 3D attiva: Revit fornisce solo i punti visibili nella vista."));

                ElementId typeId = null;
                string name = r.FilePath != null ? Path.GetFileNameWithoutExtension(r.FilePath) : null;
                if (r.FromProject)
                {
                    var list = RevitCloudOps.FindInstances(uidoc)
                        .Where(i => !NativeClouds.IsOurs(doc.GetElement(i.GetTypeId()) as PointCloudType)).ToList();
                    if (list.Count == 0) throw new InvalidOperationException(L.T("Nel progetto non ci sono nuvole di punti inserite."));
                    if (list.Count > 1) throw new InvalidOperationException(L.T("Ci sono più nuvole nel progetto: seleziona quella da convertire e riprova."));
                    typeId = list[0].GetTypeId();
                    Element type = doc.GetElement(typeId);
                    name = type != null ? type.Name : "nuvola";
                }

                RawPointWriter w = RevitCloudOps.ExtractToRaw(doc, r.FilePath, typeId, r.SpacingM, r.RawPath, r.Progress);
                long count = w.Count;
                w.Dispose();
                if (count == 0) throw new InvalidOperationException(L.T("Nessun punto estratto dalla nuvola."));
                r.Done(r.RawPath, name, null);
            }
            catch (Exception ex)
            {
                try { File.Delete(r.RawPath); } catch { }
                try { File.Delete(r.RawPath + ".txt"); } catch { }
                r.Done(null, null, ex);
            }
            finally
            {
                Busy = false;
            }
        }
    }

    /// <summary>Inserimento, rimozione e visibilità delle nuvole native del plugin (thread principale, transazioni).</summary>
    internal static class NativeSync
    {
        /// <summary>Inserisce una nuvola della libreria come nuvola di punti di Revit (si aggiunge alle altre).</summary>
        public static void Insert(Document doc, string folder, CloudInfo info, double[] placement)
        {
            string path = CloudView.EnsureBase(folder, info);
            double[] pl = Placement.IsValid(placement) ? placement : Placement.Identity();
            DeviationController.OwnChange = true;
            try
            {
                using (var t = new Transaction(doc, L.T("Inserisci nuvola di punti")))
                {
                    t.Start();
                    // stesso file già usato da un tipo del progetto: si riusa il tipo
                    PointCloudType type = new FilteredElementCollector(doc).OfClass(typeof(PointCloudType)).Cast<PointCloudType>()
                        .FirstOrDefault(pt => NativeClouds.IsOurs(pt) &&
                            string.Equals(NativeClouds.PathOf(pt), path, StringComparison.OrdinalIgnoreCase));
                    if (type == null) type = PointCloudType.Create(doc, NativeClouds.EngineId, path);
                    PointCloudInstance.Create(doc, type.Id, NativeClouds.ToTransform(NativeClouds.InstanceFromPlacement(pl, info)));
                    t.Commit();
                }
            }
            finally { DeviationController.OwnChange = false; }
        }

        /// <summary>
        /// Toglie da tutti i progetti aperti la nuvola di una cartella della libreria (tipo e istanze, cioè anche da
        /// Gestisci collegamenti), prima di eliminarla dal disco. Restituisce quante nuvole sono state tolte.
        /// </summary>
        public static int RemoveFolderFromProjects(UIApplication app, string folder)
        {
            int removed = 0;
            string dir = Path.GetFullPath(folder).TrimEnd('\\');
            foreach (Document doc in app.Application.Documents)
            {
                if (doc.IsFamilyDocument || doc.IsLinked) continue;
                var types = new FilteredElementCollector(doc).OfClass(typeof(PointCloudType)).Cast<PointCloudType>()
                    .Where(pt =>
                    {
                        if (!NativeClouds.IsOurs(pt)) return false;
                        string p = NativeClouds.PathOf(pt);
                        try { return p != null && string.Equals(Path.GetFullPath(Path.GetDirectoryName(p)).TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    }).Select(pt => pt.Id).ToList();
                if (types.Count == 0) continue;
                try
                {
                    // il tipo si porta via le sue istanze; l'evento di modifica del documento riallinea le nuvole del plugin
                    using (var t = new Transaction(doc, L.T("Rimuovi nuvola di punti")))
                    {
                        t.Start();
                        doc.Delete(types);
                        t.Commit();
                    }
                    removed += types.Count;
                    Log.Write("Libreria", string.Format("«{0}» tolta da «{1}»", Path.GetFileName(folder), doc.Title));
                }
                catch (Exception ex) { Log.Error("Rimozione dal progetto «" + doc.Title + "»", ex); }
            }
            return removed;
        }

        /// <summary>Toglie tutte le istanze delle nuvole del plugin (i tipi restano in Gestisci collegamenti).</summary>
        public static void RemoveAll(Document doc, DocumentData data)
        {
            DeviationController.OwnChange = true;
            try
            {
                using (var t = new Transaction(doc, L.T("Rimuovi nuvola di punti")))
                {
                    t.Start();
                    foreach (CloudLayer l in data.Layers)
                    {
                        try { if (doc.GetElement(l.InstanceId) != null && DocumentValidation.CanDeleteElement(doc, l.InstanceId)) doc.Delete(l.InstanceId); }
                        catch { }
                    }
                    t.Commit();
                }
            }
            catch { }
            finally { DeviationController.OwnChange = false; }
        }

        /// <summary>Nasconde o mostra nella vista le nuvole indicate (come «Nascondi elemento» di Revit).</summary>
        public static void SetHidden(Document doc, View view, IList<ElementId> ids, bool hide)
        {
            var list = ids.Where(id => doc.GetElement(id) != null).ToList();
            if (list.Count == 0) return;
            DeviationController.OwnChange = true;
            try
            {
                using (var t = new Transaction(doc, hide ? L.T("Nascondi nuvola") : L.T("Mostra nuvola")))
                {
                    t.Start();
                    if (hide) view.HideElements(list); else view.UnhideElements(list);
                    t.Commit();
                }
            }
            finally { DeviationController.OwnChange = false; }
            Redraw.Request();
        }

        /// <summary>
        /// On/Off: se almeno una nuvola è visibile nella vista le nasconde tutte, altrimenti le mostra.
        /// Visibile conta tutti gli interruttori di Revit (elemento, categoria, «Mostra nuvole di punti in questa vista»,
        /// spunta della nuvola in Visibilità/Grafica): mostrando si riaccendono tutti, altrimenti dopo aver spento le
        /// nuvole da Visibilità/Grafica il pulsante non faceva più niente.
        /// </summary>
        public static bool ToggleAllInView(Document doc, View view, DocumentData data)
        {
            var ids = data.Layers.Select(l => l.InstanceId).Where(id => doc.GetElement(id) != null).ToList();
            bool anyVisible = data.Layers.Exists(l => l.VisibleIn(view));
            if (anyVisible) { SetHidden(doc, view, ids, true); return false; }
            if (ids.Count == 0) return false;
            DeviationController.OwnChange = true;
            try
            {
                using (var t = new Transaction(doc, L.T("Mostra nuvola")))
                {
                    t.Start();
                    var hidden = ids.Where(id => doc.GetElement(id).IsHidden(view)).ToList();
                    if (hidden.Count > 0) view.UnhideElements(hidden);
                    var cat = new ElementId(BuiltInCategory.OST_PointClouds);
                    try { if (view.GetCategoryHidden(cat) && view.CanCategoryBeHidden(cat)) view.SetCategoryHidden(cat, false); } catch { }
                    CloudLayer.ShowInViewSettings(view, ids);
                    t.Commit();
                }
            }
            catch (Exception ex) { Log.Error("Mostra nuvola", ex); }
            finally { DeviationController.OwnChange = false; }
            Redraw.Request();
            return true;
        }
    }
}
