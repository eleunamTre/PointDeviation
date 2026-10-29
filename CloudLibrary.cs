using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PointDeviation
{
    // =====================================================================================
    //  Formato della libreria
    //
    //  Ogni nuvola convertita è una cartella  <libreria>\<nome>.pdcloud\  con:
    //    cloud.json   metadati (CloudInfo). Esiste solo a conversione completata.
    //    index.bin    elenco dei blocchi spaziali (tile): posizione nel file, numero di punti, ingombro.
    //    points.bin   punti, blocco dopo blocco. Record da 15 byte:
    //                 int32 X, Y, Z in passi di Scale metri, relativi a CenterM; byte R, G, B.
    //    preview.png  anteprima generata durante la conversione.
    //
    //  Dentro ogni blocco i punti sono mescolati a caso: leggere i primi k punti di un blocco
    //  dà un sottocampione uniforme di quel blocco.
    //
    //  Formato 2 (octree): i blocchi sono i nodi di un octree. Ogni nodo ha un sottocampione
    //  della sua zona con passo circa lato/OctreeGrid; i figli aggiungono dettaglio. Ogni punto
    //  sta in un solo nodo, quindi disegnare un nodo e i suoi antenati dà la densità del suo livello.
    //  Record da 18 byte: come il formato 1 più la normale (3 sbyte, *127) calcolata in conversione.
    //  Nell'indice: livello, figli (maschera e primo figlio, consecutivi), cubo del nodo,
    //  ingombro dell'intero sotto-albero. Nodo 0 = radice.
    // =====================================================================================

    [DataContract]
    internal sealed class CloudInfo
    {
        public const string FileName = "cloud.json";
        public const string IndexName = "index.bin";
        public const string PointsName = "points.bin";
        public const string PreviewName = "preview.png";
        public const string Extension = ".pdcloud";
        public const int RecordSizeV1 = 15;
        public const int RecordSizeV2 = 18;
        public const int CurrentFormat = 2;

        [DataMember] public int FormatVersion = 1;
        [DataMember] public string Name;
        [DataMember] public string SourcePath;
        [DataMember] public string SourceFormat;
        [DataMember] public string Converted;      // data e ora ISO 8601
        [DataMember] public long PointCount;
        [DataMember] public bool HasRgb;
        [DataMember] public double Scale = 0.0001; // metri per unità intera
        [DataMember] public double[] CenterM;      // punto di riferimento, coordinate originali (m)
        [DataMember] public double[] MinM;         // ingombro, coordinate originali (m)
        [DataMember] public double[] MaxM;
        [DataMember] public int TileCount;
        [DataMember] public string Notes;          // diagnostica della conversione
        [DataMember] public int OctreeGrid;        // celle per lato nel campionamento dei nodi (formato 2)
        [DataMember] public int OctreeDepth;

        /// <summary>Byte per punto in points.bin.</summary>
        public int RecordSize { get { return FormatVersion >= 2 ? RecordSizeV2 : RecordSizeV1; } }
        /// <summary>Blocchi organizzati come octree (formato 2)?</summary>
        public bool IsOctree { get { return FormatVersion >= 2; } }
        /// <summary>Normali precalcolate nei record?</summary>
        public bool HasNormals { get { return FormatVersion >= 2; } }

        public DateTime ConvertedDate
        {
            get
            {
                DateTime d;
                return DateTime.TryParse(Converted, null, System.Globalization.DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue;
            }
        }

        public double[] SizeM
        {
            get { return new[] { MaxM[0] - MinM[0], MaxM[1] - MinM[1], MaxM[2] - MinM[2] }; }
        }

        public double[] MidM
        {
            get { return new[] { (MinM[0] + MaxM[0]) * 0.5, (MinM[1] + MaxM[1]) * 0.5, (MinM[2] + MaxM[2]) * 0.5 }; }
        }

        public static bool IsCloudFolder(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, FileName)); }
            catch { return false; }
        }

        public static CloudInfo Load(string folder)
        {
            using (FileStream fs = File.OpenRead(Path.Combine(folder, FileName)))
                return (CloudInfo)new DataContractJsonSerializer(typeof(CloudInfo)).ReadObject(fs);
        }

        public void Save(string folder)
        {
            using (FileStream fs = File.Create(Path.Combine(folder, FileName)))
                new DataContractJsonSerializer(typeof(CloudInfo)).WriteObject(fs, this);
        }
    }

    internal struct TileEntry
    {
        public long FirstRecord;
        public int Count;
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ; // metri, relativi a CenterM (octree: tutto il sotto-albero)

        // solo formato 2 (octree)
        public byte Level, ChildMask;
        public int FirstChild;                           // indice del primo figlio; i figli sono consecutivi
        public float CubeX, CubeY, CubeZ, CubeSize;      // cubo del nodo (metri, relativi a CenterM)

        public int ChildCount
        {
            get { int c = 0; for (int m = ChildMask; m != 0; m >>= 1) c += m & 1; return c; }
        }

        public const string Magic = "PDCI";

        public static void Write(string path, IList<TileEntry> tiles, int version = 1)
        {
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                bw.Write(Encoding.ASCII.GetBytes(Magic));
                bw.Write(version);
                bw.Write(tiles.Count);
                foreach (TileEntry t in tiles)
                {
                    bw.Write(t.FirstRecord); bw.Write(t.Count);
                    bw.Write(t.MinX); bw.Write(t.MinY); bw.Write(t.MinZ);
                    bw.Write(t.MaxX); bw.Write(t.MaxY); bw.Write(t.MaxZ);
                    if (version >= 2)
                    {
                        bw.Write(t.Level); bw.Write(t.ChildMask); bw.Write(t.FirstChild);
                        bw.Write(t.CubeX); bw.Write(t.CubeY); bw.Write(t.CubeZ); bw.Write(t.CubeSize);
                    }
                }
            }
        }

        public static TileEntry[] Read(string path)
        {
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(br.ReadBytes(4)) != Magic)
                    throw new InvalidDataException(L.T("Indice della nuvola non valido."));
                int version = br.ReadInt32();
                int n = br.ReadInt32();
                var res = new TileEntry[n];
                for (int i = 0; i < n; i++)
                {
                    res[i].FirstRecord = br.ReadInt64(); res[i].Count = br.ReadInt32();
                    res[i].MinX = br.ReadSingle(); res[i].MinY = br.ReadSingle(); res[i].MinZ = br.ReadSingle();
                    res[i].MaxX = br.ReadSingle(); res[i].MaxY = br.ReadSingle(); res[i].MaxZ = br.ReadSingle();
                    if (version >= 2)
                    {
                        res[i].Level = br.ReadByte(); res[i].ChildMask = br.ReadByte(); res[i].FirstChild = br.ReadInt32();
                        res[i].CubeX = br.ReadSingle(); res[i].CubeY = br.ReadSingle(); res[i].CubeZ = br.ReadSingle(); res[i].CubeSize = br.ReadSingle();
                    }
                }
                return res;
            }
        }
    }

    // =====================================================================================
    //  Posizionamento: dalle coordinate originali della nuvola (metri) alle coordinate Revit (piedi)
    //      p_revit = R · (p_originale · FtPerM) + T
    // =====================================================================================

    internal enum PlacementMode { CentroSuCentro = 0, OrigineInterna = 1, Condivise = 2, UltimaInserita = 3 }

    internal static class Placement
    {
        public const double FtPerM = 1.0 / 0.3048;

        /// <summary>12 valori: matrice R per righe (9) e traslazione T in piedi (3).</summary>
        public static double[] Identity()
        {
            return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
        }

        public static double[] Translation(double tx, double ty, double tz)
        {
            return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, tx, ty, tz };
        }

        public static bool IsValid(double[] p)
        {
            return p != null && p.Length == 12;
        }

        public static string Describe(PlacementMode m)
        {
            switch (m)
            {
                case PlacementMode.OrigineInterna: return L.T("origine su origine interna");
                case PlacementMode.Condivise: return L.T("coordinate condivise");
                case PlacementMode.UltimaInserita: return L.T("origine sull'ultima inserita");
                default: return L.T("centro su centro");
            }
        }
    }

    // =====================================================================================
    //  Elenco delle cartelle-libreria (globale, in %AppData%\PointDeviation\libraries.json)
    // =====================================================================================

    [DataContract]
    internal sealed class LibraryList
    {
        [DataMember] public List<string> Folders = new List<string>();
        [DataMember] public int Current;
    }

    internal static class LibraryStore
    {
        private static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PointDeviation", "libraries.json");
            }
        }

        public static LibraryList Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (FileStream fs = File.OpenRead(FilePath))
                    {
                        var l = (LibraryList)new DataContractJsonSerializer(typeof(LibraryList)).ReadObject(fs);
                        if (l != null)
                        {
                            if (l.Folders == null) l.Folders = new List<string>();
                            if (l.Current < 0 || l.Current >= l.Folders.Count) l.Current = 0;
                            return l;
                        }
                    }
                }
            }
            catch { }
            return new LibraryList();
        }

        public static void Save(LibraryList l)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                using (FileStream fs = File.Create(FilePath))
                    new DataContractJsonSerializer(typeof(LibraryList)).WriteObject(fs, l);
            }
            catch { }
        }

        /// <summary>Nuvole convertite presenti in una cartella-libreria, dalla più recente.</summary>
        public static List<KeyValuePair<string, CloudInfo>> Scan(string folder)
        {
            var res = new List<KeyValuePair<string, CloudInfo>>();
            try
            {
                if (!Directory.Exists(folder)) return res;
                foreach (string d in Directory.GetDirectories(folder, "*" + CloudInfo.Extension))
                {
                    if (!CloudInfo.IsCloudFolder(d)) continue;
                    try { res.Add(new KeyValuePair<string, CloudInfo>(d, CloudInfo.Load(d))); } catch { }
                }
            }
            catch { }
            res.Sort((a, b) => b.Value.ConvertedDate.CompareTo(a.Value.ConvertedDate));
            return res;
        }
    }

    // =====================================================================================
    //  Lettura di una nuvola della libreria per Revit
    // =====================================================================================

    internal static class LibraryReader
    {
        /// <summary>
        /// Legge al massimo maxPoints punti, prendendo da ogni blocco la stessa frazione
        /// (i punti dei blocchi sono già mescolati), e li porta in coordinate Revit con il posizionamento dato.
        /// </summary>
        public static LasData Read(string folder, double[] placement, int maxPoints, Action<string> progress)
        {
            CloudInfo info = CloudInfo.Load(folder);
            TileEntry[] tiles = TileEntry.Read(Path.Combine(folder, CloudInfo.IndexName));
            if (!Placement.IsValid(placement)) placement = Placement.Identity();
            if (maxPoints < 1) maxPoints = 1;

            long total = 0;
            foreach (TileEntry t in tiles) total += t.Count;
            if (total == 0) throw new InvalidDataException(L.T("La nuvola non contiene punti."));

            double ratio = Math.Min(1.0, (double)maxPoints / total);
            var take = new int[tiles.Length];
            long n = 0;
            for (int i = 0; i < tiles.Length; i++)
            {
                take[i] = (int)Math.Min(tiles[i].Count, Math.Ceiling(tiles[i].Count * ratio));
                n += take[i];
            }
            if (n > int.MaxValue / 3) throw new InvalidDataException(L.T("Troppi punti richiesti."));

            double[] p = placement;
            double f = Placement.FtPerM;
            double[] c = info.CenterM;

            var result = new LasData();
            result.TotalInFile = total;
            result.Stride = Math.Max(1, (long)Math.Round(1.0 / ratio));
            double[] mid = info.MidM;
            result.CenterFileM[0] = mid[0]; result.CenterFileM[1] = mid[1]; result.CenterFileM[2] = mid[2];

            // origine dei punti caricati = posizione Revit del punto di riferimento della nuvola
            double cx = c[0] * f, cy = c[1] * f, cz = c[2] * f;
            result.Origin[0] = p[0] * cx + p[1] * cy + p[2] * cz + p[9];
            result.Origin[1] = p[3] * cx + p[4] * cy + p[5] * cz + p[10];
            result.Origin[2] = p[6] * cx + p[7] * cy + p[8] * cz + p[11];

            var xyz = new float[n * 3];
            byte[] rgb = info.HasRgb ? new byte[n * 3] : null;
            double k = info.Scale * f; // unità intera -> piedi
            double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
            double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;

            int w = 0;
            int R = info.RecordSize;
            using (var fs = new FileStream(Path.Combine(folder, CloudInfo.PointsName), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                byte[] buf = new byte[0];
                for (int i = 0; i < tiles.Length; i++)
                {
                    int cnt = take[i];
                    if (cnt == 0) continue;
                    if (buf.Length < cnt * R) buf = new byte[cnt * R];
                    fs.Position = tiles[i].FirstRecord * R;
                    int got = ReadFully(fs, buf, cnt * R) / R;

                    for (int j = 0; j < got; j++)
                    {
                        int o = j * R;
                        double lx = BitConverter.ToInt32(buf, o) * k;
                        double ly = BitConverter.ToInt32(buf, o + 4) * k;
                        double lz = BitConverter.ToInt32(buf, o + 8) * k;
                        double rx = p[0] * lx + p[1] * ly + p[2] * lz;
                        double ry = p[3] * lx + p[4] * ly + p[5] * lz;
                        double rz = p[6] * lx + p[7] * ly + p[8] * lz;
                        xyz[w * 3] = (float)rx; xyz[w * 3 + 1] = (float)ry; xyz[w * 3 + 2] = (float)rz;

                        double ax = rx + result.Origin[0], ay = ry + result.Origin[1], az = rz + result.Origin[2];
                        if (ax < mnx) mnx = ax; if (ax > mxx) mxx = ax;
                        if (ay < mny) mny = ay; if (ay > mxy) mxy = ay;
                        if (az < mnz) mnz = az; if (az > mxz) mxz = az;

                        if (rgb != null)
                        {
                            rgb[w * 3] = buf[o + 12]; rgb[w * 3 + 1] = buf[o + 13]; rgb[w * 3 + 2] = buf[o + 14];
                        }
                        w++;
                    }

                    if (progress != null && (i & 31) == 0)
                        progress(string.Format(L.T("Caricamento nuvola... {0:P0}"), (double)i / tiles.Length));
                }
            }

            if (w == 0) throw new InvalidDataException(L.T("Nessun punto letto dalla nuvola."));
            result.Xyz = xyz;
            result.Rgb = rgb;
            result.Count = w;
            result.Min[0] = mnx; result.Min[1] = mny; result.Min[2] = mnz;
            result.Max[0] = mxx; result.Max[1] = mxy; result.Max[2] = mxz;
            return result;
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
