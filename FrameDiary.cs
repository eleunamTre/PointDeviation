using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PointDeviation
{
    /// <summary>
    /// Voce 234: diario dei fotogrammi, file PointDeviation-frames.log nella cartella del registro. Con il disegno con
    /// shader acceso scrive una riga per ogni fotogramma del plugin (C), per ogni evento della libreria nativa (M segnale,
    /// P Present completo o interrotto con il motivo di un eventuale salto, G tempo della scheda video, X matrice di
    /// Revit), per i cambi di stile e di vista (E) e ogni 2 s lo stato del sistema (S: memoria, processore, memoria della
    /// scheda video, code di lettura). Tutte le righe iniziano con lo stesso orologio in ms (QueryPerformanceCounter), cosi'
    /// un difetto visto in un video si ritrova fotogramma per fotogramma. Il file ricomincia oltre 40 MB (il precedente
    /// resta come PointDeviation-frames.old.log).
    /// </summary>
    internal static class FrameDiary
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static StreamWriter _w;
        private static bool _failed;
        private static double _lastFlush, _lastSys, _lastFrame = -1, _lastCpuWall;
        private static TimeSpan _lastCpu;
        private static int _lastStyle, _gc0, _gc1, _gc2;
        private static long _lastView;
        private static readonly byte[] _buf = new byte[1 << 20];
        private const long MaxBytes = 40L << 20;

        public static string FilePath { get { return Path.Combine(Log.Folder, "PointDeviation" + Log.Instance + "-frames.log");   // voce 283 } }
        public static double NowMs { get { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; } }

        private static bool Ensure()
        {
            if (_w != null) return true;
            if (_failed || !NativeProbe.ShaderMode) return false;
            try
            {
                Directory.CreateDirectory(Log.Folder);
                string path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) Rotate(path);
                _w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false), 1 << 16);
                _w.WriteLine();
                _w.WriteLine(string.Format(Inv, "{0:F1};E;inizio diario;modifica={1};{2:yyyy-MM-dd HH:mm:ss}", NowMs, BuildInfo.Change, DateTime.Now));
                _w.WriteLine("# righe: ms;tipo;campi. C=fotogramma del plugin, M=segnale nel disegno di Revit, P=Present (completo/interrotto), G=tempo della scheda video per i punti, V=blocchi dentro l'inquadratura, B/F=faro (disegni e conteggio per Present), X=matrice di Revit, E=evento, S=sistema");
                NativeProbe.TraceOn(true);
                return true;
            }
            catch (Exception ex) { _failed = true; Log.Error("Diario", ex); return false; }
        }

        private static void Rotate(string path)
        {
            try
            {
                string old = Path.Combine(Log.Folder, "PointDeviation" + Log.Instance + "-frames.old.log");
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            catch { }
        }

        public static void Line(string text)
        {
            if (!Ensure()) return;
            try { _w.Write(NowMs.ToString("F1", Inv)); _w.Write(';'); _w.WriteLine(text); } catch { }
        }

        /// <summary>Un fotogramma del plugin con il disegno con shader.</summary>
        // voce 237: tempo dei caricamenti di questo fotogramma diviso tra preparazione dei punti (thread di lavoro) e
        // creazione dei buffer nella scheda video (thread di Revit); li somma CloudLayer.ShaderUploadAll
        public static double UpPrepMs, UpGpuMs;

        /// <summary>Voce 244: riga V (blocchi dentro l'inquadratura) scritta dopo la riga C del fotogramma.</summary>
        public static string Extra;
        public static void Frame(long viewId, string viewName, Style st, bool moving, long cap, long wanted, double scale,
                                 long points, int blocks, int notLoaded, int notBuilt, long upPts, int upBlocks,
                                 double msSelect, double msPrep, double msDraw)
        {
            if (!Ensure()) return;
            double now = NowMs;
            if (viewId != _lastView) { _lastView = viewId; Line("E;vista=" + viewName + ";id=" + viewId); }
            if (st.Version != _lastStyle)
            {
                _lastStyle = st.Version;
                Line(string.Format(Inv, "E;stile;versione={0};modo={1};trasparenza={2};analisi={3};qualita={4};dimensione_punti={5}",
                    st.Version, st.Mode, st.Transparency, st.Analysis != null ? "si" : "no", st.Quality, st.PointScale));
            }
            double interval = _lastFrame < 0 ? -1 : now - _lastFrame;
            _lastFrame = now;
            Line(string.Format(Inv,
                "C;movimento={0};limite={1:F2}M;chiesti={2:F2}M;frazione={3:F2};disegnati={4:F2}M;blocchi={5};non_letti={6};da_preparare={7};caricati={8:F2}M/{9};ms_scelta={10:F1};ms_prep={11:F1};ms_disegno={12:F1};intervallo={13:F0};ms_punti={14:F1};ms_buffer={15:F1}",
                moving ? 1 : 0, cap / 1e6, wanted / 1e6, scale, points / 1e6, blocks, notLoaded, notBuilt, upPts / 1e6, upBlocks,
                msSelect, msPrep, msDraw, interval, UpPrepMs, UpGpuMs));
            UpPrepMs = 0; UpGpuMs = 0;
            if (!string.IsNullOrEmpty(Extra)) { Line("V;" + Extra); Extra = null; }   // voce 244
            Pump();
        }

        /// <summary>Preleva le righe della libreria nativa, scrive lo stato del sistema ogni 2 s, svuota su disco ogni s.</summary>
        public static void Pump()
        {
            if (_w == null) { if (!NativeProbe.ShaderMode) return; if (!Ensure()) return; }
            try
            {
                int n = NativeProbe.TraceTake(_buf);
                if (n > 0) _w.Write(Encoding.ASCII.GetString(_buf, 0, n));
                double now = NowMs;
                if (now - _lastSys >= 2000) { _lastSys = now; SystemLine(now); }
                if (now - _lastFlush >= 1000) { _lastFlush = now; _w.Flush(); if (_w.BaseStream.Length > MaxBytes) { _w.Dispose(); _w = null; Rotate(FilePath); } }
            }
            catch { }
        }

        private static void SystemLine(double now)
        {
            var p = Process.GetCurrentProcess();
            TimeSpan cpu = p.TotalProcessorTime;
            double cpuPct = _lastCpuWall > 0 ? (cpu - _lastCpu).TotalMilliseconds / ((now - _lastCpuWall) * Environment.ProcessorCount) * 100 : -1;
            _lastCpu = cpu; _lastCpuWall = now;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            double used, budget;
            bool vm = NativeProbe.VideoMem(out used, out budget);
            Line(string.Format(Inv,
                "S;cpu_revit={0:F0}%;memoria_privata={1:F0}MB;working_set={2:F0}MB;heap_net={3:F0}MB;gc={4}/{5}/{6};vram_revit={7}MB;vram_disponibile={8}MB;thread={9};coda_lettura={10};coda_preparazione={11}",
                cpuPct, p.PrivateMemorySize64 / 1048576.0, p.WorkingSet64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0,
                g0 - _gc0, g1 - _gc1, g2 - _gc2, vm ? used.ToString("F0", Inv) : "?", vm ? budget.ToString("F0", Inv) : "?",
                p.Threads.Count, CloudLoader.Queued + CloudLoader.QueuedPrefetch, CloudBaker.Queued));
            _gc0 = g0; _gc1 = g1; _gc2 = g2;
            p.Dispose();
        }
    }
}
