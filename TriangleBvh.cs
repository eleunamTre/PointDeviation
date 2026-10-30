using System;
using System.Collections.Generic;

namespace PointDeviation
{
    /// <summary>
    /// BVH di triangoli per interrogazioni "triangolo più vicino" con distanza con segno.
    /// Dopo la costruzione è di sola lettura, quindi le query sono thread-safe.
    /// Il segno è positivo dal lato verso cui punta la normale del triangolo (ordine dei vertici, regola della mano destra).
    /// </summary>
    internal sealed class TriangleBvh
    {
        private const int LeafSize = 4;

        private struct Node
        {
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            public int Left, Right;   // -1 se foglia
            public int Start, Count;
        }

        private double[] _v;               // 9 double per triangolo (dopo la costruzione: nell'ordine delle foglie)
        private double[] _n;               // 3 double per triangolo (normale unitaria)
        private readonly double[] _cen;    // baricentri
        private readonly double[] _keys;
        private readonly int[] _idx;
        private readonly List<Node> _list = new List<Node>();
        private Node[] _nodes;

        public int TriangleCount { get { return _idx.Length; } }

        /// <summary>
        /// I triangoli grandi (pareti, solai, coperture: pochi triangoli lunghi metri) hanno riquadri che contengono
        /// moltissimi punti, e la ricerca del più vicino deve aprirli quasi tutti: è la parte lenta dell'analisi.
        /// Si dividono a metà sul lato più lungo finché ogni lato non supera una lunghezza scelta in modo che il totale
        /// resti entro un limite di memoria. La superficie (e quindi ogni distanza e il suo segno) non cambia.
        /// </summary>
        private static void Subdivide(ref double[] v, ref int count, out double edge)
        {
            edge = 0;
            if (count <= 0) return;
            // lato massimo: il più piccolo (da MinEdgeFt in su) per cui i pezzi, contati esattamente, restano nel limite.
            // (Una stima approssimata restava sotto il vero numero: si arrivava al tetto di sicurezza e i triangoli
            // rimasti restavano interi, con riquadri enormi che rallentavano tutta la ricerca.)
            double L = MinEdgeFt;
            for (int iter = 0; iter < 60; iter++)
            {
                double L2 = L * L;
                long total = 0;
                for (int t = 0; t < count && total <= SplitBudget; t++)
                {
                    int o = t * 9;
                    total += Pieces(v[o], v[o + 1], v[o + 2], v[o + 3], v[o + 4], v[o + 5], v[o + 6], v[o + 7], v[o + 8], L2, 0);
                }
                if (total <= SplitBudget) break;
                L *= 1.2;
            }
            edge = L;
            var outV = new List<double>(Math.Min(SplitBudget, count * 4) * 9);
            var stack = new Stack<double[]>();
            for (int t = 0; t < count; t++)
            {
                double e, area;
                EdgeArea(v, t, out e, out area);
                if (e <= L) { for (int j = 0; j < 9; j++) outV.Add(v[t * 9 + j]); continue; }
                var tri = new double[9];
                Array.Copy(v, t * 9, tri, 0, 9);
                stack.Push(tri);
                while (stack.Count > 0)
                {
                    double[] q = stack.Pop();
                    // lato più lungo: ab (0-1), bc (1-2), ca (2-0)
                    double lab = D2(q, 0, 1), lbc = D2(q, 1, 2), lca = D2(q, 2, 0);
                    double m = Math.Max(lab, Math.Max(lbc, lca));
                    if (m <= L * L || outV.Count / 9 + stack.Count > SplitBudget * 3) { outV.AddRange(q); continue; }
                    int i0, i1, i2; // i0-i1 è il lato da dividere, i2 il vertice opposto
                    if (m == lab) { i0 = 0; i1 = 1; i2 = 2; } else if (m == lbc) { i0 = 1; i1 = 2; i2 = 0; } else { i0 = 2; i1 = 0; i2 = 1; }
                    double mx = (q[i0 * 3] + q[i1 * 3]) * 0.5, my = (q[i0 * 3 + 1] + q[i1 * 3 + 1]) * 0.5, mz = (q[i0 * 3 + 2] + q[i1 * 3 + 2]) * 0.5;
                    // due metà con lo stesso verso dei vertici (stessa normale, quindi stesso segno)
                    var a1 = new double[9]; var a2 = new double[9];
                    SetV(a1, i0, q, i0); SetP(a1, i1, mx, my, mz); SetV(a1, i2, q, i2);
                    SetP(a2, i0, mx, my, mz); SetV(a2, i1, q, i1); SetV(a2, i2, q, i2);
                    stack.Push(a1); stack.Push(a2);
                }
            }
            v = outV.ToArray();
            count = v.Length / 9;
        }

        /// <summary>Numero di pezzi della bisezione sul lato più lungo finché ogni lato è al massimo √L2.</summary>
        private static long Pieces(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, double L2, int depth)
        {
            double ab = (bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az);
            double bc = (cx - bx) * (cx - bx) + (cy - by) * (cy - by) + (cz - bz) * (cz - bz);
            double ca = (ax - cx) * (ax - cx) + (ay - cy) * (ay - cy) + (az - cz) * (az - cz);
            double m = Math.Max(ab, Math.Max(bc, ca));
            if (m <= L2 || depth > 48) return 1;
            if (m == ab)
            {
                double mx = (ax + bx) * 0.5, my = (ay + by) * 0.5, mz = (az + bz) * 0.5;
                return Pieces(ax, ay, az, mx, my, mz, cx, cy, cz, L2, depth + 1) + Pieces(mx, my, mz, bx, by, bz, cx, cy, cz, L2, depth + 1);
            }
            if (m == bc)
            {
                double mx = (bx + cx) * 0.5, my = (by + cy) * 0.5, mz = (bz + cz) * 0.5;
                return Pieces(ax, ay, az, bx, by, bz, mx, my, mz, L2, depth + 1) + Pieces(ax, ay, az, mx, my, mz, cx, cy, cz, L2, depth + 1);
            }
            {
                double mx = (cx + ax) * 0.5, my = (cy + ay) * 0.5, mz = (cz + az) * 0.5;
                return Pieces(ax, ay, az, bx, by, bz, mx, my, mz, L2, depth + 1) + Pieces(mx, my, mz, bx, by, bz, cx, cy, cz, L2, depth + 1);
            }
        }

        private static void EdgeArea(double[] v, int t, out double maxEdge, out double area)
        {
            int o = t * 9;
            double abx = v[o + 3] - v[o], aby = v[o + 4] - v[o + 1], abz = v[o + 5] - v[o + 2];
            double acx = v[o + 6] - v[o], acy = v[o + 7] - v[o + 1], acz = v[o + 8] - v[o + 2];
            double bcx = v[o + 6] - v[o + 3], bcy = v[o + 7] - v[o + 4], bcz = v[o + 8] - v[o + 5];
            double e2 = Math.Max(abx * abx + aby * aby + abz * abz, Math.Max(acx * acx + acy * acy + acz * acz, bcx * bcx + bcy * bcy + bcz * bcz));
            maxEdge = Math.Sqrt(e2);
            double nx = aby * acz - abz * acy, ny = abz * acx - abx * acz, nz = abx * acy - aby * acx;
            area = 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
        }

        private static double D2(double[] q, int a, int b)
        {
            double dx = q[a * 3] - q[b * 3], dy = q[a * 3 + 1] - q[b * 3 + 1], dz = q[a * 3 + 2] - q[b * 3 + 2];
            return dx * dx + dy * dy + dz * dz;
        }

        private static void SetV(double[] dst, int slot, double[] src, int from)
        {
            dst[slot * 3] = src[from * 3]; dst[slot * 3 + 1] = src[from * 3 + 1]; dst[slot * 3 + 2] = src[from * 3 + 2];
        }

        private static void SetP(double[] dst, int slot, double x, double y, double z)
        {
            dst[slot * 3] = x; dst[slot * 3 + 1] = y; dst[slot * 3 + 2] = z;
        }

        /// <summary>
        /// Impronta della geometria (indipendente dall'ordine dei triangoli): stessa geometria del modello, stessa
        /// impronta anche in un'altra sessione. Serve a ritrovare le distanze salvate su disco.
        /// </summary>
        public ulong Signature()
        {
            ulong sum = (ulong)_idx.Length;
            foreach (int t in _idx)
            {
                ulong h = 1469598103934665603UL;
                for (int j = 0; j < 9; j++) h = DistCache.Mix(h, (ulong)(long)Math.Round(_v[t * 9 + j] * 1e4));
                sum += h;
            }
            return sum == 0 ? 1 : sum;
        }

        // suddivisione dei triangoli grandi: lato massimo voluto (piedi) e numero massimo di triangoli dopo la suddivisione
        private const double MinEdgeFt = 1.0;
        private const int SplitBudget = 1200000;

        public int OriginalTriangles { get; private set; }
        public double SplitEdgeFt { get; private set; }

        public TriangleBvh(double[] vertices, int triangleCount)
        {
            OriginalTriangles = triangleCount;
            double edge;
            Subdivide(ref vertices, ref triangleCount, out edge);
            SplitEdgeFt = edge;
            _v = vertices;
            _n = new double[triangleCount * 3];
            _cen = new double[triangleCount * 3];
            var valid = new List<int>(triangleCount);

            for (int t = 0; t < triangleCount; t++)
            {
                int o = t * 9;
                double abx = _v[o + 3] - _v[o], aby = _v[o + 4] - _v[o + 1], abz = _v[o + 5] - _v[o + 2];
                double acx = _v[o + 6] - _v[o], acy = _v[o + 7] - _v[o + 1], acz = _v[o + 8] - _v[o + 2];
                double nx = aby * acz - abz * acy;
                double ny = abz * acx - abx * acz;
                double nz = abx * acy - aby * acx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-12) continue; // triangolo degenere

                _n[t * 3] = nx / len; _n[t * 3 + 1] = ny / len; _n[t * 3 + 2] = nz / len;
                _cen[t * 3] = (_v[o] + _v[o + 3] + _v[o + 6]) / 3.0;
                _cen[t * 3 + 1] = (_v[o + 1] + _v[o + 4] + _v[o + 7]) / 3.0;
                _cen[t * 3 + 2] = (_v[o + 2] + _v[o + 5] + _v[o + 8]) / 3.0;
                valid.Add(t);
            }

            _idx = valid.ToArray();
            _keys = new double[_idx.Length];
            if (_idx.Length > 0) Build(0, _idx.Length);
            _nodes = _list.ToArray();
            _list.Clear();

            // triangoli riordinati come le foglie dell'albero: i triangoli di una foglia (e delle foglie vicine) sono
            // contigui in memoria, e la ricerca non salta in giro per centinaia di MB a ogni triangolo
            int m = _idx.Length;
            var v2 = new double[m * 9];
            var n2 = new double[m * 3];
            for (int k = 0; k < m; k++)
            {
                int t = _idx[k];
                Array.Copy(_v, t * 9, v2, k * 9, 9);
                Array.Copy(_n, t * 3, n2, k * 3, 3);
                _idx[k] = k;
            }
            _v = v2; _n = n2;
            _keys = null;
        }

        private int Build(int start, int count)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            double cMinX = double.MaxValue, cMinY = double.MaxValue, cMinZ = double.MaxValue;
            double cMaxX = double.MinValue, cMaxY = double.MinValue, cMaxZ = double.MinValue;

            for (int k = start; k < start + count; k++)
            {
                int t = _idx[k];
                int o = t * 9;
                for (int j = 0; j < 9; j += 3)
                {
                    double x = _v[o + j], y = _v[o + j + 1], z = _v[o + j + 2];
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }
                double cx = _cen[t * 3], cy = _cen[t * 3 + 1], cz = _cen[t * 3 + 2];
                if (cx < cMinX) cMinX = cx; if (cx > cMaxX) cMaxX = cx;
                if (cy < cMinY) cMinY = cy; if (cy > cMaxY) cMaxY = cy;
                if (cz < cMinZ) cMinZ = cz; if (cz > cMaxZ) cMaxZ = cz;
            }

            int ni = _list.Count;
            var node = new Node
            {
                MinX = minX, MinY = minY, MinZ = minZ,
                MaxX = maxX, MaxY = maxY, MaxZ = maxZ,
                Left = -1, Right = -1, Start = start, Count = count
            };
            _list.Add(node);
            if (count <= LeafSize) return ni;

            double ex = cMaxX - cMinX, ey = cMaxY - cMinY, ez = cMaxZ - cMinZ;
            if (Math.Max(ex, Math.Max(ey, ez)) <= 0.0) return ni; // baricentri coincidenti: resta foglia

            int axis = (ex >= ey && ex >= ez) ? 0 : (ey >= ez ? 1 : 2);
            for (int k = start; k < start + count; k++) _keys[k] = _cen[_idx[k] * 3 + axis];
            Array.Sort(_keys, _idx, start, count);

            int mid = count / 2;
            int l = Build(start, mid);
            int r = Build(start + mid, count - mid);
            node.Left = l; node.Right = r; node.Count = 0;
            _list[ni] = node;
            return ni;
        }

        /// <summary>
        /// Distanza con segno dal triangolo più vicino entro maxDist. Restituisce false se non c'è nulla entro maxDist.
        /// stack: array di almeno 64 interi, da allocare una volta per thread.
        /// </summary>
        public bool Nearest(double px, double py, double pz, double maxDist, int[] stack, out double signedDist)
        {
            int hint = -1;
            return Nearest(px, py, pz, maxDist, stack, ref hint, out signedDist);
        }

        /// <summary>
        /// Come sopra, partendo dal triangolo «hint» (il più vicino al punto precedente, se vicino nello spazio): la sua
        /// distanza limita subito la ricerca e si scartano quasi tutti i rami. All'uscita hint è il triangolo trovato.
        /// </summary>
        public bool Nearest(double px, double py, double pz, double maxDist, int[] stack, ref int hint, out double signedDist)
        {
            signedDist = double.NaN;
            if (_nodes.Length == 0) return false;

            double best2 = maxDist * maxDist;
            int bestTri = -1;
            double bestDot = 0.0;
            if (hint >= 0)
            {
                double hx, hy, hz;
                ClosestPointOnTriangle(px, py, pz, hint, out hx, out hy, out hz);
                double dx = px - hx, dy = py - hy, dz = pz - hz;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < best2)
                {
                    best2 = d2; bestTri = hint;
                    bestDot = dx * _n[hint * 3] + dy * _n[hint * 3 + 1] + dz * _n[hint * 3 + 2];
                }
            }
            int sp = 0;
            stack[sp++] = 0;

            while (sp > 0)
            {
                int ni = stack[--sp];
                if (BoxDist2(ref _nodes[ni], px, py, pz) >= best2) continue;

                if (_nodes[ni].Left < 0)
                {
                    int end = _nodes[ni].Start + _nodes[ni].Count;
                    for (int k = _nodes[ni].Start; k < end; k++)
                    {
                        int t = _idx[k];
                        double cx, cy, cz;
                        ClosestPointOnTriangle(px, py, pz, t, out cx, out cy, out cz);
                        double dx = px - cx, dy = py - cy, dz = pz - cz;
                        double d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 < best2)
                        {
                            best2 = d2;
                            bestTri = t;
                            bestDot = dx * _n[t * 3] + dy * _n[t * 3 + 1] + dz * _n[t * 3 + 2];
                        }
                    }
                }
                else
                {
                    int l = _nodes[ni].Left, r = _nodes[ni].Right;
                    double dl = BoxDist2(ref _nodes[l], px, py, pz);
                    double dr = BoxDist2(ref _nodes[r], px, py, pz);
                    // si visita per primo il figlio più vicino (va messo per ultimo nello stack)
                    if (dl < dr) { stack[sp++] = r; stack[sp++] = l; }
                    else { stack[sp++] = l; stack[sp++] = r; }
                }
            }

            if (bestTri < 0) { hint = -1; return false; }
            hint = bestTri;
            double dist = Math.Sqrt(best2);
            signedDist = bestDot >= 0.0 ? dist : -dist;
            return true;
        }

        private static double BoxDist2(ref Node b, double px, double py, double pz)
        {
            double dx = Math.Max(Math.Max(b.MinX - px, 0.0), px - b.MaxX);
            double dy = Math.Max(Math.Max(b.MinY - py, 0.0), py - b.MaxY);
            double dz = Math.Max(Math.Max(b.MinZ - pz, 0.0), pz - b.MaxZ);
            return dx * dx + dy * dy + dz * dz;
        }

        // Punto più vicino su un triangolo (Ericson, Real-Time Collision Detection)
        private void ClosestPointOnTriangle(double px, double py, double pz, int t, out double qx, out double qy, out double qz)
        {
            int o = t * 9;
            double ax = _v[o], ay = _v[o + 1], az = _v[o + 2];
            double bx = _v[o + 3], by = _v[o + 4], bz = _v[o + 5];
            double cx = _v[o + 6], cy = _v[o + 7], cz = _v[o + 8];

            double abx = bx - ax, aby = by - ay, abz = bz - az;
            double acx = cx - ax, acy = cy - ay, acz = cz - az;
            double apx = px - ax, apy = py - ay, apz = pz - az;

            double d1 = abx * apx + aby * apy + abz * apz;
            double d2 = acx * apx + acy * apy + acz * apz;
            if (d1 <= 0.0 && d2 <= 0.0) { qx = ax; qy = ay; qz = az; return; }

            double bpx = px - bx, bpy = py - by, bpz = pz - bz;
            double d3 = abx * bpx + aby * bpy + abz * bpz;
            double d4 = acx * bpx + acy * bpy + acz * bpz;
            if (d3 >= 0.0 && d4 <= d3) { qx = bx; qy = by; qz = bz; return; }

            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
            {
                double v = d1 / (d1 - d3);
                qx = ax + v * abx; qy = ay + v * aby; qz = az + v * abz; return;
            }

            double cpx = px - cx, cpy = py - cy, cpz = pz - cz;
            double d5 = abx * cpx + aby * cpy + abz * cpz;
            double d6 = acx * cpx + acy * cpy + acz * cpz;
            if (d6 >= 0.0 && d5 <= d6) { qx = cx; qy = cy; qz = cz; return; }

            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
            {
                double w = d2 / (d2 - d6);
                qx = ax + w * acx; qy = ay + w * acy; qz = az + w * acz; return;
            }

            double va = d3 * d6 - d5 * d4;
            if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0)
            {
                double w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                qx = bx + w * (cx - bx); qy = by + w * (cy - by); qz = bz + w * (cz - bz); return;
            }

            double denom = 1.0 / (va + vb + vc);
            double vv = vb * denom, ww = vc * denom;
            qx = ax + abx * vv + acx * ww;
            qy = ay + aby * vv + acy * ww;
            qz = az + abz * vv + acz * ww;
        }
    }
}
