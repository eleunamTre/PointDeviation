using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PointDeviation
{
    /// <summary>
    /// Costruzione dell'octree della libreria (formato 2), fuori memoria.
    ///
    /// 1. La griglia di conteggio (Grid³ celle sul cubo della nuvola) dice quanti punti ha ogni zona: la si divide
    ///    in «pezzi» (nodi dell'octree) con al massimo ChunkMax punti, che stanno comodi in memoria.
    /// 2. Ogni pezzo si legge da solo: si mescolano i punti, si calcolano le normali, e si costruisce il suo
    ///    sotto-albero dall'alto: ogni nodo tiene il primo punto di ogni cella di una griglia NodeGrid³ sul suo
    ///    cubo, il resto scende ai figli. Le foglie tengono tutto (al massimo LeafMax punti).
    /// 3. Sopra i pezzi: ogni nodo prende un campione dalle radici dei figli (dal basso verso l'alto) e quei
    ///    punti salgono, tolti dai figli. Così ogni punto è in un solo nodo.
    /// </summary>
    internal static class OctreeBuilder
    {
        public const int NodeGrid = 128;       // celle per lato nel campionamento di un nodo
        private const int LeafMax = 30000;      // punti massimi in una foglia
        private const int ChunkMax = 2000000;   // punti massimi di un pezzo elaborato in memoria
        private const int MaxLevel = 24;
        private const int TmpRecord = 15;       // record dei file temporanei: int32 x, y, z + rgb
        public const int Record = CloudInfo.RecordSizeV2;

        private sealed class Node
        {
            public int Level, X, Y, Z;                  // posizione nell'octree (solo per i livelli della griglia)
            public double CubeX, CubeY, CubeZ, Size;    // metri, relativi al centro della nuvola
            public readonly Node[] Child = new Node[8];
            public int Chunk = -1;
            public long First;
            public int Count;
            public byte[] Mem;                          // punti tenuti in memoria (radici dei pezzi e livelli superiori)
            public double[] Box = { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };

            public void Grow(double x, double y, double z)
            {
                if (x < Box[0]) Box[0] = x; if (y < Box[1]) Box[1] = y; if (z < Box[2]) Box[2] = z;
                if (x > Box[3]) Box[3] = x; if (y > Box[4]) Box[4] = y; if (z > Box[5]) Box[5] = z;
            }

            public void Grow(double[] b)
            {
                if (b[0] > b[3]) return;
                Grow(b[0], b[1], b[2]);
                Grow(b[3], b[4], b[5]);
            }

            public Node MakeChild(int oct)
            {
                double h = Size * 0.5;
                return new Node
                {
                    Level = Level + 1,
                    X = X * 2 + (oct & 1), Y = Y * 2 + ((oct >> 1) & 1), Z = Z * 2 + ((oct >> 2) & 1),
                    CubeX = CubeX + ((oct & 1) != 0 ? h : 0), CubeY = CubeY + ((oct & 2) != 0 ? h : 0), CubeZ = CubeZ + ((oct & 4) != 0 ? h : 0),
                    Size = h
                };
            }
        }

        /// <summary>Risultato: nodi in ordine per livelli (figli consecutivi), punti totali scritti, profondità.</summary>
        public sealed class Result
        {
            public List<TileEntry> Nodes;
            public long Points;
            public int Depth;
        }

        /// <param name="counts">conteggi per cella, griglia grid³ sul cubo (cubeMin, side), indice (z*grid+y)*grid+x</param>
        /// <param name="cubeMin">angolo del cubo, metri relativi al centro della nuvola</param>
        /// <param name="distribute">seconda lettura della sorgente: deve chiamare write(chunk, x, y, z, r, g, b) per ogni punto (metri relativi al centro)</param>
        public static Result Build(int[] counts, int grid, double[] cubeMin, double side, double scale, string tmpDir, string pointsPath,
            Action<Func<double, double, double, int>, Action<int, double, double, double, byte, byte, byte>> distribute,
            Action<string, double> progress, CancellationToken ct)
        {
            // ---------------------------------------------------------- pezzi
            long[] sat = SummedVolume(counts, grid);
            int levels = 0;
            while ((1 << levels) < grid) levels++;
            var chunks = new List<Node>();
            var cellChunk = new int[counts.Length];
            for (int i = 0; i < cellChunk.Length; i++) cellChunk[i] = -1;
            Node root = SplitChunks(sat, grid, levels, 0, 0, 0, 0, cubeMin, side, chunks, cellChunk);
            if (root == null) throw new InvalidDataException(L.T("Nessun punto letto dalla sorgente."));

            // ---------------------------------------------------------- distribuzione nei file temporanei dei pezzi
            const int BufRecords = 4096;
            var buffers = new byte[chunks.Count][];
            var fill = new int[chunks.Count];
            Action<int> flush = c =>
            {
                if (fill[c] == 0) return;
                using (var fs = new FileStream(Path.Combine(tmpDir, c + ".bin"), FileMode.Append, FileAccess.Write))
                    fs.Write(buffers[c], 0, fill[c] * TmpRecord);
                fill[c] = 0;
            };
            double cellSize = side / grid;
            Func<double, double, double, int> chunkOf = (x, y, z) =>
            {
                int ix = Clamp((int)((x - cubeMin[0]) / cellSize), grid), iy = Clamp((int)((y - cubeMin[1]) / cellSize), grid), iz = Clamp((int)((z - cubeMin[2]) / cellSize), grid);
                return cellChunk[(iz * grid + iy) * grid + ix];
            };
            distribute(chunkOf, (c, x, y, z, r, g, b) =>
            {
                if (buffers[c] == null) buffers[c] = new byte[BufRecords * TmpRecord];
                if (fill[c] == BufRecords) flush(c);
                int o = fill[c] * TmpRecord;
                PutInt(buffers[c], o, (int)Math.Round(x / scale));
                PutInt(buffers[c], o + 4, (int)Math.Round(y / scale));
                PutInt(buffers[c], o + 8, (int)Math.Round(z / scale));
                buffers[c][o + 12] = r; buffers[c][o + 13] = g; buffers[c][o + 14] = b;
                fill[c]++;
            });
            for (int c = 0; c < chunks.Count; c++) { flush(c); buffers[c] = null; }

            // ---------------------------------------------------------- sotto-alberi dei pezzi
            var rnd = new Random(4242);
            var marks = new bool[NodeGrid * NodeGrid * NodeGrid];
            var touched = new List<int>(1 << 16);
            long written = 0;
            int depth = 0;
            using (var outFs = new FileStream(pointsPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                for (int c = 0; c < chunks.Count; c++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress(string.Format(L.T("Costruzione dell'octree: blocco {0} di {1}..."), c + 1, chunks.Count), 0.60 + 0.35 * c / chunks.Count);
                    string tf = Path.Combine(tmpDir, c + ".bin");
                    if (!File.Exists(tf)) continue;
                    byte[] data = File.ReadAllBytes(tf);
                    int n = data.Length / TmpRecord;
                    File.Delete(tf);
                    if (n == 0) continue;

                    // punti in ordine casuale
                    var perm = new int[n];
                    for (int i = 0; i < n; i++) perm[i] = i;
                    for (int i = n - 1; i > 0; i--) { int j = rnd.Next(i + 1); int t = perm[i]; perm[i] = perm[j]; perm[j] = t; }
                    var P = new ChunkPoints(n);
                    for (int i = 0; i < n; i++)
                    {
                        int o = perm[i] * TmpRecord;
                        P.X[i] = BitConverter.ToInt32(data, o); P.Y[i] = BitConverter.ToInt32(data, o + 4); P.Z[i] = BitConverter.ToInt32(data, o + 8);
                        P.Rgb[i * 3] = data[o + 12]; P.Rgb[i * 3 + 1] = data[o + 13]; P.Rgb[i * 3 + 2] = data[o + 14];
                    }
                    data = null;
                    P.Nrm = PointNormals.Compute(P.X, P.Y, P.Z, n, scale);

                    var idx = new int[n];
                    for (int i = 0; i < n; i++) idx[i] = i;
                    Node chunkRoot = chunks[c];
                    BuildSubtree(chunkRoot, idx, n, P, scale, marks, touched, outFs, ref written, true, ref depth);
                }

                // ---------------------------------------------------------- livelli sopra i pezzi
                progress(L.T("Costruzione dell'octree: livelli superiori..."), 0.96);
                Finish(root, scale, marks, touched, rnd);
                Prune(root);
                WriteMem(root, outFs, ref written, ref depth);
            }

            // ---------------------------------------------------------- indice, per livelli
            var order = new List<Node> { root };
            for (int i = 0; i < order.Count; i++)
                foreach (Node ch in order[i].Child) if (ch != null) order.Add(ch);
            var index = new Dictionary<Node, int>();
            for (int i = 0; i < order.Count; i++) index[order[i]] = i;
            var res = new List<TileEntry>(order.Count);
            foreach (Node nd in order)
            {
                byte mask = 0; int first = -1;
                for (int o = 0; o < 8; o++)
                {
                    if (nd.Child[o] == null) continue;
                    mask |= (byte)(1 << o);
                    if (first < 0) first = index[nd.Child[o]];
                }
                double[] b = nd.Box;
                if (b[0] > b[3]) b = new[] { nd.CubeX, nd.CubeY, nd.CubeZ, nd.CubeX + nd.Size, nd.CubeY + nd.Size, nd.CubeZ + nd.Size };
                res.Add(new TileEntry
                {
                    FirstRecord = nd.First, Count = nd.Count,
                    MinX = (float)b[0], MinY = (float)b[1], MinZ = (float)b[2], MaxX = (float)b[3], MaxY = (float)b[4], MaxZ = (float)b[5],
                    Level = (byte)Math.Min(255, nd.Level), ChildMask = mask, FirstChild = Math.Max(0, first),
                    CubeX = (float)nd.CubeX, CubeY = (float)nd.CubeY, CubeZ = (float)nd.CubeZ, CubeSize = (float)nd.Size
                });
            }
            return new Result { Nodes = res, Points = written, Depth = depth };
        }

        private sealed class ChunkPoints
        {
            public readonly int[] X, Y, Z;
            public readonly byte[] Rgb;
            public sbyte[] Nrm;
            public ChunkPoints(int n) { X = new int[n]; Y = new int[n]; Z = new int[n]; Rgb = new byte[n * 3]; }
        }

        // ------------------------------------------------------------------ pezzi dalla griglia di conteggio

        private static Node SplitChunks(long[] sat, int grid, int levels, int level, int x, int y, int z, double[] cubeMin, double side,
            List<Node> chunks, int[] cellChunk)
        {
            int s = grid >> level;
            long n = BoxCount(sat, grid, x * s, y * s, z * s, (x + 1) * s, (y + 1) * s, (z + 1) * s);
            if (n == 0) return null;
            double size = side / (1 << level);
            var node = new Node { Level = level, X = x, Y = y, Z = z, CubeX = cubeMin[0] + x * size, CubeY = cubeMin[1] + y * size, CubeZ = cubeMin[2] + z * size, Size = size };
            if (n <= ChunkMax || level >= levels)
            {
                node.Chunk = chunks.Count;
                chunks.Add(node);
                for (int k = z * s; k < (z + 1) * s; k++)
                    for (int j = y * s; j < (y + 1) * s; j++)
                        for (int i = x * s; i < (x + 1) * s; i++)
                            cellChunk[(k * grid + j) * grid + i] = node.Chunk;
                return node;
            }
            for (int o = 0; o < 8; o++)
                node.Child[o] = SplitChunks(sat, grid, levels, level + 1, x * 2 + (o & 1), y * 2 + ((o >> 1) & 1), z * 2 + ((o >> 2) & 1),
                    cubeMin, side, chunks, cellChunk);
            return node;
        }

        // ------------------------------------------------------------------ sotto-albero di un pezzo (dall'alto)

        private static void BuildSubtree(Node node, int[] idx, int count, ChunkPoints P, double scale, bool[] marks, List<int> touched,
            Stream outFs, ref long written, bool isChunkRoot, ref int depth)
        {
            int[] keep;
            int keepN;
            var childIdx = new List<int>[8];

            if (count <= LeafMax || node.Level >= MaxLevel)
            {
                keep = idx; keepN = count;
            }
            else
            {
                keep = new int[Math.Min(count, NodeGrid * NodeGrid * 8)];
                keepN = 0;
                double inv = NodeGrid / node.Size;
                double half = node.Size * 0.5;
                double mx = node.CubeX + half, my = node.CubeY + half, mz = node.CubeZ + half;
                for (int k = 0; k < count; k++)
                {
                    int p = idx[k];
                    double x = P.X[p] * scale, y = P.Y[p] * scale, z = P.Z[p] * scale;
                    int ix = Clamp((int)((x - node.CubeX) * inv), NodeGrid), iy = Clamp((int)((y - node.CubeY) * inv), NodeGrid), iz = Clamp((int)((z - node.CubeZ) * inv), NodeGrid);
                    int cell = (iz * NodeGrid + iy) * NodeGrid + ix;
                    if (!marks[cell])
                    {
                        marks[cell] = true;
                        touched.Add(cell);
                        if (keepN == keep.Length) Array.Resize(ref keep, keep.Length * 2);
                        keep[keepN++] = p;
                    }
                    else
                    {
                        int oct = (x >= mx ? 1 : 0) | (y >= my ? 2 : 0) | (z >= mz ? 4 : 0);
                        if (childIdx[oct] == null) childIdx[oct] = new List<int>();
                        childIdx[oct].Add(p);
                    }
                }
                foreach (int t in touched) marks[t] = false;
                touched.Clear();
            }

            // punti del nodo
            // ordine stratificato: i primi punti coprono tutto il cubo a bassa densità, i successivi aggiungono dettaglio
            {
                var xs = new double[keepN]; var ys = new double[keepN]; var zs = new double[keepN];
                for (int k = 0; k < keepN; k++) { int p = keep[k]; xs[k] = P.X[p] * scale; ys[k] = P.Y[p] * scale; zs[k] = P.Z[p] * scale; }
                int[] ord = Stratify(xs, ys, zs, keepN, node.CubeX, node.CubeY, node.CubeZ, node.Size);
                var sorted = new int[keepN];
                for (int k = 0; k < keepN; k++) sorted[k] = keep[ord[k]];
                keep = sorted;
            }
            byte[] rec = Serialize(P, keep, keepN, scale, node);
            if (isChunkRoot) node.Mem = rec;
            else
            {
                node.First = written;
                node.Count = keepN;
                outFs.Write(rec, 0, rec.Length);
                written += keepN;
                depth = Math.Max(depth, node.Level);
            }

            for (int o = 0; o < 8; o++)
            {
                if (childIdx[o] == null || childIdx[o].Count == 0) continue;
                Node ch = node.MakeChild(o);
                node.Child[o] = ch;
                int[] ci = childIdx[o].ToArray();
                childIdx[o] = null;
                BuildSubtree(ch, ci, ci.Length, P, scale, marks, touched, outFs, ref written, false, ref depth);
                node.Grow(ch.Box);
            }
        }

        private static byte[] Serialize(ChunkPoints P, int[] keep, int n, double scale, Node node)
        {
            var rec = new byte[n * Record];
            for (int k = 0; k < n; k++)
            {
                int p = keep[k], o = k * Record;
                PutInt(rec, o, P.X[p]); PutInt(rec, o + 4, P.Y[p]); PutInt(rec, o + 8, P.Z[p]);
                rec[o + 12] = P.Rgb[p * 3]; rec[o + 13] = P.Rgb[p * 3 + 1]; rec[o + 14] = P.Rgb[p * 3 + 2];
                rec[o + 15] = (byte)P.Nrm[p * 3]; rec[o + 16] = (byte)P.Nrm[p * 3 + 1]; rec[o + 17] = (byte)P.Nrm[p * 3 + 2];
                node.Grow(P.X[p] * scale, P.Y[p] * scale, P.Z[p] * scale);
            }
            return rec;
        }

        // ------------------------------------------------------------------ livelli superiori (dal basso)

        private static void Finish(Node node, double scale, bool[] marks, List<int> touched, Random rnd)
        {
            if (node.Chunk >= 0) return;
            var kids = new List<Node>();
            for (int o = 0; o < 8; o++)
            {
                Node ch = node.Child[o];
                if (ch == null) continue;
                Finish(ch, scale, marks, touched, rnd);
                kids.Add(ch);
            }

            // tutti i punti delle radici dei figli, in ordine casuale
            int total = 0;
            foreach (Node ch in kids) total += ch.Mem != null ? ch.Mem.Length / Record : 0;
            var pool = new long[total];
            int w = 0;
            for (int c = 0; c < kids.Count; c++)
            {
                int n = kids[c].Mem != null ? kids[c].Mem.Length / Record : 0;
                for (int i = 0; i < n; i++) pool[w++] = ((long)c << 32) | (uint)i;
            }
            for (int i = total - 1; i > 0; i--) { int j = rnd.Next(i + 1); long t = pool[i]; pool[i] = pool[j]; pool[j] = t; }

            var removed = new bool[kids.Count][];
            for (int c = 0; c < kids.Count; c++) removed[c] = new bool[kids[c].Mem != null ? kids[c].Mem.Length / Record : 0];
            var acc = new List<long>();
            double inv = NodeGrid / node.Size;
            foreach (long key in pool)
            {
                int c = (int)(key >> 32), i = (int)(key & 0xFFFFFFFF);
                byte[] m = kids[c].Mem;
                int o = i * Record;
                double x = BitConverter.ToInt32(m, o) * scale, y = BitConverter.ToInt32(m, o + 4) * scale, z = BitConverter.ToInt32(m, o + 8) * scale;
                int ix = Clamp((int)((x - node.CubeX) * inv), NodeGrid), iy = Clamp((int)((y - node.CubeY) * inv), NodeGrid), iz = Clamp((int)((z - node.CubeZ) * inv), NodeGrid);
                int cell = (iz * NodeGrid + iy) * NodeGrid + ix;
                if (marks[cell]) continue;
                marks[cell] = true;
                touched.Add(cell);
                removed[c][i] = true;
                acc.Add(key);
            }
            foreach (int t in touched) marks[t] = false;
            touched.Clear();

            // punti saliti nel nodo
            var mine = new byte[acc.Count * Record];
            for (int k = 0; k < acc.Count; k++)
            {
                int c = (int)(acc[k] >> 32), i = (int)(acc[k] & 0xFFFFFFFF);
                Buffer.BlockCopy(kids[c].Mem, i * Record, mine, k * Record, Record);
            }
            node.Mem = StratifyRecords(mine, node, scale);

            // figli senza i punti saliti
            for (int c = 0; c < kids.Count; c++)
            {
                byte[] m = kids[c].Mem;
                if (m == null) continue;
                int n = m.Length / Record, left = 0;
                for (int i = 0; i < n; i++) if (!removed[c][i]) left++;
                var rest = new byte[left * Record];
                int r = 0;
                for (int i = 0; i < n; i++)
                    if (!removed[c][i]) { Buffer.BlockCopy(m, i * Record, rest, r * Record, Record); r++; }
                // i figli hanno perso i punti saliti: si riordinano perché il loro inizio resti uniforme
                kids[c].Mem = StratifyRecords(rest, kids[c], scale);
                node.Grow(kids[c].Box);
            }
        }

        /// <summary>Toglie i nodi rimasti vuoti e senza figli.</summary>
        private static bool Prune(Node node)
        {
            bool any = false;
            for (int o = 0; o < 8; o++)
            {
                if (node.Child[o] == null) continue;
                if (Prune(node.Child[o])) any = true;
                else node.Child[o] = null;
            }
            int own = node.Mem != null ? node.Mem.Length / Record : node.Count;
            return any || own > 0 || node.Level == 0;
        }

        private static void WriteMem(Node node, Stream outFs, ref long written, ref int depth)
        {
            if (node.Mem != null)
            {
                node.First = written;
                node.Count = node.Mem.Length / Record;
                outFs.Write(node.Mem, 0, node.Mem.Length);
                written += node.Count;
                node.Mem = null;
            }
            depth = Math.Max(depth, node.Level);
            if (node.Chunk >= 0) return; // il sotto-albero del pezzo è già scritto
            foreach (Node ch in node.Child) if (ch != null) WriteMem(ch, outFs, ref written, ref depth);
        }

        // ------------------------------------------------------------------ ordine stratificato dei punti di un nodo

        /// <summary>
        /// Ordine «a strati»: prima un punto per cella di una griglia 2×2×2 sul cubo del nodo, poi uno per cella
        /// di 4×4×4 (tra quelle ancora vuote), poi 8×8×8, e così via. Leggendo i primi k punti si ottiene una versione
        /// a bassa densità ma uniforme di tutto il nodo, senza i grumi e i vuoti di un ordine casuale; i successivi
        /// aggiungono dettaglio in modo progressivo. Dentro ogni strato l'ordine resta casuale.
        /// </summary>
        internal static int[] Stratify(double[] xs, double[] ys, double[] zs, int n, double cx, double cy, double cz, double size)
        {
            var order = new int[n];
            var done = new bool[n];
            int m = 0;
            var occ = new HashSet<long>();
            for (int g = 2; g <= 4096 && m < n; g *= 2)
            {
                occ.Clear();
                double inv = g / Math.Max(size, 1e-9);
                for (int k = 0; k < m; k++) occ.Add(Cell(xs[order[k]], ys[order[k]], zs[order[k]], cx, cy, cz, inv, g));
                for (int p = 0; p < n; p++)
                {
                    if (done[p]) continue;
                    if (occ.Add(Cell(xs[p], ys[p], zs[p], cx, cy, cz, inv, g))) { done[p] = true; order[m++] = p; }
                }
            }
            for (int p = 0; p < n; p++) if (!done[p]) order[m++] = p;
            return order;
        }

        private static long Cell(double x, double y, double z, double cx, double cy, double cz, double inv, int g)
        {
            long ix = Clamp((int)((x - cx) * inv), g), iy = Clamp((int)((y - cy) * inv), g), iz = Clamp((int)((z - cz) * inv), g);
            return (ix * g + iy) * g + iz;
        }

        /// <summary>Riordina a strati i record (formato 2) di un nodo.</summary>
        private static byte[] StratifyRecords(byte[] mem, Node node, double scale)
        {
            int n = mem.Length / Record;
            if (n < 2) return mem;
            var xs = new double[n]; var ys = new double[n]; var zs = new double[n];
            for (int k = 0; k < n; k++)
            {
                int o = k * Record;
                xs[k] = BitConverter.ToInt32(mem, o) * scale; ys[k] = BitConverter.ToInt32(mem, o + 4) * scale; zs[k] = BitConverter.ToInt32(mem, o + 8) * scale;
            }
            int[] ord = Stratify(xs, ys, zs, n, node.CubeX, node.CubeY, node.CubeZ, node.Size);
            var res = new byte[mem.Length];
            for (int k = 0; k < n; k++) Buffer.BlockCopy(mem, ord[k] * Record, res, k * Record, Record);
            return res;
        }

        // ------------------------------------------------------------------ utilità

        private static int Clamp(int v, int n) { return v < 0 ? 0 : v >= n ? n - 1 : v; }

        private static void PutInt(byte[] b, int o, int v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static long[] SummedVolume(int[] c, int g)
        {
            int s1 = g + 1;
            var s = new long[s1 * s1 * s1];
            for (int z = 1; z <= g; z++)
                for (int y = 1; y <= g; y++)
                {
                    long row = 0;
                    for (int x = 1; x <= g; x++)
                    {
                        row += c[((z - 1) * g + (y - 1)) * g + (x - 1)];
                        s[(z * s1 + y) * s1 + x] = row + s[(z * s1 + (y - 1)) * s1 + x] + s[((z - 1) * s1 + y) * s1 + x] - s[((z - 1) * s1 + (y - 1)) * s1 + x];
                    }
                }
            return s;
        }

        private static long BoxCount(long[] s, int g, int x0, int y0, int z0, int x1, int y1, int z1)
        {
            int s1 = g + 1;
            Func<int, int, int, long> S = (x, y, z) => s[(z * s1 + y) * s1 + x];
            return S(x1, y1, z1) - S(x0, y1, z1) - S(x1, y0, z1) - S(x1, y1, z0)
                 + S(x0, y0, z1) + S(x0, y1, z0) + S(x1, y0, z0) - S(x0, y0, z0);
        }
    }

    /// <summary>
    /// Normali dei punti di un pezzo, in conversione: piano medio dei vicini entro circa una cella e mezza,
    /// con la cella scelta in modo che ogni intorno contenga qualche decina di punti.
    /// </summary>
    internal static class PointNormals
    {
        public static sbyte[] Compute(int[] X, int[] Y, int[] Z, int n, double scale)
        {
            var res = new sbyte[n * 3];
            if (n < 4) { for (int i = 0; i < n; i++) res[i * 3 + 2] = 127; return res; }

            long minX = long.MaxValue, minY = long.MaxValue, minZ = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue, maxZ = long.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (X[i] < minX) minX = X[i]; if (X[i] > maxX) maxX = X[i];
                if (Y[i] < minY) minY = Y[i]; if (Y[i] > maxY) maxY = Y[i];
                if (Z[i] < minZ) minZ = Z[i]; if (Z[i] > maxZ) maxZ = Z[i];
            }
            double ext = Math.Max(1, Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)));

            // dimensione della cella (in unità intere): circa 8 punti per cella occupata
            double cell = Math.Max(1, ext / Math.Sqrt(n) * 2);
            for (int it = 0; it < 4; it++)
            {
                var occ = new HashSet<long>();
                for (int i = 0; i < n; i++) occ.Add(Key(X[i], Y[i], Z[i], minX, minY, minZ, cell));
                double avg = (double)n / Math.Max(1, occ.Count);   // punti per cella occupata
                // su una superficie i punti per cella crescono col quadrato del lato
                double f = Math.Max(0.5, Math.Min(2.0, Math.Sqrt(8.0 / Math.Max(0.5, avg))));
                if (Math.Abs(f - 1) < 0.1) break;
                cell *= f;
            }

            // punti ordinati per cella
            var keys = new long[n];
            var order = new int[n];
            for (int i = 0; i < n; i++) { keys[i] = Key(X[i], Y[i], Z[i], minX, minY, minZ, cell); order[i] = i; }
            Array.Sort(keys, order);
            var start = new Dictionary<long, int>();
            for (int i = 0; i < n; i++) if (i == 0 || keys[i] != keys[i - 1]) start[keys[i]] = i;

            double r2 = (1.5 * cell) * (1.5 * cell);
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
            Parallel.For(0, n, po, () => new int[256], (i, state, buf) =>
            {
                long cx = (long)Math.Floor((X[i] - minX) / cell), cy = (long)Math.Floor((Y[i] - minY) / cell), cz = (long)Math.Floor((Z[i] - minZ) / cell);
                int cnt = 0;
                for (int a = -1; a <= 1; a++)
                    for (int b = -1; b <= 1; b++)
                        for (int c = -1; c <= 1; c++)
                        {
                            long k = Pack(cx + a, cy + b, cz + c);
                            int s;
                            if (!start.TryGetValue(k, out s)) continue;
                            for (int q = s; q < n && keys[q] == k; q++)
                            {
                                int p = order[q];
                                double dx = X[p] - X[i], dy = Y[p] - Y[i], dz = Z[p] - Z[i];
                                if (dx * dx + dy * dy + dz * dz > r2) continue;
                                if (cnt < buf.Length) buf[cnt] = p;
                                cnt++;
                            }
                        }
                int m = Math.Min(cnt, buf.Length);
                if (m < 3) { res[i * 3 + 2] = 127; return buf; }
                // al massimo 64 vicini, presi a passo regolare
                int stride = Math.Max(1, m / 64);
                double sx = 0, sy = 0, sz = 0; int used = 0;
                for (int q = 0; q < m; q += stride) { int p = buf[q]; sx += X[p]; sy += Y[p]; sz += Z[p]; used++; }
                double mx = sx / used, my = sy / used, mz = sz / used;
                double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
                for (int q = 0; q < m; q += stride)
                {
                    int p = buf[q];
                    double dx = X[p] - mx, dy = Y[p] - my, dz = Z[p] - mz;
                    xx += dx * dx; xy += dx * dy; xz += dx * dz; yy += dy * dy; yz += dy * dz; zz += dz * dz;
                }
                double nx, ny, nz;
                Normals.SmallestEigenvector(xx, xy, xz, yy, yz, zz, out nx, out ny, out nz);
                if (nz < 0 || (nz == 0 && ny < 0)) { nx = -nx; ny = -ny; nz = -nz; }
                res[i * 3] = (sbyte)Math.Round(nx * 127); res[i * 3 + 1] = (sbyte)Math.Round(ny * 127); res[i * 3 + 2] = (sbyte)Math.Round(nz * 127);
                return buf;
            }, _ => { });
            return res;
        }

        private static long Key(int x, int y, int z, long minX, long minY, long minZ, double cell)
        {
            return Pack((long)Math.Floor((x - minX) / cell), (long)Math.Floor((y - minY) / cell), (long)Math.Floor((z - minZ) / cell));
        }

        private static long Pack(long x, long y, long z)
        {
            return ((x & 0x1FFFFF) << 42) | ((y & 0x1FFFFF) << 21) | (z & 0x1FFFFF);
        }
    }
}
