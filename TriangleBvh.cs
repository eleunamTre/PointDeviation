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

        private readonly double[] _v;      // 9 double per triangolo
        private readonly double[] _n;      // 3 double per triangolo (normale unitaria)
        private readonly double[] _cen;    // baricentri
        private readonly double[] _keys;
        private readonly int[] _idx;
        private readonly List<Node> _list = new List<Node>();
        private Node[] _nodes;

        public int TriangleCount { get { return _idx.Length; } }

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

        public TriangleBvh(double[] vertices, int triangleCount)
        {
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
            signedDist = double.NaN;
            if (_nodes.Length == 0) return false;

            double best2 = maxDist * maxDist;
            int bestTri = -1;
            double bestDot = 0.0;
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

            if (bestTri < 0) return false;
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
