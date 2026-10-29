using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PointDeviation
{
    internal static class CmdHelper
    {
        public static Result Run(UIApplication app, ref string message, Action action)
        {
            try
            {
                DeviationController.Init(app);
                action();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        public static void SetDisplay(DisplayMode mode)
        {
            DeviationSettings s = DeviationController.Current;
            if (mode != DisplayMode.Discrepanze) s.LastViewMode = mode;
            s.Display = mode;
            App.SyncRibbon(s);
            DeviationController.Apply(Job.Recolor);
        }
    }

    // ------------------------------------------------------------------ Inserisci

    /// <summary>Apre la libreria delle nuvole convertite (inserimento e conversione).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class LoadCloudCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            return CmdHelper.Run(app, ref message, () => DeviationController.ShowLibrary(app));
        }
    }

    // ------------------------------------------------------------------ Visualizzazione

    [Transaction(TransactionMode.ReadOnly)]
    public class DisplayRgbCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () => CmdHelper.SetDisplay(DisplayMode.Rgb));
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class DisplayElevationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () => CmdHelper.SetDisplay(DisplayMode.Quota));
        }
    }

    /// <summary>Nasconde o mostra la nuvola nella vista attiva (come «Nascondi elemento» di Revit).</summary>
    [Transaction(TransactionMode.Manual)]
    public class ToggleVisibilityCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            return CmdHelper.Run(app, ref message, () =>
            {
                DocumentData d = DeviationController.ActiveData;
                UIDocument uidoc = app.ActiveUIDocument;
                if (d == null || uidoc == null || !d.HasCloud)
                {
                    TaskDialog.Show("Point Deviation", L.T("Inserisci prima una nuvola dalla libreria."));
                    return;
                }
                DeviationSettings s = DeviationController.Current;
                NativeSync.ToggleAllInView(uidoc.Document, uidoc.ActiveView, d);
                App.SyncRibbon(s);
            });
        }
    }

    // ------------------------------------------------------------------ Regione di ritaglio

    /// <summary>Disegno di un recinto (comune ai tre comandi).</summary>
    internal static class FenceCommandHelper
    {
        public static Result Run(ExternalCommandData data, ref string message, FenceShape shape)
        {
            UIApplication app = data.Application;
            return CmdHelper.Run(app, ref message, () =>
            {
                DocumentData d = DeviationController.ActiveData;
                if (d == null || !d.HasCloud)
                {
                    TaskDialog.Show("Point Deviation", L.T("Inserisci prima una nuvola dalla libreria."));
                    return;
                }
                ClipRegion r = FenceTool.Draw(app, shape);
                if (r == null) return;
                DeviationSettings s = DeviationController.Current;
                s.Clips = new List<ClipRegion>(s.Clips ?? new List<ClipRegion>()) { r };
                App.SyncRibbon(s);
                DeviationController.NotifySettingsChanged();
                DeviationController.Apply(Job.Recolor);
            });
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class FencePolygonCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return FenceCommandHelper.Run(commandData, ref message, FenceShape.Poligono);
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class FenceRectangleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return FenceCommandHelper.Run(commandData, ref message, FenceShape.Rettangolo);
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class FenceCircleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return FenceCommandHelper.Run(commandData, ref message, FenceShape.Cerchio);
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class ClipManagerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () => DeviationController.ShowClipManager());
        }
    }

    /// <summary>Toglie la nuvola dal progetto (cancella anche il suo elemento nel modello).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class RemoveCloudCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () => DeviationController.Apply(Job.Clear));
        }
    }

    /// <summary>Passa alla vista dell'analisi (calcola le distanze se mancano).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ShowAnalysisCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // interruttore: con l'analisi visibile torna alla visualizzazione di prima (RGB o quota), altrimenti la mostra
            return CmdHelper.Run(commandData.Application, ref message, () =>
            {
                DeviationSettings s = DeviationController.Current;
                CmdHelper.SetDisplay(s.Display == DisplayMode.Discrepanze ? s.LastViewMode : DisplayMode.Discrepanze);
            });
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class InvertClipCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () =>
            {
                DeviationSettings s = DeviationController.Current;
                s.ClipInvert = !s.ClipInvert;
                DeviationController.Apply(Job.Recolor);
                App.SyncRibbon(s);
            });
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class AdvancedCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            return CmdHelper.Run(app, ref message, () => DeviationController.ShowAdvanced(app));
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class OpenLogCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!System.IO.File.Exists(Log.FilePath))
                {
                    System.IO.Directory.CreateDirectory(Log.Folder);
                    System.IO.File.AppendAllText(Log.FilePath, "");
                }
                System.Diagnostics.Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class AppearanceCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            return CmdHelper.Run(app, ref message, () => DeviationController.ShowAppearance(app));
        }
    }

    // ------------------------------------------------------------------ Analisi

    [Transaction(TransactionMode.ReadOnly)]
    public class ToggleLegendCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () =>
            {
                DeviationController.ToggleLegend();
            });
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class RecomputeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () =>
            {
                // se la modalità non è Discrepanze, "Aggiorna" porta comunque alla modalità che usa le distanze
                DeviationController.Current.Display = DisplayMode.Discrepanze;
                App.SyncRibbon(DeviationController.Current);
                DeviationController.Apply(Job.Recompute);
            });
        }
    }

    /// <summary>Memorizza la selezione corrente e calcola le discrepanze solo rispetto a quegli elementi.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeSelectionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            return CmdHelper.Run(app, ref message, () =>
            {
                UIDocument uidoc = app.ActiveUIDocument;
                DocumentData data = DeviationController.ActiveData;
                if (uidoc == null || data == null) return;

                ICollection<ElementId> ids = uidoc.Selection.GetElementIds();
                if (ids.Count == 0)
                {
                    TaskDialog.Show("Point Deviation",
                        L.T("Seleziona gli elementi del modello da confrontare con la nuvola (anche istanze di link), poi premi di nuovo «Analizza selezione»."));
                    return;
                }

                data.SelectionIds = new List<ElementId>(ids);
                DeviationSettings s = DeviationController.Current;
                s.SelectionOnly = true;
                s.Display = DisplayMode.Discrepanze;
                App.SyncRibbon(s);
                DeviationController.NotifySettingsChanged();
                DeviationController.Apply(Job.Recompute);
            });
        }
    }

    /// <summary>Torna al confronto con tutti gli elementi visibili nella vista attiva.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeViewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () =>
            {
                DeviationSettings s = DeviationController.Current;
                s.SelectionOnly = false;
                s.Display = DisplayMode.Discrepanze;
                App.SyncRibbon(s);
                DeviationController.NotifySettingsChanged();
                DeviationController.Apply(Job.Recompute);
            });
        }
    }

    /// <summary>Gestione dei profili colore (gradiente, fasce di tolleranza, classi).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ColorProfilesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () => DeviationController.ShowProfiles());
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class DisplayNormalsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () => CmdHelper.SetDisplay(DisplayMode.Normali));
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class DisplayXrayCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () => CmdHelper.SetDisplay(DisplayMode.XRay));
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class DisplayCloudCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () => CmdHelper.SetDisplay(DisplayMode.Nuvola));
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class VisibilityCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CmdHelper.Run(commandData.Application, ref message, () => DeviationController.ShowVisibility());
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MeasureCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            return CmdHelper.Run(app, ref message, () => MeasureTool.Run(app));
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class SurveyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            return CmdHelper.Run(app, ref message, () => SurveyTool.Run(app));
        }
    }
}
