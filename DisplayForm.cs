using System;
using System.Drawing;
using System.Windows.Forms;

namespace PointDeviation
{
    /// <summary>
    /// Pannello «Aspetto»: dimensione dei punti, luminosità, contrasto, trasparenza e contrasto X-ray.
    /// I valori si applicano quando il cursore si ferma (niente ridisegni a ogni scatto).
    /// </summary>
    internal sealed class DisplayForm : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UiTheme.Apply(this);
        }

        private readonly TrackBar _scale = Bar(0, 400, 25);
        private readonly TrackBar _bri = Bar(-100, 100, 20);
        private readonly TrackBar _con = Bar(-100, 100, 20);
        private readonly TrackBar _tra = Bar(0, 100, 10);
        private readonly TrackBar _xray = Bar(1, 100, 10);
        private readonly Label _scaleV = Value(), _briV = Value(), _conV = Value(), _traV = Value(), _xrayV = Value();
        private readonly Timer _debounce = new Timer { Interval = 200 };
        private bool _loading;

        public DisplayForm()
        {
            Text = L.T("Aspetto della nuvola");
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = UiTheme.Surface;

            var tl = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(12), BackColor = UiTheme.Surface };
            Row(tl, L.T("Dimensione punti (%)"), _scale, _scaleV);
            Row(tl, L.T("Luminosità"), _bri, _briV);
            Row(tl, L.T("Contrasto"), _con, _conV);
            Row(tl, L.T("Trasparenza (%)"), _tra, _traV);
            Row(tl, L.T("Contrasto X-ray"), _xray, _xrayV);

            var note = new Label
            {
                AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = UiTheme.Muted, Margin = new Padding(0, 6, 0, 6),
                Text = L.T("Da lontano i punti sono di un pixel; da vicino, dove sono radi, si ingrandiscono secondo la dimensione scelta (0 = sempre un pixel). Luminosità e contrasto non cambiano i colori dell'analisi, per restare fedeli alla legenda.")
            };
            int r = tl.RowCount++;
            tl.Controls.Add(note, 0, r);
            tl.SetColumnSpan(note, 3);

            var reset = new Button { Text = L.T("Ripristina predefiniti"), AutoSize = true };
            reset.Click += (s, e) =>
            {
                DeviationController.Current.ResetAppearance();
                LoadFromCurrent();
                DeviationController.Apply(Job.Recolor);
            };
            r = tl.RowCount++;
            tl.Controls.Add(reset, 0, r);
            tl.SetColumnSpan(reset, 3);
            Controls.Add(tl);

            LoadFromCurrent();
            foreach (TrackBar b in new[] { _scale, _bri, _con, _tra, _xray }) b.ValueChanged += OnChanged;

            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                DeviationController.Apply(Job.Recolor);
            };
            DeviationController.ActiveChanged += OnActiveChanged;
            DeviationController.SettingsChanged += OnActiveChanged;
            FormClosed += (s, e) =>
            {
                DeviationController.ActiveChanged -= OnActiveChanged;
                DeviationController.SettingsChanged -= OnActiveChanged;
                _debounce.Dispose();
            };
        }

        private static TrackBar Bar(int min, int max, int tick)
        {
            return new TrackBar
            {
                Minimum = min, Maximum = max, TickFrequency = tick, SmallChange = 1, LargeChange = tick,
                Width = 220, AutoSize = false, Height = 32, BackColor = UiTheme.Surface
            };
        }

        private static Label Value()
        {
            return new Label { AutoSize = true, MinimumSize = new Size(34, 0), Margin = new Padding(4, 8, 0, 0) };
        }

        private static void Row(TableLayoutPanel tl, string text, TrackBar bar, Label value)
        {
            int r = tl.RowCount++;
            tl.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, r);
            tl.Controls.Add(bar, 1, r);
            tl.Controls.Add(value, 2, r);
        }

        private void OnActiveChanged()
        {
            if (!IsDisposed) LoadFromCurrent();
        }

        private static int Clamp(TrackBar b, int v) { return Math.Max(b.Minimum, Math.Min(b.Maximum, v)); }

        private void LoadFromCurrent()
        {
            _loading = true;
            try
            {
                DeviationSettings c = DeviationController.Current;
                _scale.Value = Clamp(_scale, c.PointScale);
                _bri.Value = Clamp(_bri, c.Brightness);
                _con.Value = Clamp(_con, c.Contrast);
                _tra.Value = Clamp(_tra, c.Transparency);
                _xray.Value = Clamp(_xray, c.XrayContrast);
                UpdateLabels();
            }
            finally { _loading = false; }
        }

        private void UpdateLabels()
        {
            _scaleV.Text = _scale.Value == 0 ? "1 px" : _scale.Value.ToString();
            _briV.Text = _bri.Value.ToString();
            _conV.Text = _con.Value.ToString();
            _traV.Text = _tra.Value.ToString();
            _xrayV.Text = _xray.Value.ToString();
        }

        private void OnChanged(object sender, EventArgs e)
        {
            UpdateLabels();
            if (_loading) return;
            DeviationSettings c = DeviationController.Current;
            c.PointScale = _scale.Value;
            c.Brightness = _bri.Value;
            c.Contrast = _con.Value;
            c.Transparency = _tra.Value;
            c.XrayContrast = _xray.Value;
            _debounce.Stop();
            _debounce.Start();
        }
    }
}
