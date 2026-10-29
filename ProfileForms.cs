using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace PointDeviation
{
    // =====================================================================================
    //  Elenco dei profili colore
    // =====================================================================================

    internal sealed class ProfileManagerForm : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UiTheme.Apply(this);
        }

        private readonly ListBox _list = new BufferedListBox
        {
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 40,
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            BorderStyle = BorderStyle.None
        };

        public ProfileManagerForm()
        {
            Text = L.T("Profili colore");
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(500, 360);
            MinimumSize = new Size(420, 260);
            Font = new Font("Segoe UI", 9f);

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Padding = new Padding(8, 6, 8, 0),
                ForeColor = SystemColors.GrayText,
                Text = L.T("Doppio clic per usare un profilo nel progetto attivo.")
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6), FlowDirection = FlowDirection.LeftToRight };
            var use = new Button { Text = L.T("Usa nel progetto"), AutoSize = true };
            var edit = new Button { Text = L.T("Modifica…"), AutoSize = true };
            var dup = new Button { Text = L.T("Duplica"), AutoSize = true };
            var del = new Button { Text = L.T("Elimina"), AutoSize = true };
            var add = new Button { Text = L.T("Nuovo ▾"), AutoSize = true };
            var reset = new Button { Text = L.T("Ripristina predefiniti"), AutoSize = true };
            buttons.Controls.AddRange(new Control[] { use, edit, dup, del, add, reset });

            var addMenu = new ContextMenuStrip();
            addMenu.Items.Add(L.T("Gradiente (soglie e colori)"), null, (s, e) => CreateNew(ProfileKind.Gradiente));
            addMenu.Items.Add(L.T("Tolleranza a fasce (entro / avviso / fuori)"), null, (s, e) => CreateNew(ProfileKind.Fasce));
            addMenu.Items.Add(L.T("Classi (intervalli con etichetta)"), null, (s, e) => CreateNew(ProfileKind.Classi));
            addMenu.Items.Add(new ToolStripSeparator());
            addMenu.Items.Add(L.T("Completezza (punti lontani dal modello)"), null, (s, e) => CreateNew(ProfileKind.Completezza));
            addMenu.Items.Add(L.T("Interferenze (punti vicini o dentro il modello)"), null, (s, e) => CreateNew(ProfileKind.Interferenze));
            add.Click += (s, e) => addMenu.Show(add, new Point(0, add.Height));

            use.Click += (s, e) => UseSelected();
            _list.DoubleClick += (s, e) => UseSelected();
            edit.Click += (s, e) => EditSelected();
            dup.Click += (s, e) =>
            {
                ColorProfile p = Selected;
                if (p == null) return;
                ColorProfile c = p.Clone();
                c.Id = Guid.NewGuid().ToString("N");
                c.Name = p.Name + L.T(" (copia)");
                ProfileStore.Put(c);
                Reload(c.Id);
            };
            del.Click += (s, e) =>
            {
                ColorProfile p = Selected;
                if (p == null) return;
                if (MessageBox.Show(this, L.T("Eliminare il profilo «") + p.Name + "»?", L.T("Profili colore"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                ProfileStore.Remove(p.Id);
                Reload(null);
                ProfilesChanged();
            };
            reset.Click += (s, e) =>
            {
                ProfileStore.RestoreDefaults();
                Reload(null);
                ProfilesChanged();
            };

            _list.DrawItem += DrawItem;
            Controls.Add(_list);
            Controls.Add(hint);
            Controls.Add(buttons);

            DeviationController.ActiveChanged += OnActive;
            DeviationController.SettingsChanged += OnActive;
            FormClosed += (s, e) =>
            {
                DeviationController.ActiveChanged -= OnActive;
                DeviationController.SettingsChanged -= OnActive;
            };
            Reload(DeviationController.Current.ProfileId);
        }

        private void OnActive()
        {
            if (!IsDisposed) _list.Invalidate();
        }

        private ColorProfile Selected
        {
            get { return _list.SelectedIndex >= 0 && _list.SelectedIndex < ProfileStore.All.Count ? ProfileStore.All[_list.SelectedIndex] : null; }
        }

        private void Reload(string selectId)
        {
            _list.Items.Clear();
            int sel = 0;
            for (int i = 0; i < ProfileStore.All.Count; i++)
            {
                _list.Items.Add(ProfileStore.All[i].Name);
                if (ProfileStore.All[i].Id == selectId) sel = i;
            }
            if (_list.Items.Count > 0) _list.SelectedIndex = sel;
        }

        private void UseSelected()
        {
            ColorProfile p = Selected;
            if (p == null) return;
            DeviationController.Current.ProfileId = p.Id;
            ProfilesChanged();
        }

        /// <summary>Profilo attivo o profili modificati: aggiorna finestre, legenda e colori.</summary>
        internal static void ProfilesChanged()
        {
            DeviationController.NotifySettingsChanged();
            DeviationController.Apply(Job.Recolor); // ricalcola da solo se il nuovo intervallo è più ampio
        }

        private void EditSelected()
        {
            ColorProfile p = Selected;
            if (p == null) return;
            using (var ed = new ProfileEditorForm(p.Clone()))
            {
                if (ed.ShowDialog(this) != DialogResult.OK) return;
                ProfileStore.Put(ed.Result);
                Reload(ed.Result.Id);
                ProfilesChanged();
            }
        }

        private void CreateNew(ProfileKind kind)
        {
            using (var ed = new ProfileEditorForm(ColorProfile.NewOfKind(kind)))
            {
                if (ed.ShowDialog(this) != DialogResult.OK) return;
                ProfileStore.Put(ed.Result);
                Reload(ed.Result.Id);
                ProfilesChanged();
            }
        }

        private void DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= ProfileStore.All.Count) return;
            ColorProfile p = ProfileStore.All[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(selected ? Color.FromArgb(214, 232, 250) : Color.White))
                e.Graphics.FillRectangle(bg, e.Bounds);

            var bar = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top + 12, 120, 16);
            DrawBar(e.Graphics, p, bar);

            bool active = p.Id == DeviationController.Current.Profile.Id;
            using (var f = new Font(Font, active ? FontStyle.Bold : FontStyle.Regular))
                e.Graphics.DrawString((active ? "● " : "") + p.Name, f, Brushes.Black, bar.Right + 12, e.Bounds.Top + 4);
            e.Graphics.DrawString(p.Description, Font, Brushes.Gray, bar.Right + 12, e.Bounds.Top + 21);
            e.Graphics.DrawLine(Pens.Gainsboro, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        /// <summary>Barra orizzontale del profilo, dal minimo (sinistra) al massimo (destra).</summary>
        internal static void DrawBar(Graphics g, ColorProfile p, Rectangle r)
        {
            g.DrawImageUnscaled(BarImage(p, r.Width, r.Height), r.Left, r.Top);
            g.DrawRectangle(Pens.DimGray, r);
        }

        // barre già calcolate, per profilo e dimensione: ridisegnare l'elenco non ricalcola più i colori pixel per pixel
        private static readonly Dictionary<string, Bitmap> _bars = new Dictionary<string, Bitmap>();

        private static Bitmap BarImage(ColorProfile p, int w, int h)
        {
            string key;
            using (var ms = new System.IO.MemoryStream())
            {
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ColorProfile)).WriteObject(ms, p);
                key = Convert.ToBase64String(ms.ToArray()) + "|" + w + "x" + h;
            }
            Bitmap bmp;
            if (_bars.TryGetValue(key, out bmp)) return bmp;

            if (_bars.Count > 64)
            {
                foreach (Bitmap b in _bars.Values) b.Dispose();
                _bars.Clear();
            }
            double mn, mx;
            p.GetRange(out mn, out mx);
            bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                for (int x = 0; x < w; x++)
                {
                    double v = mn + (mx - mn) * (x + 0.5) / w;
                    byte cr, cg, cb;
                    Color c = p.TryColor(v, out cr, out cg, out cb) ? Color.FromArgb(cr, cg, cb) : Color.White;
                    using (var pen = new Pen(c)) g.DrawLine(pen, x, 0, x, h);
                }
            }
            _bars[key] = bmp;
            return bmp;
        }
    }

    /// <summary>Elenco disegnato prima in memoria e poi mostrato tutto insieme: niente ridisegno riga per riga.</summary>
    internal sealed class BufferedListBox : ListBox
    {
        public BufferedListBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // con UserPaint il ListBox non si disegna da solo: si disegnano qui tutte le voci visibili, in memoria
            e.Graphics.Clear(BackColor);
            for (int i = TopIndex; i < Items.Count; i++)
            {
                Rectangle r = GetItemRectangle(i);
                if (r.Top > ClientSize.Height) break;
                var state = SelectedIndex == i ? DrawItemState.Selected : DrawItemState.None;
                OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, r, i, state));
            }
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x14;
            const int WM_VSCROLL = 0x115, WM_MOUSEWHEEL = 0x20A, WM_KEYDOWN = 0x100;
            if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; } // niente cancellazione a vuoto prima del disegno
            base.WndProc(ref m);
            if (m.Msg == WM_VSCROLL || m.Msg == WM_MOUSEWHEEL || m.Msg == WM_KEYDOWN) Invalidate(); // scorrimento: ridisegno in memoria
        }
    }

    // =====================================================================================
    //  Editor di un profilo: intervalli e colori, con anteprima della legenda
    // =====================================================================================

    internal sealed class ProfileEditorForm : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UiTheme.Apply(this);
        }

        public ColorProfile Result { get; private set; }

        private readonly ColorProfile _p;
        private readonly TextBox _name = new TextBox { Width = 300 };
        private readonly CheckBox _abs = new CheckBox { Text = L.T("Usa il valore assoluto (il segno non conta: planarità, accuratezza)"), AutoSize = true };
        private readonly Panel _preview = new Panel { Width = 230, Dock = DockStyle.Right, BackColor = Color.White };
        private DataGridView _grid;

        // fasce
        private NumericUpDown _bMax, _wMax, _pMax, _pMin, _wMin, _bMin;
        private CheckBox _useWarn;
        private Button _cPass, _cWarn, _cFail;

        public ProfileEditorForm(ColorProfile working)
        {
            _p = working;
            string[] kinds = { L.T("gradiente"), L.T("tolleranza a fasce"), L.T("classi"), L.T("completezza"), L.T("interferenze") };
            Text = L.T("Profilo colore: ") + kinds[(int)working.Kind];
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 500);
            MinimumSize = new Size(640, 420);
            Font = new Font("Segoe UI", 9f);

            _preview.Paint += (s, e) => LegendForm.DrawProfile(e.Graphics, _p, MissingMode.Evidenzia, _preview.ClientRectangle, Font, true);
            _preview.Resize += (s, e) => _preview.Invalidate();

            var main = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            top.Controls.Add(new Label { Text = L.T("Nome"), AutoSize = true, Margin = new Padding(0, 6, 10, 0) }, 0, 0);
            top.Controls.Add(_name, 1, 0);
            if (!_p.IsSelection)
            {
                top.Controls.Add(_abs, 1, 1);
                var showMissing = new CheckBox
                {
                    Text = L.T("Mostra la nuvola senza modello (vista e legenda)"), AutoSize = true, Checked = !_p.HideMissing
                };
                showMissing.CheckedChanged += (s, e) => { _p.HideMissing = !showMissing.Checked; Changed(); };
                top.Controls.Add(showMissing, 1, 2);
            }
            _name.Text = _p.Name;
            _abs.Checked = _p.Absolute;
            _abs.CheckedChanged += (s, e) => { _p.Absolute = _abs.Checked; Changed(); };
            _name.TextChanged += (s, e) => { _p.Name = _name.Text; _preview.Invalidate(); };

            Control body;
            switch (_p.Kind)
            {
                case ProfileKind.Fasce: body = BuildBands(); break;
                case ProfileKind.Classi: body = BuildClasses(); break;
                case ProfileKind.Completezza:
                case ProfileKind.Interferenze: body = BuildSelection(); break;
                default: body = BuildGradient(); break;
            }
            body.Dock = DockStyle.Fill;

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var cancel = new Button { Text = L.T("Annulla"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = L.T("Applica"), AutoSize = true };
            ok.Click += (s, e) => Accept();
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;

            main.Controls.Add(body);
            main.Controls.Add(new Label { Dock = DockStyle.Top, Height = 8 });
            main.Controls.Add(top);
            Controls.Add(main);
            Controls.Add(_preview);
            Controls.Add(bottom);
        }

        private void Changed()
        {
            _preview.Invalidate();
        }

        private static string Num(double v) { return v.ToString("0.###", CultureInfo.CurrentCulture); }

        private static bool TryNum(object o, out double v)
        {
            string s = Convert.ToString(o, CultureInfo.CurrentCulture) ?? "";
            return double.TryParse(s.Replace('−', '-'), NumberStyles.Float, CultureInfo.CurrentCulture, out v)
                || double.TryParse(s.Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private Color PickColor(Color start)
        {
            using (var dlg = new ColorDialog { Color = start, FullOpen = true, AnyColor = true })
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.Color : start;
        }

        // ------------------------------------------------------------------ gradiente

        private Control BuildGradient()
        {
            var panel = new Panel();
            _grid = NewGrid();
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Valore (mm)"), Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Colore (clic per cambiare)"), Width = 200, ReadOnly = true });
            foreach (ColorStop st in _p.Stops.OrderByDescending(x => x.ValueMm))
                AddRow(Num(st.ValueMm), st.Color);

            var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36 };
            var add = new Button { Text = L.T("Aggiungi soglia"), AutoSize = true };
            var rem = new Button { Text = L.T("Rimuovi"), AutoSize = true };
            var mirror = new Button { Text = L.T("Rendi simmetrico ±"), AutoSize = true };
            add.Click += (s, e) =>
            {
                double mn, mx;
                _p.GetRange(out mn, out mx);
                AddRow(Num((mn + mx) / 2), Color.White);
                ReadGrid();
            };
            rem.Click += (s, e) => { RemoveSelectedRow(); ReadGrid(); };
            mirror.Click += (s, e) =>
            {
                // copia le soglie positive sul lato negativo con gli stessi colori (o viceversa se mancano)
                ReadGrid();
                var pos = _p.Stops.Where(x => x.ValueMm > 0).ToList();
                if (pos.Count == 0) pos = _p.Stops.Where(x => x.ValueMm < 0).Select(x => new ColorStop(-x.ValueMm, x.Color)).ToList();
                var all = new List<ColorStop>(pos);
                all.AddRange(pos.Select(x => new ColorStop(-x.ValueMm, x.Color)));
                ColorStop zero = _p.Stops.FirstOrDefault(x => x.ValueMm == 0);
                if (zero != null) all.Add(zero);
                _p.Stops = all.OrderByDescending(x => x.ValueMm).ToList();
                _grid.Rows.Clear();
                foreach (ColorStop st in _p.Stops) AddRow(Num(st.ValueMm), st.Color);
                Changed();
            };
            var mirrorBox = new CheckBox { Text = L.T("Colori a specchio sopra e sotto lo zero"), AutoSize = true, Checked = _p.Mirror, Margin = new Padding(12, 6, 0, 0) };
            mirrorBox.CheckedChanged += (s, e) => { _p.Mirror = mirrorBox.Checked; Changed(); };
            tools.Controls.AddRange(new Control[] { add, rem, mirror, mirrorBox });

            var note = new Label
            {
                Dock = DockStyle.Bottom, Height = 52, ForeColor = SystemColors.GrayText,
                Text = L.T("Ogni soglia ha un colore; tra due soglie il colore sfuma. Due soglie con lo stesso colore creano una fascia\n") +
                       L.T("uniforme. Oltre il primo e l'ultimo valore: «nessun modello». «A specchio»: inserisci solo soglie da 0 in su,\n") +
                       L.T("valgono uguali anche sotto lo zero.")
            };
            panel.Controls.Add(_grid);
            panel.Controls.Add(note);
            panel.Controls.Add(tools);
            return panel;
        }

        // ------------------------------------------------------------------ classi

        private Control BuildClasses()
        {
            var panel = new Panel();
            _grid = NewGrid();
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Da (mm)"), Width = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("A (mm)"), Width = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Colore"), Width = 90, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Etichetta"), Width = 170 });
            foreach (ColorClass c in _p.Classes)
            {
                int i = _grid.Rows.Add(Num(c.FromMm), Num(c.ToMm), "", c.Label ?? "");
                SetSwatch(_grid.Rows[i].Cells[2], c.Color);
            }

            var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36 };
            var add = new Button { Text = L.T("Aggiungi classe"), AutoSize = true };
            var rem = new Button { Text = L.T("Rimuovi"), AutoSize = true };
            add.Click += (s, e) =>
            {
                double last = _p.Classes.Count > 0 ? _p.Classes.Max(c => c.ToMm) : 0;
                int i = _grid.Rows.Add(Num(last), Num(last + 10), "", "");
                SetSwatch(_grid.Rows[i].Cells[2], Color.Gray);
                ReadGrid();
            };
            rem.Click += (s, e) => { RemoveSelectedRow(); ReadGrid(); };
            tools.Controls.AddRange(new Control[] { add, rem });

            var note = new Label
            {
                Dock = DockStyle.Bottom, Height = 36, ForeColor = SystemColors.GrayText,
                Text = L.T("Ogni classe è un intervallo con un colore pieno. Se due classi si sovrappongono vale la prima.\n") +
                       L.T("Valori che non cadono in nessuna classe: «nessun modello».")
            };
            panel.Controls.Add(_grid);
            panel.Controls.Add(note);
            panel.Controls.Add(tools);
            return panel;
        }

        // ------------------------------------------------------------------ fasce di tolleranza

        private Control BuildBands()
        {
            var tl = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 8, 0, 0) };
            Func<double, NumericUpDown> num = v => new NumericUpDown
            {
                Minimum = -100000, Maximum = 100000, DecimalPlaces = 1, Increment = 1, Width = 90,
                Value = (decimal)Math.Max(-100000, Math.Min(100000, v))
            };
            _bMax = num(_p.BandMaxMm); _wMax = num(_p.WarnMaxMm); _pMax = num(_p.PassMaxMm);
            _pMin = num(_p.PassMinMm); _wMin = num(_p.WarnMinMm); _bMin = num(_p.BandMinMm);
            _useWarn = new CheckBox { Text = L.T("Usa la fascia di avviso"), AutoSize = true, Checked = _p.UseWarn };
            _cPass = ColorButton(Color.FromArgb(_p.PassArgb), c => _p.PassArgb = c.ToArgb());
            _cWarn = ColorButton(Color.FromArgb(_p.WarnArgb), c => _p.WarnArgb = c.ToArgb());
            _cFail = ColorButton(Color.FromArgb(_p.FailArgb), c => _p.FailArgb = c.ToArgb());

            Action<string, Control, Control> row = (label, c1, c2) =>
            {
                int r = tl.RowCount++;
                tl.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 12, 4) }, 0, r);
                tl.Controls.Add(c1, 1, r);
                if (c2 != null) tl.Controls.Add(c2, 2, r);
            };
            row(L.T("Massimo (mm)"), _bMax, new Label { Text = L.T("oltre: nessun modello"), AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 6, 0, 0) });
            row(L.T("Avviso fino a (mm)"), _wMax, null);
            row(L.T("Tolleranza fino a (mm)"), _pMax, null);
            row(L.T("Tolleranza da (mm)"), _pMin, null);
            row(L.T("Avviso da (mm)"), _wMin, null);
            row(L.T("Minimo (mm)"), _bMin, null);
            row("", _useWarn, null);
            row(L.T("Colore entro tolleranza"), _cPass, null);
            row(L.T("Colore avviso"), _cWarn, null);
            row(L.T("Colore fuori tolleranza"), _cFail, null);

            EventHandler read = (s, e) => { ReadBands(); Changed(); };
            foreach (NumericUpDown n in new[] { _bMax, _wMax, _pMax, _pMin, _wMin, _bMin }) n.ValueChanged += read;
            _useWarn.CheckedChanged += (s, e) => { _wMax.Enabled = _wMin.Enabled = _cWarn.Enabled = _useWarn.Checked; read(s, e); };
            _wMax.Enabled = _wMin.Enabled = _cWarn.Enabled = _useWarn.Checked;

            var sym = new CheckBox { Text = L.T("Simmetrico ± (i valori negativi seguono quelli positivi)"), AutoSize = true, Checked = _p.Symmetric };
            Action applySym = () =>
            {
                _pMin.Enabled = !sym.Checked; _bMin.Enabled = !sym.Checked; _wMin.Enabled = !sym.Checked && _useWarn.Checked;
                if (!sym.Checked) return;
                _pMin.Value = Math.Max(_pMin.Minimum, -_pMax.Value);
                _wMin.Value = Math.Max(_wMin.Minimum, -_wMax.Value);
                _bMin.Value = Math.Max(_bMin.Minimum, -_bMax.Value);
            };
            sym.CheckedChanged += (s, e) => { _p.Symmetric = sym.Checked; applySym(); read(s, e); };
            foreach (NumericUpDown n in new[] { _bMax, _wMax, _pMax }) n.ValueChanged += (s, e) => applySym();
            _useWarn.CheckedChanged += (s, e) => applySym();
            row("", sym, null);
            applySym();

            var host = new Panel { AutoScroll = true };
            host.Controls.Add(tl);
            return host;
        }

        // ------------------------------------------------------------------ completezza / interferenze

        private Control BuildSelection()
        {
            bool clash = _p.Kind == ProfileKind.Interferenze;
            var tl = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 8, 0, 0) };
            Action<string, Control> row = (label, c) =>
            {
                int r = tl.RowCount++;
                tl.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 12, 4) }, 0, r);
                tl.Controls.Add(c, 1, r);
            };

            var intro = new Label
            {
                AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 10),
                Text = clash
                    ? L.T("Evidenzia i punti più vicini della soglia alla superficie più vicina, da entrambi i lati, e quelli che ") +
                      L.T("compenetrano il modello. Utile per verificare ingombri e spazi liberi (impianti, percorsi, sagome).")
                    : L.T("Evidenzia i punti più lontani della soglia da qualunque superficie del modello: parti costruite ma non ") +
                      L.T("modellate (o arredi, persone, vegetazione, rumore).")
            };
            int ri = tl.RowCount++;
            tl.Controls.Add(intro, 0, ri);
            tl.SetColumnSpan(intro, 2);

            var thr = new NumericUpDown { Minimum = 1, Maximum = 100000, DecimalPlaces = 0, Width = 90, Value = (decimal)Math.Max(1, Math.Min(100000, _p.ThresholdMm)) };
            thr.ValueChanged += (s, e) => { _p.ThresholdMm = (double)thr.Value; Changed(); };
            row(clash ? L.T("Distanza massima (mm)") : L.T("Distanza minima (mm)"), thr);
            row(clash ? L.T("Colore vicino al modello") : L.T("Colore dei punti lontani"), ColorButton(Color.FromArgb(_p.HighlightArgb), c => _p.HighlightArgb = c.ToArgb()));
            if (clash) row(L.T("Colore compenetrazione"), ColorButton(Color.FromArgb(_p.InsideArgb), c => _p.InsideArgb = c.ToArgb()));

            var others = new CheckBox { Text = L.T("Mostra gli altri punti in grigio (altrimenti nascosti)"), AutoSize = true, Checked = _p.ShowOthers };
            others.CheckedChanged += (s, e) => { _p.ShowOthers = others.Checked; Changed(); };
            row("", others);

            var host = new Panel { AutoScroll = true };
            host.Controls.Add(tl);
            return host;
        }

        private void ReadBands()
        {
            _p.BandMaxMm = (double)_bMax.Value; _p.WarnMaxMm = (double)_wMax.Value; _p.PassMaxMm = (double)_pMax.Value;
            _p.PassMinMm = (double)_pMin.Value; _p.WarnMinMm = (double)_wMin.Value; _p.BandMinMm = (double)_bMin.Value;
            _p.UseWarn = _useWarn.Checked;
        }

        private Button ColorButton(Color c, Action<Color> set)
        {
            var b = new Button { Width = 90, Height = 24, BackColor = c, FlatStyle = FlatStyle.Flat };
            b.Click += (s, e) =>
            {
                Color n = PickColor(b.BackColor);
                b.BackColor = n;
                set(n);
                Changed();
            };
            return b;
        }

        // ------------------------------------------------------------------ griglia comune

        private DataGridView NewGrid()
        {
            var g = new DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill
            };
            g.CellClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                DataGridViewCell cell = g.Rows[e.RowIndex].Cells[e.ColumnIndex];
                if (!cell.ReadOnly) return; // le celle colore sono le sole in sola lettura
                Color n = PickColor(cell.Style.BackColor);
                SetSwatch(cell, n);
                ReadGrid();
            };
            g.CellEndEdit += (s, e) => ReadGrid();
            return g;
        }

        private void AddRow(string value, Color c)
        {
            int i = _grid.Rows.Add(value, "");
            SetSwatch(_grid.Rows[i].Cells[1], c);
        }

        private static void SetSwatch(DataGridViewCell cell, Color c)
        {
            cell.Style.BackColor = c;
            cell.Style.SelectionBackColor = c;
            cell.Value = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
            cell.Style.ForeColor = c.GetBrightness() < 0.5 ? Color.White : Color.Black;
            cell.Style.SelectionForeColor = cell.Style.ForeColor;
        }

        private void RemoveSelectedRow()
        {
            if (_grid.CurrentRow != null) _grid.Rows.Remove(_grid.CurrentRow);
        }

        /// <summary>Riporta il contenuto della griglia nel profilo (le righe non valide vengono ignorate).</summary>
        private void ReadGrid()
        {
            if (_grid == null) return;
            if (_p.Kind == ProfileKind.Gradiente)
            {
                var stops = new List<ColorStop>();
                foreach (DataGridViewRow r in _grid.Rows)
                {
                    double v;
                    if (TryNum(r.Cells[0].Value, out v)) stops.Add(new ColorStop(v, r.Cells[1].Style.BackColor));
                }
                _p.Stops = stops;
            }
            else if (_p.Kind == ProfileKind.Classi)
            {
                var cls = new List<ColorClass>();
                foreach (DataGridViewRow r in _grid.Rows)
                {
                    double a, b;
                    if (TryNum(r.Cells[0].Value, out a) && TryNum(r.Cells[1].Value, out b))
                        cls.Add(new ColorClass(a, b, r.Cells[2].Style.BackColor, Convert.ToString(r.Cells[3].Value)));
                }
                _p.Classes = cls;
            }
            Changed();
        }

        private void Accept()
        {
            ReadGrid();
            if (_p.Kind == ProfileKind.Fasce) ReadBands();

            string err = null;
            if (string.IsNullOrWhiteSpace(_p.Name)) err = L.T("Dai un nome al profilo.");
            else if (_p.Kind == ProfileKind.Gradiente && _p.Stops.Count < 2) err = L.T("Servono almeno due soglie.");
            else if (_p.Kind == ProfileKind.Classi && (_p.Classes.Count == 0 || _p.Classes.Any(c => c.FromMm == c.ToMm)))
                err = L.T("Ogni classe deve avere «Da» diverso da «A».");
            else if (_p.Kind == ProfileKind.Fasce)
            {
                bool okOrder = _p.UseWarn
                    ? _p.BandMinMm <= _p.WarnMinMm && _p.WarnMinMm <= _p.PassMinMm && _p.PassMinMm < _p.PassMaxMm && _p.PassMaxMm <= _p.WarnMaxMm && _p.WarnMaxMm <= _p.BandMaxMm
                    : _p.BandMinMm <= _p.PassMinMm && _p.PassMinMm < _p.PassMaxMm && _p.PassMaxMm <= _p.BandMaxMm;
                if (!okOrder) err = L.T("I valori devono crescere: minimo ≤ avviso da ≤ tolleranza da < tolleranza fino a ≤ avviso fino a ≤ massimo.");
            }
            if (err != null)
            {
                MessageBox.Show(this, err, L.T("Profilo colore"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_p.Kind == ProfileKind.Gradiente) _p.Stops = _p.Stops.OrderBy(s => s.ValueMm).ToList();
            Result = _p;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
