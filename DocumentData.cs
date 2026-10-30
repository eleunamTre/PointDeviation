using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;

namespace PointDeviation
{
    /// <summary>
    /// Tutto ciò che appartiene a un singolo progetto aperto: impostazioni, nuvole inserite (una CloudLayer
    /// per istanza nativa del nostro motore), stile di disegno attuale, campione dei punti per le statistiche.
    /// Ce n'è una istanza per documento, in DeviationController.
    /// </summary>
    internal sealed class DocumentData : IDisposable
    {
        /// <summary>Chiave del progetto (percorso normalizzato, oppure "unsaved:" + titolo se mai salvato). Cambia solo con il salvataggio con nome.</summary>
        public string Key;

        public readonly DeviationSettings Settings = new DeviationSettings();

        public LasData Cloud;          // campione della nuvola (null se non caricata)
        public float[] Distances;      // null se non calcolate
        public double ComputedMm;      // scala massima usata come raggio di ricerca nell'ultimo calcolo
        public string LoadInfo = "";

        /// <summary>Elementi su cui fare l'analisi quando il confronto è "sulla selezione" (memorizzati al momento della scelta).</summary>
        public List<ElementId> SelectionIds;

        /// <summary>Nuvole del progetto (istanze native del nostro motore), lette dal documento.</summary>
        public readonly List<CloudLayer> Layers = new List<CloudLayer>();

        /// <summary>Come disegnare le nuvole adesso (letto dal server di disegno).</summary>
        public volatile Style Style;

        /// <summary>Cresce a ogni calcolo delle distanze (unica anche tra sessioni diverse).</summary>
        public int AnalysisVersion;

        /// <summary>Il caricamento automatico all'apertura è già stato tentato.</summary>
        public bool AutoLoadTried;

        public DocumentData(string key)
        {
            Key = key;
        }

        /// <summary>Nel progetto c'è almeno una nuvola del plugin.</summary>
        public bool HasCloud { get { return Layers.Count > 0; } }
        public bool HasDistances { get { return Distances != null; } }
        public bool HasRgb { get { return Layers.Exists(l => l.Info.HasRgb); } }

        // ------------------------------------------------------------------ dati

        public void SetCloud(LasData d)
        {
            Cloud = d;
            Distances = null;
            ComputedMm = 0;
        }

        public void SetDistances(float[] dist)
        {
            Distances = dist;
        }

        public void ApplySettings(DeviationSettings s)
        {
        }

        public void Clear()
        {
            Cloud = null;
            Distances = null;
            ComputedMm = 0;
            LoadInfo = "";
        }

        /// <summary>Ingombro di tutte le nuvole (Revit, piedi), allargato di pad: limita gli elementi da estrarre.</summary>
        public Outline GetOutline(double pad)
        {
            double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
            foreach (CloudLayer l in Layers)
            {
                XYZ a, b;
                l.ModelBounds(out a, out b);
                mn[0] = Math.Min(mn[0], a.X); mn[1] = Math.Min(mn[1], a.Y); mn[2] = Math.Min(mn[2], a.Z);
                mx[0] = Math.Max(mx[0], b.X); mx[1] = Math.Max(mx[1], b.Y); mx[2] = Math.Max(mx[2], b.Z);
            }
            if (Layers.Count == 0) { mn[0] = mn[1] = mn[2] = 0; mx[0] = mx[1] = mx[2] = 0; }
            return new Outline(new XYZ(mn[0] - pad, mn[1] - pad, mn[2] - pad), new XYZ(mx[0] + pad, mx[1] + pad, mx[2] + pad));
        }

        // ------------------------------------------------------------------ nuvole del progetto e stile

        /// <summary>
        /// Allinea le nuvole alle istanze native presenti nel documento: aggiunge le nuove, toglie quelle cancellate,
        /// aggiorna la posizione di quelle spostate in Revit. Sola lettura sul documento: si può chiamare negli eventi.
        /// Restituisce true se è cambiato qualcosa.
        /// </summary>
        public bool SyncLayers(Document doc)
        {
            bool changed = false;
            var seen = new HashSet<ElementId>();
            foreach (PointCloudInstance inst in NativeClouds.FindInstances(doc))
            {
                seen.Add(inst.Id);
                CloudLayer l = Layers.Find(x => x.InstanceId == inst.Id);
                string folder = CloudView.CloudFolderOf(NativeClouds.PathOf(doc.GetElement(inst.GetTypeId()) as PointCloudType) ?? "");
                if (l == null)
                {
                    if (!CloudInfo.IsCloudFolder(folder)) { Log.Write("SyncLayers", "cartella della libreria non trovata per l'istanza " + inst.Id.Value + ": '" + folder + "'"); continue; }
                    try { l = new CloudLayer(inst.Id, folder); }
                    catch (Exception ex) { Log.Error("CloudLayer " + folder, ex); continue; }
                    Layers.Add(l);
                    changed = true;
                    Log.Write("SyncLayers", string.Format("nuvola «{0}»: {1:N0} punti, {2} blocchi, formato {3}", l.Name, l.Info.PointCount, l.Tiles.Length, l.Info.FormatVersion));
                }
                double[] p = NativeClouds.PlacementFromInstance(NativeClouds.FromTransform(inst.GetTransform()), l.Info);
                if (!SameTransform(p, l.T))
                {
                    l.T = p;
                    l.GeoVersion++;
                    changed = true;
                    XYZ bmin, bmax;
                    l.ModelBounds(out bmin, out bmax);
                    Log.Write("SyncLayers", string.Format("nuvola «{0}» posizionata: ingombro nel progetto (m) {1:F1}, {2:F1}, {3:F1}  ->  {4:F1}, {5:F1}, {6:F1}",
                        l.Name, bmin.X * 0.3048, bmin.Y * 0.3048, bmin.Z * 0.3048, bmax.X * 0.3048, bmax.Y * 0.3048, bmax.Z * 0.3048));
                }
            }
            for (int i = Layers.Count - 1; i >= 0; i--)
            {
                if (seen.Contains(Layers[i].InstanceId)) continue;
                Layers[i].Dispose();
                Layers.RemoveAt(i);
                changed = true;
            }
            return changed;
        }

        private static bool SameTransform(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 1e-9) return false;
            return true;
        }

        /// <summary>Ricostruisce lo stile di disegno dalle impostazioni (colori, profilo, recinti, aspetto, qualità).</summary>
        public void RebuildStyle(DeviationSettings s)
        {
            double zmin = double.MaxValue, zmax = double.MinValue;
            foreach (CloudLayer l in Layers)
            {
                XYZ a, b;
                l.ModelBounds(out a, out b);
                zmin = Math.Min(zmin, a.Z); zmax = Math.Max(zmax, b.Z);
            }
            if (Layers.Count == 0) { zmin = 0; zmax = 1; }
            AnalysisSnapshot an = AnalysisRegistry.Get(Key);
            // Stile identico a quello attuale: si tiene (stessa versione). Un nuovo stile obbliga a ripreparare tutti
            // i blocchi di tutte le nuvole: prima succedeva a ogni inserimento, spostamento, o anche solo nascondendo
            // una nuvola (milioni di punti ripreparati con la vista ferma).
            string key = LookKey(s, an, zmin, zmax);
            if (Style != null && key == _styleKey) { Redraw.Request(); return; }
            _styleKey = key;
            Style = Style.From(s, an, zmin, zmax);
            Redraw.Request();
        }

        private string _styleKey;

        private static string LookKey(DeviationSettings s, AnalysisSnapshot an, double zmin, double zmax)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append((int)s.Display).Append('|').Append(s.InvertSign).Append('|').Append((int)s.Missing).Append('|').Append(s.Visible)
              .Append('|').Append(s.Brightness).Append('|').Append(s.Contrast).Append('|').Append(s.Transparency).Append('|').Append(s.XrayContrast)
              .Append('|').Append(s.PointScale).Append('|').Append((int)s.Quality).Append('|').Append(s.ClipInvert)
              .Append('|').Append(zmin.ToString("R")).Append('|').Append(zmax.ToString("R"))
              .Append('|').Append(an != null ? an.Version : 0).Append('|').Append(CloudDebug.Levels);
            if (s.CloudColors != null) foreach (var kv in s.CloudColors.OrderBy(k => k.Key)) sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
            sb.Append('|').Append(Json(s.Profile)).Append('|').Append(Json(s.Clips));
            return sb.ToString();
        }

        private static string Json(object o)
        {
            if (o == null) return "";
            try
            {
                using (var ms = new MemoryStream())
                {
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(o.GetType()).WriteObject(ms, o);
                    return System.Text.Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch { return Guid.NewGuid().ToString(); } // non serializzabile: si considera sempre diverso
        }

        /// <summary>Campione uniforme di tutte le nuvole, in coordinate Revit, per statistiche e limiti dell'analisi.</summary>
        public LasData BuildSample(int maxPoints)
        {
            if (Layers.Count == 0) return null;
            var parts = new List<LasData>();
            long total = 0;
            foreach (CloudLayer l in Layers) total += Math.Max(1, l.Info.PointCount);
            foreach (CloudLayer l in Layers)
            {
                int share = (int)Math.Max(1000, (double)maxPoints * Math.Max(1, l.Info.PointCount) / total);
                try { parts.Add(LibraryReader.Read(l.Folder, l.T, share, null)); } catch { }
            }
            if (parts.Count == 0) return null;
            if (parts.Count == 1) return parts[0];

            // unione: coordinate riportate all'origine della prima
            LasData first = parts[0];
            int n = 0; foreach (LasData p in parts) n += p.Count;
            var res = new LasData { Xyz = new float[n * 3], Count = n, TotalInFile = parts.Sum(p => p.TotalInFile), Stride = first.Stride };
            Array.Copy(first.Origin, res.Origin, 3);
            Array.Copy(first.CenterFileM, res.CenterFileM, 3);
            for (int k = 0; k < 3; k++) { res.Min[k] = double.MaxValue; res.Max[k] = double.MinValue; }
            int w = 0;
            foreach (LasData p in parts)
            {
                double dx = p.Origin[0] - res.Origin[0], dy = p.Origin[1] - res.Origin[1], dz = p.Origin[2] - res.Origin[2];
                for (int i = 0; i < p.Count; i++, w++)
                {
                    res.Xyz[w * 3] = (float)(p.Xyz[i * 3] + dx);
                    res.Xyz[w * 3 + 1] = (float)(p.Xyz[i * 3 + 1] + dy);
                    res.Xyz[w * 3 + 2] = (float)(p.Xyz[i * 3 + 2] + dz);
                }
                for (int k = 0; k < 3; k++) { res.Min[k] = Math.Min(res.Min[k], p.Min[k]); res.Max[k] = Math.Max(res.Max[k], p.Max[k]); }
            }
            return res;
        }

        // ------------------------------------------------------------------ calcolo distanze

        public float[] ComputeDistances(TriangleBvh bvh, double searchFt)
        {
            LasData cloud = Cloud;
            float[] xyz = cloud.Xyz;
            int n = cloud.Count;
            double ox = cloud.Origin[0], oy = cloud.Origin[1], oz = cloud.Origin[2];
            var res = new float[n];

            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
            Parallel.ForEach(Partitioner.Create(0, n, 8192), po, range =>
            {
                var stack = new int[64];
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    double d;
                    if (bvh.Nearest(xyz[i * 3] + ox, xyz[i * 3 + 1] + oy, xyz[i * 3 + 2] + oz, searchFt, stack, out d))
                        res[i] = (float)d;
                    else
                        res[i] = float.NaN;
                }
            });
            return res;
        }

        /// <summary>Statistiche delle discrepanze secondo il profilo colore: classi, media, RMS, nuvola senza modello.</summary>
        public string ComputeStats(ColorProfile profile, bool invert)
        {
            float[] d = Distances;
            if (d == null || !HasCloud || profile == null) return "";

            const double Mm = 304.8;
            int count = Cloud.Count;

            if (profile.IsSelection)
            {
                double thr = profile.ThresholdMm;
                long far = 0, near = 0, inside = 0;
                for (int i = 0; i < count; i++)
                {
                    float v = d[i];
                    if (float.IsNaN(v)) { far++; continue; }
                    double x = v * (invert ? -1.0 : 1.0) * Mm;
                    if (Math.Abs(x) > thr) far++;
                    else if (x < 0) inside++;
                    else near++;
                }
                if (profile.Kind == ProfileKind.Completezza)
                    return string.Format(L.T("Completezza ({0:0.#} mm): {1:N0} punti su {2:N0} ({3:P1}) sono lontani dal modello:\n") +
                        L.T("parti rilevate ma non modellate (o arredi, persone, rumore)."), thr, far, (long)count, (double)far / count);
                return string.Format(L.T("Interferenze ({0:0.#} mm): {1:N0} punti entro la soglia ({2:P1}), di cui {3:N0} dentro il modello ({4:P1}).\n") +
                    L.T("Oltre la soglia: {5:N0} punti. I punti molto all'interno di elementi spessi (oltre la soglia dalla superficie) non vengono rilevati."),
                    thr, near + inside, (double)(near + inside) / count, inside, (double)inside / count, far);
            }

            List<ColorClass> classes = profile.StatClasses();
            var perClass = new long[classes.Count];
            long valid = 0;
            double sum = 0, sum2 = 0, mn = double.MaxValue, mx = double.MinValue;
            double sgn = invert ? -1.0 : 1.0;
            byte r, g, b;

            for (int i = 0; i < count; i++)
            {
                float v = d[i];
                if (float.IsNaN(v)) continue;
                double x = v * sgn * Mm;
                if (!profile.TryColor(x, out r, out g, out b)) continue;
                if (profile.Absolute) x = Math.Abs(x);
                valid++;
                sum += x; sum2 += x * x;
                if (x < mn) mn = x; if (x > mx) mx = x;

                double cv = profile.Absolute ? Math.Abs(x) : x;
                for (int k = 0; k < classes.Count; k++)
                {
                    double a = Math.Min(classes[k].FromMm, classes[k].ToMm), z = Math.Max(classes[k].FromMm, classes[k].ToMm);
                    if (cv >= a - 1e-9 && cv <= z + 1e-9) { perClass[k]++; break; }
                }
            }

            long missing = count - valid;
            var sb = new System.Text.StringBuilder();
            sb.AppendFormat(L.T("Profilo «{0}»: {1:N0} punti su {2:N0} con modello entro l'intervallo ({3:P1})\n"),
                profile.Name, valid, (long)count, (double)valid / count);
            if (valid > 0)
            {
                sb.AppendFormat(L.T("Media {0:+0.0;-0.0;0.0} mm   RMS {1:F1} mm   min {2:+0.0;-0.0;0.0}   max {3:+0.0;-0.0;0.0} mm\n"),
                    sum / valid, Math.Sqrt(sum2 / valid), mn, mx);
                for (int k = 0; k < classes.Count; k++)
                    sb.AppendFormat("  {0}: {1:P1}\n", classes[k].Label ?? string.Format("{0:0.#}…{1:0.#} mm", classes[k].FromMm, classes[k].ToMm),
                        (double)perClass[k] / valid);
            }
            sb.AppendFormat(profile.HideMissing ? L.T("Nuvola senza modello (nascosta): {0:N0} punti ({1:P1})") : L.T("Nuvola senza modello: {0:N0} punti ({1:P1})"),
                missing, (double)missing / count);
            if (!profile.Absolute) sb.Append(L.T("\nPositivo = punti davanti alla superficie modellata, negativo = dietro."));
            return sb.ToString();
        }

        public void Dispose()
        {
            foreach (CloudLayer l in Layers) l.Dispose();
            Layers.Clear();
            Cloud = null;
            Distances = null;
        }
    }
}
