using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PointDeviation
{
    /// <summary>
    /// Distanze dal modello salvate su disco, accanto alla nuvola (cartella «analisi»), una per ogni analisi.
    /// L'analisi si riconosce da un'impronta della geometria del modello, della distanza massima e della posizione della
    /// nuvola: rifacendo la stessa analisi (anche dopo aver chiuso e riaperto il progetto) le distanze già calcolate si
    /// rileggono invece di ricalcolarle. Ogni punto occupa 2 byte (distanza quantizzata sulla distanza massima).
    /// </summary>
    internal sealed class DistCache
    {
        private const int Magic = 0x31445044; // "PDD1"
        private const short NoSurface = short.MinValue;
        private const int MaxFiles = 4;        // analisi conservate per nuvola

        public readonly string Key;
        private readonly double _q;            // piedi per unità
        private readonly int[] _done;          // punti calcolati per nodo (prefisso)
        private readonly FileStream _dat, _idx;
        private readonly object _lock = new object();
        private long _points;

        private DistCache(string key, double q, int nodes, FileStream dat, FileStream idx, int[] done)
        {
            Key = key; _q = q; _dat = dat; _idx = idx; _done = done;
        }

        /// <summary>Punti con la distanza salvata (diagnostica).</summary>
        public long SavedPoints { get { long s = 0; lock (_lock) foreach (int d in _done) s += d; return s; } }

        public int Done(int node) { return Volatile.Read(ref _done[node]); }

        public static string MakeKey(CloudLayer L, AnalysisSnapshot an)
        {
            ulong h = an.Signature;
            h = Mix(h, (ulong)Math.Round(an.SearchFt * 1e5));
            double[] probe = { 0, 0, 0, 1000, 0, 0, 0, 1000, 0, 0, 0, 1000 };
            for (int k = 0; k < 4; k++)
            {
                double x, y, z;
                L.ToModel(probe[k * 3], probe[k * 3 + 1], probe[k * 3 + 2], out x, out y, out z);
                h = Mix(h, (ulong)(long)Math.Round(x * 1e4));
                h = Mix(h, (ulong)(long)Math.Round(y * 1e4));
                h = Mix(h, (ulong)(long)Math.Round(z * 1e4));
            }
            return h.ToString("x16");
        }

        internal static ulong Mix(ulong h, ulong v)
        {
            h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
            h *= 0xBF58476D1CE4E5B9UL;
            return h ^ (h >> 31);
        }

        public static DistCache Open(CloudLayer L, AnalysisSnapshot an)
        {
            if (an == null || an.Signature == 0 || an.SearchFt <= 0) return null;
            try
            {
                string key = MakeKey(L, an);
                string dir = Path.Combine(L.Folder, "analisi");
                Directory.CreateDirectory(dir);
                int n = L.Tiles.Length;
                long total = 0;
                foreach (TileEntry t in L.Tiles) total = Math.Max(total, t.FirstRecord + t.Count);

                string datPath = Path.Combine(dir, key + ".dist"), idxPath = Path.Combine(dir, key + ".idx");
                var done = new int[n];
                bool fresh = !File.Exists(datPath) || !File.Exists(idxPath);
                var idx = new FileStream(idxPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                var dat = new FileStream(datPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (!fresh && idx.Length == 8 + 4L * n && dat.Length == total * 2)
                {
                    var b = new byte[idx.Length];
                    idx.Position = 0; LasSource.ReadFully(idx, b, b.Length);
                    if (BitConverter.ToInt32(b, 0) == Magic && BitConverter.ToInt32(b, 4) == n)
                        for (int i = 0; i < n; i++) done[i] = Math.Max(0, Math.Min(L.Tiles[i].Count, BitConverter.ToInt32(b, 8 + 4 * i)));
                    else fresh = true;
                }
                else fresh = true;
                if (fresh)
                {
                    Array.Clear(done, 0, n);
                    idx.SetLength(0);
                    var head = new byte[8 + 4L * n];
                    BitConverter.GetBytes(Magic).CopyTo(head, 0);
                    BitConverter.GetBytes(n).CopyTo(head, 4);
                    idx.Write(head, 0, head.Length);
                    idx.Flush();
                    dat.SetLength(total * 2);
                }
                File.SetLastWriteTimeUtc(idxPath, DateTime.UtcNow);
                Prune(dir, key);
                var dc = new DistCache(key, an.SearchFt / 32000.0, n, dat, idx, done);
                long saved = dc.SavedPoints;
                Log.Write("Analisi", string.Format("«{0}»: distanze salvate {1} ({2:N0} punti su {3:N0}, {4:F0}%)",
                    L.Name, fresh ? "nuove" : "ritrovate", saved, total, total > 0 ? 100.0 * saved / total : 0));
                return dc;
            }
            catch (Exception ex)
            {
                Log.Error("Distanze salvate", ex);
                return null;
            }
        }

        /// <summary>Si tengono solo le analisi più recenti di questa nuvola.</summary>
        private static void Prune(string dir, string keep)
        {
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("*.idx"));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int k = 0; k < files.Count; k++)
                {
                    string stem = Path.GetFileNameWithoutExtension(files[k].Name);
                    if (k < MaxFiles || stem == keep) continue;
                    try { files[k].Delete(); File.Delete(Path.Combine(dir, stem + ".dist")); } catch { }
                }
            }
            catch { }
        }

        /// <summary>Legge le distanze salvate del nodo da «from» in poi; restituisce fin dove arrivano.</summary>
        public int Read(int node, long firstRecord, int from, int to, float[] dst)
        {
            int n = Math.Min(Done(node), to);
            if (n <= from) return from;
            var b = new byte[(n - from) * 2];
            lock (_lock)
            {
                _dat.Position = (firstRecord + from) * 2;
                if (LasSource.ReadFully(_dat, b, b.Length) < b.Length) return from;
            }
            for (int k = from; k < n; k++)
            {
                short v = BitConverter.ToInt16(b, (k - from) * 2);
                dst[k] = v == NoSurface ? float.NaN : (float)(v * _q);
            }
            return n;
        }

        /// <summary>Salva le distanze del nodo [from, to) (i punti del nodo sono un prefisso dei suoi record).</summary>
        public void Write(int node, long firstRecord, int from, int to, float[] src)
        {
            if (to <= from) return;
            var b = new byte[(to - from) * 2];
            for (int k = from; k < to; k++)
            {
                float d = src[k];
                short v = float.IsNaN(d) ? NoSurface : (short)Math.Max(-32000, Math.Min(32000, Math.Round(d / _q)));
                b[(k - from) * 2] = (byte)v; b[(k - from) * 2 + 1] = (byte)(v >> 8);
            }
            lock (_lock)
            {
                if (from > _done[node]) return; // ci sarebbe un buco: non succede (i punti si calcolano in ordine)
                try
                {
                    _dat.Position = (firstRecord + from) * 2;
                    _dat.Write(b, 0, b.Length);
                    _dat.Flush();
                    if (to > _done[node])
                    {
                        _done[node] = to;
                        _idx.Position = 8 + 4L * node;
                        _idx.Write(BitConverter.GetBytes(to), 0, 4);
                        _idx.Flush();
                    }
                    _points += to - from;
                }
                catch (Exception ex) { Log.Error("Salvataggio distanze", ex); }
            }
        }

        /// <summary>Conta le distanze salvate con una superficie entro la distanza massima.</summary>
        public void CountSurface(CloudLayer L, out long withSurface, out long total)
        {
            withSurface = total = 0;
            for (int i = 0; i < L.Tiles.Length; i++)
            {
                int n = Done(i);
                if (n <= 0) continue;
                var b = new byte[n * 2];
                lock (_lock)
                {
                    _dat.Position = L.Tiles[i].FirstRecord * 2;
                    n = LasSource.ReadFully(_dat, b, b.Length) / 2;
                }
                for (int k = 0; k < n; k++) if (BitConverter.ToInt16(b, k * 2) != NoSurface) withSurface++;
                total += n;
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                try { _dat.Dispose(); } catch { }
                try { _idx.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Esito del calcolo delle distanze di una nuvola.</summary>
    internal sealed class DistRunResult
    {
        public long Total, Reused, Computed;
        public bool Finished;
    }

    /// <summary>
    /// Calcolo delle distanze di tutta la nuvola, dai livelli grossolani ai fini, con salvataggio su disco.
    /// In primo piano (finestra di avanzamento dopo l'analisi) usa tutti i processori; in background solo quando il
    /// caricamento della vista è fermo, così dopo la prima volta la navigazione non aspetta più nessun calcolo.
    /// </summary>
    internal static class DistPrefetch
    {
        private static Thread _thread;
        private static readonly object _lock = new object();
        private static readonly Dictionary<CloudLayer, AnalysisSnapshot> _targets = new Dictionary<CloudLayer, AnalysisSnapshot>();
        private static readonly Dictionary<CloudLayer, AnalysisSnapshot> _finished = new Dictionary<CloudLayer, AnalysisSnapshot>();
        private static readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private static volatile int _foreground;          // > 0: c'è un calcolo in primo piano, il background aspetta
        private static readonly ParallelOptions _parBg = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) };
        private static readonly ParallelOptions _parFg = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };

        public static void Ensure(CloudLayer L, AnalysisSnapshot an)
        {
            lock (_lock)
            {
                AnalysisSnapshot cur;
                if (_finished.TryGetValue(L, out cur) && cur == an) return;
                if (_targets.TryGetValue(L, out cur) && cur == an) return;
                _targets[L] = an;
                if (_thread == null)
                {
                    _thread = new Thread(Loop) { IsBackground = true, Name = "PointDeviation distanze", Priority = ThreadPriority.Lowest };
                    _thread.Start();
                }
            }
            _wake.Set();
        }

        public static void Stop() { lock (_lock) { _targets.Clear(); _finished.Clear(); } }

        private static bool Current(CloudLayer L, AnalysisSnapshot an)
        {
            lock (_lock) { AnalysisSnapshot cur; return _targets.TryGetValue(L, out cur) && cur == an; }
        }

        private static void Loop()
        {
            while (true)
            {
                CloudLayer L = null; AnalysisSnapshot an = null;
                lock (_lock) foreach (var kv in _targets) { L = kv.Key; an = kv.Value; break; }
                if (L == null) { _wake.WaitOne(1000); continue; }
                bool fin = false;
                try
                {
                    DistRunResult r = Run(L, an, false, () => !Current(L, an), null);
                    fin = r != null && r.Finished;
                    if (r != null && r.Finished && r.Computed > 0)
                        Log.Write("Analisi", string.Format("«{0}»: distanze di tutta la nuvola pronte in background ({1:N0} punti calcolati)", L.Name, r.Computed));
                }
                catch (Exception ex) { Log.Error("Calcolo distanze in background", ex); fin = true; }
                lock (_lock)
                {
                    AnalysisSnapshot cur;
                    if (_targets.TryGetValue(L, out cur) && cur == an)
                    {
                        _targets.Remove(L);
                        if (fin) _finished[L] = an;
                    }
                }
            }
        }

        /// <summary>
        /// Calcola (o ritrova sul disco) le distanze di tutti i punti della nuvola. progress(fatti, totale) viene chiamata
        /// dopo ogni nodo; cancelled() interrompe tra un nodo e l'altro.
        /// </summary>
        public static DistRunResult Run(CloudLayer L, AnalysisSnapshot an, bool foreground, Func<bool> cancelled, Action<long, long> progress)
        {
            DistCache dc = L.GetDistCache(an);
            if (dc == null) return null;
            int n = L.Tiles.Length;
            var res = new DistRunResult();
            var order = new int[n];
            for (int i = 0; i < n; i++) { order[i] = i; res.Total += L.Tiles[i].Count; res.Reused += dc.Done(i); }
            Array.Sort(order, (a, b) => L.Tiles[a].Level != L.Tiles[b].Level ? L.Tiles[a].Level.CompareTo(L.Tiles[b].Level) : a.CompareTo(b));
            long doneAll = res.Reused;
            if (progress != null) progress(doneAll, res.Total);
            if (doneAll >= res.Total) { res.Finished = true; return res; }

            if (foreground) Interlocked.Increment(ref _foreground);
            try
            {
                int R = L.Info.RecordSize;
                double sc = L.Info.Scale / 0.3048;
                ParallelOptions par = foreground ? _parFg : _parBg;
                using (var fs = new FileStream(L.PointsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                {
                    foreach (int i in order)
                    {
                        if (cancelled != null && cancelled()) return res;
                        TileEntry t = L.Tiles[i];
                        int from = dc.Done(i);
                        if (from >= t.Count) continue;
                        // in background la vista (e un eventuale calcolo in primo piano) ha la precedenza
                        if (!foreground)
                            while ((_foreground > 0 || CloudLoader.Busy || CloudBaker.Busy) && !(cancelled != null && cancelled())) Thread.Sleep(40);

                        int cnt = t.Count - from;
                        var raw = new byte[cnt * R];
                        fs.Position = (t.FirstRecord + from) * R;
                        int got = LasSource.ReadFully(fs, raw, raw.Length) / R;
                        var dist = new float[t.Count];
                        TriangleBvh bvh = an.Bvh;
                        double range = an.SearchFt;
                        ThreadPriority prio = foreground ? ThreadPriority.Normal : ThreadPriority.BelowNormal;
                        Parallel.For(0, got, par, () => { Thread.CurrentThread.Priority = prio; return new int[64]; }, (k, state, stack) =>
                        {
                            int o = k * R;
                            double x, y, z, d;
                            L.ToModel(BitConverter.ToInt32(raw, o) * sc, BitConverter.ToInt32(raw, o + 4) * sc, BitConverter.ToInt32(raw, o + 8) * sc,
                                out x, out y, out z);
                            dist[from + k] = bvh.Nearest(x, y, z, range, stack, out d) ? (float)d : float.NaN;
                            return stack;
                        }, _ => { Thread.CurrentThread.Priority = ThreadPriority.Normal; });
                        dc.Write(i, t.FirstRecord, from, from + got, dist);
                        res.Computed += got;
                        doneAll += got;
                        if (progress != null) progress(doneAll, res.Total);
                    }
                }
                res.Finished = true;
                lock (_lock) { _targets.Remove(L); _finished[L] = an; }
                return res;
            }
            finally
            {
                if (foreground) Interlocked.Decrement(ref _foreground);
            }
        }

        /// <summary>Quante delle distanze salvate trovano una superficie entro la distanza massima.</summary>
        public static void CountSurface(CloudLayer L, AnalysisSnapshot an, out long withSurface, out long total)
        {
            withSurface = total = 0;
            DistCache dc = L.GetDistCache(an);
            if (dc != null) dc.CountSurface(L, out withSurface, out total);
        }
    }
}
