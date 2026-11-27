using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace PointDeviation
{
    /// <summary>
    /// Voce 307: nuvole del plugin non selezionabili (opzione in Visibilità › Gestisci, spenta di serie). Revit non
    /// permette di rendere un elemento non selezionabile senza modificarlo (bloccarlo cambia il modello, e il plugin non
    /// tocca il modello): quando nella selezione entra una nuvola del plugin (clic, finestra di selezione, Tab) la si
    /// toglie subito, lasciando gli altri elementi selezionati. L'evidenziazione al passaggio del mouse resta (è di Revit).
    /// In alternativa, senza plugin: interruttore «Seleziona collegamenti» di Revit in basso a destra (vale anche per i
    /// modelli collegati).
    /// </summary>
    internal static class CloudSelection
    {
        private static Document _pendDoc;
        private static List<ElementId> _pendKeep;
        private static bool _busy;

        public static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_busy || !AdvancedSettings.Current.CloudNotSelectable) return;
            try
            {
                Document doc = e.GetDocument();
                if (doc == null) return;
                DocumentData d = DeviationController.GetData(doc);
                if (d == null || d.Layers.Count == 0) return;
                var keep = new List<ElementId>();
                int removed = 0;
                foreach (ElementId id in e.GetSelectedElements())
                {
                    if (d.Layers.Exists(l => l.InstanceId == id)) removed++;
                    else keep.Add(id);
                }
                if (removed == 0) return;
                UIApplication app = sender as UIApplication;
                UIDocument ui = app != null ? app.ActiveUIDocument : null;
                if (ui != null && ui.Document.Equals(doc))
                {
                    try
                    {
                        _busy = true;
                        ui.Selection.SetElementIds(keep);
                        Log.Debug("Selezione", string.Format("nuvola tolta dalla selezione ({0} elementi restano selezionati)", keep.Count));
                        return;
                    }
                    catch (Exception ex) { Log.Debug("Selezione", "tolta al prossimo Idling: " + ex.Message); }
                    finally { _busy = false; }
                }
                _pendDoc = doc; _pendKeep = keep;   // non si è potuto subito: al prossimo Idling
            }
            catch (Exception ex) { Log.Debug("Selezione", ex.Message); }
        }

        public static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_pendDoc == null) return;
            Document doc = _pendDoc; List<ElementId> keep = _pendKeep;
            _pendDoc = null; _pendKeep = null;
            try
            {
                UIApplication app = sender as UIApplication;
                UIDocument ui = app != null ? app.ActiveUIDocument : null;
                if (ui == null || !ui.Document.Equals(doc)) return;
                _busy = true;
                ui.Selection.SetElementIds(keep);
            }
            catch (Exception ex) { Log.Debug("Selezione", ex.Message); }
            finally { _busy = false; }
        }
    }
}
