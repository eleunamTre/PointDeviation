using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace PointDeviation
{
    public class App : IExternalApplication
    {
        // L'API di Revit non ha pulsanti a levetta singoli: On/Off e Inverti sono pulsanti normali
        // che mostrano lo stato nella propria etichetta.
        private static PushButton _visButton;
        private static PushButton _invertButton;
        private static PushButton _analysisButton;
        private static RadioButtonGroup _modeGroup;
        private static readonly Dictionary<DisplayMode, ToggleButton> _modeButtons = new Dictionary<DisplayMode, ToggleButton>();
        private static Autodesk.Revit.UI.ComboBox _quality;
        private static bool _syncing;

        public Result OnStartup(UIControlledApplication application)
        {
            try { L.Init(application.ControlledApplication.Language); } catch { }
            try { NativeClouds.Register(); } catch { }
            BuildRibbon(application);

            // ciclo di vita dei dati: un DocumentData per ogni progetto aperto
            var controlledApp = application.ControlledApplication;
            controlledApp.DocumentCreated += this.OnDocumentCreated;
            controlledApp.DocumentOpened += this.OnDocumentOpened;
            controlledApp.DocumentSavingAs += this.OnDocumentSavingAs;
            controlledApp.DocumentSavedAs += this.OnDocumentSavedAs;
            controlledApp.DocumentClosing += this.OnDocumentClosing;
            controlledApp.DocumentChanged += this.OnDocumentChanged;
            controlledApp.ApplicationInitialized += this.OnInitialized;
            application.ViewActivated += this.OnViewActivated;
            application.Idling += HybridServer.OnIdling;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            ConversionQueue.CancelAll();
            var controlledApp = application.ControlledApplication;
            controlledApp.DocumentCreated -= this.OnDocumentCreated;
            controlledApp.DocumentOpened -= this.OnDocumentOpened;
            controlledApp.DocumentSavingAs -= this.OnDocumentSavingAs;
            controlledApp.DocumentSavedAs -= this.OnDocumentSavedAs;
            controlledApp.DocumentClosing -= this.OnDocumentClosing;
            controlledApp.DocumentChanged -= this.OnDocumentChanged;
            controlledApp.ApplicationInitialized -= this.OnInitialized;
            application.ViewActivated -= this.OnViewActivated;
            application.Idling -= HybridServer.OnIdling;

            DeviationController.Shutdown(); // salva le impostazioni, libera i buffer e svuota il dizionario
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ eventi

        private void OnDocumentCreated(object sender, DocumentCreatedEventArgs e)
        {
            try { DeviationController.OnDocumentCreated(e.Document); } catch { }
        }

        private void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            try { DeviationController.OnDocumentOpened(e.Document); } catch { }
        }

        private void OnDocumentSavingAs(object sender, DocumentSavingAsEventArgs e)
        {
            try { DeviationController.OnDocumentSavingAs(e.Document, e.PathName); } catch { }
        }

        private void OnDocumentSavedAs(object sender, DocumentSavedAsEventArgs e)
        {
            try { DeviationController.OnDocumentSavedAs(e.Document); } catch { }
        }

        private void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            try { DeviationController.OnDocumentClosing(e.Document); } catch { }
        }

        private void OnInitialized(object sender, ApplicationInitializedEventArgs e)
        {
            try { DeviationController.InitAtStartup(); } catch { }
        }

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try { DeviationController.OnDocumentChanged(e.GetDocument(), e.GetAddedElementIds(), e.GetDeletedElementIds(), e.GetModifiedElementIds()); } catch { }
        }

        private void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                UIApplication uiapp = sender as UIApplication ?? new UIApplication(e.Document.Application);
                DeviationController.OnViewActivated(uiapp, e.Document);
            }
            catch { }
        }

        // ------------------------------------------------------------------ ribbon

        private static void SetText(PushButton b, string text)
        {
            try { if (b != null && b.ItemText != text) b.ItemText = text; } catch { }
        }

        /// <summary>Riallinea etichette dei pulsanti e gruppo di modalità con le impostazioni del progetto attivo.</summary>
        internal static void SyncRibbon(DeviationSettings s)
        {
            try
            {
                SetText(_visButton, L.T(s.Visible ? "Visibilità" : "Visibilità\n(spente)"));
                SetText(_invertButton, L.T(s.ClipInvert ? "Inverti\n(attivo)" : "Inverti"));
                SetText(_analysisButton, L.T(s.Display == DisplayMode.Discrepanze ? "Nascondi\nanalisi" : "Mostra\nanalisi"));

                _syncing = true;
                DisplayMode view = s.Display == DisplayMode.Discrepanze ? s.LastViewMode : s.Display;
                ToggleButton tb;
                if (_modeGroup != null && _modeButtons.TryGetValue(view, out tb) && _modeGroup.Current != tb) _modeGroup.Current = tb;
                if (_quality != null)
                {
                    IList<ComboBoxMember> items = _quality.GetItems();
                    int qi = (int)s.Quality;
                    if (qi >= 0 && qi < items.Count && _quality.Current != items[qi]) _quality.Current = items[qi];
                }
            }
            catch { }
            finally { _syncing = false; }
        }

        private static void BuildRibbon(UIControlledApplication app)
        {
            const string tabName = "Point Deviation";
            try { app.CreateRibbonTab(tabName); } catch { /* tab già esistente */ }

            string asm = Assembly.GetExecutingAssembly().Location;

            // ---------------------------------------------------------- Inserisci
            RibbonPanel pInsert = app.CreateRibbonPanel(tabName, L.T("Inserisci"));
            pInsert.AddItem(Push("LoadCloud", "Libreria\nnuvole", asm, typeof(LoadCloudCommand), "library",
                "Libreria delle nuvole convertite: converte LAS, RCP e RCS una sola volta e le inserisce nel progetto con le opzioni di coordinate."));

            // ---------------------------------------------------------- Visualizzazione
            RibbonPanel pDisplay = app.CreateRibbonPanel(tabName, L.T("Visualizzazione"));
            _modeGroup = (RadioButtonGroup)pDisplay.AddItem(new RadioButtonGroupData("DisplayModes"));
            Action<DisplayMode, string, string, Type, string, string> mode = (m, name, text, cmd, icon, tip) =>
                _modeButtons[m] = _modeGroup.AddItem(Toggle(name, text, asm, cmd, icon, tip));
            mode(DisplayMode.Rgb, "DisplayRgb", "RGB", typeof(DisplayRgbCommand), "rgb", "Colori originali della scansione.");
            mode(DisplayMode.Normali, "DisplayNormals", "Normali", typeof(DisplayNormalsCommand), "normals", "Colori in base all'orientamento delle superfici: rende leggibili forme e spigoli.");
            mode(DisplayMode.XRay, "DisplayXray", "X-ray", typeof(DisplayXrayCommand), "xray", "Punti grigi semitrasparenti: si vede attraverso la nuvola e risalta il modello.");
            mode(DisplayMode.Nuvola, "DisplayCloud", "Nuvola", typeof(DisplayCloudCommand), "cloud_single", "Un colore per ogni nuvola (si cambia dal pannello Visibilità).");
            mode(DisplayMode.Quota, "DisplayElevation", "Quota", typeof(DisplayElevationCommand), "elevation", "Colori in base all'altezza dei punti, dal basso (blu) all'alto (rosso): fa leggere piani, pendenze, gradini e terreno.");

            var qData = new ComboBoxData("Quality") { ToolTip = L.T("Quanti punti disegnare: Automatica si adatta alla vista, Media alleggerisce, Fine mostra più dettaglio.") };
            PushButtonData appData = Push("Appearance", "Aspetto", asm, typeof(AppearanceCommand), "appearance",
                "Dimensione dei punti, luminosità, contrasto, trasparenza e contrasto X-ray.");
            IList<RibbonItem> stack = pDisplay.AddStackedItems(qData, appData);
            _quality = stack[0] as Autodesk.Revit.UI.ComboBox;
            if (_quality != null)
            {
                _quality.AddItem(new ComboBoxMemberData("QAuto", L.T("Qualità automatica")));
                _quality.AddItem(new ComboBoxMemberData("QMedium", L.T("Qualità media")));
                _quality.AddItem(new ComboBoxMemberData("QFine", L.T("Qualità fine")));
                _quality.CurrentChanged += (s2, e2) =>
                {
                    if (_syncing || _quality.Current == null) return;
                    int i = _quality.GetItems().IndexOf(_quality.Current);
                    if (i < 0) return;
                    DeviationController.Current.Quality = (Quality)i;
                    DeviationController.Apply(Job.Recolor);
                };
            }

            // ---------------------------------------------------------- Visibilità
            RibbonPanel pVis = app.CreateRibbonPanel(tabName, L.T("Visibilità"));
            _visButton = (PushButton)pVis.AddItem(Push("CloudOnOff", "Visibilità", asm, typeof(ToggleVisibilityCommand), "onoff",
                "Nasconde o mostra le nuvole nella vista attiva (come «Nascondi elemento» di Revit). Le nuvole si gestiscono anche da Visibilità/Grafica e Gestisci collegamenti."));
            pVis.AddItem(Push("Visibility", "Gestisci", asm, typeof(VisibilityCommand), "visibility",
                "Elenco delle nuvole del progetto: accendile e spegnile una per una nella vista attiva e scegli il loro colore. Passando col mouse su un nome la nuvola si illumina del suo colore."));

            // ---------------------------------------------------------- Regione di ritaglio
            RibbonPanel pClip = app.CreateRibbonPanel(tabName, L.T("Regione di ritaglio"));
            var fenceData = new PulldownButtonData("Fence", L.T("Recinto"))
            {
                ToolTip = L.T("Disegna nella vista attiva un recinto: restano i punti dentro (tieni MAIUSC per tenere quelli fuori). Più recinti da viste diverse si sommano."),
                LargeImage = Icon("fence", 32),
                Image = Icon("fence", 16)
            };
            var fence = (PulldownButton)pClip.AddItem(fenceData);
            fence.AddPushButton(Push("FencePolygon", "Poligono", asm, typeof(FencePolygonCommand), "fence",
                "Clicca i vertici; INVIO conferma, BACKSPACE toglie l'ultimo punto, ESC esce."));
            fence.AddPushButton(Push("FenceRect", "Rettangolo", asm, typeof(FenceRectangleCommand), "fence_rect",
                "Premi su un angolo e trascina."));
            fence.AddPushButton(Push("FenceCircle", "Cerchio", asm, typeof(FenceCircleCommand), "fence_circle",
                "Premi sul centro e trascina il raggio."));
            _invertButton = (PushButton)pClip.AddItem(Push("ClipInvert", "Inverti", asm, typeof(InvertClipCommand), "clip_invert",
                "Inverte il ritaglio: mostra ciò che i recinti escludono e nasconde la zona isolata."));
            pClip.AddItem(Push("ClipManage", "Gestisci", asm, typeof(ClipManagerCommand), "clip_manage",
                "Ritagli salvati con un nome e recinti correnti: richiama, accendi, spegni, elimina, torna a tutti i punti."));

            // ---------------------------------------------------------- Analisi
            RibbonPanel pAnalyze = app.CreateRibbonPanel(tabName, L.T("Analisi"));
            _analysisButton = (PushButton)pAnalyze.AddItem(Push("ShowAnalysis", "Mostra\nanalisi", asm, typeof(ShowAnalysisCommand), "deviation",
                "Mostra o nasconde i colori dell'analisi. Nascondendola si torna a RGB o quota, e Legenda e Aggiorna si disattivano."));
            pAnalyze.AddItem(Push("AnalyzeView", "Analizza\nvista", asm, typeof(AnalyzeViewCommand), "analyze_view",
                "Confronta la nuvola con tutti gli elementi visibili nella vista attiva."));
            pAnalyze.AddItem(Push("AnalyzeSelection", "Analizza\nselezione", asm, typeof(AnalyzeSelectionCommand), "analyze_sel",
                "Confronta la nuvola solo con gli elementi selezionati (anche istanze di link). La selezione resta memorizzata per i ricalcoli."));
            pAnalyze.AddItem(Push("ColorProfiles", "Profili\ncolore", asm, typeof(ColorProfilesCommand), "colormap",
                "Scegli, modifica o crea i profili: gradiente, tolleranza a fasce, classi, completezza, interferenze."));
            pAnalyze.AddItem(Push("Legend", "Legenda", asm, typeof(ToggleLegendCommand), "legend",
                "Apre o chiude la legenda del profilo attivo."));
            pAnalyze.AddItem(Push("Recompute", "Aggiorna", asm, typeof(RecomputeCommand), "recompute",
                "Ricalcola le distanze dopo aver modificato il modello."));
            pAnalyze.AddItem(Push("Settings", "Impostazioni", asm, typeof(ShowCommand), "settings",
                "Modelli confrontati, profilo colore, nuvola senza modello, punti per le statistiche."));

            // ---------------------------------------------------------- Strumenti
            RibbonPanel pTools = app.CreateRibbonPanel(tabName, L.T("Strumenti"));
            pTools.AddItem(Push("Survey", "Coordinate\ndi rilievo", asm, typeof(SurveyCommand), "survey",
                "Imposta le coordinate condivise del progetto su quelle originali della nuvola selezionata."));
            pTools.AddItem(Push("Measure", "Misura\npunti", asm, typeof(MeasureCommand), "measure",
                "Clicca sui punti della nuvola: vengono creati dei marcatori a cui si agganciano quote e misure di Revit."));

            // ---------------------------------------------------------- Impostazioni del plugin
            RibbonPanel pManage = app.CreateRibbonPanel(tabName, L.T("Impostazioni"));
            pManage.AddItem(Push("Advanced", "Avanzate", asm, typeof(AdvancedCommand), "settings",
                "Registro di diagnostica, colori per livello, limite di punti e memoria. Valgono per questo computer."));
        }

        // ------------------------------------------------------------------ icone incorporate nella DLL

        private static BitmapSource Icon(string name, int size)
        {
            try
            {
                string resource = "PointDeviation.Resources." + name + size + ".png";
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                {
                    if (s == null) return null;
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = s;
                    bi.EndInit();
                    bi.Freeze();
                    return bi;
                }
            }
            catch
            {
                return null;
            }
        }

        private static readonly string[] NeedsCloud = { "DisplayNormals", "DisplayXray", "DisplayCloud", "Visibility", "Survey", "Measure", "DisplayRgb", "DisplayElevation", "CloudOnOff", "Appearance", "FencePolygon", "FenceRect", "FenceCircle", "ClipInvert", "ClipManage", "AnalyzeView", "AnalyzeSelection" };
        private static readonly string[] NeedsAnalysis = { "ShowAnalysis" };
        private static readonly string[] NeedsAnalysisShown = { "Legend", "Recompute" };

        private static PushButtonData Push(string name, string text, string assembly, Type command, string icon, string tooltip)
        {
            var d = new PushButtonData(name, L.T(text), assembly, command.FullName);
            // pulsanti grigi finché non ci sono la nuvola (o l'analisi) nel progetto attivo
            if (Array.IndexOf(NeedsAnalysisShown, name) >= 0) d.AvailabilityClassName = typeof(AnalysisShownAvailability).FullName;
            else if (Array.IndexOf(NeedsAnalysis, name) >= 0) d.AvailabilityClassName = typeof(AnalysisAvailability).FullName;
            else if (Array.IndexOf(NeedsCloud, name) >= 0) d.AvailabilityClassName = typeof(CloudAvailability).FullName;
            else d.AvailabilityClassName = typeof(ProjectAvailability).FullName;
            d.ToolTip = L.T(tooltip);
            d.LargeImage = Icon(icon, 32);
            d.Image = Icon(icon, 16);
            return d;
        }

        private static ToggleButtonData Toggle(string name, string text, string assembly, Type command, string icon, string tooltip)
        {
            var d = new ToggleButtonData(name, L.T(text), assembly, command.FullName);
            d.ToolTip = L.T(tooltip);
            d.AvailabilityClassName = typeof(CloudAvailability).FullName;
            d.LargeImage = Icon(icon, 32);
            d.Image = Icon(icon, 16);
            return d;
        }
    }

    /// <summary>Apre la finestra delle impostazioni (usata da "Impostazioni" e "Tolleranze").</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ShowCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                DeviationController.ShowWindow(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
