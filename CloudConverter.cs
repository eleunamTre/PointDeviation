using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PointDeviation
{
    /// <summary>Blocco di punti letti da una sorgente, in coordinate originali (metri).</summary>
    internal sealed class PointBlock
    {
        public double[] X, Y, Z;
        public byte[] R, G, B;
        public int Count;

        public PointBlock(int capacity)
        {
            X = new double[capacity]; Y = new double[capacity]; Z = new double[capacity];
            R = new byte[capacity]; G = new byte[capacity]; B = new byte[capacity];
        }
    }

    /// <summary>Un formato di nuvola da convertire. Deve poter essere letto più volte (il convertitore fa due passate).</summary>
    internal interface IPointSource
    {
        string FormatName { get; }
        string SourcePath { get; }
        bool HasRgb { get; }
        long Count { get; }
        double[] MinM { get; }
        double[] MaxM { get; }
        string Notes { get; }
        void ReadAll(Action<PointBlock> sink, CancellationToken ct);
    }

    // =====================================================================================
    //  Sorgente LAS (non compresso)
    // =====================================================================================

    internal sealed class LasSource : IPointSource
    {
        private readonly string _path;
        private readonly double _unit;
        private long _dataOffset, _count;
        private int _recLen, _rgbOff;
        private double _sx, _sy, _sz, _ox, _oy, _oz;

        public string FormatName { get { return "LAS"; } }
        public string SourcePath { get { return _path; } }
        public bool HasRgb { get { return _rgbOff >= 0; } }
        public long Count { get { return _count; } }
        public double[] MinM { get; private set; }
        public double[] MaxM { get; private set; }
        public string Notes { get { return ""; } }

        public LasSource(string path, double unitToMeters)
        {
            _path = path;
            _unit = unitToMeters > 0 ? unitToMeters : 1.0;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 227) throw new InvalidDataException(L.T("File LAS non valido (troppo piccolo)."));
                if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "LASF") throw new InvalidDataException(L.T("Il file non è un LAS."));

                fs.Position = 25; byte vMinor = br.ReadByte();
                fs.Position = 96; _dataOffset = br.ReadUInt32();
                fs.Position = 104;
                byte fmtRaw = br.ReadByte();
                _recLen = br.ReadUInt16();
                long count = br.ReadUInt32();
                if ((fmtRaw & 0x80) != 0) throw new NotSupportedException(L.T("I file LAZ (compressi) non sono ancora supportati."));
                int fmt = fmtRaw & 0x3F;
                if (fmt > 10 || _recLen < 12) throw new InvalidDataException(L.T("Formato punto LAS non riconosciuto."));
                if (vMinor >= 4 && fs.Length >= 255) { fs.Position = 247; count = (long)br.ReadUInt64(); }
                long avail = (fs.Length - _dataOffset) / _recLen;
                if (count <= 0 || count > avail) count = avail;
                _count = count;

                fs.Position = 131;
                _sx = br.ReadDouble(); _sy = br.ReadDouble(); _sz = br.ReadDouble();
                _ox = br.ReadDouble(); _oy = br.ReadDouble(); _oz = br.ReadDouble();
                fs.Position = 179;
                double maxX = br.ReadDouble(), minX = br.ReadDouble();
                double maxY = br.ReadDouble(), minY = br.ReadDouble();
                double maxZ = br.ReadDouble(), minZ = br.ReadDouble();
                MinM = new[] { minX * _unit, minY * _unit, minZ * _unit };
                MaxM = new[] { maxX * _unit, maxY * _unit, maxZ * _unit };

                switch (fmt)
                {
                    case 2: _rgbOff = 20; break;
                    case 3: case 5: _rgbOff = 28; break;
                    case 7: case 8: case 10: _rgbOff = 30; break;
                    default: _rgbOff = -1; break;
                }
                if (_rgbOff >= 0 && _rgbOff + 6 > _recLen) _rgbOff = -1;
            }
        }

        public void ReadAll(Action<PointBlock> sink, CancellationToken ct)
        {
            const int Block = 65536;
            var pb = new PointBlock(Block);
            var buf = new byte[_recLen * Block];
            double kx = _sx * _unit, ky = _sy * _unit, kz = _sz * _unit;
            double fx = _ox * _unit, fy = _oy * _unit, fz = _oz * _unit;

            // i colori LAS sono a 16 bit, ma alcuni programmi scrivono 0-255: si decide dal primo blocco
            int shift = -1;

            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                fs.Position = _dataOffset;
                long remaining = _count;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(Block, remaining);
                    int got = ReadFully(fs, buf, want * _recLen) / _recLen;
                    if (got == 0) break;

                    if (_rgbOff >= 0 && shift < 0)
                    {
                        int max = 0;
                        for (int r = 0; r < got; r++)
                        {
                            int o = r * _recLen + _rgbOff;
                            max = Math.Max(max, Math.Max(BitConverter.ToUInt16(buf, o), Math.Max(BitConverter.ToUInt16(buf, o + 2), BitConverter.ToUInt16(buf, o + 4))));
                        }
                        shift = max > 255 ? 8 : 0;
                    }

                    for (int r = 0; r < got; r++)
                    {
                        int o = r * _recLen;
                        pb.X[r] = BitConverter.ToInt32(buf, o) * kx + fx;
                        pb.Y[r] = BitConverter.ToInt32(buf, o + 4) * ky + fy;
                        pb.Z[r] = BitConverter.ToInt32(buf, o + 8) * kz + fz;
                        if (_rgbOff >= 0)
                        {
                            pb.R[r] = (byte)(BitConverter.ToUInt16(buf, o + _rgbOff) >> shift);
                            pb.G[r] = (byte)(BitConverter.ToUInt16(buf, o + _rgbOff + 2) >> shift);
                            pb.B[r] = (byte)(BitConverter.ToUInt16(buf, o + _rgbOff + 4) >> shift);
                        }
                    }
                    pb.Count = got;
                    sink(pb);
                    remaining -= got;
                    if (got < want) break;
                }
            }
        }

        internal static int ReadFully(Stream s, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int r = s.Read(buffer, total, count - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }
    }

    // =====================================================================================
    //  Unità di un LAS, lette dalla definizione del sistema di coordinate (se presente)
    // =====================================================================================

    internal static class LasUnits
    {
        public sealed class Result
        {
            public double MetersPerUnit = 1.0;
            public bool Found;          // l'unità è dichiarata nel file
            public string Description;  // testo da mostrare all'utente
        }

        /// <summary>
        /// Cerca l'unità lineare nei record del sistema di coordinate: GeoTIFF (GeoKeyDirectory, 34735)
        /// oppure WKT (2112), sia nei VLR sia negli EVLR dei LAS 1.4. Non solleva eccezioni.
        /// </summary>
        public static Result Detect(string path)
        {
            var res = new Result { Description = L.T("Il file non dichiara l'unità: presumo metri (il caso più comune nei rilievi).") };
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 227 || Encoding.ASCII.GetString(br.ReadBytes(4)) != "LASF") return res;
                    fs.Position = 25; byte vMinor = br.ReadByte();
                    fs.Position = 94; ushort headerSize = br.ReadUInt16();
                    fs.Position = 100; uint nVlr = br.ReadUInt32();

                    var records = new List<KeyValuePair<ushort, byte[]>>();
                    long pos = headerSize;
                    for (uint i = 0; i < nVlr && pos + 54 <= fs.Length; i++)
                    {
                        fs.Position = pos + 2;
                        string user = Encoding.ASCII.GetString(br.ReadBytes(16)).TrimEnd('\0');
                        ushort id = br.ReadUInt16();
                        ushort len = br.ReadUInt16();
                        fs.Position = pos + 54;
                        if (user == "LASF_Projection" && (id == 34735 || id == 2112))
                            records.Add(new KeyValuePair<ushort, byte[]>(id, br.ReadBytes(len)));
                        pos += 54 + len;
                    }

                    if (vMinor >= 4 && fs.Length >= 247)
                    {
                        fs.Position = 235; ulong evStart = br.ReadUInt64(); uint nEv = br.ReadUInt32();
                        long ep = (long)evStart;
                        for (uint i = 0; i < nEv && ep > 0 && ep + 60 <= fs.Length; i++)
                        {
                            fs.Position = ep + 2;
                            string user = Encoding.ASCII.GetString(br.ReadBytes(16)).TrimEnd('\0');
                            ushort id = br.ReadUInt16();
                            ulong len = br.ReadUInt64();
                            fs.Position = ep + 60;
                            if (user == "LASF_Projection" && (id == 34735 || id == 2112) && len < 1 << 20)
                                records.Add(new KeyValuePair<ushort, byte[]>(id, br.ReadBytes((int)len)));
                            ep += 60 + (long)len;
                        }
                    }

                    // il WKT, se c'è, è la descrizione più completa
                    foreach (var r in records)
                        if (r.Key == 2112 && FromWkt(Encoding.ASCII.GetString(r.Value), res)) return res;
                    foreach (var r in records)
                        if (r.Key == 34735 && FromGeoKeys(r.Value, res)) return res;
                }
            }
            catch { }
            return res;
        }

        private static bool FromWkt(string wkt, Result res)
        {
            // l'unità lineare del sistema proiettato è l'ultimo UNIT con un nome di lunghezza
            var m = System.Text.RegularExpressions.Regex.Matches(wkt,
                "(?:UNIT|LENGTHUNIT)\\[\"([^\"]+)\"\\s*,\\s*([0-9.Ee+-]+)");
            for (int i = m.Count - 1; i >= 0; i--)
            {
                string name = m[i].Groups[1].Value;
                string ln = name.ToLowerInvariant();
                if (!(ln.Contains("met") || ln.Contains("foot") || ln.Contains("feet") || ln.Contains("ft"))) continue;
                double f;
                if (!double.TryParse(m[i].Groups[2].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out f) || f <= 0) continue;
                res.MetersPerUnit = f;
                res.Found = true;
                res.Description = L.T("Unità letta dal file: ") + name + ".";
                return true;
            }
            return false;
        }

        private static bool FromGeoKeys(byte[] b, Result res)
        {
            if (b.Length < 8) return false;
            int n = BitConverter.ToUInt16(b, 6);
            ushort projected = 0;
            for (int i = 0; i < n && 8 + i * 8 + 8 <= b.Length; i++)
            {
                int o = 8 + i * 8;
                ushort key = BitConverter.ToUInt16(b, o), loc = BitConverter.ToUInt16(b, o + 2), val = BitConverter.ToUInt16(b, o + 6);
                if (loc != 0) continue;
                if (key == 3072) projected = val;
                if (key == 3076 || key == 4099)
                {
                    switch (val)
                    {
                        case 9001: Set(res, 1.0, L.T("metri")); return true;
                        case 9002: Set(res, 0.3048, L.T("piedi internazionali")); return true;
                        case 9003: Set(res, 1200.0 / 3937.0, L.T("piedi US survey")); return true;
                        case 9036: Set(res, 1000.0, L.T("chilometri")); return true;
                    }
                }
            }
            if (projected != 0)
            {
                // sistema proiettato senza unità esplicita: quasi tutti (UTM, Gauss-Boaga, ETRS89...) sono in metri
                res.MetersPerUnit = 1.0;
                res.Found = true;
                res.Description = L.T("Sistema di coordinate EPSG ") + projected + L.T(": unità metri.");
                return true;
            }
            return false;
        }

        private static void Set(Result r, double f, string name)
        {
            r.MetersPerUnit = f;
            r.Found = true;
            r.Description = L.T("Unità letta dal file: ") + name + ".";
        }
    }

    // =====================================================================================
    //  File grezzo temporaneo: lo scrive l'estrazione da Revit (RCP/RCS), lo legge il convertitore.
    //  Header "PDRW", poi record da 15 byte: float X, Y, Z (m, relativi a Center), byte R, G, B.
    // =====================================================================================

    internal sealed class RawPointWriter : IDisposable
    {
        private readonly string _path;
        private readonly BinaryWriter _bw;
        private readonly double[] _center;
        private long _count;
        private readonly double[] _min = { double.MaxValue, double.MaxValue, double.MaxValue };
        private readonly double[] _max = { double.MinValue, double.MinValue, double.MinValue };
        private const int HeaderSize = 4 + 8 + 9 * 8 + 1;

        public long Count { get { return _count; } }
        public bool HasRgb;
        public string SourcePath, FormatName, Notes;

        public RawPointWriter(string path, double[] centerM)
        {
            _path = path;
            _center = (double[])centerM.Clone();
            _bw = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20));
            _bw.Write(new byte[HeaderSize]); // riscritto alla chiusura
        }

        public void Add(double x, double y, double z, byte r, byte g, byte b)
        {
            _bw.Write((float)(x - _center[0]));
            _bw.Write((float)(y - _center[1]));
            _bw.Write((float)(z - _center[2]));
            _bw.Write(r); _bw.Write(g); _bw.Write(b);
            if (x < _min[0]) _min[0] = x; if (x > _max[0]) _max[0] = x;
            if (y < _min[1]) _min[1] = y; if (y > _max[1]) _max[1] = y;
            if (z < _min[2]) _min[2] = z; if (z > _max[2]) _max[2] = z;
            _count++;
        }

        public void Dispose()
        {
            _bw.Seek(0, SeekOrigin.Begin);
            _bw.Write(Encoding.ASCII.GetBytes("PDRW"));
            _bw.Write(_count);
            for (int i = 0; i < 3; i++) _bw.Write(_center[i]);
            for (int i = 0; i < 3; i++) _bw.Write(_count > 0 ? _min[i] : 0.0);
            for (int i = 0; i < 3; i++) _bw.Write(_count > 0 ? _max[i] : 0.0);
            _bw.Write((byte)(HasRgb ? 1 : 0));
            _bw.Dispose();

            // metadati testuali accanto al file grezzo
            File.WriteAllText(_path + ".txt", (FormatName ?? "") + "\n" + (SourcePath ?? "") + "\n" + (Notes ?? ""), Encoding.UTF8);
        }
    }

    internal sealed class RawSource : IPointSource
    {
        private readonly string _path;
        private readonly double[] _center = new double[3];
        private const int HeaderSize = 4 + 8 + 9 * 8 + 1;

        public string FormatName { get; private set; }
        public string SourcePath { get; private set; }
        public bool HasRgb { get; private set; }
        public long Count { get; private set; }
        public double[] MinM { get; private set; }
        public double[] MaxM { get; private set; }
        public string Notes { get; private set; }

        public RawSource(string path)
        {
            _path = path;
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "PDRW") throw new InvalidDataException(L.T("File temporaneo non valido."));
                Count = br.ReadInt64();
                for (int i = 0; i < 3; i++) _center[i] = br.ReadDouble();
                MinM = new[] { br.ReadDouble(), br.ReadDouble(), br.ReadDouble() };
                MaxM = new[] { br.ReadDouble(), br.ReadDouble(), br.ReadDouble() };
                HasRgb = br.ReadByte() != 0;
            }
            FormatName = "RCP"; SourcePath = ""; Notes = "";
            try
            {
                string[] meta = File.ReadAllText(path + ".txt", Encoding.UTF8).Split(new[] { '\n' }, 3);
                if (meta.Length > 0 && meta[0].Length > 0) FormatName = meta[0];
                if (meta.Length > 1) SourcePath = meta[1];
                if (meta.Length > 2) Notes = meta[2];
            }
            catch { }
        }

        public void ReadAll(Action<PointBlock> sink, CancellationToken ct)
        {
            const int Block = 65536, R = 15;
            var pb = new PointBlock(Block);
            var buf = new byte[Block * R];
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                fs.Position = HeaderSize;
                long remaining = Count;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(Block, remaining);
                    int got = LasSource.ReadFully(fs, buf, want * R) / R;
                    if (got == 0) break;
                    for (int r = 0; r < got; r++)
                    {
                        int o = r * R;
                        pb.X[r] = BitConverter.ToSingle(buf, o) + _center[0];
                        pb.Y[r] = BitConverter.ToSingle(buf, o + 4) + _center[1];
                        pb.Z[r] = BitConverter.ToSingle(buf, o + 8) + _center[2];
                        pb.R[r] = buf[o + 12]; pb.G[r] = buf[o + 13]; pb.B[r] = buf[o + 14];
                    }
                    pb.Count = got;
                    sink(pb);
                    remaining -= got;
                }
            }
        }

        public void Delete()
        {
            try { File.Delete(_path); } catch { }
            try { File.Delete(_path + ".txt"); } catch { }
        }
    }

    // =====================================================================================
    //  Convertitore: sorgente -> cartella .pdcloud nella libreria
    // =====================================================================================

    internal static class CloudConverter
    {
        private const int Grid = 128;            // celle per lato della griglia di conteggio (cubo della nuvola)
        private const int PreviewSamples = 300000;

        /// <summary>
        /// Converte la sorgente in una nuova cartella dentro libraryFolder. Lavora fuori memoria
        /// (due letture della sorgente e file temporanei per blocco), quindi regge nuvole molto grandi.
        /// progress riceve testo e frazione 0..1; può essere chiamato da un thread qualunque.
        /// </summary>
        public static string Convert(IPointSource src, string libraryFolder, string name,
            Action<string, double> progress, CancellationToken ct)
        {
            if (src.Count <= 0) throw new InvalidDataException(L.T("La sorgente non contiene punti."));
            Directory.CreateDirectory(libraryFolder);

            string finalDir = UniqueFolder(libraryFolder, name);
            string workDir = finalDir + ".part";
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            Directory.CreateDirectory(workDir);
            string tmpDir = Path.Combine(workDir, "_tmp");
            Directory.CreateDirectory(tmpDir);

            try
            {
                const double Scale = 0.0001;
                double[] min = (double[])src.MinM.Clone(), max = (double[])src.MaxM.Clone();
                for (int a = 0; a < 3; a++) if (!(max[a] > min[a])) max[a] = min[a] + 0.001;
                double[] center = { (min[0] + max[0]) * 0.5, (min[1] + max[1]) * 0.5, (min[2] + max[2]) * 0.5 };

                // griglia di conteggio: Grid³ celle sul cubo che contiene la nuvola (base dell'octree)
                double ext = Math.Max(max[0] - min[0], Math.Max(max[1] - min[1], max[2] - min[2])) * 1.000001;
                double cell = ext / Grid;
                int nx = Grid, ny = Grid, nz = Grid;
                var counts = new int[nx * ny * nz];

                // ---------------------------------------------------------- 1a passata: conteggio e campione per l'anteprima
                var sample = new List<float[]>(PreviewSamples);
                var rnd = new Random(12345);
                long seen = 0;
                double[] amin = { double.MaxValue, double.MaxValue, double.MaxValue };
                double[] amax = { double.MinValue, double.MinValue, double.MinValue };

                src.ReadAll(pb =>
                {
                    for (int i = 0; i < pb.Count; i++)
                    {
                        double x = pb.X[i], y = pb.Y[i], z = pb.Z[i];
                        counts[CellOf(x, y, z, min, cell, nx, ny, nz)]++;
                        if (x < amin[0]) amin[0] = x; if (x > amax[0]) amax[0] = x;
                        if (y < amin[1]) amin[1] = y; if (y > amax[1]) amax[1] = y;
                        if (z < amin[2]) amin[2] = z; if (z > amax[2]) amax[2] = z;

                        // campionamento a serbatoio: ogni punto ha la stessa probabilità di finire nell'anteprima
                        var pt = new float[] { (float)(x - center[0]), (float)(y - center[1]), (float)(z - center[2]), pb.R[i], pb.G[i], pb.B[i] };
                        if (seen < PreviewSamples) sample.Add(pt);
                        else
                        {
                            long j = (long)(rnd.NextDouble() * (seen + 1));
                            if (j < PreviewSamples) sample[(int)j] = pt;
                        }
                        seen++;
                    }
                    progress(L.T("Analisi della nuvola..."), 0.35 * seen / src.Count);
                }, ct);

                if (seen == 0) throw new InvalidDataException(L.T("Nessun punto letto dalla sorgente."));

                // ---------------------------------------------------------- octree (2a passata e costruzione)
                double[] cubeMin = { min[0] - center[0], min[1] - center[1], min[2] - center[2] };
                long written = 0;
                OctreeBuilder.Result tree = OctreeBuilder.Build(counts, Grid, cubeMin, ext, Scale, tmpDir,
                    Path.Combine(workDir, CloudInfo.PointsName),
                    (chunkOf, write) => src.ReadAll(pb =>
                    {
                        for (int i = 0; i < pb.Count; i++)
                        {
                            double x = pb.X[i] - center[0], y = pb.Y[i] - center[1], z = pb.Z[i] - center[2];
                            int c = chunkOf(x, y, z);
                            if (c < 0) continue;
                            write(c, x, y, z, pb.R[i], pb.G[i], pb.B[i]);
                            written++;
                        }
                        progress(L.T("Suddivisione in blocchi..."), 0.35 + 0.25 * written / seen);
                    }, ct),
                    progress, ct);
                TileEntry.Write(Path.Combine(workDir, CloudInfo.IndexName), tree.Nodes, 2);

                // ---------------------------------------------------------- anteprima e metadati
                progress(L.T("Anteprima..."), 0.98);
                try { PreviewRenderer.Render(sample, src.HasRgb, Path.Combine(workDir, CloudInfo.PreviewName)); } catch { }

                var info = new CloudInfo
                {
                    Name = name,
                    SourcePath = src.SourcePath,
                    SourceFormat = src.FormatName,
                    Converted = DateTime.Now.ToString("o"),
                    PointCount = tree.Points,
                    FormatVersion = CloudInfo.CurrentFormat,
                    OctreeGrid = OctreeBuilder.NodeGrid,
                    OctreeDepth = tree.Depth,
                    HasRgb = src.HasRgb,
                    Scale = Scale,
                    CenterM = center,
                    MinM = amin,
                    MaxM = amax,
                    TileCount = tree.Nodes.Count,
                    Notes = src.Notes
                };
                info.Save(workDir);
                CloudView.EnsureBase(workDir, info); // file .pdcl: la nuvola si inserisce anche da Inserisci > Nuvola di punti di Revit

                Directory.Delete(tmpDir, true);
                Directory.Move(workDir, finalDir);
                progress(L.T("Conversione completata."), 1.0);
                return finalDir;
            }
            catch
            {
                try { Directory.Delete(workDir, true); } catch { }
                throw;
            }
        }

        public static string SafeName(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in (name ?? "").Trim())
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
            string s = sb.ToString().Trim().TrimEnd('.');
            return s.Length == 0 ? "nuvola" : s;
        }

        private static string UniqueFolder(string lib, string name)
        {
            string baseName = SafeName(name);
            string p = Path.Combine(lib, baseName + CloudInfo.Extension);
            for (int i = 2; Directory.Exists(p) || Directory.Exists(p + ".part"); i++)
                p = Path.Combine(lib, baseName + "_" + i + CloudInfo.Extension);
            return p;
        }

        private static int CellOf(double x, double y, double z, double[] min, double cell, int nx, int ny, int nz)
        {
            int ix = (int)((x - min[0]) / cell), iy = (int)((y - min[1]) / cell), iz = (int)((z - min[2]) / cell);
            if (ix < 0) ix = 0; else if (ix >= nx) ix = nx - 1;
            if (iy < 0) iy = 0; else if (iy >= ny) iy = ny - 1;
            if (iz < 0) iz = 0; else if (iz >= nz) iz = nz - 1;
            return (iz * ny + iy) * nx + ix;
        }
    }

    // =====================================================================================
    //  Anteprima: vista assonometrica dei punti campionati, con z-buffer, salvata in PNG
    // =====================================================================================

    internal static class PreviewRenderer
    {
        public const int Width = 640, Height = 400;

        /// <summary>sample: x, y, z (m, relativi al centro), r, g, b.</summary>
        public static void Render(List<float[]> sample, bool rgb, string pngPath)
        {
            if (sample.Count == 0) return;

            // vista da sud-ovest, dall'alto di 35 gradi
            double az = 225.0 * Math.PI / 180.0, el = 35.0 * Math.PI / 180.0;
            double rx = Math.Cos(az), ry = -Math.Sin(az);              // asse orizzontale dello schermo
            double fx = Math.Sin(az), fy = Math.Cos(az);               // direzione orizzontale di vista
            double se = Math.Sin(el), ce = Math.Cos(el);

            int n = sample.Count;
            var u = new double[n]; var v = new double[n]; var d = new double[n];
            double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
            double zmin = double.MaxValue, zmax = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                float[] p = sample[i];
                double h = p[0] * fx + p[1] * fy;
                u[i] = p[0] * rx + p[1] * ry;
                v[i] = p[2] * ce - h * se;                // alto sullo schermo
                d[i] = h * ce + p[2] * se;                // più grande = più vicino all'osservatore
                if (u[i] < umin) umin = u[i]; if (u[i] > umax) umax = u[i];
                if (v[i] < vmin) vmin = v[i]; if (v[i] > vmax) vmax = v[i];
                if (p[2] < zmin) zmin = p[2]; if (p[2] > zmax) zmax = p[2];
            }

            const int margin = 12;
            double sc = Math.Min((Width - 2 * margin) / Math.Max(1e-6, umax - umin), (Height - 2 * margin) / Math.Max(1e-6, vmax - vmin));
            double ou = (Width - (umax - umin) * sc) / 2, ov = (Height - (vmax - vmin) * sc) / 2;

            var pixels = new int[Width * Height];                 // ARGB, sfondo trasparente
            var depth = new double[Width * Height];
            for (int i = 0; i < depth.Length; i++) depth[i] = double.MinValue;
            int splat = n < 60000 ? 2 : 1;

            for (int i = 0; i < n; i++)
            {
                int px = (int)(ou + (u[i] - umin) * sc);
                int py = Height - 1 - (int)(ov + (v[i] - vmin) * sc);
                float[] p = sample[i];
                byte r, g, b;
                if (rgb) { r = (byte)p[3]; g = (byte)p[4]; b = (byte)p[5]; }
                else ColorScale.Elevation((p[2] - zmin) / Math.Max(1e-6, zmax - zmin), out r, out g, out b);
                int argb = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

                for (int dy = 0; dy < splat; dy++)
                    for (int dx = 0; dx < splat; dx++)
                    {
                        int x = px + dx, y = py + dy;
                        if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                        int k = y * Width + x;
                        if (d[i] > depth[k]) { depth[k] = d[i]; pixels[k] = argb; }
                    }
            }

            using (var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < Height; y++)
                    Marshal.Copy(pixels, y * Width, bd.Scan0 + y * bd.Stride, Width);
                bmp.UnlockBits(bd);
                bmp.Save(pngPath, ImageFormat.Png);
            }
        }
    }
}
