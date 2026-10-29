using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PointDeviation
{
    /// <summary>
    /// Impostazioni avanzate: registro di diagnostica (livello, file), strumenti di diagnostica del disegno,
    /// prestazioni (limite di punti, memoria). Valgono per il computer, non per il progetto.
    /// </summary>
    internal sealed class AdvancedForm : Form
    {
        private readonly ListBox _nav = new ListBox { Dock = DockStyle.Left, Width = 170, BorderStyle = BorderStyle.None, IntegralHeight = false, ItemHeight = 30, DrawMode = DrawMode.OwnerDrawFixed };
        private readonly Panel _host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 14) };
        private readonly Panel[] _pages = new Panel[3];

        private readonly System.Windows.Forms.CheckBox _logOn = new System.Windows.Forms.CheckBox { AutoSize = true };
        private readonly ListBox _level = new ListBox { Height = 4 * 44, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44, BorderStyle = BorderStyle.FixedSingle };
        private readonly TextBox _path = new TextBox { ReadOnly = true, Width = 380 };
        private readonly System.Windows.Forms.CheckBox _levels = new System.Windows.Forms.CheckBox { AutoSize = true };
        private readonly NumericUpDown _budget = new NumericUpDown { Minimum = 25, Maximum = 400, Increment = 25, Width = 80 };
        private readonly NumericUpDown _gpuKeep = new NumericUpDown { Minimum = 2, Maximum = 600, Width = 80 };
        private readonly NumericUpDown _memKeep = new NumericUpDown { Minimum = 5, Maximum = 3600, Width = 80 };

        private static readonly string[][] Levels =
        {
            new[] { "Errori", "Solo gli errori che il plugin non riesce a gestire." },
            new[] { "Avvisi", "Anche le situazioni anomale gestite (es. camera non disponibile)." },
            new[] { "Info", "Anche caricamento delle nuvole, posizionamento, estrazione (consigliato)." },
            new[] { "Debug", "Anche lo stato del disegno ogni pochi secondi. Rallenta un poco: solo per indagare un problema." }
        };

        public AdvancedForm()
        {
            Text = L.T("Impostazioni avanzate");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(700, 470);
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = UiTheme.Surface;

            _nav.BackColor = UiTheme.Panel;
            foreach (string s in new[] { "Registro", "Diagnostica", "Prestazioni" }) _nav.Items.Add(L.T(s));
            _nav.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var br = new SolidBrush(sel ? Color.FromArgb(200, 222, 245) : UiTheme.Panel)) e.Graphics.FillRectangle(br, e.Bounds);
                TextRenderer.DrawText(e.Graphics, _nav.Items[e.Index].ToString(), UiTheme.Base, new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height),
                    UiTheme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            _nav.SelectedIndexChanged += (s, e) => ShowPage(_nav.SelectedIndex);

            _pages[0] = PageLog();
            _pages[1] = PageDiag();
            _pages[2] = PagePerf();
            foreach (Panel p in _pages) { p.Dock = DockStyle.Fill; p.Visible = false; _host.Controls.Add(p); }

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10, 8, 10, 8) };
            var close = new Button { Text = L.T("Chiudi"), AutoSize = true };
            var apply = new Button { Text = L.T("Applica"), AutoSize = true };
            close.Click += (s, e) => Close();
            apply.Click += (s, e) => Apply();
            buttons.Controls.Add(close);
            buttons.Controls.Add(apply);

            Controls.Add(_host);
            Controls.Add(_nav);
            Controls.Add(buttons);
            LoadValues();
            _nav.SelectedIndex = 0;
            UiTheme.Apply(this, apply);
        }

        private void ShowPage(int i)
        {
            for (int k = 0; k < _pages.Length; k++) _pages[k].Visible = k == i;
        }

        private static Label Title(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", 15f), Margin = new Padding(0, 0, 0, 8) };
        }

        private static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = UiTheme.Muted, Margin = new Padding(0, 2, 0, 10) };
        }

        private static FlowLayoutPanel Stack()
        {
            return new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        }

        private Panel PageLog()
        {
            var p = Stack();
            p.Controls.Add(Title(L.T("Registro")));
            p.Controls.Add(Note(L.T("Il registro aiuta a capire cosa succede quando qualcosa non va: caricamento delle nuvole, errori di disegno e di lettura, posizionamento.")));
            _logOn.Text = L.T("Scrivi il registro");
            _logOn.CheckedChanged += (s, e) => _level.Enabled = _logOn.Checked;
            p.Controls.Add(_logOn);
            p.Controls.Add(new Label { Text = L.T("Livello di dettaglio:"), AutoSize = true, Margin = new Padding(0, 12, 0, 4) });
            _level.Width = 470;
            foreach (string[] l in Levels) _level.Items.Add(l);
            _level.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                var it = (string[])_level.Items[e.Index];
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var br = new SolidBrush(sel ? UiTheme.Accent : UiTheme.Surface)) e.Graphics.FillRectangle(br, e.Bounds);
                Color fg = sel ? Color.White : _level.Enabled ? UiTheme.Ink : UiTheme.Muted;
                using (var bold = new Font("Segoe UI", 9f, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, L.T(it[0]), bold, new Point(e.Bounds.X + 8, e.Bounds.Y + 4), fg);
                TextRenderer.DrawText(e.Graphics, L.T(it[1]), UiTheme.Base, new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 22, e.Bounds.Width - 12, 20),
                    sel ? Color.White : UiTheme.Muted, TextFormatFlags.EndEllipsis);
            };
            p.Controls.Add(_level);

            p.Controls.Add(new Label { Text = L.T("File del registro:"), AutoSize = true, Margin = new Padding(0, 14, 0, 4) });
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            _path.Text = Log.FilePath;
            var open = new Button { Text = L.T("Apri"), AutoSize = true };
            var folder = new Button { Text = L.T("Cartella"), AutoSize = true };
            open.Click += (s, e) =>
            {
                try
                {
                    if (!File.Exists(Log.FilePath)) { Directory.CreateDirectory(Log.Folder); File.AppendAllText(Log.FilePath, ""); }
                    Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
                }
                catch { }
            };
            folder.Click += (s, e) => { try { Directory.CreateDirectory(Log.Folder); Process.Start("explorer.exe", "\"" + Log.Folder + "\""); } catch { } };
            row.Controls.Add(_path); row.Controls.Add(open); row.Controls.Add(folder);
            p.Controls.Add(row);
            return p;
        }

        private Panel PageDiag()
        {
            var p = Stack();
            p.Controls.Add(Title(L.T("Diagnostica")));
            _levels.Text = L.T("Colori per livello dell'octree");
            p.Controls.Add(_levels);
            p.Controls.Add(Note(L.T("Ogni livello ha un colore (rosso il più grossolano, poi arancio, giallo, verde, azzurro, blu, viola…). Punti grandi a colori pieni, punti di un pixel più chiari. Serve a vedere quali blocchi si disegnano e dove.")));
            p.Controls.Add(Note(L.T("La riga di stato con punti, blocchi da caricare e da costruire è in Visibilità › Gestisci.")));
            return p;
        }

        private Panel PagePerf()
        {
            var p = Stack();
            p.Controls.Add(Title(L.T("Prestazioni")));
            p.Controls.Add(Row(L.T("Limite di punti disegnati (% della qualità scelta)"), _budget));
            p.Controls.Add(Note(L.T("Più punti danno più dettaglio ma chiedono più memoria video e rendono la navigazione meno fluida.")));
            p.Controls.Add(Row(L.T("Libera la memoria video dopo (secondi fuori vista)"), _gpuKeep));
            p.Controls.Add(Row(L.T("Libera la memoria del computer dopo (secondi fuori vista)"), _memKeep));
            p.Controls.Add(Note(L.T("Tempi lunghi: tornando su una zona appena vista è già pronta. Tempi brevi: meno memoria occupata.")));
            return p;
        }

        private static Control Row(string text, Control c)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
            row.Controls.Add(new Label { Text = text, AutoSize = true, Width = 360, Margin = new Padding(0, 5, 10, 0) });
            row.Controls.Add(c);
            return row;
        }

        private void LoadValues()
        {
            AdvancedSettings a = AdvancedSettings.Current;
            _logOn.Checked = a.LogLevel != LogLevel.Spento;
            _level.SelectedIndex = Math.Max(0, (int)(a.LogLevel == LogLevel.Spento ? LogLevel.Info : a.LogLevel) - 1);
            _level.Enabled = _logOn.Checked;
            _levels.Checked = CloudDebug.Levels;
            _budget.Value = a.BudgetPercent;
            _gpuKeep.Value = a.GpuKeepSeconds;
            _memKeep.Value = a.MemKeepSeconds;
        }

        private void Apply()
        {
            AdvancedSettings a = AdvancedSettings.Current;
            a.LogLevel = _logOn.Checked ? (LogLevel)(_level.SelectedIndex + 1) : LogLevel.Spento;
            a.BudgetPercent = (int)_budget.Value;
            a.GpuKeepSeconds = (int)_gpuKeep.Value;
            a.MemKeepSeconds = (int)_memKeep.Value;
            a.Save();
            bool levelsChanged = CloudDebug.Levels != _levels.Checked;
            CloudDebug.Levels = _levels.Checked;
            if (levelsChanged) DeviationController.Apply(Job.Recolor);
            Redraw.Request();
        }
    }
}
