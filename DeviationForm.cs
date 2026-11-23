using System;
using System.Drawing;
using System.Windows.Forms;

namespace PointDeviation
{
    /// <summary>
    /// Finestra modeless con le impostazioni. Legge e scrive DeviationController.Current,
    /// condiviso con i pulsanti della barra multifunzione.
    /// Tolleranza, scala e opzioni colore si applicano in tempo reale (solo ricolorazione).
    /// </summary>
    internal sealed class DeviationForm : PdForm
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UiTheme.Apply(this);
        }


        private readonly TextBox _path = new TextBox { Width = 290 };
        private readonly NumericUpDown _maxPts = Num(50m, 20000m, 2000m, 0, 500m);
        private readonly ComboBox _scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 290 };
        private readonly CheckBox _links = new CheckBox { Text = L.T("Includi i modelli collegati (link)"), AutoSize = true };
        // voce 259: punti la cui superficie più vicina è di un link, distinti da quelli del modello in uso
        private readonly ComboBox _linkLook = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        private readonly TrackBar _linkStr = new TrackBar { Minimum = 0, Maximum = 90, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Width = 150, AutoSize = false, Height = 32 };
        private readonly Label _linkVal = new Label { AutoSize = true, MinimumSize = new Size(34, 0), Margin = new Padding(4, 8, 0, 0) };
        private readonly Timer _linkTimer = new Timer { Interval = 250 };
        // voce 261: stessa resa per i punti senza modello
        private readonly ComboBox _missLook = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };   // voce 265
        private readonly TrackBar _missStr = new TrackBar { Minimum = 0, Maximum = 90, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Width = 150, AutoSize = false, Height = 32 };
        private readonly Label _missVal = new Label { AutoSize = true, MinimumSize = new Size(34, 0), Margin = new Padding(4, 8, 0, 0) };
        private readonly ComboBox _profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly ComboBox _missing = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        private readonly CheckBox _invert = new CheckBox { Text = L.T("Inverti segno (se rosso e blu risultano scambiati)"), AutoSize = true };
        private readonly Label _status = new Label { AutoSize = true, MaximumSize = new Size(430, 0), Text = L.T("Pronto.") };
        private readonly Label _stats = new Label { AutoSize = true, MaximumSize = new Size(430, 0) };

        public DeviationForm()
        {
            Text = L.T("Point Deviation: impostazioni");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = new Font("Segoe UI", 9f);

            _scope.Items.Add(L.T("Elementi visibili nella vista attiva"));
            _scope.Items.Add(L.T("Selezione memorizzata (anche link)"));
            _scope.SelectedIndex = 0;
            _missing.Items.Add(L.T("Evidenzia in viola"));
            _missing.Items.Add(L.T("Grigio"));
            _missing.Items.Add(L.T("Nascondi"));
            _missing.SelectedIndex = 0;

            var tl = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(10),
                Dock = DockStyle.Fill
            };
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Action<string, Control> addRow = (text, c) =>
            {
                int r = tl.RowCount;
                tl.RowCount = r + 1;
                tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                if (text != null)
                {
                    tl.Controls.Add(new Label
                    {
                        Text = text,
                        AutoSize = true,
                        Anchor = AnchorStyles.Left,
                        Margin = new Padding(0, 6, 10, 3)
                    }, 0, r);
                    tl.Controls.Add(c, 1, r);
                }
                else
                {
                    tl.Controls.Add(c, 0, r);
                    tl.SetColumnSpan(c, 2);
                }
            };

            // --- Nuvola: si sceglie e si inserisce dalla libreria
            _path.ReadOnly = true;
            var openLib = new Button { Text = L.T("Libreria…"), AutoSize = true };
            openLib.Click += (s, e) => DeviationController.ShowLibrary();
            var fileRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            fileRow.Controls.Add(_path);
            fileRow.Controls.Add(openLib);
            addRow(L.T("Nuvole"), fileRow);

            addRow(L.T("Punti per le statistiche (migliaia)"), _maxPts);
            addRow(L.T("Modello"), _scope);
            addRow(null, _links);
            _linkLook.Items.Add(L.T("Sbiaditi"));
            _linkLook.Items.Add(L.T("Trasparenti"));
            _linkLook.SelectedIndex = 0;
            _linkStr.BackColor = UiTheme.Surface;
            var linkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            linkRow.Controls.Add(_linkLook);
            linkRow.Controls.Add(_linkStr);
            linkRow.Controls.Add(_linkVal);
            addRow(L.T("Punti vicini ai link"), linkRow);
            new ToolTip().SetToolTip(_linkLook, L.T("Trasparenti solo con il disegno con shader; altrimenti sbiaditi."));

            // --- Azioni
            var btnRecalc = new Button { Text = L.T("Ricalcola distanze"), AutoSize = true };
            btnRecalc.Click += (s, e) =>
            {
                Sync();
                DeviationController.Current.Display = DisplayMode.Discrepanze;
                DeviationController.Apply(Job.Recompute);
            };
            var btnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            btnRow.Controls.Add(btnRecalc);
            addRow(null, btnRow);

            // --- Profilo colore (tempo reale)
            var editProfiles = new Button { Text = L.T("Profili…"), AutoSize = true };
            editProfiles.Click += (s, e) => DeviationController.ShowProfiles();
            var profRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            profRow.Controls.Add(_profile);
            profRow.Controls.Add(editProfiles);
            addRow(L.T("Profilo colore"), profRow);
            addRow(L.T("Nuvola senza modello"), _missing);
            _missStr.BackColor = UiTheme.Surface;
            _missLook.Items.Add(L.T("Sbiaditi"));
            _missLook.Items.Add(L.T("Trasparenti"));
            _missLook.SelectedIndex = 0;
            var missRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            missRow.Controls.Add(_missLook);
            missRow.Controls.Add(_missStr);
            missRow.Controls.Add(_missVal);
            addRow(L.T("Punti senza modello"), missRow);
            new ToolTip().SetToolTip(_missLook, L.T("Trasparenti solo con il disegno con shader; altrimenti sbiaditi. 0% = colore pieno."));
            addRow(null, _invert);
            addRow(null, _status);
            addRow(null, _stats);

            Controls.Add(tl);

            // i valori si caricano PRIMA di collegare gli eventi, così non partono ricalcoli
            LoadFromCurrent();

            _profile.SelectedIndexChanged += OnStyleChanged;
            _missing.SelectedIndexChanged += OnStyleChanged;
            _linkLook.SelectedIndexChanged += OnStyleChanged;
            // il cursore ricolora tutta la nuvola: si applica quando si ferma
            _linkStr.ValueChanged += (s, e) => { _linkVal.Text = _linkStr.Value + "%"; if (_loading) return; _linkTimer.Stop(); _linkTimer.Start(); };
            _linkTimer.Tick += (s, e) => { _linkTimer.Stop(); OnStyleChanged(s, e); };
            _missLook.SelectedIndexChanged += OnStyleChanged;
            _missStr.ValueChanged += (s, e) => { _missVal.Text = _missStr.Value + "%"; if (_loading) return; _linkTimer.Stop(); _linkTimer.Start(); };
            _invert.CheckedChanged += OnStyleChanged;

            // cambiare l'insieme dei modelli confrontati richiede di ricalcolare le distanze
            _links.CheckedChanged += (s, e) => { if (_loading) return; Sync(); DeviationController.Apply(Job.Recompute); };
            _scope.SelectedIndexChanged += (s, e) => { if (_loading) return; Sync(); DeviationController.Apply(Job.Recompute); };

            // con più progetti aperti la finestra mostra sempre i valori del progetto attivo
            DeviationController.ActiveChanged += OnActiveChanged;
            DeviationController.SettingsChanged += OnSettingsChanged;
            FormClosed += (s, e) =>
            {
                DeviationController.ActiveChanged -= OnActiveChanged;
                DeviationController.SettingsChanged -= OnSettingsChanged;
                _linkTimer.Dispose();
            };
        }

        private bool _loading;

        private void OnActiveChanged()
        {
            if (IsDisposed) return;
            _loading = true;
            try
            {
                LoadFromCurrent();
                SetStatus(L.T("Progetto attivo cambiato."));
                SetStats("");
            }
            finally { _loading = false; }
        }

        private void OnSettingsChanged()
        {
            if (IsDisposed) return;
            _loading = true;
            try { LoadFromCurrent(); }
            finally { _loading = false; }
        }

        private static NumericUpDown Num(decimal min, decimal max, decimal val, int decimals, decimal increment)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Increment = increment,
                Value = val,
                Width = 90
            };
        }

        private static void SetClamped(NumericUpDown n, double value)
        {
            decimal d;
            try { d = (decimal)value; } catch { d = n.Minimum; }
            if (d < n.Minimum) d = n.Minimum;
            if (d > n.Maximum) d = n.Maximum;
            n.Value = d;
        }

        private void LoadFromCurrent()
        {
            DeviationSettings c = DeviationController.Current;

            DocumentData dd = DeviationController.ActiveData;
            _path.Text = dd != null && dd.HasCloud ? string.Join(", ", dd.Layers.ConvertAll(l => l.Name)) : L.T("(nessuna: inseriscila dalla libreria)");

            SetClamped(_maxPts, c.MaxPoints / 1000.0);
            _scope.SelectedIndex = c.SelectionOnly ? 1 : 0;
            _links.Checked = c.IncludeLinks;
            _linkLook.SelectedIndex = Math.Max(0, Math.Min(1, c.LinkLook));
            _linkStr.Value = Math.Max(0, Math.Min(90, c.LinkStrength));
            _linkVal.Text = _linkStr.Value + "%";
            _missStr.Value = Math.Max(0, Math.Min(90, c.MissingStrength));
            _missLook.SelectedIndex = Math.Max(0, Math.Min(1, c.MissingLook));
            _missVal.Text = _missStr.Value + "%";
            _profile.Items.Clear();
            int sel = 0;
            for (int i = 0; i < ProfileStore.All.Count; i++)
            {
                _profile.Items.Add(ProfileStore.All[i].Name);
                if (ProfileStore.All[i].Id == c.Profile.Id) sel = i;
            }
            if (_profile.Items.Count > 0) _profile.SelectedIndex = sel;
            _missing.SelectedIndex = (int)c.Missing;
            _invert.Checked = c.InvertSign;
        }

        /// <summary>Scrive i valori dei controlli nelle impostazioni condivise (senza toccare modalità di visualizzazione, aspetto, visibilità e ritaglio).</summary>
        private DeviationSettings Sync()
        {
            DeviationSettings s = DeviationController.Current;
            s.MaxPoints = (int)_maxPts.Value * 1000;
            s.SelectionOnly = _scope.SelectedIndex == 1;
            s.IncludeLinks = _links.Checked;
            s.LinkLook = Math.Max(0, _linkLook.SelectedIndex);
            s.LinkStrength = _linkStr.Value;
            s.MissingStrength = _missStr.Value;
            s.MissingLook = Math.Max(0, _missLook.SelectedIndex);
            if (_profile.SelectedIndex >= 0 && _profile.SelectedIndex < ProfileStore.All.Count)
                s.ProfileId = ProfileStore.All[_profile.SelectedIndex].Id;
            s.Missing = (MissingMode)Math.Max(0, _missing.SelectedIndex);
            s.InvertSign = _invert.Checked;
            return s;
        }

        private void OnStyleChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            Sync();
            DeviationController.Apply(Job.Recolor);
        }

        public void SetStatus(string text)
        {
            _status.Text = text;
            _status.Refresh();
        }

        public void SetStats(string text)
        {
            _stats.Text = text;
            _stats.Refresh();
        }
    }
}
