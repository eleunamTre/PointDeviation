using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PointDeviation
{
    /// <summary>
    /// Libreria delle nuvole convertite. In alto le cartelle-libreria (se ne possono aggiungere più d'una)
    /// e la ricerca; al centro una scheda per nuvola con anteprima, «Anteprima» e «Inserisci»;
    /// la prima scheda «+» converte un nuovo file (LAS, RCP, RCS) o una nuvola già inserita nel progetto.
    /// </summary>
    internal sealed class LibraryForm : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UiTheme.Apply(this);
        }

        private readonly ComboBox _folders = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
        private readonly TextBox _search = new TextBox { Width = 180 };
        private readonly FlowLayoutPanel _cards = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(242, 242, 242),
            Padding = new Padding(10)
        };
        private readonly Label _status = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ProgressBar _progress = new ProgressBar { Width = 180, Visible = false, Maximum = 1000 };
        private readonly Button _cancel = new Button { Text = L.T("Annulla"), AutoSize = true, Visible = false };

        private LibraryList _libs;
        private CancellationTokenSource _cts;
        private bool _busy;

        public LibraryForm()
        {
            Text = L.T("Libreria nuvole");
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 560);
            MinimumSize = new Size(560, 360);
            Font = new Font("Segoe UI", 9f);

            // ---------------------------------------------------------- barra superiore
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 7, 8, 0), WrapContents = false };
            var addFolder = new Button { Text = L.T("Aggiungi cartella…"), AutoSize = true };
            var removeFolder = new Button { Text = L.T("Togli dall'elenco"), AutoSize = true };
            var openFolder = new Button { Text = L.T("Apri"), AutoSize = true };
            var refresh = new Button { Text = L.T("Aggiorna"), AutoSize = true };
            top.Controls.Add(new Label { Text = L.T("Libreria"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            top.Controls.Add(_folders);
            top.Controls.Add(addFolder);
            top.Controls.Add(removeFolder);
            top.Controls.Add(openFolder);
            top.Controls.Add(refresh);
            top.Controls.Add(new Label { Text = L.T("Cerca"), AutoSize = true, Margin = new Padding(16, 6, 4, 0) });
            top.Controls.Add(_search);

            // ---------------------------------------------------------- barra di stato
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 34, ColumnCount = 3, Padding = new Padding(8, 4, 8, 4) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(_status, 0, 0);
            bottom.Controls.Add(_progress, 1, 0);
            bottom.Controls.Add(_cancel, 2, 0);
            _cancel.Click += (s, e) => { if (_cts != null) _cts.Cancel(); };

            Controls.Add(_cards);
            Controls.Add(top);
            Controls.Add(bottom);

            _libs = LibraryStore.Load();
            FillFolders();

            addFolder.Click += (s, e) => AddFolder();
            removeFolder.Click += (s, e) =>
            {
                if (_folders.SelectedIndex < 0) return;
                _libs.Folders.RemoveAt(_folders.SelectedIndex);
                _libs.Current = 0;
                LibraryStore.Save(_libs);
                FillFolders();
            };
            openFolder.Click += (s, e) =>
            {
                string f = CurrentFolder;
                if (f != null && Directory.Exists(f)) Process.Start("explorer.exe", "\"" + f + "\"");
            };
            refresh.Click += (s, e) => RefreshCards();
            _folders.SelectedIndexChanged += (s, e) =>
            {
                _libs.Current = Math.Max(0, _folders.SelectedIndex);
                LibraryStore.Save(_libs);
                RefreshCards();
            };
            _search.TextChanged += (s, e) => RefreshCards();

            DeviationController.ActiveChanged += OnActive;
            FormClosing += (s, e) =>
            {
                if (_busy && MessageBox.Show(this, L.T("C'è una conversione in corso. Interromperla?"), L.T("Libreria nuvole"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                if (_cts != null) _cts.Cancel();
                DeviationController.ActiveChanged -= OnActive;
            };

            RefreshCards();
        }

        private void OnActive()
        {
            if (!IsDisposed) RefreshCards();
        }

        public void SetStatus(string text)
        {
            if (IsDisposed) return;
            _status.Text = (text ?? "").Replace("\n", "   ");
            _status.Refresh();
        }

        private string CurrentFolder
        {
            get { return _folders.SelectedIndex >= 0 && _folders.SelectedIndex < _libs.Folders.Count ? _libs.Folders[_folders.SelectedIndex] : null; }
        }

        // ------------------------------------------------------------------ cartelle

        private void FillFolders()
        {
            _folders.Items.Clear();
            foreach (string f in _libs.Folders) _folders.Items.Add(f);
            if (_folders.Items.Count > 0) _folders.SelectedIndex = Math.Min(_libs.Current, _folders.Items.Count - 1);
            else RefreshCards();
        }

        private bool AddFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = L.T("Scegli la cartella dove salvare le nuvole convertite"), ShowNewFolderButton = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                string f = dlg.SelectedPath;
                int i = _libs.Folders.FindIndex(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
                if (i < 0) { _libs.Folders.Add(f); i = _libs.Folders.Count - 1; }
                _libs.Current = i;
                LibraryStore.Save(_libs);
                FillFolders();
                return true;
            }
        }

        // ------------------------------------------------------------------ schede

        private void RefreshCards()
        {
            _cards.SuspendLayout();
            Control[] old = _cards.Controls.Cast<Control>().ToArray();
            _cards.Controls.Clear();
            foreach (Control c in old) c.Dispose();

            _cards.Controls.Add(AddCard());

            string folder = CurrentFolder;
            if (folder == null)
            {
                SetStatus(L.T("Aggiungi una cartella-libreria per iniziare: le nuvole convertite verranno salvate lì."));
            }
            else
            {
                string q = _search.Text.Trim();
                // nuvole già inserite nel progetto attivo (possono essere più d'una)
                DocumentData ad = DeviationController.ActiveData;
                var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (ad != null) foreach (CloudLayer l in ad.Layers) inUse.Add(SafeFull(l.Folder));
                int n = 0;
                foreach (var kv in LibraryStore.Scan(folder))
                {
                    if (q.Length > 0 && kv.Value.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    bool used = inUse.Contains(SafeFull(kv.Key));
                    _cards.Controls.Add(CloudCard(kv.Key, kv.Value, used));
                    n++;
                }
                if (!_busy) SetStatus(n == 0 ? L.T("Nessuna nuvola convertita in questa cartella. Premi + per convertirne una.") : n + L.T(" nuvole."));
            }
            _cards.ResumeLayout();
            if (IsHandleCreated) UiTheme.Apply(this);
        }

        private static string SafeFull(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\'); } catch { return p; }
        }

        private const int CardW = 200, CardH = 236;

        private Control AddCard()
        {
            var card = new Panel { Width = CardW, Height = CardH, Margin = new Padding(8), BackColor = Color.FromArgb(242, 242, 242), Cursor = Cursors.Hand };
            var plus = new Label
            {
                Text = "+",
                Font = new Font("Segoe UI Light", 54f),
                ForeColor = Color.FromArgb(44, 62, 80),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill
            };
            var caption = new Label { Text = L.T("Converti una nuvola"), Dock = DockStyle.Bottom, Height = 40, TextAlign = ContentAlignment.TopCenter, ForeColor = Color.DimGray };
            card.Controls.Add(plus);
            card.Controls.Add(caption);

            var menu = new ContextMenuStrip();
            menu.Items.Add(L.T("Da file (LAS, RCP, RCS)…"), null, (s, e) => ConvertFromFile());
            menu.Items.Add(L.T("Da una nuvola già inserita nel progetto…"), null, (s, e) => ConvertFromProject());
            EventHandler open = (s, e) => { if (!_busy) menu.Show(card, new Point(CardW / 2, CardH / 2)); };
            card.Click += open; plus.Click += open; caption.Click += open;
            return card;
        }

        private Control CloudCard(string folder, CloudInfo info, bool inUse)
        {
            var card = new Panel { Width = CardW, Height = CardH, Margin = new Padding(8), BackColor = Color.White };
            if (inUse) card.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(31, 114, 207), 3)) e.Graphics.DrawRectangle(pen, 1, 1, card.Width - 3, card.Height - 3);
            };

            var date = new Label
            {
                Text = info.ConvertedDate == DateTime.MinValue ? "" : info.ConvertedDate.ToString("dd/MM/yyyy"),
                Font = new Font(Font.FontFamily, 7.5f), ForeColor = Color.Gray,
                Location = new Point(8, 6), AutoSize = true
            };
            var pic = new PictureBox { Location = new Point(8, 22), Size = new Size(CardW - 16, 112), SizeMode = PictureBoxSizeMode.Zoom };
            string png = Path.Combine(folder, CloudInfo.PreviewName);
            if (File.Exists(png))
            {
                // copia in memoria: il file resta libero (eliminabile, sovrascrivibile)
                try { using (var tmp = Image.FromFile(png)) pic.Image = new Bitmap(tmp); } catch { }
            }
            var name = new Label
            {
                Text = info.Name, Location = new Point(8, 138), Size = new Size(CardW - 16, 34),
                Font = new Font(Font, FontStyle.Bold), AutoEllipsis = true
            };
            var meta = new Label
            {
                Text = string.Format("{0} · {1} punti{2}", info.SourceFormat, Short(info.PointCount), info.HasRgb ? L.T(" · RGB") : "")
                       + (info.IsOctree ? "" : L.T(" · formato vecchio"))
                       + (inUse ? L.T("\nin uso in questo progetto") : ""),
                Location = new Point(8, 172), Size = new Size(CardW - 16, 30), ForeColor = inUse ? Color.FromArgb(31, 114, 207) : Color.DimGray,
                Font = new Font(Font.FontFamily, 8f)
            };
            var preview = new Button { Text = L.T("Anteprima"), Location = new Point(8, 204), Size = new Size(88, 26), FlatStyle = FlatStyle.System };
            var insert = new Button { Text = L.T("Inserisci ▾"), Location = new Point(102, 204), Size = new Size(90, 26), FlatStyle = FlatStyle.System };

            ContextMenuStrip ins = InsertMenu(folder);
            insert.Click += (s, e) => ins.Show(insert, new Point(0, insert.Height));
            preview.Click += (s, e) =>
            {
                using (var pf = new CloudPreviewForm(folder, info, pic.Image, InsertMenu(folder)))
                    pf.ShowDialog(this);
            };

            var ctx = new ContextMenuStrip();
            ctx.Items.Add(L.T("Apri cartella"), null, (s, e) => Process.Start("explorer.exe", "\"" + folder + "\""));
            ctx.Items.Add(L.T("Elimina dalla libreria…"), null, (s, e) => DeleteCloud(folder, info));
            card.ContextMenuStrip = ctx;
            pic.ContextMenuStrip = ctx;

            card.Controls.AddRange(new Control[] { date, pic, name, meta, preview, insert });
            card.Disposed += (s, e) => { if (pic.Image != null) pic.Image.Dispose(); };
            return card;
        }

        private ContextMenuStrip InsertMenu(string folder)
        {
            var m = new ContextMenuStrip();
            m.Items.Add(L.T("Centro su centro"), null, (s, e) => Insert(folder, PlacementMode.CentroSuCentro));
            m.Items.Add(L.T("Origine su origine interna"), null, (s, e) => Insert(folder, PlacementMode.OrigineInterna));
            m.Items.Add(L.T("Coordinate condivise"), null, (s, e) => Insert(folder, PlacementMode.Condivise));
            m.Items.Add(L.T("Origine sull'ultima inserita"), null, (s, e) => Insert(folder, PlacementMode.UltimaInserita));
            return m;
        }

        private void Insert(string folder, PlacementMode mode)
        {
            if (DeviationController.ActiveData == null)
            {
                SetStatus(L.T("Apri un progetto Revit prima di inserire una nuvola."));
                return;
            }
            SetStatus(L.T("Inserimento in corso..."));
            DeviationController.InsertFromLibrary(folder, mode);
            RefreshCards();
        }

        private void DeleteCloud(string folder, CloudInfo info)
        {
            if (MessageBox.Show(this, L.T("Eliminare «") + info.Name + L.T("» dalla libreria?\nLa cartella convertita viene cancellata; il file originale non viene toccato."),
                L.T("Libreria nuvole"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { Directory.Delete(folder, true); }
            catch (Exception ex) { MessageBox.Show(this, L.T("Impossibile eliminare: ") + ex.Message, L.T("Libreria nuvole")); }
            RefreshCards();
        }

        private static string Short(long n)
        {
            if (n >= 1000000) return (n / 1e6).ToString("0.#") + " M";
            if (n >= 1000) return (n / 1e3).ToString("0") + " k";
            return n.ToString();
        }

        // ------------------------------------------------------------------ conversione

        private bool EnsureFolder()
        {
            if (CurrentFolder != null) return true;
            MessageBox.Show(this, L.T("Scegli prima la cartella-libreria dove salvare le nuvole convertite."), L.T("Libreria nuvole"));
            return AddFolder();
        }

        private void ConvertFromFile()
        {
            if (!EnsureFolder()) return;
            string path;
            using (var dlg = new OpenFileDialog
            {
                Title = L.T("Nuvola da convertire"),
                Filter = L.T("Nuvole supportate (*.las;*.e57;*.rcp;*.rcs)|*.las;*.e57;*.rcp;*.rcs|LAS (*.las)|*.las|E57 (*.e57)|*.e57|ReCap (*.rcp;*.rcs)|*.rcp;*.rcs")
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool recap = ext == ".rcp" || ext == ".rcs";
            bool e57 = ext == ".e57";
            // E57: sempre in metri, nessuna unità da scegliere
            LasUnits.Result units = recap || e57 ? null : LasUnits.Detect(path);
            using (var opt = new ConvertOptionsForm(Path.GetFileNameWithoutExtension(path), _libs.Folders, _folders.SelectedIndex, units, recap))
            {
                if (opt.ShowDialog(this) != DialogResult.OK) return;
                if (recap) StartExtraction(path, false, opt);
                else if (e57) StartConversion(() => new E57Source(path), opt.CloudName, opt.Folder, null);
                else StartConversion(() => new LasSource(path, opt.UnitToMeters), opt.CloudName, opt.Folder, null);
            }
        }

        private void ConvertFromProject()
        {
            if (!EnsureFolder()) return;
            using (var opt = new ConvertOptionsForm("", _libs.Folders, _folders.SelectedIndex, null, true))
            {
                if (opt.ShowDialog(this) != DialogResult.OK) return;
                StartExtraction(null, true, opt);
            }
        }

        /// <summary>RCP/RCS: prima Revit estrae i punti in un file temporaneo (thread API), poi parte la conversione.</summary>
        private void StartExtraction(string path, bool fromProject, ConvertOptionsForm opt)
        {
            string raw = Path.Combine(opt.Folder, "_estrazione_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            string chosenName = opt.CloudName;
            string dest = opt.Folder;
            SetBusy(true, L.T("Lettura della nuvola da Revit..."));

            bool queued = DeviationController.RequestExtract(new ExtractRequest
            {
                FilePath = path,
                FromProject = fromProject,
                SpacingM = opt.SpacingM,
                RawPath = raw,
                Progress = msg => SetStatus(msg),
                Done = (rawPath, suggestedName, error) =>
                {
                    if (error != null || rawPath == null)
                    {
                        SetBusy(false, L.T("Lettura non riuscita: ") + (error != null ? error.Message : L.T("nessun punto.")));
                        return;
                    }
                    string name = string.IsNullOrWhiteSpace(chosenName) ? suggestedName : chosenName;
                    _busy = false;
                    StartConversion(() => new RawSource(rawPath), name, dest, rawPath);
                }
            });
            if (!queued) SetBusy(false, L.T("Un'altra lettura è già in corso."));
        }

        private void StartConversion(Func<IPointSource> makeSource, string name, string folder, string rawToDelete)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "nuvola";
            SetBusy(true, L.T("Conversione di «") + name + "»...");
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            DateTime lastUi = DateTime.MinValue;

            Task.Run(() =>
            {
                IPointSource src = makeSource();
                return CloudConverter.Convert(src, folder, name, (msg, f) =>
                {
                    if ((DateTime.Now - lastUi).TotalMilliseconds < 150 && f < 1.0) return;
                    lastUi = DateTime.Now;
                    OnUi(() =>
                    {
                        _status.Text = msg;
                        _progress.Value = Math.Max(0, Math.Min(1000, (int)(f * 1000)));
                    });
                }, ct);
            }, ct).ContinueWith(t =>
            {
                if (rawToDelete != null)
                {
                    try { File.Delete(rawToDelete); } catch { }
                    try { File.Delete(rawToDelete + ".txt"); } catch { }
                }
                OnUi(() =>
                {
                    if (t.IsCanceled || (t.Exception != null && t.Exception.InnerException is OperationCanceledException))
                        SetBusy(false, L.T("Conversione annullata."));
                    else if (t.Exception != null)
                        SetBusy(false, L.T("Conversione non riuscita: ") + t.Exception.InnerException.Message);
                    else
                    {
                        SetBusy(false, L.T("Conversione completata: «") + name + L.T("». Premi Inserisci sulla scheda."));
                        int i = _libs.Folders.FindIndex(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase));
                        if (i >= 0 && i != _folders.SelectedIndex) _folders.SelectedIndex = i; else RefreshCards();
                    }
                });
            });
        }

        /// <summary>Esegue sul thread della finestra (da un thread di lavoro), se la finestra è ancora aperta.</summary>
        private void OnUi(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() => { if (!IsDisposed) a(); }));
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void SetBusy(bool busy, string text)
        {
            _busy = busy;
            _progress.Visible = busy;
            _cancel.Visible = busy;
            if (busy) _progress.Value = 0;
            SetStatus(text);
        }
    }

    // =====================================================================================
    //  Opzioni di conversione
    // =====================================================================================

    internal sealed class ConvertOptionsForm : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UiTheme.Apply(this);
        }

        private readonly List<string> _unitNames = new List<string> { L.T("Metri"), L.T("Millimetri"), L.T("Piedi internazionali"), L.T("Piedi US survey") };
        private readonly List<double> _unitFactors = new List<double> { 1.0, 0.001, 0.3048, 1200.0 / 3937.0 };

        private readonly TextBox _name = new TextBox { Width = 300 };
        private readonly ComboBox _dest = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly ComboBox _unit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        private readonly NumericUpDown _spacing = new NumericUpDown { Minimum = 1, Maximum = 200, Value = 5, DecimalPlaces = 0, Width = 80 };
        private readonly List<string> _folders;

        public string CloudName { get { return _name.Text.Trim(); } }
        public string Folder { get { return _folders[Math.Max(0, _dest.SelectedIndex)]; } }
        public double UnitToMeters { get { return _unitFactors[Math.Max(0, _unit.SelectedIndex)]; } }
        public double SpacingM { get { return (double)_spacing.Value / 1000.0; } }

        public ConvertOptionsForm(string name, List<string> folders, int folderIndex, LasUnits.Result units, bool showSpacing)
        {
            _folders = folders;
            Text = L.T("Converti nella libreria");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = new Font("Segoe UI", 9f);

            var tl = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
            Action<string, Control> row = (t, c) =>
            {
                int r = tl.RowCount++;
                tl.Controls.Add(new Label { Text = t, AutoSize = true, Margin = new Padding(0, 6, 10, 4) }, 0, r);
                tl.Controls.Add(c, 1, r);
            };

            _name.Text = name;
            row(L.T("Nome"), _name);
            foreach (string f in folders) _dest.Items.Add(f);
            _dest.SelectedIndex = Math.Max(0, Math.Min(folderIndex, folders.Count - 1));
            row(L.T("Salva in"), _dest);

            if (units != null)
            {
                // unità letta dal file: preselezionata (o aggiunta se non è tra quelle comuni), sempre modificabile
                int idx = _unitFactors.FindIndex(f => Math.Abs(f - units.MetersPerUnit) < 1e-9);
                if (idx < 0)
                {
                    _unitNames.Add(string.Format(L.T("Dal file ({0:0.######} m per unità)"), units.MetersPerUnit));
                    _unitFactors.Add(units.MetersPerUnit);
                    idx = _unitFactors.Count - 1;
                }
                foreach (string u in _unitNames) _unit.Items.Add(u);
                _unit.SelectedIndex = idx;
                row(L.T("Unità del file"), _unit);

                var unitNote = new Label
                {
                    AutoSize = true, MaximumSize = new Size(300, 0), Margin = new Padding(0, 0, 0, 6),
                    ForeColor = units.Found ? Color.FromArgb(0, 120, 60) : SystemColors.GrayText,
                    Text = units.Description
                };
                int ur = tl.RowCount++;
                tl.Controls.Add(unitNote, 1, ur);
            }
            if (showSpacing)
            {
                row(L.T("Decimazione: passo minimo (mm)"), _spacing);
                var note = new Label
                {
                    AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText,
                    Text = L.T("Le nuvole ReCap vengono lette attraverso Revit (serve un progetto aperto). ") +
                           L.T("Decimazione: i punti più vicini di questo passo vengono diradati (il file originale non cambia). Un passo più piccolo dà più punti ma richiede più tempo e spazio. ") +
                           L.T("Lascia vuoto il nome per usare quello della nuvola.")
                };
                int r = tl.RowCount++;
                tl.Controls.Add(note, 0, r);
                tl.SetColumnSpan(note, 2);
            }

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var cancel = new Button { Text = L.T("Annulla"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = L.T("Converti"), DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            int br = tl.RowCount++;
            tl.Controls.Add(buttons, 0, br);
            tl.SetColumnSpan(buttons, 2);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(tl);
        }
    }

    // =====================================================================================
    //  Anteprima grande con i dati della nuvola
    // =====================================================================================

    internal sealed class CloudPreviewForm : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UiTheme.Apply(this);
        }

        public CloudPreviewForm(string folder, CloudInfo info, Image image, ContextMenuStrip insertMenu)
        {
            Text = info.Name;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 470);
            Font = new Font("Segoe UI", 9f);

            var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Image = image };
            var side = new Panel { Dock = DockStyle.Right, Width = 300, Padding = new Padding(12) };

            double[] size = info.SizeM, mid = info.MidM;
            string notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : L.T("\n\nNote di conversione:\n") + info.Notes;
            var text = new TextBox
            {
                Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
                Text = string.Format(
                    L.T("Formato d'origine: {0}\r\nFile: {1}\r\nConvertita: {2}\r\nPunti: {3:N0}{4}\r\n\r\n") +
                    L.T("Dimensioni: {5:F2} × {6:F2} × {7:F2} m\r\n\r\nCentro (coordinate originali):\r\n  X = {8:F3}\r\n  Y = {9:F3}\r\n  Z = {10:F3} m\r\n\r\n") +
                    L.T("Cartella:\r\n{11}{12}") + "\r\n\r\n" +
                    (info.IsOctree ? string.Format(L.T("Octree: {0:N0} nodi, {1} livelli."), info.TileCount, info.OctreeDepth + 1)
                                   : L.T("Formato vecchio (blocchi senza octree e senza normali): funziona, ma riconvertendola dal file originale si carica più in fretta.")),
                    info.SourceFormat, info.SourcePath, info.ConvertedDate == DateTime.MinValue ? "" : info.ConvertedDate.ToString("g"),
                    info.PointCount, info.HasRgb ? L.T(", con colori RGB") : L.T(", senza colori"),
                    size[0], size[1], size[2], mid[0], mid[1], mid[2], folder, notes.Replace("\n", "\r\n"))
            };
            var insert = new Button { Text = L.T("Inserisci ▾"), Dock = DockStyle.Bottom, Height = 30 };
            insert.Click += (s, e) => insertMenu.Show(insert, new Point(0, insert.Height));
            foreach (ToolStripItem it in insertMenu.Items) it.Click += (s, e) => Close();

            side.Controls.Add(text);
            side.Controls.Add(insert);
            Controls.Add(pic);
            Controls.Add(side);
        }
    }
}
