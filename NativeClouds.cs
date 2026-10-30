using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;

namespace PointDeviation
{
    // =====================================================================================
    //  Nuvole native di Revit.
    //
    //  Il plugin registra un motore di nuvole ("pdcl"). Ogni nuvola della libreria ha un file
    //  <nome>.pdcl (vista di base, RGB) e, per le analisi, file-variante in <cartella>\varianti\.
    //  Un file .pdcl descrive COME colorare la nuvola (modalità, profilo, recinti, posizione):
    //  Revit crea un PointCloudType da quel file e chiede i punti al motore, che li legge dalla
    //  libreria e li colora al volo. Per cambiare colori o recinti si crea una nuova variante e
    //  si sostituisce il tipo dell'istanza: Revit rilegge i punti.
    // =====================================================================================

    /// <summary>Contenuto di un file .pdcl.</summary>
    [DataContract]
    internal sealed class CloudView
    {
        public const string Extension = ".pdcl";
        public const string VariantsFolder = "varianti";

        [DataMember] public int FormatVersion = 1;
        [DataMember] public string Label;            // nome mostrato in Gestisci collegamenti
        [DataMember] public int Display;             // non più usato: i colori li gestisce il plugin
        [DataMember] public double[] Transform;      // non più usato: la posizione è quella dell'istanza

        public static CloudView Load(string path)
        {
            using (FileStream fs = File.OpenRead(path))
                return (CloudView)new DataContractJsonSerializer(typeof(CloudView)).ReadObject(fs);
        }

        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(CloudView)).WriteObject(ms, this);
                return ms.ToArray();
            }
        }

        /// <summary>Cartella .pdcloud a cui si riferisce un file .pdcl (la sua cartella o quella superiore per le varianti).</summary>
        public static string CloudFolderOf(string pdclPath)
        {
            string dir = Path.GetDirectoryName(pdclPath);
            if (CloudInfo.IsCloudFolder(dir)) return dir;
            string up = Path.GetDirectoryName(dir);
            return CloudInfo.IsCloudFolder(up) ? up : dir;
        }

        /// <summary>File di base (RGB) di una nuvola della libreria; lo crea se manca (nuvole convertite prima di questa versione).</summary>
        public static string EnsureBase(string folder, CloudInfo info)
        {
            string path = Path.Combine(folder, CloudConverter.SafeName(info.Name) + Extension);
            if (!File.Exists(path))
            {
                var v = new CloudView { Label = info.Name, Display = (int)DisplayMode.Rgb, Transform = Placement.Identity() };
                File.WriteAllBytes(path, v.ToBytes());
            }
            return path;
        }

    }

    /// <summary>Dati di analisi di un progetto visibili al motore (che lavora anche su thread di Revit diversi dal principale).</summary>
    internal sealed class AnalysisSnapshot
    {
        public TriangleBvh Bvh;
        public double SearchFt;
        public int Version;
        public ulong Signature;   // impronta della geometria (TriangleBvh.Signature), per le distanze salvate su disco
    }

    internal static class AnalysisRegistry
    {
        private static readonly ConcurrentDictionary<string, AnalysisSnapshot> _map = new ConcurrentDictionary<string, AnalysisSnapshot>();

        public static void Set(string docKey, AnalysisSnapshot s) { if (docKey != null) _map[docKey] = s; }
        public static void Remove(string docKey) { AnalysisSnapshot x; if (docKey != null) _map.TryRemove(docKey, out x); }
        public static AnalysisSnapshot Get(string docKey)
        {
            AnalysisSnapshot s;
            return docKey != null && _map.TryGetValue(docKey, out s) ? s : null;
        }
    }

    // =====================================================================================
    //  Motore
    // =====================================================================================

    internal static class NativeClouds
    {
        public const string EngineId = "pdcl";
        private const double FtPerM = 1.0 / 0.3048;

        public static void Register()
        {
            // file-based: le nuvole compaiono in Gestisci collegamenti e il formato nel comando Inserisci > Nuvola di punti
            PointCloudEngineRegistry.RegisterPointCloudEngine(EngineId, new PdEngine(), true);
        }

        public static Transform ToTransform(double[] p)
        {
            Transform t = Transform.Identity;
            t.BasisX = new XYZ(p[0], p[3], p[6]);
            t.BasisY = new XYZ(p[1], p[4], p[7]);
            t.BasisZ = new XYZ(p[2], p[5], p[8]);
            t.Origin = new XYZ(p[9], p[10], p[11]);
            return t;
        }

        /// <summary>
        /// Trasformazione dell'istanza Revit a partire dal posizionamento della nuvola. I punti che il motore
        /// consegna sono relativi al centro della nuvola (numeri piccoli, precisi in float): lo spostamento del
        /// centro entra qui, nell'origine dell'istanza. Revit non applica GetOffset ai punti, quindi l'offset
        /// dichiarato al motore è zero e tutto passa da questa trasformazione.
        /// </summary>
        public static double[] InstanceFromPlacement(double[] p, CloudInfo info)
        {
            double cx = info.CenterM[0] * FtPerM, cy = info.CenterM[1] * FtPerM, cz = info.CenterM[2] * FtPerM;
            var r = (double[])p.Clone();
            r[9] = p[0] * cx + p[1] * cy + p[2] * cz + p[9];
            r[10] = p[3] * cx + p[4] * cy + p[5] * cz + p[10];
            r[11] = p[6] * cx + p[7] * cy + p[8] * cz + p[11];
            return r;
        }

        /// <summary>Operazione inversa: dal transform dell'istanza (anche spostata in Revit) al posizionamento.</summary>
        public static double[] PlacementFromInstance(double[] t, CloudInfo info)
        {
            double cx = info.CenterM[0] * FtPerM, cy = info.CenterM[1] * FtPerM, cz = info.CenterM[2] * FtPerM;
            var r = (double[])t.Clone();
            r[9] = t[9] - (t[0] * cx + t[1] * cy + t[2] * cz);
            r[10] = t[10] - (t[3] * cx + t[4] * cy + t[5] * cz);
            r[11] = t[11] - (t[6] * cx + t[7] * cy + t[8] * cz);
            return r;
        }

        public static double[] FromTransform(Transform t)
        {
            return new[]
            {
                t.BasisX.X, t.BasisY.X, t.BasisZ.X,
                t.BasisX.Y, t.BasisY.Y, t.BasisZ.Y,
                t.BasisX.Z, t.BasisY.Z, t.BasisZ.Z,
                t.Origin.X, t.Origin.Y, t.Origin.Z
            };
        }

        /// <summary>Tipo di nuvola del nostro motore: per identificatore del motore oppure per estensione del file.</summary>
        public static bool IsOurs(PointCloudType t)
        {
            if (t == null) return false;
            try
            {
                string id = t.EngineIdentifier ?? "";
                if (id.Equals(EngineId, StringComparison.OrdinalIgnoreCase) || id.Equals("." + EngineId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            string path = PathOf(t);
            return path != null && path.EndsWith(CloudView.Extension, StringComparison.OrdinalIgnoreCase);
        }

        public static string PathOf(PointCloudType t)
        {
            // GetPath restituisce un ModelPath: si converte nel percorso leggibile del file .pdcl
            try { return ModelPathUtils.ConvertModelPathToUserVisiblePath(t.GetPath()); } catch { return null; }
        }

        /// <summary>Istanze di nuvole del nostro motore presenti nel progetto.</summary>
        public static List<PointCloudInstance> FindInstances(Document doc)
        {
            var res = new List<PointCloudInstance>();
            try
            {
                foreach (PointCloudInstance i in new FilteredElementCollector(doc).OfClass(typeof(PointCloudInstance)))
                    if (IsOurs(doc.GetElement(i.GetTypeId()) as PointCloudType)) res.Add(i);
            }
            catch { }
            return res;
        }

        // ------------------------------------------------------------------ caricata / scaricata

        private static readonly ConcurrentDictionary<string, int> _loaded = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static string Key(string folder)
        {
            try { return Path.GetFullPath(folder).TrimEnd('\\'); } catch { return folder ?? ""; }
        }

        /// <summary>
        /// La nuvola è caricata in Revit? Revit crea l'accesso del motore quando la nuvola è caricata e lo libera
        /// quando la si scarica da Gestisci collegamenti: il disegno del plugin segue lo stesso stato.
        /// Finché Revit non ha ancora creato nessun accesso (progetto appena aperto) la si considera caricata.
        /// </summary>
        public static bool IsLoaded(string folder)
        {
            int n;
            return !_loaded.TryGetValue(Key(folder), out n) || n > 0;
        }

        private static void Loaded(string folder, int delta)
        {
            _loaded.AddOrUpdate(Key(folder), Math.Max(0, delta), (k, v) => Math.Max(0, v + delta));
        }

        private sealed class PdEngine : IPointCloudEngine
        {
            public IPointCloudAccess CreatePointCloudAccess(string identifier)
            {
                try { var a = new PdAccess(identifier); Log.Write("Motore", "nuvola caricata: " + identifier); return a; }
                catch (Exception ex) { Log.Error("Motore " + identifier, ex); return null; }
            }

            public void Free() { }
        }

        /// <summary>
        /// Accesso a una nuvola per Revit. Il motore consegna solo il contorno dell'ingombro (punti radi lungo gli
        /// spigoli): basta perché Revit mostri, selezioni e gestisca la nuvola come elemento. I punti veri li disegna
        /// il plugin (CloudLayer / HybridServer), con dimensione, colori e trasparenza che Revit non permette.
        /// </summary>
        internal sealed class PdAccess : IPointCloudAccess
        {
            private readonly string _name, _folder;
            private readonly Outline _extent;
            private readonly CloudPoint[] _outline;
            private bool _freed;

            public PdAccess(string identifier)
            {
                CloudView v = CloudView.Load(identifier);
                _folder = CloudView.CloudFolderOf(identifier);
                CloudInfo info = CloudInfo.Load(_folder);
                TileEntry[] tiles = TileEntry.Read(Path.Combine(_folder, CloudInfo.IndexName));
                _name = v.Label ?? info.Name;

                float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
                foreach (TileEntry t in tiles)
                {
                    mnx = Math.Min(mnx, t.MinX); mny = Math.Min(mny, t.MinY); mnz = Math.Min(mnz, t.MinZ);
                    mxx = Math.Max(mxx, t.MaxX); mxy = Math.Max(mxy, t.MaxY); mxz = Math.Max(mxz, t.MaxZ);
                }
                if (tiles.Length == 0) { mnx = mny = mnz = 0; mxx = mxy = mxz = 1; }
                double f = FtPerM;
                _extent = new Outline(new XYZ(mnx * f, mny * f, mnz * f), new XYZ(mxx * f, mxy * f, mxz * f));

                // nessun punto per Revit: i punti li disegna il plugin (niente contorno tratteggiato)
                _outline = new CloudPoint[0];
                Loaded(_folder, +1);
            }

            public string GetName() { return _name; }
            public PointCloudColorEncoding GetColorEncoding() { return PointCloudColorEncoding.ARGB; }
            public Outline GetExtent() { return _extent; }
            public XYZ GetOffset() { return XYZ.Zero; } // il centro della nuvola è nella trasformazione dell'istanza
            public double GetUnitsToFeetConversionFactor() { return 1.0; }
            public void SetUserData(int userData) { }

            public void Free()
            {
                if (_freed) return;
                _freed = true;
                Log.Write("Motore", "nuvola scaricata: " + _folder);
                Loaded(_folder, -1);
                Redraw.Request();
            }

            public IPointSetIterator CreatePointSetIterator(PointCloudFilter rFilter, ElementId viewId)
            {
                return new OutlineIterator(_outline, rFilter);
            }

            public IPointSetIterator CreatePointSetIterator(PointCloudFilter rFilter, double density, ElementId viewId)
            {
                return new OutlineIterator(_outline, rFilter);
            }

            public int ReadPoints(PointCloudFilter rFilter, ElementId viewId, IntPtr buffer, int nBufferSize)
            {
                try { return new OutlineIterator(_outline, rFilter).ReadPoints(buffer, nBufferSize); }
                catch { return 0; }
            }
        }

        /// <summary>Restituisce i punti del contorno che passano il filtro di Revit (vista, section box).</summary>
        private sealed class OutlineIterator : IPointSetIterator
        {
            private readonly CloudPoint[] _pts;
            private readonly PointCloudFilter _filter;
            private int _pos;

            public OutlineIterator(CloudPoint[] pts, PointCloudFilter filter)
            {
                _pts = pts;
                _filter = filter;
            }

            public unsafe int ReadPoints(IntPtr buffer, int bufferSize)
            {
                int written = 0;
                try
                {
                    if (buffer == IntPtr.Zero || bufferSize <= 0) return 0;
                    CloudPoint* dst = (CloudPoint*)buffer.ToPointer();
                    while (written < bufferSize && _pos < _pts.Length)
                    {
                        CloudPoint p = _pts[_pos++];
                        if (_filter != null && !_filter.TestPoint(p)) continue;
                        dst[written++] = p;
                    }
                }
                catch { }
                return written;
            }

            public void Free() { }
        }
    }
}
