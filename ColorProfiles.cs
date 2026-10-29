using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace PointDeviation
{
    /// <summary>Tipo di profilo colore.</summary>
    internal enum ProfileKind
    {
        /// <summary>Sfumatura continua tra soglie con un colore ciascuna.</summary>
        Gradiente = 0,
        /// <summary>Tolleranza a fasce: entro tolleranza / avviso / fuori tolleranza.</summary>
        Fasce = 1,
        /// <summary>Classi libere: intervalli con colore ed etichetta (es. livelli di accuratezza).</summary>
        Classi = 2,
        /// <summary>Completezza: evidenzia i punti più lontani della soglia da qualunque superficie (parti non modellate).</summary>
        Completezza = 3,
        /// <summary>Interferenze: evidenzia i punti più vicini della soglia e quelli che compenetrano il modello.</summary>
        Interferenze = 4
    }

    /// <summary>Esito della valutazione di un punto.</summary>
    internal enum PointState { Visibile = 0, SenzaModello = 1, Nascosto = 2 }

    [DataContract]
    internal sealed class ColorStop
    {
        [DataMember] public double ValueMm;
        [DataMember] public int Argb;

        public ColorStop() { }
        public ColorStop(double v, Color c) { ValueMm = v; Argb = c.ToArgb(); }
        public Color Color { get { return Color.FromArgb(Argb); } set { Argb = value.ToArgb(); } }
    }

    [DataContract]
    internal sealed class ColorClass
    {
        [DataMember] public double FromMm;
        [DataMember] public double ToMm;
        [DataMember] public int Argb;
        [DataMember] public string Label;

        public ColorClass() { }
        public ColorClass(double from, double to, Color c, string label) { FromMm = from; ToMm = to; Argb = c.ToArgb(); Label = label; }
        public Color Color { get { return Color.FromArgb(Argb); } set { Argb = value.ToArgb(); } }
    }

    /// <summary>
    /// Profilo colore: trasforma una distanza (mm, con segno) in un colore e definisce la legenda.
    /// L'intervallo del profilo (da MinMm a MaxMm) è anche il raggio di ricerca del modello:
    /// i punti senza superfici di modello entro quell'intervallo sono "nuvola senza modello".
    /// </summary>
    [DataContract]
    internal sealed class ColorProfile
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Name = "Nuovo profilo";
        [DataMember] public ProfileKind Kind;
        /// <summary>Usa il valore assoluto della distanza (planarità, accuratezza): il segno non conta.</summary>
        [DataMember] public bool Absolute;
        /// <summary>Gradiente a specchio: le soglie positive valgono anche sotto lo zero; la legenda va da −max a +max.</summary>
        [DataMember] public bool Mirror;
        /// <summary>
        /// Nasconde i punti senza modello entro l'intervallo (vista e legenda). Utile quando le parti non modellate
        /// si controllano con un profilo di completezza. (Negato perché i file vecchi, senza il campo, valgano «mostra».)
        /// </summary>
        [DataMember] public bool HideMissing;
        /// <summary>Fasce: i valori negativi sono l'opposto di quelli positivi.</summary>
        [DataMember] public bool Symmetric;

        // Completezza / interferenze
        [DataMember] public double ThresholdMm = 150;
        [DataMember] public int HighlightArgb = Color.FromArgb(235, 110, 90).ToArgb();
        [DataMember] public int InsideArgb = Color.FromArgb(150, 0, 40).ToArgb();
        /// <summary>Gli altri punti: in grigio (true) o nascosti (false).</summary>
        [DataMember] public bool ShowOthers = true;

        // Gradiente
        [DataMember] public List<ColorStop> Stops = new List<ColorStop>();

        // Fasce
        [DataMember] public double BandMinMm = -150, WarnMinMm = -50, PassMinMm = -20, PassMaxMm = 20, WarnMaxMm = 50, BandMaxMm = 150;
        [DataMember] public bool UseWarn = true;
        [DataMember] public int PassArgb = Color.FromArgb(0, 150, 60).ToArgb();
        [DataMember] public int WarnArgb = Color.FromArgb(250, 210, 0).ToArgb();
        [DataMember] public int FailArgb = Color.FromArgb(220, 30, 30).ToArgb();

        // Classi
        [DataMember] public List<ColorClass> Classes = new List<ColorClass>();

        public ColorProfile Clone()
        {
            using (var ms = new MemoryStream())
            {
                var ser = new DataContractJsonSerializer(typeof(ColorProfile));
                ser.WriteObject(ms, this);
                ms.Position = 0;
                return (ColorProfile)ser.ReadObject(ms);
            }
        }

        // ------------------------------------------------------------------ intervallo

        /// <summary>Valore minimo e massimo rappresentati (mm). Con Absolute il minimo è 0.</summary>
        public void GetRange(out double min, out double max)
        {
            switch (Kind)
            {
                case ProfileKind.Completezza:
                case ProfileKind.Interferenze:
                    min = 0; max = Math.Max(1.0, ThresholdMm);
                    return;
                case ProfileKind.Fasce:
                    min = BandMinMm; max = BandMaxMm; break;
                case ProfileKind.Classi:
                    if (Classes.Count == 0) { min = 0; max = 1; break; }
                    min = Classes.Min(c => Math.Min(c.FromMm, c.ToMm));
                    max = Classes.Max(c => Math.Max(c.FromMm, c.ToMm));
                    break;
                default:
                    if (Stops.Count == 0) { min = 0; max = 1; break; }
                    min = Stops.Min(s => s.ValueMm);
                    max = Stops.Max(s => s.ValueMm);
                    if (Mirror)
                    {
                        max = Math.Max(Math.Abs(min), Math.Abs(max));
                        min = -max;
                        return;
                    }
                    break;
            }
            if (Absolute) { min = Math.Max(0.0, min); max = Math.Max(Math.Abs(min), Math.Abs(max)); }
            if (max <= min) max = min + 1.0;
        }

        /// <summary>Raggio di ricerca del modello (mm): la distanza massima rappresentata dal profilo.</summary>
        public double SearchRangeMm
        {
            get
            {
                double mn, mx;
                GetRange(out mn, out mx);
                return Math.Max(1.0, Math.Max(Math.Abs(mn), Math.Abs(mx)));
            }
        }

        // ------------------------------------------------------------------ colore di una distanza

        /// <summary>
        /// Colore di una distanza in mm. Restituisce false se la distanza è fuori dall'intervallo del profilo
        /// (quel punto va trattato come "nuvola senza modello").
        /// </summary>
        public bool TryColor(double mm, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (double.IsNaN(mm)) return false;
            if (Kind == ProfileKind.Completezza || Kind == ProfileKind.Interferenze)
                return Evaluate(mm, out r, out g, out b) == PointState.Visibile;

            double v = Absolute ? Math.Abs(mm) : mm;
            double mn, mx;
            GetRange(out mn, out mx);
            const double eps = 1e-9;
            if (v < mn - eps || v > mx + eps) return false;
            if (Mirror && Kind == ProfileKind.Gradiente) v = Math.Abs(v);

            switch (Kind)
            {
                case ProfileKind.Fasce:
                {
                    Color c;
                    if (v >= PassMinMm && v <= PassMaxMm) c = Color.FromArgb(PassArgb);
                    else if (UseWarn && v >= WarnMinMm && v <= WarnMaxMm) c = Color.FromArgb(WarnArgb);
                    else c = Color.FromArgb(FailArgb);
                    r = c.R; g = c.G; b = c.B;
                    return true;
                }

                case ProfileKind.Classi:
                {
                    foreach (ColorClass k in Classes)
                    {
                        double a = Math.Min(k.FromMm, k.ToMm), z = Math.Max(k.FromMm, k.ToMm);
                        if (v >= a - eps && v <= z + eps)
                        {
                            Color c = k.Color;
                            r = c.R; g = c.G; b = c.B;
                            return true;
                        }
                    }
                    return false; // buco tra le classi
                }

                default:
                {
                    if (Stops.Count == 0) return false;
                    List<ColorStop> st = SortedStops();
                    if (st.Count == 1 || v <= st[0].ValueMm) { Set(st[0].Color, out r, out g, out b); return true; }
                    for (int i = 1; i < st.Count; i++)
                    {
                        if (v <= st[i].ValueMm)
                        {
                            double span = st[i].ValueMm - st[i - 1].ValueMm;
                            double t = span <= 0 ? 1.0 : (v - st[i - 1].ValueMm) / span;
                            Color c0 = st[i - 1].Color, c1 = st[i].Color;
                            r = Lerp(c0.R, c1.R, t); g = Lerp(c0.G, c1.G, t); b = Lerp(c0.B, c1.B, t);
                            return true;
                        }
                    }
                    Set(st[st.Count - 1].Color, out r, out g, out b);
                    return true;
                }
            }
        }

        /// <summary>
        /// Valutazione completa di un punto. mm = distanza con segno dalla superficie più vicina,
        /// NaN se nessuna superficie entro il raggio di ricerca.
        /// </summary>
        public PointState Evaluate(double mm, out byte r, out byte g, out byte b)
        {
            r = g = b = 150;
            switch (Kind)
            {
                case ProfileKind.Completezza:
                    if (double.IsNaN(mm) || Math.Abs(mm) > ThresholdMm)
                    {
                        Set(Color.FromArgb(HighlightArgb), out r, out g, out b);
                        return PointState.Visibile;
                    }
                    return ShowOthers ? PointState.Visibile : PointState.Nascosto;

                case ProfileKind.Interferenze:
                    if (!double.IsNaN(mm) && mm < 0 && Math.Abs(mm) <= ThresholdMm)
                    {
                        Set(Color.FromArgb(InsideArgb), out r, out g, out b);
                        return PointState.Visibile;
                    }
                    if (!double.IsNaN(mm) && Math.Abs(mm) <= ThresholdMm)
                    {
                        Set(Color.FromArgb(HighlightArgb), out r, out g, out b);
                        return PointState.Visibile;
                    }
                    return ShowOthers ? PointState.Visibile : PointState.Nascosto;

                default:
                    return TryColor(mm, out r, out g, out b) ? PointState.Visibile : PointState.SenzaModello;
            }
        }

        /// <summary>Profili che non usano il concetto di «nuvola senza modello».</summary>
        public bool IsSelection { get { return Kind == ProfileKind.Completezza || Kind == ProfileKind.Interferenze; } }

        private List<ColorStop> _sorted;
        private int _sortedHash;

        private List<ColorStop> SortedStops()
        {
            int h = Stops.Count;
            foreach (ColorStop s in Stops) h = h * 31 + s.ValueMm.GetHashCode() * 7 + s.Argb;
            if (_sorted == null || h != _sortedHash)
            {
                _sorted = Stops.OrderBy(s => s.ValueMm).ToList();
                _sortedHash = h;
            }
            return _sorted;
        }

        private static void Set(Color c, out byte r, out byte g, out byte b) { r = c.R; g = c.G; b = c.B; }

        private static byte Lerp(byte a, byte b, double t)
        {
            return (byte)Math.Round(a + (b - a) * Math.Max(0.0, Math.Min(1.0, t)));
        }

        // ------------------------------------------------------------------ legenda e statistiche

        /// <summary>Valori da segnare con una tacca sulla legenda.</summary>
        public List<double> Ticks()
        {
            var t = new List<double>();
            switch (Kind)
            {
                case ProfileKind.Fasce:
                    t.AddRange(new[] { BandMinMm, PassMinMm, PassMaxMm, BandMaxMm });
                    if (UseWarn) { t.Add(WarnMinMm); t.Add(WarnMaxMm); }
                    break;
                case ProfileKind.Classi:
                    foreach (ColorClass c in Classes) { t.Add(c.FromMm); t.Add(c.ToMm); }
                    break;
                case ProfileKind.Completezza:
                case ProfileKind.Interferenze:
                    t.Add(0); t.Add(ThresholdMm);
                    break;
                default:
                    foreach (ColorStop s in Stops)
                    {
                        t.Add(s.ValueMm);
                        if (Mirror) t.Add(-s.ValueMm);
                    }
                    break;
            }
            double mn, mx;
            GetRange(out mn, out mx);
            return t.Where(v => v >= mn - 1e-9 && v <= mx + 1e-9).Distinct().OrderBy(v => v).ToList();
        }

        /// <summary>Classi per le statistiche (vuoto per il gradiente).</summary>
        public List<ColorClass> StatClasses()
        {
            switch (Kind)
            {
                case ProfileKind.Fasce:
                {
                    var l = new List<ColorClass>();
                    l.Add(new ColorClass(PassMinMm, PassMaxMm, Color.FromArgb(PassArgb), L.T("Entro tolleranza")));
                    if (UseWarn)
                    {
                        l.Add(new ColorClass(WarnMinMm, WarnMaxMm, Color.FromArgb(WarnArgb), L.T("Avviso")));
                    }
                    l.Add(new ColorClass(BandMinMm, BandMaxMm, Color.FromArgb(FailArgb), L.T("Fuori tolleranza")));
                    return l;
                }
                case ProfileKind.Classi:
                    return Classes;
                default:
                    return new List<ColorClass>();
            }
        }

        public string Description
        {
            get
            {
                double mn, mx;
                GetRange(out mn, out mx);
                if (Kind == ProfileKind.Completezza)
                    return string.Format(L.T("completezza: punti a più di {0:0.#} mm dal modello"), ThresholdMm);
                if (Kind == ProfileKind.Interferenze)
                    return string.Format(L.T("interferenze: punti entro {0:0.#} mm o dentro il modello"), ThresholdMm);
                string kind = Kind == ProfileKind.Fasce ? L.T("fasce") : Kind == ProfileKind.Classi ? L.T("classi") : Mirror ? L.T("gradiente a specchio") : L.T("gradiente");
                return string.Format("{0}, da {1:0.#} a {2:0.#} mm{3}", kind, mn, mx, Absolute ? L.T(" (valore assoluto)") : "");
            }
        }

        // ------------------------------------------------------------------ profili predefiniti

        public static List<ColorProfile> Defaults()
        {
            return new List<ColorProfile>
            {
                new ColorProfile
                {
                    Id = "def-discrepanza", Name = L.T("Discrepanza (−100 / +100 mm)"), Kind = ProfileKind.Gradiente,
                    Stops = new List<ColorStop>
                    {
                        new ColorStop(-100, Color.FromArgb(20, 40, 230)),
                        new ColorStop(-55, Color.FromArgb(0, 200, 230)),
                        new ColorStop(-10, Color.FromArgb(0, 190, 60)),
                        new ColorStop(10, Color.FromArgb(0, 190, 60)),
                        new ColorStop(55, Color.FromArgb(255, 220, 0)),
                        new ColorStop(100, Color.FromArgb(230, 20, 20))
                    }
                },
                new ColorProfile
                {
                    Id = "def-planarita", Name = L.T("Planarità (0 – 150 mm)"), Kind = ProfileKind.Gradiente, Absolute = true,
                    Stops = new List<ColorStop>
                    {
                        new ColorStop(0, Color.FromArgb(25, 15, 40)),
                        new ColorStop(15, Color.FromArgb(60, 40, 200)),
                        new ColorStop(45, Color.FromArgb(0, 190, 230)),
                        new ColorStop(75, Color.FromArgb(40, 200, 70)),
                        new ColorStop(105, Color.FromArgb(250, 220, 0)),
                        new ColorStop(130, Color.FromArgb(240, 120, 0)),
                        new ColorStop(150, Color.FromArgb(150, 0, 0))
                    }
                },
                new ColorProfile
                {
                    Id = "def-tolleranza", Name = L.T("Tolleranza ±20 / ±50 mm"), Kind = ProfileKind.Fasce,
                    BandMinMm = -150, WarnMinMm = -50, PassMinMm = -20, PassMaxMm = 20, WarnMaxMm = 50, BandMaxMm = 150, UseWarn = true
                },
                new ColorProfile
                {
                    Id = "def-loa", Name = L.T("Livello di accuratezza (LOA)"), Kind = ProfileKind.Classi, Absolute = true,
                    Classes = new List<ColorClass>
                    {
                        new ColorClass(0, 1, Color.FromArgb(0, 110, 50), L.T("LOA50  0–1 mm")),
                        new ColorClass(1, 5, Color.FromArgb(60, 180, 60), L.T("LOA40  1–5 mm")),
                        new ColorClass(5, 15, Color.FromArgb(200, 220, 0), L.T("LOA30  5–15 mm")),
                        new ColorClass(15, 50, Color.FromArgb(250, 140, 0), L.T("LOA20  15–50 mm")),
                        new ColorClass(50, 150, Color.FromArgb(220, 30, 30), L.T("LOA10  > 50 mm"))
                    }
                },
                new ColorProfile
                {
                    Id = "def-specchio", Name = L.T("Discrepanza a specchio (±150 mm)"), Kind = ProfileKind.Gradiente, Mirror = true,
                    Stops = new List<ColorStop>
                    {
                        new ColorStop(0, Color.FromArgb(25, 15, 40)),
                        new ColorStop(15, Color.FromArgb(60, 40, 200)),
                        new ColorStop(45, Color.FromArgb(0, 190, 230)),
                        new ColorStop(75, Color.FromArgb(40, 200, 70)),
                        new ColorStop(105, Color.FromArgb(250, 220, 0)),
                        new ColorStop(130, Color.FromArgb(240, 120, 0)),
                        new ColorStop(150, Color.FromArgb(150, 0, 0))
                    }
                },
                new ColorProfile
                {
                    Id = "def-completezza", Name = L.T("Completezza (150 mm)"), Kind = ProfileKind.Completezza, ThresholdMm = 150
                },
                new ColorProfile
                {
                    Id = "def-interferenze", Name = L.T("Interferenze (150 mm)"), Kind = ProfileKind.Interferenze, ThresholdMm = 150
                }
            };
        }

        public static ColorProfile NewOfKind(ProfileKind kind)
        {
            ColorProfile template = Defaults().First(p => p.Kind == kind).Clone();
            template.Id = Guid.NewGuid().ToString("N");
            switch (kind)
            {
                case ProfileKind.Fasce: template.Name = L.T("Nuova tolleranza"); break;
                case ProfileKind.Classi: template.Name = L.T("Nuove classi"); break;
                case ProfileKind.Completezza: template.Name = L.T("Nuova verifica di completezza"); break;
                case ProfileKind.Interferenze: template.Name = L.T("Nuova verifica di interferenze"); break;
                default: template.Name = L.T("Nuovo gradiente"); break;
            }
            return template;
        }
    }

    // =====================================================================================
    //  Archivio dei profili (globale, %AppData%\PointDeviation\profiles.json)
    // =====================================================================================

    [DataContract]
    internal sealed class ProfileFile
    {
        [DataMember] public List<ColorProfile> Profiles = new List<ColorProfile>();
        /// <summary>Predefiniti già proposti: quelli nuovi delle versioni successive vengono aggiunti una volta sola.</summary>
        [DataMember] public List<string> KnownDefaults = new List<string>();
    }

    internal static class ProfileStore
    {
        private static List<ColorProfile> _cache;
        private static List<string> _known = new List<string>();

        private static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PointDeviation", "profiles.json");
            }
        }

        public static List<ColorProfile> All
        {
            get
            {
                if (_cache == null) _cache = Load();
                return _cache;
            }
        }

        public const string DefaultId = "def-discrepanza";

        public static ColorProfile Find(string id)
        {
            ColorProfile p = All.FirstOrDefault(x => x.Id == id);
            return p ?? All.FirstOrDefault(x => x.Id == DefaultId) ?? All.FirstOrDefault() ?? ColorProfile.Defaults()[0];
        }

        private static List<ColorProfile> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (FileStream fs = File.OpenRead(FilePath))
                    {
                        var f = (ProfileFile)new DataContractJsonSerializer(typeof(ProfileFile)).ReadObject(fs);
                        if (f != null && f.Profiles != null && f.Profiles.Count > 0)
                        {
                            foreach (ColorProfile p in f.Profiles)
                            {
                                if (p.Stops == null) p.Stops = new List<ColorStop>();
                                if (p.Classes == null) p.Classes = new List<ColorClass>();
                            }
                            _known = f.KnownDefaults ?? f.Profiles.Where(p => p.Id.StartsWith("def-")).Select(p => p.Id).ToList();
                            bool added = false;
                            foreach (ColorProfile d in ColorProfile.Defaults())
                            {
                                if (_known.Contains(d.Id)) continue;
                                if (!f.Profiles.Any(p => p.Id == d.Id)) f.Profiles.Add(d);
                                _known.Add(d.Id);
                                added = true;
                            }
                            if (added) { _cache = f.Profiles; Save(); }
                            return f.Profiles;
                        }
                    }
                }
            }
            catch { }
            List<ColorProfile> defs = ColorProfile.Defaults();
            _known = defs.Select(p => p.Id).ToList();
            return defs;
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                using (FileStream fs = File.Create(FilePath))
                    new DataContractJsonSerializer(typeof(ProfileFile)).WriteObject(fs, new ProfileFile { Profiles = All, KnownDefaults = _known });
            }
            catch { }
        }

        /// <summary>Aggiunge o sostituisce un profilo (per Id) e salva.</summary>
        public static void Put(ColorProfile p)
        {
            int i = All.FindIndex(x => x.Id == p.Id);
            if (i >= 0) All[i] = p; else All.Add(p);
            Save();
        }

        public static void Remove(string id)
        {
            All.RemoveAll(x => x.Id == id);
            if (All.Count == 0) All.AddRange(ColorProfile.Defaults());
            Save();
        }

        public static void RestoreDefaults()
        {
            foreach (ColorProfile d in ColorProfile.Defaults())
            {
                int i = All.FindIndex(x => x.Id == d.Id);
                if (i >= 0) All[i] = d; else All.Add(d);
            }
            Save();
        }
    }
}
