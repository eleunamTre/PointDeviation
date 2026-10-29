using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PointDeviation
{
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
        [DataMember] public int GpuKeepSeconds = 15;
        [DataMember] public int MemKeepSeconds = 60;

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
            BudgetPercent = Math.Max(25, Math.Min(400, BudgetPercent == 0 ? 100 : BudgetPercent));
            GpuKeepSeconds = Math.Max(2, Math.Min(600, GpuKeepSeconds == 0 ? 15 : GpuKeepSeconds));
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
