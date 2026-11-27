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

        /// <summary>
        /// Diagnostica della grafica per il registro: alimentazione (rete o batteria) e scheda video scelta in Windows per
        /// Revit (Impostazioni › Schermo › Grafica). Sui portatili con due schede, a batteria o con la scheda integrata, il
        /// disegno dei punti rallenta di molte volte (registri del 12/11: da 0,1 a 2-3 ms per milione di triangoli).
        /// </summary>
        public static string GraphicsDiag()
        {
            string power = "?";
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                power = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "rete"
                    : ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline ? string.Format("batteria ({0:P0})", ps.BatteryLifePercent) : "sconosciuta";
            }
            catch { }
            string gpu = "predefinita (nessuna scelta in Windows)";
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                {
                    string v = k != null ? k.GetValue(exe) as string : null;
                    if (!string.IsNullOrEmpty(v))
                        gpu = v.Contains("GpuPreference=2") ? "prestazioni elevate" : v.Contains("GpuPreference=1") ? "risparmio energia (integrata)"
                            : v.Contains("GpuPreference=0") ? "lascia decidere a Windows" : v;
                }
            }
            catch { }
            return "alimentazione " + power + ", scheda video per Revit in Windows: " + gpu;
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
                        LoaderThreads = c >= 16 ? 6 : 4, PrefetchMargin = 1.25, UploadMovingMs = 20, UploadStillMs = 120 };
                default:
                    return new PerfValues { BudgetPercent = 100, GpuKeepSeconds = 45, MemKeepSeconds = 60, BakerThreads = Math.Max(2, Math.Min(6, c - 2)),
                        LoaderThreads = c >= 8 ? 4 : 2, PrefetchMargin = 1.2, UploadMovingMs = 20, UploadStillMs = 120 };
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

        // voce 283: con due Revit aperti (uno per lavorare, uno per le prove) ognuno scrive i suoi file. Il primo Revit
        // usa i nomi di sempre, il secondo «PointDeviation-2.log» e «PointDeviation-2-frames.log», e cosi' via. Il numero
        // e' tenuto da un mutex di Windows per tutta la vita del processo: chiuso Revit, il numero si libera.
        private static string _instance;
        private static System.Threading.Mutex _instanceMutex;
        public static string Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (_lock)
                {
                    if (_instance != null) return _instance;
                    string tag = "";
                    try
                    {
                        for (int i = 1; i <= 9; i++)
                        {
                            bool created;
                            var m = new System.Threading.Mutex(true, @"Local\PointDeviation_Instance_" + i, out created);
                            if (created) { _instanceMutex = m; tag = i == 1 ? "" : "-" + i; break; }
                            m.Dispose();
                        }
                    }
                    catch { tag = ""; }
                    _instance = tag;
                    return _instance;
                }
            }
        }

        public static string FilePath { get { return Path.Combine(Folder, "PointDeviation" + Instance + ".log"); } }

        public static LogLevel Level { get { return AdvancedSettings.Current.LogLevel; } }

        /// <summary>
        /// Voce 284: all'avvio, in background, si cancellano i registri e i diari dei Revit «di prova» (-2 ... -9) e le
        /// copie vecchie dei diari (*.old.log) non piu' scritti da 7 giorni. I file del Revit principale restano: hanno
        /// gia' un tetto (registro 2 MB, diario 40 MB piu' una copia). I file in uso non si toccano.
        /// </summary>
        public static void CleanOld()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(Folder)) return;
                    var rx = new System.Text.RegularExpressions.Regex(@"^PointDeviation(-[2-9](-frames)?(\.old)?|(-[2-9])?-frames\.old)\.log$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    int n = 0;
                    foreach (string f in Directory.GetFiles(Folder, "PointDeviation*.log"))
                    {
                        string name = Path.GetFileName(f);
                        if (!rx.IsMatch(name)) continue;
                        if (Instance != "" && (name.StartsWith("PointDeviation" + Instance + ".", StringComparison.OrdinalIgnoreCase) || name.StartsWith("PointDeviation" + Instance + "-", StringComparison.OrdinalIgnoreCase))) continue;   // file di questo Revit
                        if ((DateTime.Now - File.GetLastWriteTime(f)).TotalDays < 7) continue;
                        try { File.Delete(f); n++; } catch { }
                    }
                    if (n > 0) Write("Registro", n + " registri o diari vecchi cancellati (non scritti da 7 giorni, voce 284)");
                }
                catch { }
            });
        }

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
        /// <summary>Navigazione: resta la vista precedente finché la nuova non è pronta, poi cambio tutto insieme (voce 106).
        /// Spento (predefinito): il dettaglio compare man mano, zona per zona.</summary>
        [DataMember] public bool HoldViewUntilReady;
        /// <summary>Navigazione: stesso dettaglio su tutta la vista. Spento (predefinito): dettaglio pieno al centro della
        /// finestra e sempre più rado verso i bordi (caricamento a fuoco, voce 148).</summary>
        [DataMember] public bool UniformDetail;
        /// <summary>Limite di punti fisso: spento (predefinito) il limite si adatta alla velocità di disegno misurata
        /// (voce 165); acceso vale sempre quello del profilo.</summary>
        [DataMember] public bool FixedBudget;
        /// <summary>Prova (voce 178): i punti li disegna Revit (motore di nuvole) invece del plugin.</summary>
        [DataMember] public bool NativeDisplay;
        /// <summary>Prova della libreria nativa DirectX (voce 180): solo diagnostica, non disegna.</summary>
        [DataMember] public bool NativeProbe;
        /// <summary>Disegno della nuvola con shader propri dentro DirectX 11 di Revit (voce 194, sperimentale).</summary>
        [DataMember] public bool NativeShader;
        /// <summary>Voce 255, 277: con lo shader, camera delle immagini intermedie ricavata dal «faro» (pochi punti
        /// consegnati al motore delle nuvole di Revit). Acceso di serie dalla 277: si salva il contrario (NoBeaconCamera),
        /// cosi' anche le impostazioni gia' salvate con la vecchia voce spenta partono accese.</summary>
        [DataMember] public bool NoBeaconCamera;
        public bool BeaconCamera { get { return !NoBeaconCamera; } set { NoBeaconCamera = !value; } }
        /// <summary>Voce 272: con il disegno con shader, nelle immagini intermedie la profondità del modello si completa con
        /// la mesh dell'analisi. Spento (predefinito) = si usa; acceso = solo la profondità copiata da Revit.</summary>
        [DataMember] public bool NoMeshDepth;
        /// <summary>Voce 274: con la mesh, tolleranza di 1 cm (più l'inclinazione) a favore dei punti rilevati sulle
        /// superfici modellate. Spento (predefinito) = tolleranza attiva; acceso = nessuna tolleranza.</summary>
        [DataMember] public bool NoMeshTolerance;
        [DataMember] public bool NoFlatBeaconFix;   // voce 282
        [DataMember] public bool NoFlatPostDraw;    // voce 294
        [DataMember] public bool NoFlatFaroDraw;    // voce 297
        [DataMember] public bool NoFlatFaroDepth;   // voce 298
        [DataMember] public bool NoFlatFaroKeep;    // voce 299
        [DataMember] public bool NoFlatFaroAlpha;   // voce 301
        [DataMember] public bool NoFlatFaroCamera;  // voce 302
        [DataMember] public bool NoFlatHoverFix;    // voce 303
        [DataMember] public bool FlatHoverRestore;  // voce 304: spento = al faro non si disegna (nuovo); acceso = copia rimessa (303)
        [DataMember] public bool NoFastMatrixRetry; // voce 304
        [DataMember] public bool MeshWithoutLinks;  // voce 296: mesh anche nelle viste senza collegamenti (spento)
        [DataMember] public bool FlatSimple;        // voce 290, 293: spento di serie (riserva se le viste 2D non vanno)
        [DataMember] public bool FlatBeacon;        // voce 286: faro anche in pianta e sezione (spento: come prima del faro)
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

    /// <summary>Informazioni sulla versione mostrate in Avanzate › Diagnostica e scritte nel registro all'avvio.</summary>
    internal static class BuildInfo
    {
        /// <summary>Ultima voce di MODIFICHE.txt compresa in questa versione (da aggiornare a ogni voce).</summary>
        public const int Change = 306;

        public static string Describe()
        {
            string ver = "?", built = "?";
            try
            {
                var asm = typeof(BuildInfo).Assembly;
                ver = asm.GetName().Version.ToString();
                built = System.IO.File.GetLastWriteTime(asm.Location).ToString("dd/MM/yyyy HH:mm");
            }
            catch { }
            return string.Format(L.T("PointDeviation {0}, modifica {1}, compilato il {2}."), ver, Change, built);
        }
    }
}
