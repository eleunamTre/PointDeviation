using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PointDeviation
{
    /// <summary>
    /// Voce 312: cosa sta facendo il plugin nel thread di Revit (per il registro dei blocchi). Solo un'etichetta: non
    /// cambia niente nel disegno.
    /// </summary>
    internal static class Busy
    {
        private static volatile string _where = "";
        private static int _since;
        public static void Enter(string where) { _since = Environment.TickCount; _where = where ?? ""; }
        public static void Leave() { _where = ""; }
        public static string Where { get { return _where; } }
        public static int SinceMs { get { return Environment.TickCount - _since; } }
    }

    /// <summary>
    /// Voce 312: registro dei blocchi di Revit. Un thread a parte guarda ogni mezzo secondo se Windows considera la
    /// finestra di Revit «non risponde» (nessun messaggio gestito da 5 s, come nel titolo «(Non risponde)»). Quando
    /// succede scrive nel registro l'ora, l'uso della CPU di Revit durante il blocco e se in quel momento il thread di
    /// Revit era dentro il plugin (e dove). Così si vede se un blocco viene dal plugin o da Revit.
    /// </summary>
    internal static class HangWatch
    {
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);

        private static Thread _t;
        private static volatile bool _stop;
        private static IntPtr _wnd;

        public static void Start(IntPtr mainWindow)
        {
            if (_t != null) return;
            _wnd = mainWindow;
            _t = new Thread(Run) { IsBackground = true, Name = "PointDeviation blocchi", Priority = ThreadPriority.BelowNormal };
            _t.Start();
        }

        public static void Stop() { _stop = true; }

        private static void Run()
        {
            Process me = Process.GetCurrentProcess();
            bool hung = false;
            DateTime from = DateTime.Now;
            TimeSpan cpu0 = TimeSpan.Zero;
            var wheres = new List<string>();
            while (!_stop)
            {
                try
                {
                    Thread.Sleep(500);
                    if (AdvancedSettings.Current.NoHangWatch || _wnd == IntPtr.Zero) { hung = false; continue; }
                    bool now = IsHungAppWindow(_wnd);
                    string w = Busy.Where;
                    if (now && !hung)
                    {
                        hung = true; from = DateTime.Now.AddSeconds(-5); wheres.Clear();
                        me.Refresh(); cpu0 = me.TotalProcessorTime;
                        Log.Write("Blocco", string.Format("Revit non risponde (da circa le {0:HH:mm:ss}); plugin in quel momento: {1}",
                            from, w.Length > 0 ? "«" + w + "» da " + Busy.SinceMs + " ms" : "fuori dal plugin"));
                    }
                    if (hung && now && w.Length > 0 && !wheres.Contains(w)) wheres.Add(w);
                    if (hung && !now)
                    {
                        hung = false;
                        me.Refresh();
                        double s = (DateTime.Now - from).TotalSeconds;
                        double pct = s > 0 ? 100.0 * (me.TotalProcessorTime - cpu0).TotalSeconds / Math.Max(1.0, s - 5) / Environment.ProcessorCount : 0;
                        Log.Write("Blocco", string.Format("Revit di nuovo attivo dopo circa {0:F0} s; CPU di Revit durante il blocco {1:F0}% (100% = tutti i {2} processori); plugin durante il blocco: {3}",
                            s, pct, Environment.ProcessorCount, wheres.Count > 0 ? string.Join(", ", wheres) : "mai (il blocco non è nel codice del plugin)"));
                    }
                }
                catch { }
            }
        }
    }
}
