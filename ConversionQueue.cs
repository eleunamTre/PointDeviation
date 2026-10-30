using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PointDeviation
{
    internal enum ConversionState { InCoda, InCorso, Completata, NonRiuscita, Annullata }

    /// <summary>Una conversione nella coda (LAS, E57, o la seconda fase di un RCP).</summary>
    internal sealed class ConversionJob
    {
        public int Id;
        public string Name, Folder;
        public volatile string Phase;
        public double Fraction;
        public volatile ConversionState State;
        public string Error, ResultFolder;
        public readonly CancellationTokenSource Cts = new CancellationTokenSource();
        internal Func<IPointSource> Make;
        internal string RawToDelete;
        public bool Active { get { return State == ConversionState.InCoda || State == ConversionState.InCorso; } }
    }

    /// <summary>
    /// Conversioni in background, indipendenti dalla finestra Libreria: si possono accodare più nuvole, chiudere la
    /// finestra e continuare a usare Revit. Le conversioni non usano l'API di Revit (i punti degli RCP sono già stati
    /// letti attraverso Revit prima di arrivare qui), quindi possono girare su un thread di lavoro.
    /// A fine conversione compare un avviso in basso a destra di Revit.
    /// </summary>
    internal static class ConversionQueue
    {
        private static readonly object _lock = new object();
        private static readonly List<ConversionJob> _jobs = new List<ConversionJob>();
        private static Control _ui;      // controllo invisibile creato sul thread dell'interfaccia di Revit
        private static int _nextId;
        private static bool _running;

        /// <summary>Qualcosa è cambiato (avanzamento o stato); chiamato sul thread dell'interfaccia di Revit.</summary>
        public static event Action<ConversionJob> Changed;

        public static List<ConversionJob> Jobs { get { lock (_lock) return new List<ConversionJob>(_jobs); } }

        public static bool Busy { get { lock (_lock) return _jobs.Exists(j => j.Active); } }

        /// <summary>
        /// Da chiamare sul thread dell'interfaccia di Revit (comandi, finestra Libreria): le notifiche delle conversioni
        /// vengono riportate su quel thread attraverso un controllo invisibile. (Prima si usava il contesto di
        /// sincronizzazione di chi accodava: per gli RCP l'accodamento arriva da un evento di Revit, dove quel contesto
        /// non è quello dell'interfaccia, e la Libreria veniva aggiornata da un altro thread: errore e Revit bloccato.)
        /// </summary>
        public static void InitUi()
        {
            if (_ui != null && !_ui.IsDisposed) return;
            var c = new Control();
            IntPtr h = c.Handle; // crea la finestra sul thread corrente
            _ui = c;
        }

        public static ConversionJob Enqueue(Func<IPointSource> make, string name, string folder, string rawToDelete)
        {
            InitUi();
            var job = new ConversionJob { Make = make, Name = name, Folder = folder, RawToDelete = rawToDelete, Phase = L.T("In coda"), State = ConversionState.InCoda };
            lock (_lock)
            {
                job.Id = ++_nextId;
                _jobs.Add(job);
                if (!_running) { _running = true; Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning); }
            }
            Raise(job);
            Log.Write("Conversione", "in coda: «" + name + "» -> " + folder);
            return job;
        }

        public static void Cancel(ConversionJob job)
        {
            if (job == null || !job.Active) return;
            job.Cts.Cancel();
            if (job.State == ConversionState.InCoda) { job.State = ConversionState.Annullata; Finish(job); }
        }

        /// <summary>Alla chiusura di Revit: si interrompe tutto (le cartelle parziali vengono eliminate dal convertitore).</summary>
        public static void CancelAll()
        {
            foreach (ConversionJob j in Jobs) if (j.Active) j.Cts.Cancel();
        }

        private static void Worker()
        {
            // sotto la navigazione: la conversione cede il processore a Revit
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            while (true)
            {
                ConversionJob job;
                lock (_lock)
                {
                    job = _jobs.Find(j => j.State == ConversionState.InCoda);
                    if (job == null) { _running = false; return; }
                    job.State = ConversionState.InCorso;
                }
                Run(job);
            }
        }

        private static void Run(ConversionJob job)
        {
            DateTime lastUi = DateTime.MinValue;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                job.Phase = L.T("Lettura del file...");
                Raise(job);
                IPointSource src = job.Make();
                job.ResultFolder = CloudConverter.Convert(src, job.Folder, job.Name, (msg, f) =>
                {
                    job.Phase = msg; job.Fraction = f;
                    if ((DateTime.Now - lastUi).TotalMilliseconds < 250 && f < 1.0) return;
                    lastUi = DateTime.Now;
                    Raise(job);
                }, job.Cts.Token);
                job.State = ConversionState.Completata;
                Log.Write("Conversione", string.Format("completata: «{0}» in {1:F0} s", job.Name, sw.Elapsed.TotalSeconds));
            }
            catch (Exception ex)
            {
                Exception e = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
                if (e is OperationCanceledException || job.Cts.IsCancellationRequested) job.State = ConversionState.Annullata;
                else
                {
                    job.State = ConversionState.NonRiuscita;
                    job.Error = e.Message;
                    Log.Error("Conversione «" + job.Name + "»", e);
                }
            }
            finally
            {
                if (job.RawToDelete != null)
                {
                    try { File.Delete(job.RawToDelete); } catch { }
                    try { File.Delete(job.RawToDelete + ".txt"); } catch { }
                }
            }
            Finish(job);
        }

        private static void Finish(ConversionJob job)
        {
            lock (_lock) _jobs.Remove(job);
            Raise(job);
            if (job.State == ConversionState.Completata || job.State == ConversionState.NonRiuscita) Post(() => ConversionToast.Show(job));
        }

        private static void Raise(ConversionJob job)
        {
            Post(() => { Action<ConversionJob> h = Changed; if (h != null) h(job); });
        }

        private static void Post(Action a)
        {
            Control ui = _ui;
            if (ui == null || ui.IsDisposed) return;
            try { ui.BeginInvoke((Action)(() => { try { a(); } catch (Exception ex) { Log.Error("Conversione (interfaccia)", ex); } })); }
            catch (Exception ex) { Log.Error("Conversione (avviso)", ex); }
        }
    }

    /// <summary>Avviso a fine conversione, in basso a destra della finestra di Revit; si chiude da solo.</summary>
    internal sealed class ConversionToast : Form
    {
        private static readonly List<ConversionToast> _open = new List<ConversionToast>();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 20000 };

        public static void Show(ConversionJob job)
        {
            var t = new ConversionToast(job);
            IntPtr owner = DeviationController.MainWindow;
            Rectangle area = (owner != IntPtr.Zero ? Screen.FromHandle(owner) : Screen.PrimaryScreen).WorkingArea;
            int y = area.Bottom - 16 - t.Height;
            foreach (ConversionToast o in _open) if (!o.IsDisposed) y = Math.Min(y, o.Top - 8 - t.Height);
            t.Location = new Point(area.Right - 16 - t.Width, y);
            _open.Add(t);
            t.FormClosed += (s, e) => _open.Remove(t);
            if (owner != IntPtr.Zero) t.Show(new WindowOwner(owner)); else t.Show();
        }

        private ConversionToast(ConversionJob job)
        {
            bool ok = job.State == ConversionState.Completata;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = false;
            BackColor = Color.White;
            Size = new Size(360, 96);
            Padding = new Padding(1);

            var accent = new Panel { Dock = DockStyle.Left, Width = 5, BackColor = ok ? Color.FromArgb(39, 130, 70) : Color.FromArgb(192, 57, 43) };
            var title = new Label
            {
                Text = ok ? L.T("Conversione completata") : L.T("Conversione non riuscita"),
                Font = new Font("Segoe UI Semibold", 10f), Location = new Point(16, 10), AutoSize = true,
                ForeColor = ok ? Color.FromArgb(39, 130, 70) : Color.FromArgb(192, 57, 43)
            };
            var text = new Label
            {
                Text = ok ? "«" + job.Name + "» " + L.T("è pronta nella libreria.") : "«" + job.Name + "»: " + job.Error,
                Font = new Font("Segoe UI", 9f), Location = new Point(16, 34), Size = new Size(330, 30), AutoEllipsis = true, ForeColor = UiTheme.Ink
            };
            var open = new Button { Text = L.T("Apri libreria"), Location = new Point(176, 62), Size = new Size(100, 26), FlatStyle = FlatStyle.System };
            var close = new Button { Text = L.T("Chiudi"), Location = new Point(282, 62), Size = new Size(66, 26), FlatStyle = FlatStyle.System };
            open.Click += (s, e) => { Close(); DeviationController.ShowLibrary(); };
            close.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { title, text, open, close, accent });
            Paint += (s, e) => { using (var p = new Pen(UiTheme.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); };
            _timer.Tick += (s, e) => Close();
            _timer.Start();
            FormClosed += (s, e) => _timer.Dispose();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
    }
}
