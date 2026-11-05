using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Drawing.Color;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;
using Panel = System.Windows.Forms.Panel;
using Point = System.Drawing.Point;
using ToolTip = System.Windows.Forms.ToolTip;

namespace PointDeviation
{
    /// <summary>
    /// Completezza: elenco delle zone rilevate ma non modellate (gruppi di punti lontani dal modello), con superficie
    /// stimata, tipo, livello; zoom, isolamento della zona (con due recinti) ed esportazione in CSV.
    /// </summary>
    internal sealed class MissingZonesForm : PdForm
    {
        // parametri ricordati per la sessione
        private static double _lastCell = 0.20, _lastMinArea = 0.5;
        private static int _lastMinPts = 10;
        private static double _lastThreshold = -1;

        private readonly NumericUpDown _thr = new NumericUpDown { Minimum = 10, Maximum = 5000, Increment = 10, DecimalPlaces = 0, Width = 70 };
        private readonly NumericUpDown _cell = new NumericUpDown { Minimum = 0.05m, Maximum = 2m, Increment = 0.05m, DecimalPlaces = 2, Width = 70 };
        private readonly NumericUpDown _minPts = new NumericUpDown { Minimum = 1, Maximum = 10000, Increment = 5, DecimalPlaces = 0, Width = 70 };
        private readonly NumericUpDown _minArea = new NumericUpDown { Minimum = 0, Maximum = 1000, Increment = 0.25m, DecimalPlaces = 2, Width = 70 };
        private readonly CheckBox _inClip = new CheckBox { Text = L.T("Solo dentro i recinti attivi"), AutoSize = true };
        private readonly Button _find = new Button { Text = L.T("Trova zone"), AutoSize = true };
        private readonly ListView _list = new ListView
        {
            View = System.Windows.Forms.View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None
        };
        private readonly Label _summary = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
        private readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 6, Minimum = 0, Maximum = 1000, Visible = false };
        private readonly Button _zoom = new Button { Text = L.T("Zoom sulla zona"), AutoSize = true, Enabled = false };
        private readonly Button _isolate = new Button { Text = L.T("Isola la zona"), AutoSize = true, Enabled = false };
        private readonly Button _showAll = new Button { Text = L.T("Mostra tutti i punti"), AutoSize = true, Enabled = false };
        private readonly Button _export = new Button { Text = L.T("Esporta CSV..."), AutoSize = true, Enabled = false };

        private List<MissingZone> _zones = new List<MissingZone>();
        private MissingZoneOptions _usedOptions;
        private volatile bool _cancel;
        private bool _busy;
        private int _sortCol = 1;
        private bool _sortDesc = true;

        // Isolamento: i recinti di prima si rimettono con «Mostra tutti i punti» o chiudendo la finestra. Si ricordano
        // per progetto e fuori dalla finestra: prima stavano nella finestra, e richiudendola e riaprendola la zona
        // restava isolata senza modo di tornare indietro (e la nuova ricerca la prendeva per un recinto dell'utente).
        private sealed class Saved { public List<ClipRegion> Clips; public bool Invert; }
        private static readonly Dictionary<string, Saved> _saved = new Dictionary<string, Saved>();

        public MissingZonesForm()
        {
            Text = L.T("Zone mancanti (completezza)");
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 460);
            MinimumSize = new Size(620, 340);

            // ---- parametri
            ColorProfile p = DeviationController.Current.Profile;
            double thr = _lastThreshold > 0 ? _lastThreshold
                : p != null && p.Kind == ProfileKind.Completezza ? p.ThresholdMm
                : (ProfileStore.All.FirstOrDefault(x => x.Kind == ProfileKind.Completezza) ?? new ColorProfile { ThresholdMm = 150 }).ThresholdMm;
            SetClamped(_thr, thr);
            SetClamped(_cell, _lastCell);
            SetClamped(_minPts, _lastMinPts);
            SetClamped(_minArea, _lastMinArea);
            _inClip.Checked = DeviationController.Current.ClipEnabled && !ZoneClipsActive(DeviationController.Current);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 4), WrapContents = true };
            top.Controls.Add(Caption(L.T("Soglia (mm)"))); top.Controls.Add(_thr);
            top.Controls.Add(Caption(L.T("Cella (m)"))); top.Controls.Add(_cell);
            top.Controls.Add(Caption(L.T("Punti minimi per cella"))); top.Controls.Add(_minPts);
            top.Controls.Add(Caption(L.T("Superficie minima (m²)"))); top.Controls.Add(_minArea);
            _inClip.Margin = new Padding(3, 6, 12, 0);
            top.Controls.Add(_inClip);
            top.Controls.Add(_find);

            var tips = new ToolTip();
            tips.SetToolTip(_thr, L.T("Punti più lontani di così da qualunque superficie del modello (o senza superficie entro la distanza massima dell'analisi)."));
            tips.SetToolTip(_cell, L.T("I punti lontani si raggruppano in cubetti di questo lato; cubetti che si toccano formano una zona."));
            tips.SetToolTip(_minPts, L.T("Cubetti con meno punti sono rumore (persone, riflessi, punti isolati). I cubetti attaccati a una zona ne bastano un quinto."));
            tips.SetToolTip(_minArea, L.T("Zone più piccole non vanno nell'elenco (sono contate a parte nel riepilogo)."));

            // ---- elenco
            _list.Columns.Add("#", 44, HorizontalAlignment.Right);
            _list.Columns.Add(L.T("Superficie (m²)"), 100, HorizontalAlignment.Right);
            _list.Columns.Add(L.T("Punti"), 90, HorizontalAlignment.Right);
            _list.Columns.Add(L.T("Tipo"), 100);
            _list.Columns.Add(L.T("Ingombro X × Y × H (m)"), 150);
            _list.Columns.Add(L.T("Livello"), 130);
            _list.Columns.Add(L.T("Da quota (m)"), 90, HorizontalAlignment.Right);
            _list.ColumnClick += (s, e) =>
            {
                if (e.Column == _sortCol) _sortDesc = !_sortDesc; else { _sortCol = e.Column; _sortDesc = e.Column != 0 && e.Column != 3 && e.Column != 5; }
                Fill();
            };
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.DoubleClick += (s, e) => ZoomTo(Selected());
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) ZoomTo(Selected()); };

            // ---- piede
            var foot = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, WrapContents = false, Padding = new Padding(0, 2, 0, 0) };
            foot.Controls.Add(_zoom); foot.Controls.Add(_isolate); foot.Controls.Add(_showAll); foot.Controls.Add(_export);
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 84, Padding = new Padding(10, 4, 10, 6) };
            bottom.Controls.Add(_summary);
            bottom.Controls.Add(foot);
            var hint = new Label
            {
                Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(10, 3, 10, 0), ForeColor = UiTheme.Muted, AutoEllipsis = true,
                Text = L.T("Doppio clic su una zona per inquadrarla. «Isola» vale finché la finestra è aperta. Superficie stimata dai cubetti; «volume» = oggetti, arredi o angoli.")
            };

            Controls.Add(_list);
            Controls.Add(_bar);
            Controls.Add(top);
            Controls.Add(hint);
            Controls.Add(bottom);

            _find.Click += (s, e) => Start();
            _zoom.Click += (s, e) => ZoomTo(Selected());
            _isolate.Click += (s, e) => Isolate(Selected());
            _showAll.Click += (s, e) => RestoreClips();
            _export.Click += (s, e) => Export();
            AcceptButton = _find;

            UiTheme.Apply(this, _find);
            _summary.Text = L.T("Imposta i parametri e premi «Trova zone». Servono le distanze dell'analisi (Analizza vista o selezione).");
            _summary.ForeColor = UiTheme.Muted;
            UpdateButtons(); // una zona rimasta isolata (es. progetto riaperto) si può togliere subito
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cancel = true;
            // l'isolamento serve a guardare le zone dell'elenco: chiudendo la finestra si torna ai recinti di prima
            if (ZoneClipsActive(DeviationController.Current)) RestoreClips();
            base.OnFormClosing(e);
        }

        private static Label Caption(string t)
        {
            return new Label { Text = t, AutoSize = true, Margin = new Padding(3, 6, 2, 0) };
        }

        private static void SetClamped(NumericUpDown n, double v)
        {
            decimal d = (decimal)v;
            n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, d));
        }

        private MissingZone Selected()
        {
            return _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as MissingZone : null;
        }

        private void UpdateButtons()
        {
            bool sel = Selected() != null && !_busy;
            _zoom.Enabled = sel;
            _isolate.Enabled = sel;
            _showAll.Enabled = ZoneClipsActive(DeviationController.Current) && !_busy;
            _export.Enabled = _zones.Count > 0 && !_busy;
            _find.Enabled = !_busy;
        }

        // ------------------------------------------------------------------ calcolo

        private void Start()
        {
            if (_busy) return;
            var opt = new MissingZoneOptions
            {
                ThresholdMm = (double)_thr.Value,
                CellM = (double)_cell.Value,
                MinPointsPerCell = (int)_minPts.Value,
                MinAreaM2 = (double)_minArea.Value
            };
            _lastThreshold = opt.ThresholdMm; _lastCell = opt.CellM; _lastMinPts = opt.MinPointsPerCell; _lastMinArea = opt.MinAreaM2;
            if (_inClip.Checked)
            {
                // durante l'isolamento di una zona valgono i recinti di prima, non quelli della zona
                DeviationSettings s = DeviationController.Current;
                Saved sv = ZoneClipsActive(s) ? SavedFor() : null;
                opt.Clip = ZoneClipsActive(s) ? (sv != null ? ClipSet.From(sv.Clips, sv.Invert) : null) : ClipSet.From(s.Clips, s.ClipInvert);
            }

            _busy = true; _cancel = false;
            _bar.Value = 0; _bar.Visible = true;
            _summary.ForeColor = UiTheme.Muted;
            _summary.Text = L.T("Preparazione...");
            UpdateButtons();

            // nuvole, analisi e livelli si leggono sul thread di Revit; il calcolo poi va in background
            ApiQueue.Run(app =>
            {
                try
                {
                    UIDocument uidoc = app.ActiveUIDocument;
                    DocumentData d = DeviationController.ActiveData;
                    List<CloudLayer> layers = d != null ? d.Layers.FindAll(l => l.IsOctree) : new List<CloudLayer>();
                    AnalysisSnapshot an = d != null ? AnalysisRegistry.Get(d.Key) : null;
                    var levels = new List<KeyValuePair<string, double>>();
                    if (uidoc != null)
                        foreach (Level lv in new FilteredElementCollector(uidoc.Document).OfClass(typeof(Level)).Cast<Level>())
                            levels.Add(new KeyValuePair<string, double>(lv.Name, lv.ProjectElevation));
                    levels.Sort((a, b) => a.Value.CompareTo(b.Value));
                    if (d != null && layers.Count < d.Layers.Count)
                        Log.Write("Completezza", string.Format("{0} nuvole in formato vecchio (senza octree) escluse", d.Layers.Count - layers.Count));
                    Task.Factory.StartNew(() => Work(layers, an, levels, opt), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    Log.Error("Zone mancanti", ex);
                    Ui(() => Done(new MissingZoneResult { Error = ex.Message }, opt));
                }
            });
        }

        private void Work(List<CloudLayer> layers, AnalysisSnapshot an, List<KeyValuePair<string, double>> levels, MissingZoneOptions opt)
        {
            MissingZoneResult r;
            if (an == null || layers.Count == 0)
                r = new MissingZoneResult { Error = L.T("Nessuna analisi attiva: esegui prima «Analizza vista» o «Analizza selezione».") };
            else
            {
                long lastUi = 0;
                r = MissingZones.Find(layers, an, opt, () => _cancel, (text, f) =>
                {
                    long now = Environment.TickCount;
                    if (now - Interlocked.Read(ref lastUi) < 100) return;
                    Interlocked.Exchange(ref lastUi, now);
                    Ui(() =>
                    {
                        _summary.Text = text;
                        _bar.Value = Math.Max(0, Math.Min(_bar.Maximum, (int)(f * _bar.Maximum)));
                    });
                });
                // livello sotto ogni zona (con 10 cm di tolleranza: il pavimento sta appena sotto il livello)
                foreach (MissingZone z in r.Zones)
                {
                    KeyValuePair<string, double>? best = null;
                    foreach (var lv in levels) if (lv.Value <= z.MinZ + 0.1 / 0.3048) best = lv;
                    if (best == null && levels.Count > 0) best = levels[0];
                    if (best != null) { z.Level = best.Value.Key; z.AboveLevelM = (z.MinZ - best.Value.Value) * 0.3048; }
                }
            }
            if (_cancel) return;
            Ui(() => Done(r, opt));
        }

        private void Ui(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        private void Done(MissingZoneResult r, MissingZoneOptions opt)
        {
            _busy = false;
            _bar.Visible = false;
            if (r.Cancelled) { UpdateButtons(); return; }
            if (r.Error != null)
            {
                _summary.ForeColor = Color.FromArgb(192, 57, 43);
                _summary.Text = r.Error;
                UpdateButtons();
                return;
            }
            _zones = r.Zones;
            _usedOptions = opt;
            Fill();
            double area = r.Zones.Sum(z => z.AreaM2);
            long pts = r.Zones.Sum(z => z.Points);
            _summary.ForeColor = UiTheme.Ink;
            _summary.Text = string.Format(L.T("{0:N0} zone, {1:N1} m² stimati, {2:N0} punti. Punti lontani dal modello: {3:N0} su {4:N0} ({5:P1}); rumore {6:N0}; zone sotto la superficie minima: {7:N0} ({8:N0} punti). Calcolo in {9:F1} s."),
                r.Zones.Count, area, pts, r.FarPoints, r.TotalPoints, r.TotalPoints > 0 ? (double)r.FarPoints / r.TotalPoints : 0,
                r.NoisePoints, r.SmallZones, r.SmallZonePoints, r.Seconds);
            if (r.Zones.Count > 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
            UpdateButtons();
        }

        private static string ShapeText(ZoneShape s)
        {
            switch (s)
            {
                case ZoneShape.Verticale: return L.T("verticale");
                case ZoneShape.Orizzontale: return L.T("orizzontale");
                case ZoneShape.Inclinata: return L.T("inclinata");
                default: return L.T("volume");
            }
        }

        private void Fill()
        {
            IEnumerable<MissingZone> q = _zones;
            Func<MissingZone, IComparable> key;
            switch (_sortCol)
            {
                case 0: key = z => z.Number; break;
                case 2: key = z => z.Points; break;
                case 3: key = z => ShapeText(z.Shape); break;
                case 4: key = z => z.SizeXM * z.SizeYM * Math.Max(0.01, z.SizeZM); break;
                case 5: key = z => z.Level ?? ""; break;
                case 6: key = z => z.AboveLevelM; break;
                default: key = z => z.AreaM2; break;
            }
            q = _sortDesc ? q.OrderByDescending(key) : q.OrderBy(key);

            MissingZone sel = Selected();
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (MissingZone z in q)
                {
                    var it = new ListViewItem(z.Number.ToString(CultureInfo.CurrentCulture)) { Tag = z };
                    it.SubItems.Add(z.AreaM2.ToString("N2", CultureInfo.CurrentCulture));
                    it.SubItems.Add(z.Points.ToString("N0", CultureInfo.CurrentCulture));
                    it.SubItems.Add(ShapeText(z.Shape));
                    it.SubItems.Add(string.Format(CultureInfo.CurrentCulture, "{0:0.0} × {1:0.0} × {2:0.0}", z.SizeXM, z.SizeYM, z.SizeZM));
                    it.SubItems.Add(z.Level ?? "");
                    it.SubItems.Add(z.Level != null ? z.AboveLevelM.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) : "");
                    if (ReferenceEquals(z, sel)) it.Selected = true;
                    _list.Items.Add(it);
                }
            }
            finally { _list.EndUpdate(); }
        }

        // ------------------------------------------------------------------ azioni sulla vista

        private static void Expanded(MissingZone z, double marginM, out XYZ a, out XYZ b)
        {
            double m = marginM / 0.3048;
            a = new XYZ(z.MinX - m, z.MinY - m, z.MinZ - m);
            b = new XYZ(z.MaxX + m, z.MaxY + m, z.MaxZ + m);
        }

        private void ZoomTo(MissingZone z)
        {
            if (z == null) return;
            XYZ a, b;
            Expanded(z, 1.0, out a, out b);
            ApiQueue.Run(app =>
            {
                UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                UIView uiv = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id);
                if (uiv != null) uiv.ZoomAndCenterRectangle(a, b);
            });
        }

        /// <summary>
        /// Isola la zona con due recinti: uno in pianta (limita X e Y) e uno in prospetto (limita X e Z); insieme tengono
        /// solo i punti nel parallelepipedo della zona, in qualunque vista. I recinti di prima si conservano.
        /// </summary>
        private void Isolate(MissingZone z)
        {
            if (z == null) return;
            DeviationSettings s = DeviationController.Current;
            if (!ZoneClipsActive(s))
            {
                // primo isolamento (o i recinti sono stati cambiati da fuori): si ricordano quelli attuali
                string key = DocKey();
                if (key != null)
                    _saved[key] = new Saved { Clips = s.Clips != null ? s.Clips.Select(r => r.Clone()).ToList() : new List<ClipRegion>(), Invert = s.ClipInvert };
            }
            XYZ a, b;
            Expanded(z, 0.3, out a, out b);
            string name = string.Format(L.T("Zona {0}"), z.Number);
            var zoneClips = new List<ClipRegion>
            {
                Rect(name + L.T(" (pianta)"), new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 1.0, 0.0 }, a.X, a.Y, b.X, b.Y),
                Rect(name + L.T(" (prospetto)"), new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, a.X, a.Z, b.X, b.Z)
            };
            s.Clips = zoneClips;
            s.ClipInvert = false;
            App.SyncRibbon(s);
            DeviationController.NotifySettingsChanged();
            DeviationController.Apply(Job.Recolor);
            ZoomTo(z);
            UpdateButtons();
        }

        private static string DocKey()
        {
            DocumentData d = DeviationController.ActiveData;
            return d != null ? d.Key : null;
        }

        private static Saved SavedFor()
        {
            string key = DocKey();
            Saved sv;
            return key != null && _saved.TryGetValue(key, out sv) ? sv : null;
        }

        /// <summary>
        /// I recinti attivi sono quelli di un isolamento di zona? Si riconoscono da soli (due rettangoli «Zona N (pianta)»
        /// e «Zona N (prospetto)»), così valgono anche dopo aver chiuso la finestra o riaperto il progetto.
        /// </summary>
        private static bool ZoneClipsActive(DeviationSettings s)
        {
            if (s == null || s.Clips == null || s.Clips.Count != 2) return false;
            ClipRegion p = s.Clips[0], e = s.Clips[1];
            return p != null && e != null && p.Name != null && e.Name != null
                && p.Name.EndsWith(L.T(" (pianta)")) && e.Name.EndsWith(L.T(" (prospetto)"))
                && p.U != null && p.U.Length == 4 && e.U != null && e.U.Length == 4;
        }

        private static ClipRegion Rect(string name, double[] origin, double[] right, double[] up, double u0, double v0, double u1, double v1)
        {
            return new ClipRegion
            {
                Name = name, Enabled = true, KeepInside = true,
                Origin = origin, Right = right, Up = up,
                U = new[] { u0, u1, u1, u0 },
                V = new[] { v0, v0, v1, v1 }
            };
        }

        /// <summary>Toglie l'isolamento: i recinti di prima se si conoscono, altrimenti (progetto riaperto) nessun recinto.</summary>
        private void RestoreClips()
        {
            DeviationSettings s = DeviationController.Current;
            // se nel frattempo i recinti sono stati cambiati a mano, si lasciano come sono
            if (ZoneClipsActive(s))
            {
                Saved sv = SavedFor();
                s.Clips = sv != null ? sv.Clips : new List<ClipRegion>();
                s.ClipInvert = sv != null && sv.Invert;
                string key = DocKey();
                if (key != null) _saved.Remove(key);
                App.SyncRibbon(s);
                DeviationController.NotifySettingsChanged();
                DeviationController.Apply(Job.Recolor);
                Log.Write("Completezza", "isolamento della zona tolto: " + (sv != null ? sv.Clips.Count + " recinti di prima rimessi" : "nessun recinto"));
            }
            if (!IsDisposed) UpdateButtons();
        }

        // ------------------------------------------------------------------ esportazione

        private void Export()
        {
            if (_zones.Count == 0) return;
            using (var dlg = new SaveFileDialog
            {
                Filter = "CSV|*.csv",
                FileName = L.T("zone_mancanti") + ".csv"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    CultureInfo c = CultureInfo.CurrentCulture;
                    string sep = c.TextInfo.ListSeparator;
                    if (string.IsNullOrEmpty(sep) || sep == c.NumberFormat.NumberDecimalSeparator) sep = ";";
                    var sb = new StringBuilder();
                    MissingZoneOptions o = _usedOptions ?? new MissingZoneOptions();
                    sb.AppendLine(string.Format(c, L.T("# Zone mancanti: soglia {0:0} mm, cella {1:0.00} m, minimo {2} punti per cella, superficie minima {3:0.##} m²; coordinate interne del progetto in metri"),
                        o.ThresholdMm, o.CellM, o.MinPointsPerCell, o.MinAreaM2));
                    string[] head = { "#", L.T("Superficie (m²)"), L.T("Punti"), L.T("Tipo"), L.T("Livello"), L.T("Da quota (m)"),
                        "X min", "Y min", "Z min", "X max", "Y max", "Z max", L.T("Centro X"), L.T("Centro Y"), L.T("Centro Z") };
                    sb.AppendLine(string.Join(sep, head));
                    foreach (MissingZone z in _zones.OrderBy(x => x.Number))
                    {
                        Func<double, string> m = v => (v * 0.3048).ToString("0.000", c);
                        string[] row =
                        {
                            z.Number.ToString(c), z.AreaM2.ToString("0.00", c), z.Points.ToString(c), ShapeText(z.Shape),
                            Csv(z.Level ?? "", sep), z.Level != null ? z.AboveLevelM.ToString("0.00", c) : "",
                            m(z.MinX), m(z.MinY), m(z.MinZ), m(z.MaxX), m(z.MaxY), m(z.MaxZ),
                            m((z.MinX + z.MaxX) / 2), m((z.MinY + z.MaxY) / 2), m((z.MinZ + z.MaxZ) / 2)
                        };
                        sb.AppendLine(string.Join(sep, row));
                    }
                    // con BOM: Excel riconosce gli accenti
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                    Log.Write("Completezza", string.Format("esportate {0} zone in {1}", _zones.Count, dlg.FileName));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Point Deviation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private static string Csv(string v, string sep)
        {
            return v.Contains(sep) || v.Contains("\"") ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }
}
