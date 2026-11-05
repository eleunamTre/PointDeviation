using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PointDeviation
{
    /// <summary>Una zona rilevata ma non modellata: gruppo connesso di celle con punti lontani dal modello.</summary>
    internal sealed class MissingZone
    {
        public int Number;
        public long Points;
        public int Cells;
        public double AreaM2;                         // superficie stimata (celle proiettate sul piano principale)
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ; // ingombro in coordinate di Revit (piedi)
        public double NormalX, NormalY, NormalZ;      // direzione di minore estensione (normale del piano principale)
        public ZoneShape Shape;
        public string Level;                          // livello sotto la zona (riempito dalla finestra)
        public double AboveLevelM;                    // quota del fondo della zona sopra quel livello
        /// <summary>
        /// Coordinate condivise (piedi): ingombro (minX, minY, minZ, maxX, maxY, maxZ, dagli 8 spigoli dell'ingombro
        /// interno: con il nord ruotato è un po' più grande) e centro (cx, cy, cz). null se non disponibili.
        /// </summary>
        public double[] Shared;

        public double SizeXM { get { return (MaxX - MinX) * 0.3048; } }
        public double SizeYM { get { return (MaxY - MinY) * 0.3048; } }
        public double SizeZM { get { return (MaxZ - MinZ) * 0.3048; } }
    }

    internal enum ZoneShape { Verticale, Orizzontale, Inclinata, Volume }

    /// <summary>
    /// Zona evidenziata nel disegno, in qualunque modalità di visualizzazione: i punti lontani dal modello dentro
    /// l'ingombro della zona si colorano di viola, gli altri restano nello stile corrente. Fa parte dello stile
    /// (cambia la sua impronta), quindi accendendola o cambiando zona i blocchi si ripreparano.
    /// </summary>
    internal static class ZoneHighlight
    {
        private static double[] _box;   // minX, minY, minZ, maxX, maxY, maxZ (piedi, coordinate di Revit)
        private static double _thrFt;

        public static double[] Box { get { return _box; } }
        public static double ThresholdFt { get { return _thrFt; } }

        /// <summary>Impronta per lo stile (vuota se spenta).</summary>
        public static string Key
        {
            get
            {
                double[] b = _box;
                return b == null ? "" : string.Join(",", b.Select(v => v.ToString("R"))) + "@" + _thrFt.ToString("R");
            }
        }

        /// <summary>Accende l'evidenziazione su una zona (null = spenta). Restituisce true se è cambiata.</summary>
        public static bool Set(MissingZone z, double thresholdMm)
        {
            const double m = 0.05 / 0.3048; // margine: i punti al bordo delle celle
            double[] nb = z == null ? null : new[] { z.MinX - m, z.MinY - m, z.MinZ - m, z.MaxX + m, z.MaxY + m, z.MaxZ + m };
            double nt = thresholdMm / 304.8;
            string before = Key;
            _box = nb; _thrFt = nt;
            return Key != before;
        }
    }

    internal sealed class MissingZoneOptions
    {
        public double ThresholdMm = 150;
        public double CellM = 0.20;
        public int MinPointsPerCell = 10;
        public double MinAreaM2 = 0.5;
        public ClipSet Clip;                           // null = tutta la nuvola
    }

    internal sealed class MissingZoneResult
    {
        public List<MissingZone> Zones = new List<MissingZone>();
        public long TotalPoints, FarPoints, NoisePoints, SmallZonePoints;
        public int SmallZones;
        public double Seconds;
        public bool Cancelled;
        public string Error;
    }

    /// <summary>
    /// Completezza: raggruppa i punti più lontani della soglia dal modello in zone. I punti lontani si contano in celle
    /// cubiche (lato «cella»); le celle con pochi punti sono rumore e si scartano; le celle rimaste che si toccano (anche
    /// solo per uno spigolo) formano una zona. Per ogni zona: punti, ingombro, superficie stimata (le celle proiettate
    /// sul piano principale, trovato con le componenti principali) e orientamento.
    /// Le distanze sono quelle salvate su disco dall'analisi: se mancano ancora (calcolo in background in corso) si
    /// completano prima.
    /// </summary>
    internal static class MissingZones
    {
        private const int Bits = 21;
        private const long Off = 1L << (Bits - 1);
        private const long Mask = (1L << Bits) - 1;

        private static long Pack(long ix, long iy, long iz)
        {
            return ((ix + Off) << (2 * Bits)) | ((iy + Off) << Bits) | (iz + Off);
        }

        private static void Unpack(long k, out long ix, out long iy, out long iz)
        {
            ix = ((k >> (2 * Bits)) & Mask) - Off;
            iy = ((k >> Bits) & Mask) - Off;
            iz = (k & Mask) - Off;
        }

        /// <summary>progress(testo, frazione 0..1).</summary>
        public static MissingZoneResult Find(List<CloudLayer> layers, AnalysisSnapshot an, MissingZoneOptions opt,
            Func<bool> cancelled, Action<string, double> progress)
        {
            var res = new MissingZoneResult();
            var sw = Stopwatch.StartNew();
            try
            {
                // 1. distanze di tutti i punti (di solito già salvate: si rileggono)
                long all = 0;
                foreach (CloudLayer l in layers) foreach (TileEntry t in l.Tiles) all += t.Count;
                long before = 0;
                foreach (CloudLayer l in layers)
                {
                    long b0 = before;
                    DistRunResult r = DistPrefetch.Run(l, an, true, cancelled, (done, tot) =>
                    {
                        if (progress != null) progress(L.T("Distanze dal modello..."), 0.4 * (b0 + done) / Math.Max(1, all));
                    });
                    if (cancelled()) { res.Cancelled = true; return res; }
                    if (r == null) { res.Error = L.T("Distanze non disponibili: impossibile scrivere nella cartella della libreria."); return res; }
                    foreach (TileEntry t in l.Tiles) before += t.Count;
                }
                double tDist = sw.Elapsed.TotalSeconds;

                // 2. punti lontani contati per cella (ogni thread il proprio dizionario, poi si uniscono)
                double cellFt = opt.CellM / 0.3048;
                double thrFt = opt.ThresholdMm / 304.8;
                double ox = double.MaxValue, oy = double.MaxValue, oz = double.MaxValue;
                foreach (CloudLayer l in layers)
                {
                    Autodesk.Revit.DB.XYZ a, b;
                    l.ModelBounds(out a, out b);
                    ox = Math.Min(ox, a.X); oy = Math.Min(oy, a.Y); oz = Math.Min(oz, a.Z);
                }
                var cells = new Dictionary<long, int>();
                long far = 0, total = 0, doneTiles = 0, allTiles = 0;
                foreach (CloudLayer l in layers) allTiles += l.Tiles.Length;
                var merge = new object();
                int workers = Math.Max(1, Environment.ProcessorCount - 1);

                foreach (CloudLayer l in layers)
                {
                    DistCache dc = l.GetDistCache(an);
                    if (dc == null) continue;
                    int R = l.Info.RecordSize;
                    double sc = l.Info.Scale / 0.3048;
                    int next = -1;
                    CloudLayer layer = l;
                    Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
                    {
                        var local = new Dictionary<long, int>();
                        long lfar = 0, ltot = 0;
                        float[] dist = new float[0];
                        byte[] raw = new byte[0];
                        using (var fs = new FileStream(layer.PointsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                        {
                            while (!cancelled())
                            {
                                int i = Interlocked.Increment(ref next);
                                if (i >= layer.Tiles.Length) break;
                                TileEntry t = layer.Tiles[i];
                                int n = t.Count;
                                if (n > 0)
                                {
                                    if (dist.Length < n) dist = new float[n];
                                    int got = dc.Read(i, t.FirstRecord, 0, n, dist);
                                    ltot += got;
                                    // prima si guardano le distanze: le posizioni si leggono solo se c'è qualche punto lontano
                                    int firstFar = -1;
                                    for (int k = 0; k < got; k++)
                                    {
                                        float v = dist[k];
                                        if (float.IsNaN(v) || Math.Abs(v) > thrFt) { firstFar = k; break; }
                                    }
                                    if (firstFar >= 0)
                                    {
                                        int cnt = got - firstFar;
                                        if (raw.Length < cnt * R) raw = new byte[cnt * R];
                                        fs.Position = (t.FirstRecord + firstFar) * R;
                                        int rd = LasSource.ReadFully(fs, raw, cnt * R) / R;
                                        for (int k = 0; k < rd; k++)
                                        {
                                            float v = dist[firstFar + k];
                                            if (!(float.IsNaN(v) || Math.Abs(v) > thrFt)) continue;
                                            int o = k * R;
                                            double x, y, z;
                                            layer.ToModel(BitConverter.ToInt32(raw, o) * sc, BitConverter.ToInt32(raw, o + 4) * sc, BitConverter.ToInt32(raw, o + 8) * sc,
                                                out x, out y, out z);
                                            if (opt.Clip != null && !opt.Clip.Contains(x, y, z)) continue;
                                            long key = Pack((long)Math.Floor((x - ox) / cellFt), (long)Math.Floor((y - oy) / cellFt), (long)Math.Floor((z - oz) / cellFt));
                                            int c;
                                            local.TryGetValue(key, out c);
                                            local[key] = c + 1;
                                            lfar++;
                                        }
                                    }
                                }
                                long d = Interlocked.Increment(ref doneTiles);
                                if (progress != null && (d & 31) == 0) progress(L.T("Raggruppamento dei punti lontani..."), 0.4 + 0.5 * d / Math.Max(1, allTiles));
                            }
                        }
                        lock (merge)
                        {
                            far += lfar; total += ltot;
                            foreach (var kv in local)
                            {
                                int c;
                                cells.TryGetValue(kv.Key, out c);
                                cells[kv.Key] = c + kv.Value;
                            }
                        }
                    });
                    if (cancelled()) { res.Cancelled = true; return res; }
                }
                res.TotalPoints = total;
                res.FarPoints = far;
                double tCells = sw.Elapsed.TotalSeconds;
                if (progress != null) progress(L.T("Zone connesse..."), 0.92);

                // 3. zone: si parte dalle celle con abbastanza punti (il rumore sparso non ne ha) e si allarga alle celle
                //    che le toccano (26 vicine), accettando anche celle più povere: una superficie obliqua taglia molte
                //    celle solo di striscio, con pochi punti, che altrimenti sparirebbero come rumore
                int weak = Math.Min(opt.MinPointsPerCell, Math.Max(2, opt.MinPointsPerCell / 5));
                int allCells = cells.Count;
                var seen = new HashSet<long>();
                var queue = new Queue<long>();
                var members = new List<long>();
                long inZones = 0;
                foreach (var seed in cells)
                {
                    if (seed.Value < opt.MinPointsPerCell || !seen.Add(seed.Key)) continue;
                    if (cancelled()) { res.Cancelled = true; return res; }
                    members.Clear();
                    queue.Enqueue(seed.Key);
                    while (queue.Count > 0)
                    {
                        long k = queue.Dequeue();
                        members.Add(k);
                        long ix, iy, iz;
                        Unpack(k, out ix, out iy, out iz);
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dz = -1; dz <= 1; dz++)
                                {
                                    if (dx == 0 && dy == 0 && dz == 0) continue;
                                    long nk = Pack(ix + dx, iy + dy, iz + dz);
                                    int c;
                                    if (cells.TryGetValue(nk, out c) && c >= weak && seen.Add(nk)) queue.Enqueue(nk);
                                }
                    }
                    MissingZone z = Describe(members, cells, cellFt, ox, oy, oz);
                    inZones += z.Points;
                    if (z.AreaM2 < opt.MinAreaM2) { res.SmallZones++; res.SmallZonePoints += z.Points; continue; }
                    res.Zones.Add(z);
                }
                res.NoisePoints = far - inZones;
                res.Zones.Sort((a, b) => b.AreaM2.CompareTo(a.AreaM2));
                for (int i = 0; i < res.Zones.Count; i++) res.Zones[i].Number = i + 1;

                res.Seconds = sw.Elapsed.TotalSeconds;
                Log.Write("Completezza", string.Format(
                    "soglia {0:0} mm, cella {1:0.00} m, min {2} punti/cella, min {3:0.##} m²{4}: {5:N0} punti lontani su {6:N0} ({7:P1}); " +
                    "celle {8:N0} (rumore {9:N0} punti, minimo nelle celle aggiunte {16}); zone {10:N0} (+{11:N0} piccole, {12:N0} punti); tempi: distanze {13:F1} s, celle {14:F1} s, zone {15:F1} s",
                    opt.ThresholdMm, opt.CellM, opt.MinPointsPerCell, opt.MinAreaM2, opt.Clip != null ? ", dentro i recinti" : "",
                    far, total, total > 0 ? (double)far / total : 0, allCells, res.NoisePoints, res.Zones.Count, res.SmallZones, res.SmallZonePoints,
                    tDist, tCells - tDist, res.Seconds - tCells, weak));
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                Log.Error("Zone mancanti", ex);
            }
            return res;
        }

        /// <summary>Punti, ingombro, piano principale (componenti principali dei centri delle celle) e superficie stimata.</summary>
        private static MissingZone Describe(List<long> members, Dictionary<long, int> counts, double cellFt, double ox, double oy, double oz)
        {
            var z = new MissingZone { Cells = members.Count };
            long mnx = long.MaxValue, mny = long.MaxValue, mnz = long.MaxValue, mxx = long.MinValue, mxy = long.MinValue, mxz = long.MinValue;
            double sw = 0, sx = 0, sy = 0, sz = 0;
            foreach (long k in members)
            {
                long ix, iy, iz;
                Unpack(k, out ix, out iy, out iz);
                int c = counts[k];
                z.Points += c;
                if (ix < mnx) mnx = ix; if (iy < mny) mny = iy; if (iz < mnz) mnz = iz;
                if (ix > mxx) mxx = ix; if (iy > mxy) mxy = iy; if (iz > mxz) mxz = iz;
                sw += c; sx += c * (ix + 0.5); sy += c * (iy + 0.5); sz += c * (iz + 0.5);
            }
            z.MinX = ox + mnx * cellFt; z.MaxX = ox + (mxx + 1) * cellFt;
            z.MinY = oy + mny * cellFt; z.MaxY = oy + (mxy + 1) * cellFt;
            z.MinZ = oz + mnz * cellFt; z.MaxZ = oz + (mxz + 1) * cellFt;

            // covarianza dei centri delle celle (in celle), pesata con i punti
            double cx = sx / sw, cy = sy / sw, cz = sz / sw;
            var m = new double[3, 3];
            foreach (long k in members)
            {
                long ix, iy, iz;
                Unpack(k, out ix, out iy, out iz);
                double w = counts[k];
                double dx = ix + 0.5 - cx, dy = iy + 0.5 - cy, dz = iz + 0.5 - cz;
                m[0, 0] += w * dx * dx; m[0, 1] += w * dx * dy; m[0, 2] += w * dx * dz;
                m[1, 1] += w * dy * dy; m[1, 2] += w * dy * dz; m[2, 2] += w * dz * dz;
            }
            m[1, 0] = m[0, 1]; m[2, 0] = m[0, 2]; m[2, 1] = m[1, 2];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) m[i, j] /= sw;
            double[] ev; double[,] vec;
            Eigen3(m, out ev, out vec); // ev in ordine decrescente, vettori in colonna

            z.NormalX = vec[0, 2]; z.NormalY = vec[1, 2]; z.NormalZ = vec[2, 2];

            // superficie: impronta delle celle sul piano dei due assi principali, su una griglia di mezza cella (ogni
            // cella copre 2×2 quadretti attorno alla sua proiezione). Con una griglia di una cella intera le pareti
            // oblique risultavano più piccole (le proiezioni di celle vicine finivano nello stesso quadretto).
            // Verificato su superfici sintetiche: pareti, solai e falde con errore tra 0 e +17% (i bordi).
            var proj = new HashSet<long>();
            foreach (long k in members)
            {
                long ix, iy, iz;
                Unpack(k, out ix, out iy, out iz);
                double px = ix + 0.5 - cx, py = iy + 0.5 - cy, pz = iz + 0.5 - cz;
                double pu = px * vec[0, 0] + py * vec[1, 0] + pz * vec[2, 0];
                double pv = px * vec[0, 1] + py * vec[1, 1] + pz * vec[2, 1];
                long bu = (long)Math.Floor((pu - 0.5) * 2 + 0.5), bv = (long)Math.Floor((pv - 0.5) * 2 + 0.5);
                for (int a = 0; a < 2; a++)
                    for (int b = 0; b < 2; b++)
                        proj.Add(((bu + a + Off) << 32) | ((bv + b + Off) & 0xFFFFFFFFL));
            }
            double cellM = cellFt * 0.3048;
            z.AreaM2 = proj.Count * cellM * cellM / 4;

            // forma: spessa (arredi, oggetti, angoli tra pareti) o piana con il suo orientamento
            // (una superficie piana a cavallo tra due strati di celle ha varianza fino a 0,25 celle² lungo la normale)
            bool thick = members.Count >= 8 && ev[1] > 1e-9 && ev[2] / ev[1] > 0.2 && ev[2] > 0.4;
            double nz = Math.Abs(z.NormalZ);
            // orizzontale entro ~14°, verticale entro ~15°
            z.Shape = thick ? ZoneShape.Volume : nz > 0.97 ? ZoneShape.Orizzontale : nz < 0.26 ? ZoneShape.Verticale : ZoneShape.Inclinata;
            return z;
        }

        /// <summary>Autovalori (decrescenti) e autovettori (colonne) di una matrice simmetrica 3×3, metodo di Jacobi.</summary>
        internal static void Eigen3(double[,] a0, out double[] ev, out double[,] v)
        {
            var a = (double[,])a0.Clone();
            v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (off < 1e-12) break;
                for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-15) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
            }
            ev = new[] { a[0, 0], a[1, 1], a[2, 2] };
            // ordine decrescente
            int[] o = { 0, 1, 2 };
            double[] e = ev;
            Array.Sort(o, (x, y) => e[y].CompareTo(e[x]));
            var ev2 = new double[3];
            var v2 = new double[3, 3];
            for (int j = 0; j < 3; j++)
            {
                ev2[j] = ev[o[j]];
                for (int k = 0; k < 3; k++) v2[k, j] = v[k, o[j]];
            }
            ev = ev2; v = v2;
        }
    }
}
