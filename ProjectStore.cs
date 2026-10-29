using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace PointDeviation
{
    /// <summary>Impostazioni salvate per progetto (nessun dato di nuvola: solo parametri e percorso del LAS).</summary>
    [DataContract]
    internal sealed class StoredSettings
    {
        [DataMember] public string LasPath;
        [DataMember] public double UnitToMeters;
        [DataMember] public double ShiftXm;
        [DataMember] public double ShiftYm;
        [DataMember] public double ShiftZm;
        [DataMember] public int MaxPoints;
        [DataMember] public bool SelectionOnly;
        [DataMember] public bool IncludeLinks;
        [DataMember] public int Missing;
        [DataMember] public bool InvertSign;
        [DataMember] public int Display;
        [DataMember] public int Brightness;
        [DataMember] public int Contrast;
        [DataMember] public int Transparency;
        [DataMember] public double[] CloudPlacement;
        [DataMember] public string ProfileId;
        [DataMember] public int? PointScale;   // nullable: i file vecchi senza il campo non devono valere 0
        [DataMember] public int? XrayContrast;
        [DataMember] public int? Quality;
        [DataMember] public Dictionary<string, int> CloudColors;
        [DataMember] public int LastViewMode;
        [DataMember] public List<ClipRegion> Clips;
        [DataMember] public bool ClipInvert;
        [DataMember] public List<ClipPreset> ClipPresets;
        [DataMember] public double[] LastPlacement;

        public static StoredSettings From(DeviationSettings s)
        {
            return new StoredSettings
            {
                LasPath = s.LasPath,
                UnitToMeters = s.UnitToMeters,
                ShiftXm = s.ShiftXm,
                ShiftYm = s.ShiftYm,
                ShiftZm = s.ShiftZm,
                MaxPoints = s.MaxPoints,
                SelectionOnly = s.SelectionOnly,
                IncludeLinks = s.IncludeLinks,
                ProfileId = s.ProfileId,
                PointScale = s.PointScale,
                XrayContrast = s.XrayContrast,
                Quality = (int)s.Quality,
                CloudColors = s.CloudColors,
                LastViewMode = (int)s.LastViewMode,
                Clips = s.Clips,
                ClipInvert = s.ClipInvert,
                ClipPresets = s.ClipPresets,
                Missing = (int)s.Missing,
                InvertSign = s.InvertSign,
                Display = (int)s.Display,
                Brightness = s.Brightness,
                Contrast = s.Contrast,
                Transparency = s.Transparency,
                CloudPlacement = s.CloudPlacement,
                LastPlacement = s.LastPlacement
            };
        }

        /// <summary>Applica i valori letti dal file, ignorando quelli non validi.</summary>
        public void ApplyTo(DeviationSettings s)
        {
            if (LasPath != null) s.LasPath = LasPath;
            if (UnitToMeters > 0.0) s.UnitToMeters = UnitToMeters;
            s.ShiftXm = ShiftXm;
            s.ShiftYm = ShiftYm;
            s.ShiftZm = ShiftZm;
            if (MaxPoints > 0) s.MaxPoints = MaxPoints;
            s.SelectionOnly = SelectionOnly;
            s.IncludeLinks = IncludeLinks;
            if (!string.IsNullOrEmpty(ProfileId)) s.ProfileId = ProfileId;
            if (PointScale.HasValue && PointScale.Value >= 0 && PointScale.Value <= 400) s.PointScale = PointScale.Value;
            if (XrayContrast.HasValue && XrayContrast.Value >= 1 && XrayContrast.Value <= 100) s.XrayContrast = XrayContrast.Value;
            if (Quality.HasValue && Enum.IsDefined(typeof(PointDeviation.Quality), Quality.Value)) s.Quality = (PointDeviation.Quality)Quality.Value;
            if (CloudColors != null) s.CloudColors = new Dictionary<string, int>(CloudColors, StringComparer.OrdinalIgnoreCase);
            if (Enum.IsDefined(typeof(DisplayMode), LastViewMode) && LastViewMode != 0) s.LastViewMode = (DisplayMode)LastViewMode;
            if (Clips != null) s.Clips = Clips.FindAll(c => c != null && c.IsValid);
            s.ClipInvert = ClipInvert;
            if (ClipPresets != null) s.ClipPresets = ClipPresets.FindAll(c => c != null && c.Regions != null);
            if (Enum.IsDefined(typeof(MissingMode), Missing)) s.Missing = (MissingMode)Missing;
            s.InvertSign = InvertSign;
            if (Enum.IsDefined(typeof(DisplayMode), Display)) s.Display = (DisplayMode)Display;
            s.Brightness = Math.Max(-100, Math.Min(100, Brightness));
            s.Contrast = Math.Max(-100, Math.Min(100, Contrast));
            s.Transparency = Math.Max(0, Math.Min(100, Transparency));
            if (Placement.IsValid(CloudPlacement)) s.CloudPlacement = CloudPlacement;
            if (Placement.IsValid(LastPlacement)) s.LastPlacement = LastPlacement;
        }
    }

    [DataContract]
    internal sealed class StoredEntry
    {
        [DataMember] public string Key;
        [DataMember] public StoredSettings Settings;
    }

    [DataContract]
    internal sealed class StoredFile
    {
        [DataMember] public List<StoredEntry> Entries;
    }

    /// <summary>
    /// Archivio delle impostazioni per progetto in %AppData%\PointDeviation\projects.json.
    /// La chiave è il percorso completo del file .rvt (minuscolo); i progetti mai salvati non hanno chiave.
    /// </summary>
    internal static class ProjectStore
    {
        private static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PointDeviation", "projects.json");
            }
        }

        public static string KeyFor(string pathName)
        {
            if (string.IsNullOrWhiteSpace(pathName)) return null;
            try { return Path.GetFullPath(pathName).ToLowerInvariant(); }
            catch { return pathName.ToLowerInvariant(); }
        }

        private static StoredFile ReadFile()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (FileStream fs = File.OpenRead(FilePath))
                    {
                        var f = (StoredFile)new DataContractJsonSerializer(typeof(StoredFile)).ReadObject(fs);
                        if (f != null)
                        {
                            if (f.Entries == null) f.Entries = new List<StoredEntry>();
                            return f;
                        }
                    }
                }
            }
            catch { /* file mancante o illeggibile: si riparte vuoti */ }
            return new StoredFile { Entries = new List<StoredEntry>() };
        }

        public static StoredSettings Load(string key)
        {
            if (key == null) return null;
            foreach (StoredEntry e in ReadFile().Entries)
                if (e.Key == key) return e.Settings;
            return null;
        }

        public static void Save(string key, DeviationSettings settings)
        {
            if (key == null || settings == null) return;
            try
            {
                StoredFile f = ReadFile();
                StoredEntry entry = null;
                foreach (StoredEntry e in f.Entries)
                    if (e.Key == key) { entry = e; break; }
                if (entry == null) { entry = new StoredEntry { Key = key }; f.Entries.Add(entry); }
                entry.Settings = StoredSettings.From(settings);

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                using (FileStream fs = File.Create(FilePath))
                    new DataContractJsonSerializer(typeof(StoredFile)).WriteObject(fs, f);
            }
            catch { /* la persistenza è un di più: un errore non deve disturbare Revit */ }
        }
    }
}
