using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;

namespace PointDeviation
{
    // =====================================================================================
    //  Sorgente E57 (ASTM E2807), lettura diretta senza librerie esterne.
    //  Il file è diviso in pagine (di solito 1024 byte: 1020 di dati + 4 di controllo CRC); la descrizione è un XML,
    //  i punti di ogni scansione sono un «CompressedVector»: pacchetti di dati in cui ogni campo (X, Y, Z, colore…)
    //  ha il proprio flusso di byte, codificato a bit (interi, interi scalati) o come numeri a virgola mobile.
    //  Si leggono coordinate cartesiane o sferiche, colori (o intensità come grigio), stato di validità, e la posizione
    //  di ogni scansione (rotazione e traslazione) per portare tutto nello stesso sistema.
    // =====================================================================================

    internal sealed class E57Source : IPointSource
    {
        private readonly string _path;
        private readonly List<Scan> _scans = new List<Scan>();
        private long _pageSize = 1024;

        public string FormatName { get { return "E57"; } }
        public string SourcePath { get { return _path; } }
        public bool HasRgb { get; private set; }
        public long Count { get; private set; }
        public double[] MinM { get; private set; }
        public double[] MaxM { get; private set; }
        public string Notes { get; private set; }

        private enum Kind { Float, Integer, Scaled }

        private sealed class Field
        {
            public string Name;
            public int Stream;          // indice del flusso (ordine nel prototipo)
            public Kind Kind;
            public bool Single;         // Float a 32 bit
            public long Min, Max;       // Integer / ScaledInteger
            public double Scale = 1, Offset;
            public int Bits;            // bit per valore (interi)
            public double FMin = double.NaN, FMax = double.NaN; // limiti dichiarati dei Float (per i colori)
        }

        private sealed class Scan
        {
            public string Name;
            public long SectionOffset;  // posizione fisica dell'intestazione della sezione binaria
            public long Records;
            public int StreamCount;
            public Field X, Y, Z, Range, Azimuth, Elevation, R, G, B, Intensity, CartInvalid, SphInvalid;
            public bool Spherical;
            public double[] Rot = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }; // matrice 3x3 per righe
            public double[] T = { 0, 0, 0 };
            public bool HasPose;
            public double[] BMin, BMax; // ingombro dichiarato (sistema locale)
        }

        public E57Source(string path)
        {
            _path = path;
            string xml;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var h = new byte[48];
                if (fs.Read(h, 0, 48) != 48 || Encoding.ASCII.GetString(h, 0, 8) != "ASTM-E57")
                    throw new InvalidDataException(L.T("Il file non è un E57."));
                long xmlPhys = BitConverter.ToInt64(h, 24), xmlLen = BitConverter.ToInt64(h, 32);
                _pageSize = BitConverter.ToInt64(h, 40);
                if (_pageSize < 64 || _pageSize > (1 << 24)) _pageSize = 1024;
                var buf = new byte[xmlLen];
                ReadLogical(fs, ToLogical(xmlPhys), buf, 0, (int)xmlLen);
                xml = Encoding.UTF8.GetString(buf);
            }
            ParseXml(xml);
            if (_scans.Count == 0) throw new InvalidDataException(L.T("Il file E57 non contiene scansioni con punti."));

            long n = 0;
            foreach (Scan s in _scans) n += s.Records;
            Count = n;
            HasRgb = _scans.Exists(s => s.R != null || s.Intensity != null);
            ComputeBounds();
            int withColor = _scans.FindAll(s => s.R != null).Count, withInt = _scans.FindAll(s => s.R == null && s.Intensity != null).Count;
            Notes = string.Format(L.T("E57: {0} scansioni, {1:N0} punti"), _scans.Count, n)
                + (withColor > 0 ? string.Format(L.T(", {0} con colori"), withColor) : "")
                + (withInt > 0 ? string.Format(L.T(", {0} con intensità (in grigio)"), withInt) : "");
            Log.Write("E57", Notes + " – " + Path.GetFileName(path));
        }

        // ------------------------------------------------------------------ pagine

        private long ToLogical(long phys) { long d = _pageSize - 4; return phys / _pageSize * d + phys % _pageSize; }

        private long ToPhysical(long logical) { long d = _pageSize - 4; return logical / d * _pageSize + logical % d; }

        /// <summary>Legge count byte logici (saltando i 4 byte di controllo alla fine di ogni pagina).</summary>
        private void ReadLogical(Stream fs, long logical, byte[] dst, int off, int count)
        {
            long d = _pageSize - 4;
            while (count > 0)
            {
                long inPage = logical % d;
                int chunk = (int)Math.Min(count, d - inPage);
                fs.Position = ToPhysical(logical);
                int got = 0;
                while (got < chunk)
                {
                    int n = fs.Read(dst, off + got, chunk - got);
                    if (n <= 0) throw new EndOfStreamException(L.T("File E57 troncato."));
                    got += n;
                }
                off += chunk; count -= chunk; logical += chunk;
            }
        }

        // ------------------------------------------------------------------ XML

        private static XmlElement Child(XmlNode n, string name)
        {
            if (n == null) return null;
            foreach (XmlNode c in n.ChildNodes)
                if (c.NodeType == XmlNodeType.Element && c.LocalName == name) return (XmlElement)c;
            return null;
        }

        private static double Num(XmlNode n, double def)
        {
            if (n == null) return def;
            double v;
            return double.TryParse(n.InnerText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static double Attr(XmlElement e, string a, double def)
        {
            string s = e.GetAttribute(a);
            double v;
            return !string.IsNullOrEmpty(s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private void ParseXml(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);
            XmlElement root = doc.DocumentElement;
            XmlElement data3D = Child(root, "data3D");
            if (data3D == null) return;
            int idx = 0;
            foreach (XmlNode node in data3D.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                idx++;
                var e = (XmlElement)node;
                XmlElement pts = Child(e, "points");
                if (pts == null) continue;
                XmlElement codecs = Child(pts, "codecs");
                if (codecs != null && codecs.HasChildNodes)
                    throw new NotSupportedException(L.T("Il file E57 usa una compressione non standard (codec) non supportata."));
                var s = new Scan
                {
                    Name = Child(e, "name") != null ? Child(e, "name").InnerText : "scan " + idx,
                    SectionOffset = (long)Attr(pts, "fileOffset", -1),
                    Records = (long)Attr(pts, "recordCount", 0)
                };
                if (s.SectionOffset < 0 || s.Records <= 0) continue;

                XmlElement proto = Child(pts, "prototype");
                if (proto == null) continue;
                int stream = 0;
                foreach (XmlNode pn in proto.ChildNodes)
                {
                    if (pn.NodeType != XmlNodeType.Element) continue;
                    var pe = (XmlElement)pn;
                    var f = new Field { Name = pe.LocalName, Stream = stream++ };
                    string type = pe.GetAttribute("type");
                    if (type == "Float")
                    {
                        f.Kind = Kind.Float;
                        f.Single = pe.GetAttribute("precision") == "single";
                        f.FMin = Attr(pe, "minimum", double.NaN); f.FMax = Attr(pe, "maximum", double.NaN);
                    }
                    else if (type == "Integer" || type == "ScaledInteger")
                    {
                        f.Kind = type == "Integer" ? Kind.Integer : Kind.Scaled;
                        f.Min = (long)Attr(pe, "minimum", 0);
                        f.Max = (long)Attr(pe, "maximum", 0);
                        f.Scale = Attr(pe, "scale", 1); f.Offset = Attr(pe, "offset", 0);
                        ulong span = (ulong)(f.Max - f.Min);
                        int bits = 0;
                        while (bits < 64 && (span >> bits) != 0) bits++;
                        f.Bits = bits;
                    }
                    else continue; // stringhe o altro: il flusso esiste ma non serve
                    switch (f.Name)
                    {
                        case "cartesianX": s.X = f; break;
                        case "cartesianY": s.Y = f; break;
                        case "cartesianZ": s.Z = f; break;
                        case "sphericalRange": s.Range = f; break;
                        case "sphericalAzimuth": s.Azimuth = f; break;
                        case "sphericalElevation": s.Elevation = f; break;
                        case "colorRed": s.R = f; break;
                        case "colorGreen": s.G = f; break;
                        case "colorBlue": s.B = f; break;
                        case "intensity": s.Intensity = f; break;
                        case "cartesianInvalidState": s.CartInvalid = f; break;
                        case "sphericalInvalidState": s.SphInvalid = f; break;
                    }
                }
                s.StreamCount = stream;
                if (s.R == null || s.G == null || s.B == null) s.R = s.G = s.B = null;
                if (s.X == null || s.Y == null || s.Z == null)
                {
                    if (s.Range == null || s.Azimuth == null || s.Elevation == null) continue; // senza coordinate
                    s.Spherical = true;
                }

                XmlElement pose = Child(e, "pose");
                if (pose != null)
                {
                    XmlElement r = Child(pose, "rotation"), t = Child(pose, "translation");
                    if (r != null)
                    {
                        double w = Num(Child(r, "w"), 1), x = Num(Child(r, "x"), 0), y = Num(Child(r, "y"), 0), z = Num(Child(r, "z"), 0);
                        double nq = Math.Sqrt(w * w + x * x + y * y + z * z);
                        if (nq > 1e-12) { w /= nq; x /= nq; y /= nq; z /= nq; }
                        s.Rot = new[]
                        {
                            1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y),
                            2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x),
                            2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)
                        };
                        s.HasPose = true;
                    }
                    if (t != null) { s.T = new[] { Num(Child(t, "x"), 0), Num(Child(t, "y"), 0), Num(Child(t, "z"), 0) }; s.HasPose = true; }
                }
                XmlElement cb = Child(e, "cartesianBounds");
                if (cb != null && Child(cb, "xMinimum") != null)
                {
                    s.BMin = new[] { Num(Child(cb, "xMinimum"), 0), Num(Child(cb, "yMinimum"), 0), Num(Child(cb, "zMinimum"), 0) };
                    s.BMax = new[] { Num(Child(cb, "xMaximum"), 0), Num(Child(cb, "yMaximum"), 0), Num(Child(cb, "zMaximum"), 0) };
                    if (!(s.BMax[0] >= s.BMin[0] && s.BMax[1] >= s.BMin[1] && s.BMax[2] >= s.BMin[2])) s.BMin = s.BMax = null;
                }
                _scans.Add(s);
            }
        }

        // ------------------------------------------------------------------ ingombro

        private void ComputeBounds()
        {
            double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
            bool all = true;
            foreach (Scan s in _scans)
            {
                if (s.BMin == null) { all = false; break; }
                // ingombro dichiarato (nel sistema della scansione): si trasformano gli 8 vertici
                for (int c = 0; c < 8; c++)
                {
                    double x = (c & 1) != 0 ? s.BMax[0] : s.BMin[0], y = (c & 2) != 0 ? s.BMax[1] : s.BMin[1], z = (c & 4) != 0 ? s.BMax[2] : s.BMin[2];
                    double px, py, pz;
                    Apply(s, x, y, z, out px, out py, out pz);
                    mn[0] = Math.Min(mn[0], px); mn[1] = Math.Min(mn[1], py); mn[2] = Math.Min(mn[2], pz);
                    mx[0] = Math.Max(mx[0], px); mx[1] = Math.Max(mx[1], py); mx[2] = Math.Max(mx[2], pz);
                }
            }
            if (!all)
            {
                // ingombro non dichiarato: lettura completa (costa una passata in più)
                mn = new[] { double.MaxValue, double.MaxValue, double.MaxValue }; mx = new[] { double.MinValue, double.MinValue, double.MinValue };
                ReadAll(pb =>
                {
                    for (int i = 0; i < pb.Count; i++)
                    {
                        double x = pb.X[i], y = pb.Y[i], z = pb.Z[i];
                        if (x < mn[0]) mn[0] = x; if (x > mx[0]) mx[0] = x;
                        if (y < mn[1]) mn[1] = y; if (y > mx[1]) mx[1] = y;
                        if (z < mn[2]) mn[2] = z; if (z > mx[2]) mx[2] = z;
                    }
                }, CancellationToken.None);
            }
            if (mn[0] > mx[0]) { mn = new double[3]; mx = new[] { 1.0, 1.0, 1.0 }; }
            MinM = mn; MaxM = mx;
        }

        private static void Apply(Scan s, double x, double y, double z, out double px, out double py, out double pz)
        {
            double[] r = s.Rot;
            px = r[0] * x + r[1] * y + r[2] * z + s.T[0];
            py = r[3] * x + r[4] * y + r[5] * z + s.T[1];
            pz = r[6] * x + r[7] * y + r[8] * z + s.T[2];
        }

        // ------------------------------------------------------------------ lettura dei punti

        /// <summary>Flusso di byte di un campo, accumulato pacchetto dopo pacchetto e letto a bit o a valori interi.</summary>
        private sealed class ByteStream
        {
            private byte[] _buf = new byte[1 << 16];
            private int _len;        // byte validi
            private long _bit;       // posizione di lettura in bit

            public void Append(byte[] src, int off, int count)
            {
                int start = (int)(_bit >> 3);
                if (start > 0 && start * 2 >= _len)
                {
                    // si compatta: i byte già letti non servono più
                    Buffer.BlockCopy(_buf, start, _buf, 0, _len - start);
                    _len -= start; _bit -= (long)start << 3;
                }
                if (_len + count > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + count));
                Buffer.BlockCopy(src, off, _buf, _len, count);
                _len += count;
            }

            public long AvailableBits { get { return ((long)_len << 3) - _bit; } }

            public ulong ReadBits(int n)
            {
                if (n == 0) return 0;
                ulong v = 0;
                int got = 0;
                while (got < n)
                {
                    int byteIdx = (int)(_bit >> 3), bitIdx = (int)(_bit & 7);
                    int take = Math.Min(8 - bitIdx, n - got);
                    ulong part = (ulong)((_buf[byteIdx] >> bitIdx) & ((1 << take) - 1));
                    v |= part << got;
                    got += take; _bit += take;
                }
                return v;
            }

            public double ReadDouble()
            {
                int i = (int)(_bit >> 3); _bit += 64;
                return BitConverter.ToDouble(_buf, i);
            }

            public float ReadSingle()
            {
                int i = (int)(_bit >> 3); _bit += 32;
                return BitConverter.ToSingle(_buf, i);
            }
        }

        private static int BitsPerValue(Field f) { return f.Kind == Kind.Float ? (f.Single ? 32 : 64) : f.Bits; }

        private static double Value(Field f, ByteStream s)
        {
            switch (f.Kind)
            {
                case Kind.Float: return f.Single ? s.ReadSingle() : s.ReadDouble();
                case Kind.Integer: return (long)s.ReadBits(f.Bits) + f.Min;
                default: return ((long)s.ReadBits(f.Bits) + f.Min) * f.Scale + f.Offset;
            }
        }

        private static byte Color(Field f, double v)
        {
            double lo, hi;
            if (f.Kind == Kind.Float) { lo = double.IsNaN(f.FMin) ? 0 : f.FMin; hi = double.IsNaN(f.FMax) ? 1 : f.FMax; }
            else if (f.Kind == Kind.Integer) { lo = f.Min; hi = f.Max; }
            else { lo = f.Min * f.Scale + f.Offset; hi = f.Max * f.Scale + f.Offset; }
            if (!(hi > lo)) return 0;
            double t = (v - lo) / (hi - lo);
            return (byte)Math.Max(0, Math.Min(255, Math.Round(t * 255)));
        }

        public void ReadAll(Action<PointBlock> sink, CancellationToken ct)
        {
            const int Block = 65536;
            var pb = new PointBlock(Block);
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                foreach (Scan s in _scans)
                {
                    ct.ThrowIfCancellationRequested();
                    ReadScan(fs, s, pb, sink, ct);
                }
                if (pb.Count > 0) { sink(pb); pb.Count = 0; }
            }
        }

        private void ReadScan(FileStream fs, Scan s, PointBlock pb, Action<PointBlock> sink, CancellationToken ct)
        {
            // intestazione della sezione binaria (32 byte logici)
            var head = new byte[32];
            long secLogical = ToLogical(s.SectionOffset);
            ReadLogical(fs, secLogical, head, 0, 32);
            if (head[0] != 1) throw new InvalidDataException(L.T("Sezione dei punti E57 non valida."));
            long secLen = BitConverter.ToInt64(head, 8);
            long dataPhys = BitConverter.ToInt64(head, 16);
            long pos = ToLogical(dataPhys), end = secLogical + secLen;

            // campi da leggere e relativi flussi
            var fields = new List<Field>();
            Action<Field> use = f => { if (f != null && !fields.Contains(f)) fields.Add(f); };
            if (s.Spherical) { use(s.Range); use(s.Azimuth); use(s.Elevation); use(s.SphInvalid); }
            else { use(s.X); use(s.Y); use(s.Z); use(s.CartInvalid); }
            if (s.R != null) { use(s.R); use(s.G); use(s.B); }
            else if (s.Intensity != null) use(s.Intensity);
            var streams = new ByteStream[s.StreamCount];
            foreach (Field f in fields) streams[f.Stream] = new ByteStream();

            long done = 0;
            var ph = new byte[6];
            var lens = new int[s.StreamCount];
            byte[] packet = new byte[65536];
            while (done < s.Records && pos < end)
            {
                ct.ThrowIfCancellationRequested();
                ReadLogical(fs, pos, ph, 0, 4);
                int type = ph[0];
                int plen = BitConverter.ToUInt16(ph, 2) + 1;
                if (type == 1)
                {
                    ReadLogical(fs, pos, packet, 0, plen);
                    int count = BitConverter.ToUInt16(packet, 4);
                    if (count != s.StreamCount) throw new InvalidDataException(L.T("Pacchetto E57 non coerente con la descrizione dei punti."));
                    int off = 6 + 2 * count;
                    for (int k = 0; k < count; k++) lens[k] = BitConverter.ToUInt16(packet, 6 + 2 * k);
                    for (int k = 0; k < count; k++)
                    {
                        if (streams[k] != null && lens[k] > 0) streams[k].Append(packet, off, lens[k]);
                        off += lens[k];
                    }
                    // record completi disponibili in tutti i flussi che servono
                    long avail = long.MaxValue;
                    foreach (Field f in fields)
                    {
                        int b = BitsPerValue(f);
                        if (b > 0) avail = Math.Min(avail, streams[f.Stream].AvailableBits / b);
                    }
                    if (avail == long.MaxValue) avail = s.Records - done;
                    avail = Math.Min(avail, s.Records - done);
                    for (long r = 0; r < avail; r++)
                    {
                        double a, b, c;
                        bool valid = true;
                        if (s.Spherical)
                        {
                            double rg = Value(s.Range, streams[s.Range.Stream]);
                            double az = Value(s.Azimuth, streams[s.Azimuth.Stream]);
                            double el = Value(s.Elevation, streams[s.Elevation.Stream]);
                            if (s.SphInvalid != null && Value(s.SphInvalid, streams[s.SphInvalid.Stream]) != 0) valid = false;
                            double ce = Math.Cos(el);
                            a = rg * ce * Math.Cos(az); b = rg * ce * Math.Sin(az); c = rg * Math.Sin(el);
                        }
                        else
                        {
                            a = Value(s.X, streams[s.X.Stream]);
                            b = Value(s.Y, streams[s.Y.Stream]);
                            c = Value(s.Z, streams[s.Z.Stream]);
                            if (s.CartInvalid != null && Value(s.CartInvalid, streams[s.CartInvalid.Stream]) != 0) valid = false;
                        }
                        byte cr = 200, cg = 200, cb = 200;
                        if (s.R != null)
                        {
                            cr = Color(s.R, Value(s.R, streams[s.R.Stream]));
                            cg = Color(s.G, Value(s.G, streams[s.G.Stream]));
                            cb = Color(s.B, Value(s.B, streams[s.B.Stream]));
                        }
                        else if (s.Intensity != null) cr = cg = cb = Color(s.Intensity, Value(s.Intensity, streams[s.Intensity.Stream]));
                        if (!valid || double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(c)) continue;

                        double px, py, pz;
                        Apply(s, a, b, c, out px, out py, out pz);
                        int i = pb.Count;
                        pb.X[i] = px; pb.Y[i] = py; pb.Z[i] = pz;
                        pb.R[i] = cr; pb.G[i] = cg; pb.B[i] = cb;
                        if (++pb.Count == pb.X.Length) { sink(pb); pb.Count = 0; }
                    }
                    done += avail;
                }
                // pacchetti indice (0) o vuoti (2): si saltano
                pos += plen;
            }
        }
    }
}
