using System;
using System.IO;
using System.Text;

namespace PointDeviation
{
    /// <summary>Nuvola caricata: coordinate in piedi (unità interne Revit), relative a Origin.</summary>
    internal sealed class LasData
    {
        public double[] Origin = new double[3];     // piedi, coordinate Revit
        public float[] Xyz;                          // 3 float per punto, relativi a Origin
        public byte[] Rgb;                           // 3 byte per punto, null se il file non ha colori
        public int Count;                            // punti effettivamente caricati
        public long TotalInFile;
        public long Stride;                          // 1 punto ogni Stride
        public double[] Min = new double[3];         // bbox assoluto (piedi)
        public double[] Max = new double[3];
        public double[] CenterFileM = new double[3]; // centro nuvola nel file, in metri, prima della traslazione
    }

    /// <summary>Lettore LAS 1.0-1.4 non compresso (formati punto 0-10). Legge X, Y, Z e, se presenti, i colori RGB.</summary>
    internal static class LasReader
    {
        private const double FtPerM = 1.0 / 0.3048;

        public static LasData Read(string path, double unitToMeters,
            double shiftXm, double shiftYm, double shiftZm,
            int maxPoints, Action<string> progress)
        {
            if (maxPoints < 1) maxPoints = 1;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 227) throw new InvalidDataException(L.T("File LAS non valido (troppo piccolo)."));

                string sig = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (sig != "LASF") throw new InvalidDataException(L.T("Il file non è un LAS (firma LASF mancante)."));

                fs.Position = 25; byte vMinor = br.ReadByte();
                fs.Position = 96; uint dataOffset = br.ReadUInt32();
                fs.Position = 104;
                byte fmtRaw = br.ReadByte();
                ushort recLen = br.ReadUInt16();
                uint legacyCount = br.ReadUInt32();

                if ((fmtRaw & 0x80) != 0)
                    throw new NotSupportedException(
                        L.T("File LAZ (compresso) non supportato in questa versione. ") +
                        L.T("Convertilo in LAS non compresso (es. con PDAL o LAStools)."));
                int fmt = fmtRaw & 0x3F;
                if (fmt > 10) throw new NotSupportedException(L.T("Formato punto LAS ") + fmt + L.T(" non riconosciuto."));
                if (recLen < 12) throw new InvalidDataException(L.T("Lunghezza record punto non valida."));

                long total = legacyCount;
                if (vMinor >= 4 && fs.Length >= 255) { fs.Position = 247; total = (long)br.ReadUInt64(); }

                long avail = (fs.Length - dataOffset) / recLen;
                if (total <= 0 || total > avail) total = avail;
                if (total <= 0) throw new InvalidDataException(L.T("Il file non contiene punti."));

                fs.Position = 131;
                double sx = br.ReadDouble(), sy = br.ReadDouble(), sz = br.ReadDouble();
                double ox = br.ReadDouble(), oy = br.ReadDouble(), oz = br.ReadDouble();
                fs.Position = 179;
                double maxX = br.ReadDouble(), minX = br.ReadDouble();
                double maxY = br.ReadDouble(), minY = br.ReadDouble();
                double maxZ = br.ReadDouble(), minZ = br.ReadDouble();

                var result = new LasData();
                result.TotalInFile = total;

                // centro del bbox (unità del file) -> metri -> piedi, con traslazione utente
                double cX = (minX + maxX) * 0.5, cY = (minY + maxY) * 0.5, cZ = (minZ + maxZ) * 0.5;
                result.CenterFileM[0] = cX * unitToMeters;
                result.CenterFileM[1] = cY * unitToMeters;
                result.CenterFileM[2] = cZ * unitToMeters;
                result.Origin[0] = (cX * unitToMeters + shiftXm) * FtPerM;
                result.Origin[1] = (cY * unitToMeters + shiftYm) * FtPerM;
                result.Origin[2] = (cZ * unitToMeters + shiftZm) * FtPerM;

                long stride = Math.Max(1, (total + maxPoints - 1) / maxPoints);
                long expected = (total + stride - 1) / stride;
                result.Stride = stride;

                // coordinata assoluta (piedi) = raw * k + off
                double kx = sx * unitToMeters * FtPerM, ky = sy * unitToMeters * FtPerM, kz = sz * unitToMeters * FtPerM;
                double fx = (ox * unitToMeters + shiftXm) * FtPerM;
                double fy = (oy * unitToMeters + shiftYm) * FtPerM;
                double fz = (oz * unitToMeters + shiftZm) * FtPerM;

                int rgbOff = RgbOffset(fmt, recLen);
                var rgb16 = rgbOff >= 0 ? new ushort[expected * 3] : null;

                var xyz = new float[expected * 3];
                int n = 0;
                double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
                double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;

                const int Block = 16384;
                var buf = new byte[(long)recLen * Block];
                fs.Position = dataOffset;
                long remaining = total;
                long gi = 0;
                int blockCounter = 0;

                while (remaining > 0)
                {
                    int want = (int)Math.Min(Block, remaining);
                    int got = ReadFully(fs, buf, want * recLen);
                    int cnt = got / recLen;
                    if (cnt == 0) break;

                    for (int r = 0; r < cnt; r++, gi++)
                    {
                        if (gi % stride != 0) continue;
                        int o = r * recLen;
                        double x = BitConverter.ToInt32(buf, o) * kx + fx;
                        double y = BitConverter.ToInt32(buf, o + 4) * ky + fy;
                        double z = BitConverter.ToInt32(buf, o + 8) * kz + fz;

                        if (x < mnx) mnx = x; if (x > mxx) mxx = x;
                        if (y < mny) mny = y; if (y > mxy) mxy = y;
                        if (z < mnz) mnz = z; if (z > mxz) mxz = z;

                        xyz[n * 3] = (float)(x - result.Origin[0]);
                        xyz[n * 3 + 1] = (float)(y - result.Origin[1]);
                        xyz[n * 3 + 2] = (float)(z - result.Origin[2]);

                        if (rgb16 != null)
                        {
                            rgb16[n * 3] = BitConverter.ToUInt16(buf, o + rgbOff);
                            rgb16[n * 3 + 1] = BitConverter.ToUInt16(buf, o + rgbOff + 2);
                            rgb16[n * 3 + 2] = BitConverter.ToUInt16(buf, o + rgbOff + 4);
                        }
                        n++;
                    }

                    remaining -= cnt;
                    if (progress != null && (blockCounter++ & 63) == 0)
                        progress(string.Format(L.T("Lettura LAS... {0:P0}"), (double)(total - remaining) / total));
                    if (cnt < want) break; // file troncato
                }

                if (n == 0) throw new InvalidDataException(L.T("Nessun punto letto dal file."));

                result.Xyz = xyz;
                result.Count = n;
                result.Min[0] = mnx; result.Min[1] = mny; result.Min[2] = mnz;
                result.Max[0] = mxx; result.Max[1] = mxy; result.Max[2] = mxz;
                result.Rgb = ConvertRgb(rgb16, n);
                return result;
            }
        }

        /// <summary>Offset dei colori RGB nel record punto, -1 se il formato non li prevede.</summary>
        private static int RgbOffset(int fmt, int recLen)
        {
            int off;
            switch (fmt)
            {
                case 2: off = 20; break;
                case 3: off = 28; break;
                case 5: off = 28; break;
                case 7:
                case 8:
                case 10: off = 30; break;
                default: return -1;
            }
            return off + 6 <= recLen ? off : -1;
        }

        /// <summary>I colori LAS sono a 16 bit, ma molti software scrivono valori 0-255: si rileva e si riporta a 8 bit.</summary>
        private static byte[] ConvertRgb(ushort[] rgb16, int n)
        {
            if (rgb16 == null) return null;

            ushort max = 0;
            for (int i = 0; i < n * 3; i++) if (rgb16[i] > max) max = rgb16[i];
            if (max == 0) return null; // colori tutti a zero: file senza colori reali

            bool wide = max > 255;
            var rgb = new byte[n * 3];
            for (int i = 0; i < n * 3; i++) rgb[i] = wide ? (byte)(rgb16[i] >> 8) : (byte)rgb16[i];
            return rgb;
        }

        private static int ReadFully(Stream s, byte[] buffer, int count)
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
}
