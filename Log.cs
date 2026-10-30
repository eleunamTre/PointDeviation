using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PointDeviation
{
    /// <summary>Profilo delle prestazioni: limiti predefiniti secondo il computer.</summary>
    internal enum HardwareProfile { Automatico = 0, Leggero = 1, Medio = 2, Potente = 3, Personalizzato = 4 }

    /// <summary>Caratteristiche del computer usate per scegliere i limiti predefiniti.</summary>
    internal static class Hardware
    {
        public static int Cores { get { return Environment.ProcessorCount; } }

        private static double _ramGb = -1;
        /// <summary>Memoria fisica installata (GB), 0 se non disponibile.</summary>
        public static double RamGb
        {
            get
            {
                if (_ramGb >= 0) return _ramGb;
                _ramGb = 0;
                try
                {
                    var m = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemoryStatusEx)) };
                    if (GlobalMemoryStatusEx(ref m)) _ramGb = m.TotalPhys / (1024.0 * 1024 * 1024);
                }
                catch { }
                return _ramGb;
            }
        }

        /// <summary>Profilo che corrisponde a questo computer.</summary>
        public static HardwareProfile Detected
        {
            get
            {
                double ram = RamGb;
                if (Cores <= 4 || (ram > 0 && ram < 12)) return HardwareProfile.Leggero;
                if (Cores >= 12 && (ram <= 0 || ram >= 24)) return HardwareProfile.Potente;
                return HardwareProfile.Medio;
            }
        }

        public static string Describe()
        {
            return RamGb > 0 ? string.Format(L.T("{0} processori, {1:F0} GB di memoria"), Cores, RamGb) : string.Format(L.T("{0} processori"), Cores);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
    }

    /// <summary>Valori effettivi di un profilo (vedi AdvancedSettings.Effective).</summary>
    internal sealed class PerfValues
    {
        public int BudgetPercent, GpuKeepSeconds, MemKeepSeconds;
        public int BakerThreads, LoaderThreads;
        public double PrefetchMargin;          // 0 = niente precaricamento
        public int UploadMovingMs, UploadStillMs;

        public static PerfValues For(HardwareProfile p)
        {
            int c = Hardware.Cores;
            switch (p)
            {
                case HardwareProfile.Leggero:
                    return new PerfValues { BudgetPercent = 60, GpuKeepSeconds = 20, MemKeepSeconds = 30, BakerThreads = Math.Max(1, Math.Min(2, c - 2)),
                        LoaderThreads = 2, PrefetchMargin = 0, UploadMovingMs = 15, UploadStillMs = 80 };
                case HardwareProfile.Potente:
                    return new PerfValues { BudgetPercent = 130, GpuKeepSeconds = 60, MemKeepSeconds = 120, BakerThreads = Math.Max(2, Math.Min(12, c - 4)),
                        LoaderThreads = c >= 16 ? 6 : 4, PrefetchMargin = 1.6, UploadMovingMs = 20, UploadStillMs = 120 };
                default:
                    return new PerfValues { BudgetPercent = 100, GpuKeepSeconds = 45, MemKeepSeconds = 60, BakerThreads = Math.Max(2, Math.Min(6, c - 2)),
                        LoaderThreads = c >= 8 ? 4 : 2, PrefetchMargin = 1.4, UploadMovingMs = 20, UploadStillMs = 120 };
            }
        }
    }

    /// <summary>Livello del registro: si scrivono i messaggi fino a questo livello compreso.</summary>
    internal enum LogLevel { Spento = 0, Errori = 1, Avvisi = 2, Info = 3, Debug = 4 }

    /// <summary>
    /// Registro di diagnostica: %LOCALAPPDATA%\PointDeviation\PointDeviation.log
    /// Errori che altrimenti verrebbero ignorati per non disturbare Revit (disegno, caricamento, motore) e,
    /// a livello Debug, lo stato periodico del disegno. Ogni messaggio uguale si scrive al massimo una volta al minuto;
    /// il file si riparte da zero oltre 2 MB. Il livello si sceglie in Impostazioni avanzate.
    /// </summary>
    internal static class Log
    {
        private static readonly object _lock = new object();
        private static readonly System.Collections.Generic.Dictionary<string, DateTime> _last = new System.Collections.Generic.Dictionary<string, DateTime>();

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PointDeviation"); }
        }

        public static string FilePath { get { return Path.Combine(Folder, "PointDeviation.log"); } }

        public static LogLevel Level { get { return AdvancedSettings.Current.LogLevel; } }

        /// <summary>Informazioni (livello Info).</summary>
        public static void Write(string where, string message) { Put(LogLevel.Info, where, message); }

        /// <summary>Situazioni anomale gestite (livello Avvisi).</summary>
        public static void Warn(string where, string message) { Put(LogLevel.Avvisi, where, message); }

        /// <summary>Stato dettagliato e periodico (livello Debug).</summary>
        public static void Debug(string where, string message) { Put(LogLevel.Debug, where, message); }

        public static void Error(string where, Exception ex)
        {
            if (ex == null) return;
            Put(LogLevel.Errori, where, ex.GetType().Name + ": " + ex.Message + "\r\n    " + (ex.StackTrace ?? "").Replace("\n", "\n    "));
        }

        private static void Put(LogLevel level, string where, string message)
        {
            try
            {
                if (level > Level) return;
                lock (_lock)
                {
                    string key = where + "|" + message;
                    DateTime now = DateTime.Now, t;
                    if (_last.TryGetValue(key, out t) && (now - t).TotalSeconds < 60) return;
                    _last[key] = now;
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) fi.Delete();
                    string tag = level == LogLevel.Errori ? "ERRORE " : level == LogLevel.Avvisi ? "AVVISO " : level == LogLevel.Debug ? "debug " : "";
                    File.AppendAllText(path, string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1}[{2}]  {3}\r\n", now, tag, where, message), Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Impostazioni avanzate del plugin (per computer, non per progetto):
    /// %LOCALAPPDATA%\PointDeviation\advanced.json
    /// </summary>
    [DataContract]
    internal sealed class AdvancedSettings
    {
        [DataMember] public LogLevel LogLevel = LogLevel.Info;
        /// <summary>Limite di punti disegnati, in percentuale del valore della qualità scelta.</summary>
        [DataMember] public int BudgetPercent = 100;
        /// <summary>Secondi fuori vista prima di liberare la memoria della scheda video e quella del computer.</summary>
        [DataMember] public int GpuKeepSeconds = 45;
        [DataMember] public int MemKeepSeconds = 60;
        /// <summary>Analisi: la finestra aspetta il calcolo di tutte le distanze (invece dei soli livelli principali).</summary>
        [DataMember] public bool AnalysisFullWait;
        /// <summary>Profilo delle prestazioni; con «Personalizzato» valgono i valori sopra.</summary>
        [DataMember] public HardwareProfile Profile = HardwareProfile.Automatico;

        /// <summary>Profilo in uso (Automatico risolto secondo il computer).</summary>
        public HardwareProfile ResolvedProfile { get { return Profile == HardwareProfile.Automatico ? Hardware.Detected : Profile; } }

        /// <summary>Valori in uso: quelli del profilo, o quelli scelti a mano con «Personalizzato».</summary>
        public PerfValues Effective
        {
            get
            {
                if (Profile != HardwareProfile.Personalizzato) return PerfValues.For(ResolvedProfile);
                PerfValues v = PerfValues.For(Hardware.Detected);
                v.BudgetPercent = BudgetPercent; v.GpuKeepSeconds = GpuKeepSeconds; v.MemKeepSeconds = MemKeepSeconds;
                return v;
            }
        }

        private static AdvancedSettings _current;

        private static string FilePath { get { return Path.Combine(Log.Folder, "advanced.json"); } }

        public static AdvancedSettings Current
        {
            get
            {
                if (_current != null) return _current;
                AdvancedSettings s = null;
                try
                {
                    if (File.Exists(FilePath))
                        using (FileStream fs = File.OpenRead(FilePath))
                            s = (AdvancedSettings)new DataContractJsonSerializer(typeof(AdvancedSettings)).ReadObject(fs);
                }
                catch { }
                _current = (s ?? new AdvancedSettings()).Normalized();
                return _current;
            }
        }

        private AdvancedSettings Normalized()
        {
            if (LogLevel < LogLevel.Spento || LogLevel > LogLevel.Debug) LogLevel = LogLevel.Info;
            if (Profile < HardwareProfile.Automatico || Profile > HardwareProfile.Personalizzato) Profile = HardwareProfile.Automatico;
            BudgetPercent = Math.Max(25, Math.Min(400, BudgetPercent == 0 ? 100 : BudgetPercent));
            GpuKeepSeconds = Math.Max(2, Math.Min(600, GpuKeepSeconds == 0 ? 45 : GpuKeepSeconds));
            MemKeepSeconds = Math.Max(5, Math.Min(3600, MemKeepSeconds == 0 ? 60 : MemKeepSeconds));
            return this;
        }

        public void Save()
        {
            Normalized();
            try
            {
                Directory.CreateDirectory(Log.Folder);
                using (FileStream fs = File.Create(FilePath))
                    new DataContractJsonSerializer(typeof(AdvancedSettings)).WriteObject(fs, this);
            }
            catch (Exception ex) { Log.Error("Impostazioni avanzate", ex); }
        }
    }
}
