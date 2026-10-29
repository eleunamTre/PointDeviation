using System;
using System.IO;
using System.Text;

namespace PointDeviation
{
    /// <summary>
    /// Registro di diagnostica: %LOCALAPPDATA%\PointDeviation\PointDeviation.log
    /// Errori che altrimenti verrebbero ignorati per non disturbare Revit (disegno, caricamento, motore).
    /// Ogni messaggio uguale si scrive al massimo una volta al minuto; il file si riparte da zero oltre 2 MB.
    /// </summary>
    internal static class Log
    {
        private static readonly object _lock = new object();
        private static readonly System.Collections.Generic.Dictionary<string, DateTime> _last = new System.Collections.Generic.Dictionary<string, DateTime>();

        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PointDeviation", "PointDeviation.log"); }
        }

        public static void Write(string where, string message)
        {
            try
            {
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
                    File.AppendAllText(path, string.Format("{0:yyyy-MM-dd HH:mm:ss}  [{1}]  {2}\r\n", now, where, message), Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Error(string where, Exception ex)
        {
            if (ex == null) return;
            Write(where, ex.GetType().Name + ": " + ex.Message + "\r\n    " + (ex.StackTrace ?? "").Replace("\n", "\n    "));
        }
    }
}
