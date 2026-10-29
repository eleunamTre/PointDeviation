using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace PointDeviation
{
    internal enum Job { None = 0, Recolor = 1, Recompute = 2, Reload = 3, Clear = 4, Insert = 5 }

    /// <summary>Come colorare la nuvola. Discrepanze = analisi (con il profilo colore); le altre sono visualizzazioni.</summary>
    internal enum DisplayMode { Discrepanze = 0, Rgb = 1, Quota = 2, Normali = 3, Nuvola = 4, XRay = 5 }

    /// <summary>Quanti punti disegnare: Automatica si adatta, Media alleggerisce, Fine infittisce.</summary>
    internal enum Quality { Automatica = 0, Media = 1, Fine = 2 }

    /// <summary>Come trattare i punti della nuvola senza alcuna geometria di modello entro la scala massima.</summary>
    internal enum MissingMode { Evidenzia = 0, Grigio = 1, Nascondi = 2 }

    /// <summary>Impostazioni di un singolo progetto (ce n'è una copia per ogni documento aperto).</summary>
    internal sealed class DeviationSettings
    {
        /// <summary>Nuvola del progetto: cartella .pdcloud della libreria, oppure file .las caricato direttamente.</summary>
        public string LasPath = "";

        /// <summary>Posizionamento della nuvola della libreria (vedi Placement), fissato all'inserimento.</summary>
        public double[] CloudPlacement;
        /// <summary>Posizionamento dell'ultima nuvola inserita in questo progetto (per "origine sull'ultima inserita").</summary>
        public double[] LastPlacement;
        /// <summary>Modalità scelta per il prossimo inserimento.</summary>
        public PlacementMode PendingPlacement = PlacementMode.CentroSuCentro;
        public double UnitToMeters = 1.0;
        public double ShiftXm, ShiftYm, ShiftZm;
        public int MaxPoints = 500000;
        public bool SelectionOnly;
        public bool IncludeLinks;
        /// <summary>Profilo colore attivo per questo progetto (vedi ProfileStore).</summary>
        public string ProfileId = ProfileStore.DefaultId;
        public MissingMode Missing = MissingMode.Evidenzia;
        public bool InvertSign;

        public DisplayMode Display = DisplayMode.Discrepanze;
        /// <summary>Ultima visualizzazione non di analisi (RGB o quota): ci si torna nascondendo l'analisi.</summary>
        public DisplayMode LastViewMode = DisplayMode.Rgb;
        public bool Visible = true;
        /// <summary>Recinti di ritaglio correnti (si sostituisce la lista, non la si modifica: le copie di lavoro la condividono).</summary>
        public List<ClipRegion> Clips = new List<ClipRegion>();
        /// <summary>Inverte il risultato dell'insieme dei recinti.</summary>
        public bool ClipInvert;
        /// <summary>Ritagli salvati con un nome.</summary>
        public List<ClipPreset> ClipPresets = new List<ClipPreset>();

        public bool ClipEnabled { get { return Clips != null && Clips.Exists(c => c.Enabled); } }

        // aspetto della nuvola
        public int Brightness;    // -100..100 (solo RGB e Quota)
        public int Contrast;      // -100..100 (solo RGB e Quota)
        public int Transparency;  // 0..90
        public int PointScale = 100;    // % della dimensione automatica dei punti; 0 = punti di un pixel
        public int XrayContrast = 30;   // 1..100: più alto = punti X-ray più opachi
        public Quality Quality = Quality.Automatica;
        /// <summary>Colore di ogni nuvola (per cartella) nella modalità «Nuvola».</summary>
        public Dictionary<string, int> CloudColors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public ColorProfile Profile { get { return ProfileStore.Find(ProfileId); } }

        /// <summary>Raggio di ricerca del modello: la distanza massima rappresentata dal profilo.</summary>
        public double MaxRangeMm { get { return Profile.SearchRangeMm; } }
        public double MaxRangeFt { get { return MaxRangeMm / 304.8; } }

        public DeviationSettings Clone() { return (DeviationSettings)MemberwiseClone(); }

        public void ResetAppearance()
        {
            Brightness = 0;
            Contrast = 0;
            Transparency = 0;
            PointScale = 100;
            XrayContrast = 30;
        }
    }
}
