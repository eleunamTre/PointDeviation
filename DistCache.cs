using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PointDeviation
{
    /// <summary>
    /// Distanze dal modello salvate su disco, accanto alla nuvola (cartella «analysis»), una per ogni analisi.
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
                string dir = Path.Combine(L.Folder, "analysis");
                // cartella delle prime versioni: si rinomina, così le distanze già calcolate non vanno perse
                string old = Path.Combine(L.Folder, "analisi");
                if (!Directory.Exists(dir) && Directory.Exists(old)) try { Directory.Move(old, dir); } catch { }
                Directory.CreateDirectory(dir);
                int n = L.Tiles.Length;
                long total = 0;
                foreach (TileEntry t in L.Tiles) total = Math.Max(total, t.FirstRecord + t.Count);

                string datPath = Path.Combine(dir, key + ".dist"), idxPath = Path.Combine(dir, key + ".idx");
                var done = new int[n];
                bool fresh = !File.Exists(datPath) || !File.Exists(idxPath);
                // data di modifica aggiornata prima di aprire (serve a tenere le analisi più recenti); aperti in
                // condivisione, perché una nuvola ricaricata può avere ancora aperta la copia precedente
                if (File.Exists(idxPath)) try { File.SetLastWriteTimeUtc(idxPath, DateTime.UtcNow); } catch { }
                var idx = new FileStream(idxPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                var dat = new FileStream(datPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
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

    /// <summary>
    /// Calcolo delle distanze di un gruppo di punti: i punti si ordinano lungo una curva che segue lo spazio (Morton),
    /// così ogni punto parte dal triangolo trovato per il precedente, che è vicino, e la ricerca nell'albero dei
    /// triangoli si riduce a pochi passi. I punti di un nodo sono in ordine a strati (sparsi), da qui l'ordinamento.
    /// </summary>
    internal static class DistCompute
    {
        private const int Chunk = 1024;

        /// <summary>xyz: coordinate di modello (3 per punto); i risultati vanno in dist[offset + k].</summary>
        public static void Run(TriangleBvh bvh, double range, double[] xyz, float[] dist, int offset, ParallelOptions par, ThreadPriority prio)
        {
            int n = xyz.Length / 3;
            if (n == 0) return;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                double x = xyz[k * 3], y = xyz[k * 3 + 1], z = xyz[k * 3 + 2];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
            double ext = Math.Max(1e-9, Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)));
            double f = 1023.0 / ext;
            var keys = new uint[n];
            var idx = new int[n];
            for (int k = 0; k < n; k++)
            {
                idx[k] = k;
                keys[k] = Part((uint)((xyz[k * 3] - minX) * f)) | (Part((uint)((xyz[k * 3 + 1] - minY) * f)) << 1) | (Part((uint)((xyz[k * 3 + 2] - minZ) * f)) << 2);
            }
            Array.Sort(keys, idx);
            int chunks = (n + Chunk - 1) / Chunk;
            Parallel.For(0, chunks, par, () => { Thread.CurrentThread.Priority = prio; return new int[64]; }, (c, state, stack) =>
            {
                int hint = -1;
                int end = Math.Min(n, (c + 1) * Chunk);
                for (int q = c * Chunk; q < end; q++)
                {
                    int k = idx[q];
                    double d;
                    dist[offset + k] = bvh.Nearest(xyz[k * 3], xyz[k * 3 + 1], xyz[k * 3 + 2], range, stack, ref hint, out d) ? (float)d : float.NaN;
                }
                return stack;
            }, _ => { Thread.CurrentThread.Priority = ThreadPriority.Normal; });
        }

        /// <summary>Come Run, ma su un solo thread e con buffer forniti (riusati da un nodo all'altro).</summary>
        public static void RunSequential(TriangleBvh bvh, double range, double[] xyz, int n, float[] dist, int offset, uint[] keys, int[] idx, int[] stack)
        {
            if (n == 0) return;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                double x = xyz[k * 3], y = xyz[k * 3 + 1], z = xyz[k * 3 + 2];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
            double ext = Math.Max(1e-9, Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)));
            double f = 1023.0 / ext;
            for (int k = 0; k < n; k++)
            {
                idx[k] = k;
                keys[k] = Part((uint)((xyz[k * 3] - minX) * f)) | (Part((uint)((xyz[k * 3 + 1] - minY) * f)) << 1) | (Part((uint)((xyz[k * 3 + 2] - minZ) * f)) << 2);
            }
            Array.Sort(keys, idx, 0, n);
            int hint = -1;
            for (int q = 0; q < n; q++)
            {
                int k = idx[q];
                double d;
                dist[offset + k] = bvh.Nearest(xyz[k * 3], xyz[k * 3 + 1], xyz[k * 3 + 2], range, stack, ref hint, out d) ? (float)d : float.NaN;
            }
        }

        // 10 bit per asse distribuiti ogni 3 bit
        private static uint Part(uint v)
        {
            v = Math.Min(v, 1023u);
            v = (v | (v << 16)) & 0x030000FF;
            v = (v | (v << 8)) & 0x0300F00F;
            v = (v | (v << 4)) & 0x030C30C3;
            v = (v | (v << 2)) & 0x09249249;
            return v;
        }
    }

    /// <summary>Esito del calcolo delle distanze di una nuvola.</summary>
    internal sealed class DistRunResult
    {
        public long Total, Reused, Computed;
        public double ReadMs, ComputeMs, WriteMs;   // diagnostica (media per thread)
        public int Gc2, Threads;
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
                // Un nodo per thread (non un nodo alla volta diviso tra i thread): ogni thread lavora in sequenza sui
                // punti del suo nodo, ordinati nello spazio, riusando i propri buffer. Prima ogni nodo allocava array
                // grandi (memoria «large object» di .NET) e aspettava tutti i thread alla fine: con 3918 nodi il tempo
                // se ne andava in raccolte della memoria di Revit e in attese.
                int workers = Math.Max(1, par.MaxDegreeOfParallelism);
                int next = -1;
                long ticksRead = 0, ticksCalc = 0, ticksWrite = 0, computed = 0;
                int gc0 = GC.CollectionCount(2);
                ThreadPriority prio = foreground ? ThreadPriority.Normal : ThreadPriority.BelowNormal;
                var sync = new object();
                Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
                {
                    ThreadPriority old = Thread.CurrentThread.Priority;
                    Thread.CurrentThread.Priority = prio;
                    try
                    {
                        byte[] raw = new byte[0]; double[] xyz = new double[0]; float[] dist = new float[0];
                        uint[] keys = new uint[0]; int[] idx = new int[0];
                        var stack = new int[64];
                        using (var fs = new FileStream(L.PointsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                        {
                            while (true)
                            {
                                if (cancelled != null && cancelled()) return;
                                int oi = Interlocked.Increment(ref next);
                                if (oi >= n) return;
                                int i = order[oi];
                                TileEntry t = L.Tiles[i];
                                int from = dc.Done(i);
                                if (from >= t.Count) continue;
                                // in background la vista (e un eventuale calcolo in primo piano) ha la precedenza
                                if (!foreground)
                                    while ((_foreground > 0 || CloudLoader.Busy || CloudBaker.Busy) && !(cancelled != null && cancelled())) Thread.Sleep(40);

                                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                                int cnt = t.Count - from;
                                if (raw.Length < cnt * R) raw = new byte[cnt * R];
                                fs.Position = (t.FirstRecord + from) * R;
                                int got = LasSource.ReadFully(fs, raw, cnt * R) / R;
                                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                                if (xyz.Length < got * 3) xyz = new double[got * 3];
                                if (dist.Length < t.Count) dist = new float[t.Count];
                                if (keys.Length < got) { keys = new uint[got]; idx = new int[got]; }
                                for (int k = 0; k < got; k++)
                                {
                                    int o = k * R;
                                    L.ToModel(BitConverter.ToInt32(raw, o) * sc, BitConverter.ToInt32(raw, o + 4) * sc, BitConverter.ToInt32(raw, o + 8) * sc,
                                        out xyz[k * 3], out xyz[k * 3 + 1], out xyz[k * 3 + 2]);
                                }
                                DistCompute.RunSequential(an.Bvh, an.SearchFt, xyz, got, dist, from, keys, idx, stack);
                                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                                dc.Write(i, t.FirstRecord, from, from + got, dist);
                                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                                Interlocked.Add(ref ticksRead, t1 - t0);
                                Interlocked.Add(ref ticksCalc, t2 - t1);
                                Interlocked.Add(ref ticksWrite, t3 - t2);
                                Interlocked.Add(ref computed, got);
                                long d = Interlocked.Add(ref doneAll, got);
                                if (progress != null) lock (sync) progress(d, res.Total);
                            }
                        }
                    }
                    finally { Thread.CurrentThread.Priority = old; }
                });
                double tf = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                // tempi sommati sui thread, divisi per il numero di thread: confrontabili con il tempo reale
                res.ReadMs = ticksRead * tf / workers; res.ComputeMs = ticksCalc * tf / workers; res.WriteMs = ticksWrite * tf / workers;
                res.Computed = computed;
                res.Gc2 = GC.CollectionCount(2) - gc0;
                res.Threads = workers;
                if (cancelled != null && cancelled()) return res;
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
