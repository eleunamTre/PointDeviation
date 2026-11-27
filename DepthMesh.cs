using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PointDeviation
{
    /// <summary>
    /// Voce 278: mesh del modello per la profondità in movimento, preparata da sola (senza analisi).
    /// Nelle immagini intermedie la libreria nativa disegna questa mesh nella profondità, con lo scarto misurato di
    /// Revit (voci 272-276), così la nuvola resta dietro al modello anche quando Revit non ha ridisegnato tutto.
    /// La geometria si legge solo nel thread di Revit: si lavora nell'evento Idling a passi di circa 40 ms, così Revit
    /// resta usabile. Elementi della vista attiva (nascosti esclusi) più quelli dei collegamenti visibili.
    /// Voce 312: la mesh è fatta di parti (il modello in uso per quella vista, ogni file collegato) tenute in memoria:
    /// si rifà solo la parte che è cambiata. Il riquadro di sezione non rifà la mesh.
    /// Voce 313: le parti dei collegamenti sono nelle coordinate del file collegato (la posizione si applica quando si
    /// mettono insieme: spostare un collegamento non rilegge niente) e si salvano su disco, così alla riapertura di
    /// Revit non si rileggono finché il file collegato non cambia. La lettura va avanti solo quando non si usano mouse
    /// e tastiera (un passo può durare centinaia di ms con elementi pesanti e in movimento si vedeva a scatti).
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

        /// <summary>Voce 312: una parte della mesh (modello in uso per una vista, o un file collegato nelle sue coordinate).</summary>
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
            public string Key; public bool Host; public string DocKey; public string DiskFile;
            public Document Doc; public List<ElementId> Ids;
        }

        private sealed class Job
        {
            public string Key, Comp;
            public string ViewName;
            public readonly List<string> PartKeys = new List<string>();
            public readonly List<Transform> PartXf = new List<Transform>();   // voce 313: posizione di ogni parte (null = progetto)
            public readonly List<Todo> Todos = new List<Todo>();
            public int Source, Index;
            public List<float> Verts = new List<float>(1 << 16);
            public readonly List<double> Scratch = new List<double>(1 << 12);
            public double Ox, Oy, Oz; public bool HasOrigin;
            public int PartTris, PartElements;
            public int Triangles, Steps, Reused, Built, FromDisk, Paused, Small;
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
        private const int InputPauseMs = 1000;         // voce 313: niente passi se mouse o tastiera usati da meno di 1 s
        private const int MaxPauseMs = 3000;           // ...ma almeno un passo ogni 3 s
        private const long DiskMaxBytes = 1L << 30;    // voce 313: al massimo 1 GB di parti su disco
        private const string DiskTag = "pdm1";

        private static Mesh _ready;
        private static Job _job;
        private static string _dirtyKey;   // documento con modifiche in attesa
        private static long _dirtyAt;
        private static bool _dirtyHost, _dirtyLinks;   // voce 312: quali parti sono vecchie
        private static long _useTick;
        private static int _lastStep;
        private static readonly Dictionary<string, Part> _parts = new Dictionary<string, Part>();
        private static readonly Options Opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };
        // voce 314: per la sola profondita' basta il dettaglio grossolano (geometria piu' semplice e meno triangoli)
        private static readonly Options OptCoarse = new Options { DetailLevel = ViewDetailLevel.Coarse, ComputeReferences = false, IncludeNonVisibleObjects = false };
        private const double SmallFt = 1.0;   // voce 314: elementi con tutte le misure sotto 30 cm saltati
        private static string _lastQ;
        /// <summary>Voce 314: qualita' della mesh (entra nelle chiavi delle parti e dei file su disco).</summary>
        private static string Quality { get { var a = AdvancedSettings.Current; return (a.MeshFineDetail ? "F" : "C") + (a.MeshSmallElements ? "s" : ""); } }

        [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private static string KeyOf(Document doc, View view) { return DeviationController.KeyOf(doc) + "|" + view.Id.Value; }
        private static bool Cache { get { return !AdvancedSettings.Current.NoMeshCache; } }

        /// <summary>Mesh pronta per questa vista, o null.</summary>
        public static Mesh For(View view)
        {
            Mesh m = _ready;
            if (m == null || view == null) return null;
            return m.Key == KeyOf(view.Document, view) ? m : null;
        }

        /// <summary>Voce 313: mouse o tastiera usati da poco, con Revit in primo piano.</summary>
        private static bool UserActive()
        {
            try
            {
                var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                if (!GetLastInputInfo(ref li)) return false;
                if ((uint)Environment.TickCount - li.dwTime >= InputPauseMs) return false;
                uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return pid == (uint)Process.GetCurrentProcess().Id;
            }
            catch { return false; }
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
                string q = Quality;
                if (_lastQ != null && q != _lastQ) { _job = null; _ready = null; }   // voce 314: qualita' cambiata in Avanzate
                _lastQ = q;
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
                // voce 313: mentre si usano mouse o tastiera si aspetta (al massimo 3 s tra un passo e l'altro)
                if (_job.Source < _job.Todos.Count && !AdvancedSettings.Current.MeshWhileActive && UserActive() && Environment.TickCount - _lastStep < MaxPauseMs) { _job.Paused++; return true; }
                _lastStep = Environment.TickCount;
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
                bool host = deleted != null && deleted.Count > 0, links = false, types = false;
                int seen = 0;
                bool box = AdvancedSettings.Current.MeshOnSectionBox;
                foreach (ICollection<ElementId> list in new[] { added, modified })
                {
                    if (list == null) continue;
                    foreach (ElementId id in list)
                    {
                        if (++seen > 500) { host = links = types = true; break; }
                        Element e = doc.GetElement(id);
                        if (e == null || e is View || e is PointCloudInstance || e is PointCloudType) continue;
                        if (e is RevitLinkType) { links = types = true; continue; }
                        if (e is RevitLinkInstance) { links = true; continue; }   // posizione nuova: si rimettono insieme le parti
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
                // collegamento ricaricato o scaricato (RevitLinkType): i suoi elementi possono essere cambiati
                _dirtyLinks |= types;
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

        // ------------------------------------------------------------------ parti su disco (voce 313)

        private static string DiskFolder { get { return Path.Combine(Log.Folder, "mesh"); } }

        /// <summary>File della parte di un collegamento: dipende dal file collegato (percorso, data, dimensione). Null se
        /// il collegamento non è un file su disco (per esempio nel cloud) o il salvataggio è spento.</summary>
        private static string DiskFileFor(Document ld)
        {
            try
            {
                if (AdvancedSettings.Current.NoMeshDisk) return null;
                string p = ld.PathName;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
                var fi = new FileInfo(p);
                string id = DiskTag + "|" + Quality + "|" + p.ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id));
                    return Path.Combine(DiskFolder, BitConverter.ToString(h).Replace("-", "").Substring(0, 24) + ".pdmesh");
                }
            }
            catch { return null; }
        }

        private static Part LoadDisk(string file, string key, string docKey)
        {
            try
            {
                if (file == null || !File.Exists(file)) return null;
                using (var br = new BinaryReader(File.OpenRead(file)))
                {
                    if (br.ReadInt32() != 0x504D3331) return null;   // "13MP"
                    int tris = br.ReadInt32(), elems = br.ReadInt32();
                    var o = new[] { br.ReadDouble(), br.ReadDouble(), br.ReadDouble() };
                    if (tris < 0 || tris > MaxTriangles) return null;
                    byte[] raw = br.ReadBytes(tris * 36);
                    if (raw.Length != tris * 36) return null;
                    var v = new float[tris * 9];
                    Buffer.BlockCopy(raw, 0, v, 0, raw.Length);
                    try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch { }
                    return new Part { Key = key, Host = false, DocKey = docKey, Verts = v, Origin = o, Triangles = tris, Elements = elems, Used = ++_useTick };
                }
            }
            catch { return null; }
        }

        private static void SaveDisk(string file, Part p)
        {
            if (file == null) return;
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(DiskFolder);
                    string tmp = file + ".tmp";
                    using (var bw = new BinaryWriter(File.Create(tmp)))
                    {
                        bw.Write(0x504D3331); bw.Write(p.Triangles); bw.Write(p.Elements);
                        bw.Write(p.Origin[0]); bw.Write(p.Origin[1]); bw.Write(p.Origin[2]);
                        var raw = new byte[(long)p.Triangles * 36];
                        Buffer.BlockCopy(p.Verts, 0, raw, 0, raw.Length);
                        bw.Write(raw);
                    }
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);
                    // al massimo 1 GB: si tolgono le parti usate meno di recente
                    var files = new List<FileInfo>(new DirectoryInfo(DiskFolder).GetFiles("*.pdmesh"));
                    long total = 0; foreach (FileInfo f in files) total += f.Length;
                    files.Sort((a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
                    foreach (FileInfo f in files) { if (total <= DiskMaxBytes) break; if (f.FullName == file) continue; total -= f.Length; try { f.Delete(); } catch { } }
                }
                catch (Exception ex) { Log.Write("Mesh", "parte della mesh non salvata su disco: " + ex.Message + " (voce 313)"); }
            });
        }

        // ------------------------------------------------------------------ preparazione

        private static Job Plan(Document doc, View view, string key, string docKey)
        {
            var j = new Job { Key = key, ViewName = view.Name };
            j.Work.Start();
            // modello in uso: elementi della vista (quelli nascosti nella vista restano fuori)
            string hk = "H|" + key + "|" + Quality;
            j.PartKeys.Add(hk); j.PartXf.Add(null);
            if (!_parts.ContainsKey(hk))
                j.Todos.Add(new Todo { Key = hk, Host = true, DocKey = docKey, Doc = doc, Ids = new List<ElementId>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds()) });
            // collegamenti visibili nella vista
            bool linksHidden = false;
            try { linksHidden = view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_RvtLinks)); } catch { }
            var comp = new List<string> { hk };
            if (!linksHidden)
                foreach (Element el in new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance)))
                {
                    var li = el as RevitLinkInstance;
                    Document ld = li != null ? li.GetLinkDocument() : null;
                    if (ld == null) continue;
                    Transform xf = li.GetTotalTransform();
                    string lk = "L|" + ld.PathName + "|" + ld.Title + "|" + Quality;
                    string ck = lk + "|" + XfKey(xf);
                    if (comp.Contains(ck)) continue;
                    comp.Add(ck);
                    j.PartKeys.Add(lk); j.PartXf.Add(xf);
                    if (_parts.ContainsKey(lk) || j.Todos.Exists(t => t.Key == lk)) continue;
                    string file = DiskFileFor(ld);
                    Part fromDisk = LoadDisk(file, lk, docKey);
                    if (fromDisk != null) { _parts[lk] = fromDisk; j.FromDisk++; continue; }
                    j.Todos.Add(new Todo { Key = lk, Host = false, DocKey = docKey, Doc = ld, DiskFile = file, Ids = new List<ElementId>(new FilteredElementCollector(ld).WhereElementIsNotElementType().ToElementIds()) });
                }
            j.Comp = string.Join("\n", comp);
            j.Reused = j.PartKeys.Count - j.Todos.Count - j.FromDisk;
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
                // voce 313: i collegamenti si leggono nelle loro coordinate (la posizione si applica alla fine)
                // voce 314: di serie lettura veloce (dettaglio grossolano, senza normali, elementi piccoli saltati);
                // con «dettaglio fine» la lettura di prima
                int n = AdvancedSettings.Current.MeshFineDetail ? ModelMeshExtractor.AddElementPublic(e, Opt, null, j.Scratch)
                    : AddFast(e, j.Scratch, AdvancedSettings.Current.MeshSmallElements ? 0 : SmallFt);
                if (n < 0) { j.Small++; continue; }
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
            if (!t.Host) SaveDisk(t.DiskFile, p);   // voce 313
            j.Verts = new List<float>(1 << 16); j.HasOrigin = false; j.PartTris = 0; j.PartElements = 0;
        }

        private static void Finish(Job j)
        {
            // si mettono insieme le parti (riusate, lette da disco e appena fatte), ognuna nella sua posizione, relative
            // all'origine della prima con triangoli
            var list = new List<KeyValuePair<Part, Transform>>();
            for (int i = 0; i < j.PartKeys.Count; i++) { Part p; if (_parts.TryGetValue(j.PartKeys[i], out p)) { p.Used = ++_useTick; list.Add(new KeyValuePair<Part, Transform>(p, j.PartXf[i])); } }
            int tris = 0, elems = 0, links = 0;
            double[] o = null;
            foreach (var kv in list)
            {
                Part p = kv.Key;
                tris += p.Triangles; elems += p.Elements; if (!p.Host && p.Triangles > 0) links++;
                if (o == null && p.Triangles > 0) o = Place(kv.Value, p.Origin);
            }
            Trim(j.PartKeys);
            if (tris <= 0 || o == null) { Log.Write("Mesh", string.Format("«{0}»: nessun triangolo, mesh non preparata", j.ViewName)); _ready = new Mesh { Key = j.Key, Comp = j.Comp, Verts = new float[0], Origin = new double[3] }; return; }
            if (tris > MaxTriangles) tris = MaxTriangles;
            var v = new float[(long)tris * 9];
            int at = 0;
            foreach (var kv in list)
            {
                Part p = kv.Key; Transform xf = kv.Value;
                if (p.Triangles <= 0) continue;
                double[] po = Place(xf, p.Origin);
                float dx = (float)(po[0] - o[0]), dy = (float)(po[1] - o[1]), dz = (float)(po[2] - o[2]);
                int n = Math.Min(p.Verts.Length, v.Length - at);
                if (xf == null)
                    for (int i = 0; i < n; i += 3) { v[at + i] = p.Verts[i] + dx; v[at + i + 1] = p.Verts[i + 1] + dy; v[at + i + 2] = p.Verts[i + 2] + dz; }
                else
                {
                    // punto nel progetto = posizione(origine della parte) + rotazione(vertice relativo)
                    XYZ bx = xf.BasisX, by = xf.BasisY, bz = xf.BasisZ;
                    float xx = (float)bx.X, xy = (float)bx.Y, xz = (float)bx.Z, yx = (float)by.X, yy = (float)by.Y, yz = (float)by.Z, zx = (float)bz.X, zy = (float)bz.Y, zz = (float)bz.Z;
                    for (int i = 0; i < n; i += 3)
                    {
                        float a = p.Verts[i], b = p.Verts[i + 1], c = p.Verts[i + 2];
                        v[at + i] = a * xx + b * yx + c * zx + dx;
                        v[at + i + 1] = a * xy + b * yy + c * zy + dy;
                        v[at + i + 2] = a * xz + b * yz + c * zz + dz;
                    }
                }
                at += n;
                if (at >= v.Length) break;
            }
            _ready = new Mesh { Key = j.Key, Comp = j.Comp, Verts = v, Origin = new[] { o[0], o[1], o[2] }, Triangles = at / 9, Elements = elems, Links = links };
            Log.Write("Mesh", string.Format("«{0}»: mesh per la profondità pronta, {1:N0} elementi ({2} collegamenti), {3:N0} triangoli{4}; parti riusate {5}, lette da disco {6}, rifatte {7}; lavoro {8:F1} s in {9} passi (passo più lungo {10:F0} ms, elemento più lento {11:F0} ms: {12}; attese per mouse e tastiera {13}), dettaglio {15}, elementi piccoli saltati {16}, tempo totale {14:F1} s (voci 278, 312-314)",
                j.ViewName, elems, links, at / 9, j.Capped ? " (limite raggiunto)" : "", j.Reused, j.FromDisk, j.Built, j.Work.Elapsed.TotalSeconds, j.Steps, j.MaxStepMs, j.MaxElemMs, j.MaxElem, j.Paused, j.Wall.Elapsed.TotalSeconds,
                AdvancedSettings.Current.MeshFineDetail ? "fine" : "grossolano", j.Small));
            Redraw.Request();
        }

        /// <summary>
        /// Voce 314: triangoli di un elemento per la sola profondita': dettaglio grossolano, triangolazione meno fitta
        /// delle superfici curve, nessun calcolo delle normali (non servono per la profondita'; prima per ogni triangolo
        /// di una superficie curva si proiettava il baricentro sulla faccia, il passo piu' lento). -1 = elemento piccolo saltato.
        /// </summary>
        private static int AddFast(Element e, List<double> v, double minSize)
        {
            if (e.Category == null || e.Category.CategoryType != CategoryType.Model) return 0;
            if (e is RevitLinkInstance || e is PointCloudInstance) return 0;
            if (minSize > 0)
            {
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch { }
                if (bb != null && bb.Max.X - bb.Min.X < minSize && bb.Max.Y - bb.Min.Y < minSize && bb.Max.Z - bb.Min.Z < minSize) return -1;
            }
            GeometryElement ge = null;
            try { ge = e.get_Geometry(OptCoarse); } catch { }
            return ge == null ? 0 : CollectFast(ge, v);
        }

        private static int CollectFast(GeometryElement ge, List<double> v)
        {
            int n = 0;
            foreach (GeometryObject go in ge)
            {
                var solid = go as Solid;
                if (solid != null)
                {
                    if (solid.Faces.Size == 0) continue;
                    foreach (Face face in solid.Faces)
                    {
                        Autodesk.Revit.DB.Mesh m = null;
                        try { m = face.Triangulate(0.0); } catch { }
                        if (m != null) n += AddMesh(m, v);
                    }
                    continue;
                }
                var mesh = go as Autodesk.Revit.DB.Mesh;
                if (mesh != null) { n += AddMesh(mesh, v); continue; }
                var inst = go as GeometryInstance;
                if (inst != null)
                {
                    GeometryElement sub = null;
                    try { sub = inst.GetInstanceGeometry(); } catch { }
                    if (sub != null) n += CollectFast(sub, v);
                    continue;
                }
                var nested = go as GeometryElement;
                if (nested != null) n += CollectFast(nested, v);
            }
            return n;
        }

        private static int AddMesh(Autodesk.Revit.DB.Mesh m, List<double> v)
        {
            int n = m.NumTriangles;
            for (int i = 0; i < n; i++)
            {
                MeshTriangle t = m.get_Triangle(i);
                for (int k = 0; k < 3; k++) { XYZ p = t.get_Vertex(k); v.Add(p.X); v.Add(p.Y); v.Add(p.Z); }
            }
            return n;
        }

        private static double[] Place(Transform xf, double[] p)
        {
            if (xf == null) return p;
            XYZ q = xf.OfPoint(new XYZ(p[0], p[1], p[2]));
            return new[] { q.X, q.Y, q.Z };
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
