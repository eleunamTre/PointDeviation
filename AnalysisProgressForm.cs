using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PointDeviation
{
    /// <summary>
    /// Dopo l'analisi: calcola subito le distanze di tutte le nuvole (con tutti i processori) mostrando l'avanzamento,
    /// poi l'esito. Così la navigazione in modalità discrepanze è fluida fin dall'inizio. Si può lasciar proseguire il
    /// calcolo in background e chiudere la finestra.
    /// </summary>
    internal sealed class AnalysisProgressForm : Form
    {
        private const double ForegroundShare = 0.3; // frazione dei punti calcolata con la finestra aperta
        private readonly List<CloudLayer> _layers;
        private readonly AnalysisSnapshot _an;
        private readonly Label _title = new Label { AutoSize = false, Height = 26, Dock = DockStyle.Top, Font = new Font("Segoe UI Semibold", 11f) };
        private readonly Label _detail = new Label { AutoSize = false, Height = 40, Dock = DockStyle.Top, ForeColor = UiTheme.Muted };
        private readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Minimum = 0, Maximum = 1000 };
        private readonly Label _result = new Label { AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        private readonly Button _button = new Button { Width = 200, Height = 30, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
        private volatile bool _cancel;
        private bool _done;
        private readonly Stopwatch _sw = new Stopwatch();

        public AnalysisProgressForm(List<CloudLayer> layers, AnalysisSnapshot an)
        {
            _layers = layers; _an = an;
            Text = L.T("Analisi delle discrepanze");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(560, 236);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 8) };
            body.Controls.Add(_result);
            body.Controls.Add(_bar);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
            body.Controls.Add(_detail);
            body.Controls.Add(_title);
            var foot = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(18, 8, 18, 12) };
            _button.Location = new Point(foot.Width - _button.Width - 18, 10);
            foot.Controls.Add(_button);
            foot.Resize += (s, e) => _button.Location = new Point(foot.ClientSize.Width - _button.Width - 18, 8);
            Controls.Add(body);
            Controls.Add(foot);

            _title.Text = L.T("Calcolo delle distanze dal modello...");
            _detail.Text = "";
            _button.Text = L.T("Naviga subito");
            _button.Click += (s, e) => { if (_done) Close(); else { _cancel = true; Close(); } };
            UiTheme.Apply(this, _button);
            _title.Font = new Font("Segoe UI Semibold", 11f);
            _detail.ForeColor = UiTheme.Muted;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _sw.Start();
            Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // chiudendo durante il calcolo il lavoro continua in background (riprende da dove è arrivato)
            if (!_done) _cancel = true;
            base.OnFormClosing(e);
        }

        private void Work()
        {
            long total = 0, reused = 0, computed = 0, surface = 0, counted = 0, remaining = 0;
            string error = null;
            try
            {
                var totals = new long[_layers.Count];
                for (int k = 0; k < _layers.Count; k++) foreach (TileEntry t in _layers[k].Tiles) totals[k] += t.Count;
                // In primo piano solo i livelli principali (la parte più grossolana della nuvola, una frazione dei punti
                // distribuita in modo uniforme): bastano per navigare quasi a ogni zoom. Il dettaglio fine si completa in
                // background, e dove serve prima lo calcola la vista stessa.
                var stops = new long[_layers.Count];
                long all = 0;
                double share = AdvancedSettings.Current.AnalysisFullWait ? 1.0 : ForegroundShare;
                for (int k = 0; k < _layers.Count; k++) { stops[k] = share >= 1.0 ? long.MaxValue : (long)(totals[k] * share); all += share >= 1.0 ? totals[k] : stops[k]; }
                long before = 0;
                long lastUi = 0;
                for (int k = 0; k < _layers.Count && !_cancel; k++)
                {
                    CloudLayer l = _layers[k];
                    long b0 = before, stop = stops[k];
                    DistRunResult r = DistPrefetch.Run(l, _an, true, () => _cancel, (done, tot) =>
                    {
                        long now = _sw.ElapsedMilliseconds;
                        if (now - lastUi < 80 && done < Math.Min(stop, tot)) return;
                        lastUi = now;
                        Ui(b0 + Math.Min(done, stop), all, l.Name);
                    }, stop);
                    if (r == null) { error = L.T("Impossibile salvare le distanze accanto alla nuvola (controlla di poter scrivere nella cartella della libreria)."); break; }
                    total += r.Total; reused += r.Reused; computed += r.Computed;
                    remaining += Math.Max(0, r.Total - r.Reused - r.Computed);
                    if (!r.Finished) DistPrefetch.Ensure(l, _an); // il resto in background
                    Log.Write("Analisi", string.Format("«{0}»: {4} thread, per thread lettura {1:F1} s, calcolo {2:F1} s, scrittura {3:F1} s; raccolte memoria (gen 2) {5}", l.Name, r.ReadMs / 1000, r.ComputeMs / 1000, r.WriteMs / 1000, r.Threads, r.Gc2));
                    before += stop == long.MaxValue ? totals[k] : stop;
                }
                if (!_cancel && error == null)
                    foreach (CloudLayer l in _layers)
                    {
                        long ws, tt;
                        DistPrefetch.CountSurface(l, _an, out ws, out tt);
                        surface += ws; counted += tt;
                    }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("Calcolo distanze", ex);
            }
            _sw.Stop();
            if (_cancel) return;

            double sec = _sw.Elapsed.TotalSeconds;
            Log.Write("Analisi", string.Format("distanze di {0:N0} punti pronte in {1:F1} s ({2:N0} calcolate, {3:N0} ritrovate, {5:N0} lasciate al background){4}",
                total - remaining, sec, computed, reused, error != null ? ", errore: " + error : "", remaining));
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _done = true;
                    _bar.Value = _bar.Maximum;
                    _button.Text = L.T("Chiudi");
                    if (error != null)
                    {
                        _title.Text = L.T("Analisi non completata");
                        _title.ForeColor = Color.FromArgb(192, 57, 43);
                        _result.Text = error;
                        return;
                    }
                    _title.Text = L.T("Analisi completata");
                    _title.ForeColor = Color.FromArgb(39, 130, 70);
                    _detail.Text = string.Format(L.T("{0:N0} punti in {1:F1} s"), total - remaining, sec);
                    string txt = remaining > 0
                        ? string.Format(L.T("Livelli principali pronti: puoi navigare. Il dettaglio fine ({0:F0}% dei punti) si completa in background, circa {1:F0} s."),
                            100.0 * remaining / Math.Max(1, total), computed > 0 ? remaining / (computed / Math.Max(0.1, sec)) * 1.2 : 0)
                        : reused >= total
                        ? L.T("Distanze ritrovate da un'analisi precedente identica: nessun calcolo necessario.")
                        : reused > 0
                            ? string.Format(L.T("{0:N0} distanze calcolate, {1:N0} ritrovate da un'analisi precedente."), computed, reused)
                            : string.Format(L.T("{0:N0} distanze calcolate e salvate accanto alla nuvola."), computed);
                    if (counted > 0)
                        txt += "\n" + string.Format(L.T("Punti con una superficie del modello entro la distanza massima: {0:F0}%."), 100.0 * surface / counted);
                    if (counted > 0 && surface == 0)
                        txt += "\n" + L.T("Nessun punto vicino al modello: controlla la posizione della nuvola, la distanza massima o gli elementi analizzati.");
                    _result.Text = txt;
                }));
            }
            catch { }
        }

        private void Ui(long done, long all, string name)
        {
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (_done || IsDisposed) return;
                    double f = all > 0 ? (double)done / all : 1;
                    _bar.Value = Math.Max(0, Math.Min(_bar.Maximum, (int)(f * _bar.Maximum)));
                    double sec = _sw.Elapsed.TotalSeconds;
                    string eta = f > 0.03 && f < 1 ? string.Format(L.T(", circa {0:F0} s alla fine"), sec / f - sec) : "";
                    _detail.Text = string.Format(L.T("«{0}»\n{1:N0} di {2:N0} punti ({3:F0}%){4}"), name, done, all, f * 100, eta);
                }));
            }
            catch { }
        }
    }
}
