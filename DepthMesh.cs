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
    /// Voce 312: la mesh è fatta di parti (il modello in uso per quella vista, ogni collegamento con la sua posizione)
    /// tenute in memoria: si rifà solo la parte che è cambiata. Cambio di vista con gli stessi collegamenti: si rifà solo
    /// il modello in uso (o niente, se la vista era già stata preparata). Il riquadro di sezione non rifà la mesh.
    /// </summary>
    internal static class DepthMesh
    {
        internal sealed class Mesh
        {
            public string Key;
            public string Comp;       // voce 312: elenco delle parti che la compongono
            public float[] Verts;     // 9 float per triangolo, relativi a Origin
            public double[] Origin;
            public int Triangles, Elements, Links;
        }

        /// <summary>Voce 312: una parte della mesh (modello in uso per una vista, o un collegamento in una posizione).</summary>
        private sealed class Part
        {
            public string Key;
            public bool Host;
            public string DocKey;
            public float[] Verts;     // relativi a Origin
            public double[] Origin;
            public int Triangles, Elements;
            public long Used;
        }

        private sealed class Todo
        {
            public string Key; public bool Host; public string DocKey;
            public Document Doc; public Transform Xf; public List<ElementId> Ids;
        }

        private sealed class Job
        {
            public string Key, Comp;
            public string ViewName;
            public readonly List<string> PartKeys = new List<string>();
            public readonly List<Todo> Todos = new List<Todo>();
            public int Source, Index;
            public List<float> Verts = new List<float>(1 << 16);
            public readonly List<double> Scratch = new List<double>(1 << 12);
            public double Ox, Oy, Oz; public bool HasOrigin;
            public int PartTris, PartElements;
            public int Triangles, Steps, Reused, Built;
            public readonly Stopwatch Work = new Stopwatch();
            public readonly Stopwatch Wall = Stopwatch.StartNew();
            public bool Capped;
            public double MaxElemMs; public string MaxElem = "";
            public double MaxStepMs;
        }

        private const int MaxTriangles = 4000000;
        private const int StepMs = 40;
        private const int QuietMs = 2000;
        private const long CacheTriangles = 4000000;   // voce 312: parti tenute in memoria (circa 36 byte a triangolo)

        private static Mesh _ready;
        private static Job _job;
        private static string _dirtyKey;   // documento con modifiche in attesa
        private static long _dirtyAt;
        private static bool _dirtyHost, _dirtyLinks;   // voce 312: quali parti sono vecchie
        private static long _useTick;
        private static readonly Dictionary<string, Part> _parts = new Dictionary<string, Part>();
        private static readonly Options Opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };

        private static string KeyOf(Document doc, View view) { return DeviationController.KeyOf(doc) + "|" + view.Id.Value; }
        private static bool Cache { get { return !AdvancedSettings.Current.NoMeshCache; } }

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
                if (_job != null && _job.Key != key) _job = null;   // vista cambiata: si ricomincia (le parti finite restano)
                if (_job == null)
                {
                    if (!dirty && _ready != null && _ready.Key == key) return false;
                    if (dirty)
                    {
                        _dirtyKey = null;
                        if (!Cache || _dirtyHost) Drop(docKey, true);
                        if (!Cache || _dirtyLinks) Drop(null, false);
                        _dirtyHost = _dirtyLinks = false;
                    }
                    if (!Cache) _parts.Clear();
                    _job = Plan(doc, view, key, docKey);
                    // voce 296: la mesh serve per i collegamenti (senza, la profondita' di Revit basta). Senza collegamenti
                    // visibili non si prepara: si segna una mesh vuota per questa vista e niente mesh in movimento.
                    if (_job.PartKeys.Count <= 1 && !AdvancedSettings.Current.MeshWithoutLinks)
                    {
                        _ready = new Mesh { Key = key, Comp = _job.Comp, Verts = new float[0], Origin = new double[3], Triangles = 0 };
                        _job = null;
                        return false;
                    }
                    // voce 312: stesse parti di prima (per esempio dopo una modifica che non toccava la geometria): niente da rifare
                    if (_ready != null && _ready.Key == key && _ready.Comp == _job.Comp && _job.Todos.Count == 0) { _job = null; return false; }
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
                bool host = deleted != null && deleted.Count > 0, links = false;
                int seen = 0;
                bool box = AdvancedSettings.Current.MeshOnSectionBox;
                foreach (ICollection<ElementId> list in new[] { added, modified })
                {
                    if (list == null) continue;
                    foreach (ElementId id in list)
                    {
                        if (++seen > 500) { host = links = true; break; }
                        Element e = doc.GetElement(id);
                        if (e == null || e is View || e is PointCloudInstance || e is PointCloudType) continue;
                        if (e is RevitLinkType) { links = true; continue; }
                        if (e is RevitLinkInstance) { links = true; continue; }   // posizione nuova: la parte si ritrova dalla chiave
                        if (e.Category == null || e.Category.CategoryType != CategoryType.Model) continue;
                        // voce 312: il riquadro di sezione non cambia la geometria della mesh (opzione per rifarla lo stesso)
                        if (!box && e.Category.Id.Value == (long)BuiltInCategory.OST_SectionBox) continue;
                        host = true;
                    }
                    if (seen > 500) break;
                }
                if (!host && !links) return;
                _dirtyKey = DeviationController.KeyOf(doc);
                _dirtyAt = Environment.TickCount;
                _dirtyHost |= host;
                // un collegamento spostato ha una chiave nuova (trasformazione nella chiave); ricaricato o scaricato
                // (RevitLinkType) i suoi elementi possono essere cambiati: si rifanno tutte le parti dei collegamenti
                if (links) foreach (ElementId id in modified ?? new ElementId[0]) if (doc.GetElement(id) is RevitLinkType) { _dirtyLinks = true; break; }
                if (seen > 500) _dirtyLinks = true;
            }
            catch { }
        }

        /// <summary>Voce 312: toglie le parti del modello in uso di un documento (host) o tutte quelle dei collegamenti.</summary>
        private static void Drop(string docKey, bool hostParts)
        {
            var gone = new List<string>();
            foreach (var kv in _parts)
                if (hostParts ? (kv.Value.Host && kv.Value.DocKey == docKey) : !kv.Value.Host) gone.Add(kv.Key);
            foreach (string k in gone) _parts.Remove(k);
        }

        private static string XfKey(Transform t)
        {
            if (t == null) return "I";
            Func<XYZ, string> f = p => p.X.ToString("F5") + "," + p.Y.ToString("F5") + "," + p.Z.ToString("F5");
            return f(t.Origin) + ";" + f(t.BasisX) + ";" + f(t.BasisY) + ";" + f(t.BasisZ);
        }

        private static Job Plan(Document doc, View view, string key, string docKey)
        {
            var j = new Job { Key = key, ViewName = view.Name };
            j.Work.Start();
            // modello in uso: elementi della vista (quelli nascosti nella vista restano fuori)
            string hk = "H|" + key;
            j.PartKeys.Add(hk);
            if (!_parts.ContainsKey(hk))
                j.Todos.Add(new Todo { Key = hk, Host = true, DocKey = docKey, Doc = doc, Ids = new List<ElementId>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds()) });
            // collegamenti visibili nella vista
            bool linksHidden = false;
            try { linksHidden = view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_RvtLinks)); } catch { }
            if (!linksHidden)
                foreach (Element el in new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance)))
                {
                    var li = el as RevitLinkInstance;
                    Document ld = li != null ? li.GetLinkDocument() : null;
                    if (ld == null) continue;
                    Transform xf = li.GetTotalTransform();
                    string lk = "L|" + ld.PathName + "|" + ld.Title + "|" + XfKey(xf);
                    if (j.PartKeys.Contains(lk)) continue;
                    j.PartKeys.Add(lk);
                    if (!_parts.ContainsKey(lk))
                        j.Todos.Add(new Todo { Key = lk, Host = false, DocKey = docKey, Doc = ld, Xf = xf, Ids = new List<ElementId>(new FilteredElementCollector(ld).WhereElementIsNotElementType().ToElementIds()) });
                }
            j.Comp = string.Join("\n", j.PartKeys);
            j.Reused = j.PartKeys.Count - j.Todos.Count;
            j.Work.Stop();
            return j;
        }

        private static void Step(Job j)
        {
            j.Work.Start(); j.Steps++;
            Busy.Enter("mesh per la profondità");
            var sw = Stopwatch.StartNew();
            var el = new Stopwatch();
            while (sw.ElapsedMilliseconds < StepMs)
            {
                if (j.Source >= j.Todos.Count || j.Capped) { j.Index = -1; break; }
                Todo t = j.Todos[j.Source];
                if (j.Index >= t.Ids.Count) { EndPart(j, t); j.Source++; j.Index = 0; continue; }
                el.Restart();
                Element e = t.Doc.GetElement(t.Ids[j.Index++]);
                if (e == null) continue;
                j.Scratch.Clear();
                int n = ModelMeshExtractor.AddElementPublic(e, Opt, t.Xf, j.Scratch);
                double ms = el.Elapsed.TotalMilliseconds;
                if (ms > j.MaxElemMs) { j.MaxElemMs = ms; j.MaxElem = (e.Category != null ? e.Category.Name : "?") + " " + e.Id.Value + (t.Host ? "" : " (collegamento)"); }
                if (n <= 0) continue;
                if (!j.HasOrigin) { j.Ox = j.Scratch[0]; j.Oy = j.Scratch[1]; j.Oz = j.Scratch[2]; j.HasOrigin = true; }
                for (int i = 0; i < n * 9; i += 3)
                {
                    j.Verts.Add((float)(j.Scratch[i] - j.Ox)); j.Verts.Add((float)(j.Scratch[i + 1] - j.Oy)); j.Verts.Add((float)(j.Scratch[i + 2] - j.Oz));
                }
                j.PartTris += n; j.PartElements++; j.Triangles += n;
                if (j.Triangles >= MaxTriangles) j.Capped = true;
            }
            double st = sw.Elapsed.TotalMilliseconds;
            if (st > j.MaxStepMs) j.MaxStepMs = st;
            Busy.Enter("Idling");
            j.Work.Stop();
        }

        /// <summary>Voce 312: parte finita, messa in memoria (anche vuota: un collegamento senza geometria non si rilegge).</summary>
        private static void EndPart(Job j, Todo t)
        {
            var p = new Part { Key = t.Key, Host = t.Host, DocKey = t.DocKey, Verts = j.Verts.ToArray(), Origin = new[] { j.Ox, j.Oy, j.Oz }, Triangles = j.PartTris, Elements = j.PartElements, Used = ++_useTick };
            _parts[t.Key] = p;
            j.Built++;
            j.Verts = new List<float>(1 << 16); j.HasOrigin = false; j.PartTris = 0; j.PartElements = 0;
        }

        private static void Finish(Job j)
        {
            // si mettono insieme le parti (riusate e appena fatte), relative all'origine della prima con triangoli
            var list = new List<Part>();
            foreach (string k in j.PartKeys) { Part p; if (_parts.TryGetValue(k, out p)) { p.Used = ++_useTick; list.Add(p); } }
            int tris = 0, elems = 0, links = 0;
            double[] o = null;
            foreach (Part p in list) { tris += p.Triangles; elems += p.Elements; if (!p.Host && p.Triangles > 0) links++; if (o == null && p.Triangles > 0) o = p.Origin; }
            Trim(j.PartKeys);
            if (tris <= 0 || o == null) { Log.Write("Mesh", string.Format("«{0}»: nessun triangolo, mesh non preparata", j.ViewName)); _ready = new Mesh { Key = j.Key, Comp = j.Comp, Verts = new float[0], Origin = new double[3] }; return; }
            if (tris > MaxTriangles) tris = MaxTriangles;
            var v = new float[(long)tris * 9];
            int at = 0;
            foreach (Part p in list)
            {
                if (p.Triangles <= 0) continue;
                float dx = (float)(p.Origin[0] - o[0]), dy = (float)(p.Origin[1] - o[1]), dz = (float)(p.Origin[2] - o[2]);
                int n = Math.Min(p.Verts.Length, v.Length - at);
                for (int i = 0; i < n; i += 3) { v[at + i] = p.Verts[i] + dx; v[at + i + 1] = p.Verts[i + 1] + dy; v[at + i + 2] = p.Verts[i + 2] + dz; }
                at += n;
                if (at >= v.Length) break;
            }
            _ready = new Mesh { Key = j.Key, Comp = j.Comp, Verts = v, Origin = new[] { o[0], o[1], o[2] }, Triangles = at / 9, Elements = elems, Links = links };
            Log.Write("Mesh", string.Format("«{0}»: mesh per la profondità pronta, {1:N0} elementi ({2} collegamenti), {3:N0} triangoli{4}; parti riusate {5}, rifatte {6}; lavoro {7:F1} s in {8} passi (passo più lungo {9:F0} ms, elemento più lento {10:F0} ms: {11}), tempo totale {12:F1} s (voci 278, 312)",
                j.ViewName, elems, links, at / 9, j.Capped ? " (limite raggiunto)" : "", j.Reused, j.Built, j.Work.Elapsed.TotalSeconds, j.Steps, j.MaxStepMs, j.MaxElemMs, j.MaxElem, j.Wall.Elapsed.TotalSeconds));
            Redraw.Request();
        }

        /// <summary>Voce 312: parti in memoria entro il limite, si tolgono le meno usate (mai quelle della mesh appena fatta).</summary>
        private static void Trim(List<string> keep)
        {
            long total = 0;
            foreach (Part p in _parts.Values) total += p.Triangles;
            while (total > CacheTriangles)
            {
                Part old = null;
                foreach (Part p in _parts.Values) if (!keep.Contains(p.Key) && (old == null || p.Used < old.Used)) old = p;
                if (old == null) break;
                _parts.Remove(old.Key); total -= old.Triangles;
            }
        }
    }
}
