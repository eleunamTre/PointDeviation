using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace PointDeviation
{
    /// <summary>
    /// Voce 278: mesh del modello per la profondità in movimento, preparata da sola (senza analisi).
    /// Nelle immagini intermedie la libreria nativa disegna questa mesh nella profondità, con lo scarto misurato di
    /// Revit (voci 272-276), così la nuvola resta dietro al modello anche quando Revit non ha ridisegnato tutto.
    /// La geometria si legge solo nel thread di Revit: si lavora nell'evento Idling a passi di circa 40 ms, così Revit
    /// resta usabile. Elementi della vista attiva (nascosti esclusi) più quelli dei collegamenti visibili.
    /// Si rifà quando cambia la vista o quando cambiano elementi del modello (dopo 2 s di calma).
    /// </summary>
    internal static class DepthMesh
    {
        internal sealed class Mesh
        {
            public string Key;
            public float[] Verts;     // 9 float per triangolo, relativi a Origin
            public double[] Origin;
            public int Triangles, Elements, Links;
        }

        private sealed class Job
        {
            public string Key;
            public Document Doc;
            public View View;
            public string ViewName;
            public readonly List<KeyValuePair<Document, Transform>> Sources = new List<KeyValuePair<Document, Transform>>();
            public readonly List<List<ElementId>> Ids = new List<List<ElementId>>();
            public int Source, Index;
            public readonly List<float> Verts = new List<float>(1 << 20);
            public readonly List<double> Scratch = new List<double>(1 << 12);
            public double Ox, Oy, Oz; public bool HasOrigin;
            public int Triangles, Elements, Links, Steps;
            public readonly Stopwatch Work = new Stopwatch();
            public readonly Stopwatch Wall = Stopwatch.StartNew();
            public bool Capped;
        }

        private const int MaxTriangles = 4000000;
        private const int StepMs = 40;
        private const int QuietMs = 2000;

        private static Mesh _ready;
        private static Job _job;
        private static string _dirtyKey;   // documento con modifiche in attesa
        private static long _dirtyAt;
        private static readonly Options Opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };

        private static string KeyOf(Document doc, View view) { return DeviationController.KeyOf(doc) + "|" + view.Id.Value; }

        /// <summary>Mesh pronta per questa vista, o null.</summary>
        public static Mesh For(View view)
        {
            Mesh m = _ready;
            if (m == null || view == null) return null;
            return m.Key == KeyOf(view.Document, view) ? m : null;
        }

        /// <summary>Da Idling: avvia o continua la preparazione. True se c'e' ancora lavoro (Revit ci richiami subito).</summary>
        public static bool Tick(UIApplication app)
        {
            try
            {
                if (!NativeProbe.ShaderMode || AdvancedSettings.Current.NoMeshDepth || !AdvancedSettings.Current.BeaconCamera) { _job = null; return false; }
                UIDocument ud = app.ActiveUIDocument;
                View view = ud != null ? ud.ActiveView as View3D : null;
                if (view == null || view.IsTemplate) return false;
                Document doc = ud.Document;
                string key = KeyOf(doc, view);
                string docKey = DeviationController.KeyOf(doc);
                bool dirty = _dirtyKey == docKey;
                if (dirty && Environment.TickCount - _dirtyAt < QuietMs) return true;   // si aspetta la fine delle modifiche
                if (_job != null && _job.Key != key) _job = null;   // vista cambiata: si ricomincia
                if (_job == null)
                {
                    if (!dirty && _ready != null && _ready.Key == key) return false;
                    _job = Start(doc, view, key);
                    if (dirty) _dirtyKey = null;
                }
                Step(_job);
                if (_job.Index < 0) { Finish(_job); _job = null; return false; }
                return true;
            }
            catch (Exception ex) { Log.Error("Mesh", ex); _job = null; return false; }
        }

        /// <summary>Da DocumentChanged: modifiche a elementi di modello o ai collegamenti rendono vecchia la mesh.</summary>
        public static void OnChanged(Document doc, ICollection<ElementId> added, ICollection<ElementId> deleted, ICollection<ElementId> modified)
        {
            try
            {
                if (doc == null) return;
                bool relevant = deleted != null && deleted.Count > 0;
                int seen = 0;
                foreach (ICollection<ElementId> list in new[] { added, modified })
                {
                    if (relevant || list == null) continue;
                    foreach (ElementId id in list)
                    {
                        if (++seen > 500) { relevant = true; break; }
                        Element e = doc.GetElement(id);
                        if (e == null || e is View || e is PointCloudInstance || e is PointCloudType) continue;
                        if (e is RevitLinkInstance || e is RevitLinkType) { relevant = true; break; }
                        if (e.Category != null && e.Category.CategoryType == CategoryType.Model) { relevant = true; break; }
                    }
                }
                if (!relevant) return;
                _dirtyKey = DeviationController.KeyOf(doc);
                _dirtyAt = Environment.TickCount;
            }
            catch { }
        }

        private static Job Start(Document doc, View view, string key)
        {
            var j = new Job { Key = key, Doc = doc, View = view, ViewName = view.Name };
            j.Work.Start();
            // modello in uso: elementi della vista (quelli nascosti nella vista restano fuori)
            j.Sources.Add(new KeyValuePair<Document, Transform>(doc, null));
            j.Ids.Add(new List<ElementId>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds()));
            // collegamenti visibili nella vista
            bool linksHidden = false;
            try { linksHidden = view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_RvtLinks)); } catch { }
            if (!linksHidden)
                foreach (Element el in new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance)))
                {
                    var li = el as RevitLinkInstance;
                    Document ld = li != null ? li.GetLinkDocument() : null;
                    if (ld == null) continue;
                    j.Sources.Add(new KeyValuePair<Document, Transform>(ld, li.GetTotalTransform()));
                    j.Ids.Add(new List<ElementId>(new FilteredElementCollector(ld).WhereElementIsNotElementType().ToElementIds()));
                }
            j.Work.Stop();
            return j;
        }

        private static void Step(Job j)
        {
            j.Work.Start(); j.Steps++;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < StepMs)
            {
                if (j.Source >= j.Sources.Count || j.Capped) { j.Index = -1; break; }
                List<ElementId> ids = j.Ids[j.Source];
                if (j.Index >= ids.Count) { if (j.Source > 0 && j.Index > 0) j.Links++; j.Source++; j.Index = 0; continue; }
                Document src = j.Sources[j.Source].Key;
                Transform xf = j.Sources[j.Source].Value;
                Element e = src.GetElement(ids[j.Index++]);
                if (e == null) continue;
                j.Scratch.Clear();
                int n = ModelMeshExtractor.AddElementPublic(e, Opt, xf, j.Scratch);
                if (n <= 0) continue;
                if (!j.HasOrigin) { j.Ox = j.Scratch[0]; j.Oy = j.Scratch[1]; j.Oz = j.Scratch[2]; j.HasOrigin = true; }
                for (int i = 0; i < n * 9; i += 3)
                {
                    j.Verts.Add((float)(j.Scratch[i] - j.Ox)); j.Verts.Add((float)(j.Scratch[i + 1] - j.Oy)); j.Verts.Add((float)(j.Scratch[i + 2] - j.Oz));
                }
                j.Triangles += n; j.Elements++;
                if (j.Triangles >= MaxTriangles) j.Capped = true;
            }
            j.Work.Stop();
        }

        private static void Finish(Job j)
        {
            if (j.Triangles <= 0) { Log.Write("Mesh", string.Format("«{0}»: nessun triangolo, mesh non preparata", j.ViewName)); return; }
            _ready = new Mesh { Key = j.Key, Verts = j.Verts.ToArray(), Origin = new[] { j.Ox, j.Oy, j.Oz }, Triangles = j.Triangles, Elements = j.Elements, Links = j.Links };
            Log.Write("Mesh", string.Format("«{0}»: mesh per la profondità pronta, {1:N0} elementi ({2} collegamenti), {3:N0} triangoli{4}; lavoro {5:F1} s in {6} passi, tempo totale {7:F1} s (voce 278)",
                j.ViewName, j.Elements, j.Links, j.Triangles, j.Capped ? " (limite raggiunto)" : "", j.Work.Elapsed.TotalSeconds, j.Steps, j.Wall.Elapsed.TotalSeconds));
            Redraw.Request();
        }
    }
}
