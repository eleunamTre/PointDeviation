using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PointDeviation
{
    /// <summary>
    /// Disponibilità dei pulsanti: Revit la chiede spesso, quindi i controlli leggono solo lo stato in memoria.
    /// I pulsanti non disponibili appaiono grigi e non si possono premere.
    /// </summary>
    public class CloudAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selected)
        {
            try
            {
                if (app.ActiveUIDocument == null) return false;
                DocumentData d = DeviationController.GetData(app.ActiveUIDocument.Document);
                return d != null && d.HasCloud;
            }
            catch { return false; }
        }
    }

    /// <summary>Come sopra, e in più serve un'analisi già calcolata (Analizza vista o Analizza selezione).</summary>
    public class AnalysisAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selected)
        {
            try
            {
                if (app.ActiveUIDocument == null) return false;
                DocumentData d = DeviationController.GetData(app.ActiveUIDocument.Document);
                return d != null && d.HasCloud && d.HasDistances && AnalysisRegistry.Get(d.Key) != null;
            }
            catch { return false; }
        }
    }

    /// <summary>Analisi calcolata e visibile (Legenda, Aggiorna): nascondendo l'analisi questi pulsanti si spengono.</summary>
    public class AnalysisShownAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selected)
        {
            try
            {
                if (app.ActiveUIDocument == null) return false;
                DocumentData d = DeviationController.GetData(app.ActiveUIDocument.Document);
                return d != null && d.HasCloud && d.HasDistances && AnalysisRegistry.Get(d.Key) != null
                    && d.Settings.Display == DisplayMode.Discrepanze;
            }
            catch { return false; }
        }
    }

    /// <summary>Serve solo un progetto aperto (libreria, profili, impostazioni).</summary>
    public class ProjectAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selected)
        {
            try { return app.ActiveUIDocument != null && !app.ActiveUIDocument.Document.IsFamilyDocument; }
            catch { return false; }
        }
    }
}
