using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Form = System.Windows.Forms.Form;
using TextBox = System.Windows.Forms.TextBox;
using View = Autodesk.Revit.DB.View;
using Control = System.Windows.Forms.Control;

namespace PointDeviation
{
    // =====================================================================================
    //  Dati: un recinto è un poligono disegnato sul piano di una vista. Un punto della nuvola
    //  è "dentro" se la sua proiezione sul piano della vista cade nel poligono: in pratica il
    //  recinto è un prisma infinito lungo la direzione di vista.
    // =====================================================================================

    [DataContract]
    internal sealed class ClipRegion
    {
        [DataMember] public string Name;
        [DataMember] public bool Enabled = true;
        /// <summary>true = tiene i punti dentro il poligono; false = tiene quelli fuori.</summary>
        [DataMember] public bool KeepInside = true;
        [DataMember] public double[] Origin;  // piano della vista (piedi)
        [DataMember] public double[] Right;
        [DataMember] public double[] Up;
        [DataMember] public double[] U;       // vertici sul piano (piedi)
        [DataMember] public double[] V;

        public ClipRegion Clone()
        {
            var c = (ClipRegion)MemberwiseClone();
            c.U = (double[])U.Clone();
            c.V = (double[])V.Clone();
            return c;
        }

        public bool IsValid { get { return U != null && V != null && U.Length >= 3 && U.Length == V.Length && Origin != null && Right != null && Up != null; } }

        public bool Passes(double x, double y, double z)
        {
            double dx = x - Origin[0], dy = y - Origin[1], dz = z - Origin[2];
            double u = dx * Right[0] + dy * Right[1] + dz * Right[2];
            double v = dx * Up[0] + dy * Up[1] + dz * Up[2];
            bool inside = false;
            int n = U.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((V[i] > v) != (V[j] > v) && u < (U[j] - U[i]) * (v - V[i]) / (V[j] - V[i]) + U[i])
                    inside = !inside;
            }
            return KeepInside ? inside : !inside;
        }
    }

    [DataContract]
    internal sealed class ClipPreset
    {
        [DataMember] public string Name;
        [DataMember] public bool Invert;
        [DataMember] public List<ClipRegion> Regions = new List<ClipRegion>();
    }

    /// <summary>Insieme dei recinti attivi: un punto è visibile se passa tutti i recinti accesi (poi l'eventuale inversione).</summary>
    internal sealed class ClipSet
    {
        private readonly ClipRegion[] _regions;
        private readonly bool _invert;

        private ClipSet(ClipRegion[] r, bool invert) { _regions = r; _invert = invert; }

        /// <summary>null se non c'è nessun recinto acceso (nessun ritaglio).</summary>
        public static ClipSet From(List<ClipRegion> regions, bool invert)
        {
            if (regions == null) return null;
            ClipRegion[] on = regions.Where(r => r != null && r.Enabled && r.IsValid).ToArray();
            return on.Length == 0 ? null : new ClipSet(on, invert);
        }

        public bool Contains(double x, double y, double z)
        {
            bool all = true;
            for (int i = 0; i < _regions.Length && all; i++) all = _regions[i].Passes(x, y, z);
            return _invert ? !all : all;
        }
    }

    internal enum FenceShape { Poligono = 0, Rettangolo = 1, Cerchio = 2 }

    // =====================================================================================
    //  Disegno del recinto sopra la vista attiva
    // =====================================================================================

    internal static class FenceTool
    {
        /// <summary>Fa disegnare un recinto nella vista attiva. null se annullato o vista non adatta.</summary>
        public static ClipRegion Draw(UIApplication app, FenceShape shape)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null) return null;
            View view = uidoc.ActiveView;

            var v3 = view as View3D;
            if (v3 != null && v3.IsPerspective)
            {
                TaskDialog.Show("Point Deviation", L.T("Il recinto si disegna in una vista ortogonale: passa a una vista 3D non prospettica, una pianta o una sezione."));
                return null;
            }
            if (!(view is View3D || view is ViewPlan || view is ViewSection))
            {
                TaskDialog.Show("Point Deviation", L.T("Attiva una vista 3D, una pianta, una sezione o un prospetto."));
                return null;
            }

            UIView uiv = uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == view.Id);
            if (uiv == null) return null;

            Autodesk.Revit.DB.Rectangle wr = uiv.GetWindowRectangle();
            IList<XYZ> corners = uiv.GetZoomCorners();
            XYZ origin = view.Origin, right = view.RightDirection, up = view.UpDirection;
            Func<XYZ, double> U = p => (p - origin).DotProduct(right);
            Func<XYZ, double> V = p => (p - origin).DotProduct(up);
            double u0 = U(corners[0]), v0 = V(corners[0]), u1 = U(corners[1]), v1 = V(corners[1]);

            var screen = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
            if (screen.Width < 50 || screen.Height < 50) return null;

            List<PointF> pts;
            bool invert;
            using (var draw = new FenceDrawLayer(screen))
            using (var input = new FenceInputLayer(screen, shape, draw))
            {
                draw.Show(new WindowOwner(app.MainWindowHandle));
                DialogResult res = input.ShowDialog(new WindowOwner(app.MainWindowHandle));
                draw.Close();
                if (res != DialogResult.OK) return null;
                pts = input.Result;
                invert = input.Inverted;
            }
            if (pts == null || pts.Count < 3) return null;

            // pixel della finestra -> coordinate sul piano della vista (lineare nelle viste ortogonali)
            double w = screen.Width, h = screen.Height;
            var region = new ClipRegion
            {
                Name = string.Format("{0} {1}", L.T(shape == FenceShape.Cerchio ? L.T("Cerchio") : shape == FenceShape.Rettangolo ? L.T("Rettangolo") : L.T("Poligono")), view.Name),
                KeepInside = !invert,
                Origin = new[] { origin.X, origin.Y, origin.Z },
                Right = new[] { right.X, right.Y, right.Z },
                Up = new[] { up.X, up.Y, up.Z },
                U = pts.Select(p => u0 + p.X / w * (u1 - u0)).ToArray(),
                V = pts.Select(p => v1 - p.Y / h * (v1 - v0)).ToArray()
            };
            return region;
        }
    }

    /// <summary>Strato che disegna il recinto: trasparente dove non c'è nulla, semitrasparente sul disegno.</summary>
    internal sealed class FenceDrawLayer : Form
    {
        private static readonly Color Key = Color.FromArgb(255, 0, 255);
        public List<PointF> Points = new List<PointF>();
        public PointF? Mouse;
        public bool Invert;
        public bool IsClosed;   // poligono già chiuso (rettangolo/cerchio in corso)
        public string Banner = "";
        /// <summary>Segni puntuali (Misura punti), disegnati come piccole croci.</summary>
        public List<PointF> Marks = new List<PointF>();

        public FenceDrawLayer(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Key;
            TransparencyKey = Key;
            Opacity = 0.62;
            DoubleBuffered = true;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        /// <summary>
        /// Trasparente ai clic (WS_EX_TRANSPARENT) e fuori dalla barra delle applicazioni (WS_EX_TOOLWINDOW):
        /// mouse e tastiera li riceve lo strato di input sopra. Disattivare la finestra (Enabled = false)
        /// non va: Windows Forms rifiuta poi di aprire la finestra modale di input.
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill = Invert ? Color.FromArgb(255, 170, 120) : Color.FromArgb(150, 235, 150);
            Color line = Invert ? Color.FromArgb(200, 80, 20) : Color.FromArgb(20, 120, 40);

            var poly = new List<PointF>(Points);
            if (!IsClosed && Mouse.HasValue) poly.Add(Mouse.Value);
            if (poly.Count >= 3)
                using (var br = new SolidBrush(fill)) g.FillPolygon(br, poly.ToArray());
            if (poly.Count >= 2)
                using (var pen = new Pen(line, 2f))
                {
                    if (IsClosed || poly.Count >= 3) g.DrawPolygon(pen, poly.ToArray());
                    else g.DrawLines(pen, poly.ToArray());
                }
            using (var br = new SolidBrush(line))
                foreach (PointF p in Points) g.FillRectangle(br, p.X - 3, p.Y - 3, 6, 6);
            using (var pen = new Pen(Color.FromArgb(230, 90, 20), 2f))
                foreach (PointF m in Marks)
                {
                    g.DrawLine(pen, m.X - 8, m.Y, m.X + 8, m.Y);
                    g.DrawLine(pen, m.X, m.Y - 8, m.X, m.Y + 8);
                    g.DrawEllipse(pen, m.X - 5, m.Y - 5, 10, 10);
                }

            // barra dei comandi in alto al centro
            if (!string.IsNullOrEmpty(Banner))
            {
                using (var f = new Font("Segoe UI Semibold", 10f))
                {
                    SizeF sz = g.MeasureString(Banner, f);
                    var r = new RectangleF((Width - sz.Width) / 2 - 14, 24, sz.Width + 28, sz.Height + 12);
                    using (var path = Rounded(r, 8))
                    using (var br = new SolidBrush(Color.FromArgb(70, 70, 70)))
                        g.FillPath(br, path);
                    g.DrawString(Banner, f, Brushes.White, r.X + 14, r.Y + 6);
                }
            }
        }

        private static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>
    /// Strato invisibile sopra la vista che riceve mouse e tastiera durante il disegno.
    /// Poligono: clic = vertice, INVIO / doppio clic / tasto destro = conferma, BACKSPACE = togli l'ultimo, ESC = esci.
    /// Rettangolo e cerchio: premi, trascina, rilascia (o due clic). MAIUSC tenuto premuto = tieni i punti fuori.
    /// </summary>
    internal sealed class FenceInputLayer : Form
    {
        private readonly FenceShape _shape;
        private readonly FenceDrawLayer _draw;
        private PointF? _anchor; // primo angolo o centro
        private bool _dragging;

        public List<PointF> Result { get; private set; }
        public bool Inverted { get; private set; }

        public FenceInputLayer(Rectangle bounds, FenceShape shape, FenceDrawLayer draw)
        {
            _shape = shape;
            _draw = draw;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            Opacity = 0.01; // quasi invisibile ma riceve i clic ovunque
            KeyPreview = true;
            TopMost = true;
            Cursor = Cursors.Cross;
            UpdateBanner();
        }

        private void UpdateBanner()
        {
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            _draw.Invert = shift;
            string tail = shift ? L.T("PUNTI FUORI dal recinto") : L.T("tieni MAIUSC per i punti fuori");
            if (_shape == FenceShape.Poligono)
                _draw.Banner = _draw.Points.Count == 0
                    ? L.T("Clicca i vertici") + "  |  " + tail + "  |  " + L.T("ESC per uscire")
                    : L.T("BACKSPACE annulla il punto  |  INVIO conferma  |  ESC esce") + "  |  " + tail;
            else
                _draw.Banner = L.T(_shape == FenceShape.Cerchio ? L.T("Premi sul centro e trascina il raggio") : L.T("Premi su un angolo e trascina"))
                    + "  |  " + tail + "  |  " + L.T("ESC per uscire");
            _draw.Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (k == Keys.Enter) { Confirm(); return true; }
            if (k == Keys.Back && _shape == FenceShape.Poligono && _draw.Points.Count > 0)
            {
                _draw.Points.RemoveAt(_draw.Points.Count - 1);
                UpdateBanner();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); UpdateBanner(); }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); UpdateBanner(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var p = new PointF(e.X, e.Y);
            if (_shape == FenceShape.Poligono)
            {
                if (e.Button == MouseButtons.Right) { Confirm(); return; }
                if (e.Button != MouseButtons.Left) return;
                if (e.Clicks >= 2) { Confirm(); return; }
                _draw.Points.Add(p);
                UpdateBanner();
                return;
            }

            if (e.Button != MouseButtons.Left) return;
            if (_anchor == null) { _anchor = p; _dragging = true; UpdateShape(p); }
            else { UpdateShape(p); Confirm(); } // secondo clic
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = new PointF(e.X, e.Y);
            if (_shape == FenceShape.Poligono) _draw.Mouse = p;
            else if (_anchor != null) UpdateShape(p);
            UpdateBanner();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_shape == FenceShape.Poligono || _anchor == null || !_dragging) return;
            _dragging = false;
            PointF a = _anchor.Value;
            // un trascinamento vero conferma; un semplice clic aspetta il secondo clic
            if (Math.Abs(e.X - a.X) + Math.Abs(e.Y - a.Y) > 6) { UpdateShape(new PointF(e.X, e.Y)); Confirm(); }
        }

        private void UpdateShape(PointF p)
        {
            PointF a = _anchor.Value;
            _draw.Points.Clear();
            _draw.IsClosed = true;
            if (_shape == FenceShape.Rettangolo)
            {
                _draw.Points.Add(new PointF(a.X, a.Y));
                _draw.Points.Add(new PointF(p.X, a.Y));
                _draw.Points.Add(new PointF(p.X, p.Y));
                _draw.Points.Add(new PointF(a.X, p.Y));
            }
            else
            {
                double r = Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
                for (int i = 0; i < 64; i++)
                {
                    double t = i * Math.PI * 2 / 64;
                    _draw.Points.Add(new PointF((float)(a.X + r * Math.Cos(t)), (float)(a.Y + r * Math.Sin(t))));
                }
            }
            _draw.Invalidate();
        }

        private void Confirm()
        {
            var pts = new List<PointF>(_draw.Points);
            if (pts.Count < 3) return;
            // area minima: evita recinti nati da un doppio clic accidentale
            double area = 0;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++) area += (pts[j].X + pts[i].X) * (pts[j].Y - pts[i].Y);
            if (Math.Abs(area) / 2 < 25) return;
            Result = pts;
            Inverted = (ModifierKeys & Keys.Shift) != 0;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // =====================================================================================
    //  Gestione dei ritagli: recinti correnti e ritagli salvati con un nome
    // =====================================================================================

    internal sealed class ClipManagerForm : Form
    {
        private readonly ListBox _presets = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        private readonly CheckedListBox _regions = new CheckedListBox { Dock = DockStyle.Fill, IntegralHeight = false, CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle };
        private readonly CheckBox _invert = new CheckBox { AutoSize = true };
        private readonly TextBox _name = new TextBox { Width = 220 };
        private bool _loading;

        public ClipManagerForm()
        {
            Text = L.T("Gestisci ritagli");
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 470);
            MinimumSize = new Size(360, 380);
            _invert.Text = L.T("Inverti (mostra ciò che il ritaglio esclude)");

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(UiTheme.Section(L.T("Ritagli salvati")), 0, 0);
            root.Controls.Add(_presets, 0, 1);
            var presetBtns = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var apply = new Button { Text = L.T("Richiama"), AutoSize = true };
            var delPreset = new Button { Text = L.T("Elimina"), AutoSize = true };
            presetBtns.Controls.AddRange(new Control[] { apply, delPreset });
            root.Controls.Add(presetBtns, 0, 2);

            root.Controls.Add(UiTheme.Section(L.T("Ritaglio corrente (spunta = acceso)")), 0, 3);
            root.Controls.Add(_regions, 0, 4);
            var regBtns = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var delRegion = new Button { Text = L.T("Togli recinto"), AutoSize = true };
            var clear = new Button { Text = L.T("Tutti i punti"), AutoSize = true };
            regBtns.Controls.AddRange(new Control[] { delRegion, clear, _invert });
            root.Controls.Add(regBtns, 0, 5);

            var save = new Button { Text = L.T("Salva"), AutoSize = true };
            var saveRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            saveRow.Controls.Add(new Label { Text = L.T("Salva il ritaglio corrente come"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            saveRow.Controls.Add(_name);
            saveRow.Controls.Add(save);
            root.Controls.Add(saveRow, 0, 6);
            Controls.Add(root);

            apply.Click += (s, e) => ApplyPreset();
            _presets.DoubleClick += (s, e) => ApplyPreset();
            delPreset.Click += (s, e) =>
            {
                int i = _presets.SelectedIndex - 1; // la riga 0 è "Tutti i punti"
                DeviationSettings c = DeviationController.Current;
                if (i < 0 || c.ClipPresets == null || i >= c.ClipPresets.Count) return;
                var list = new List<ClipPreset>(c.ClipPresets);
                list.RemoveAt(i);
                c.ClipPresets = list;
                Reload();
            };
            _regions.ItemCheck += (s, e) =>
            {
                if (_loading) return;
                DeviationSettings c = DeviationController.Current;
                var list = c.Clips.Select(r => r.Clone()).ToList();
                if (e.Index < list.Count) list[e.Index].Enabled = e.NewValue == CheckState.Checked;
                c.Clips = list;
                Changed();
            };
            delRegion.Click += (s, e) =>
            {
                DeviationSettings c = DeviationController.Current;
                int i = _regions.SelectedIndex;
                if (i < 0 || i >= c.Clips.Count) return;
                var list = new List<ClipRegion>(c.Clips);
                list.RemoveAt(i);
                c.Clips = list;
                Changed();
                Reload();
            };
            clear.Click += (s, e) =>
            {
                DeviationController.Current.Clips = new List<ClipRegion>();
                DeviationController.Current.ClipInvert = false;
                Changed();
                Reload();
            };
            _invert.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                DeviationController.Current.ClipInvert = _invert.Checked;
                Changed();
            };
            save.Click += (s, e) =>
            {
                DeviationSettings c = DeviationController.Current;
                string n = _name.Text.Trim();
                if (n.Length == 0) n = L.T("Ritaglio") + " " + ((c.ClipPresets != null ? c.ClipPresets.Count : 0) + 1);
                var list = c.ClipPresets != null ? new List<ClipPreset>(c.ClipPresets) : new List<ClipPreset>();
                list.RemoveAll(p => p.Name == n);
                list.Add(new ClipPreset { Name = n, Invert = c.ClipInvert, Regions = c.Clips.Select(r => r.Clone()).ToList() });
                c.ClipPresets = list;
                _name.Text = "";
                Reload();
            };

            DeviationController.ActiveChanged += Reload;
            DeviationController.SettingsChanged += Reload;
            FormClosed += (s, e) =>
            {
                DeviationController.ActiveChanged -= Reload;
                DeviationController.SettingsChanged -= Reload;
            };
            UiTheme.Apply(this, save);
            Reload();
        }

        private void ApplyPreset()
        {
            DeviationSettings c = DeviationController.Current;
            int i = _presets.SelectedIndex;
            if (i < 0) return;
            if (i == 0) { c.Clips = new List<ClipRegion>(); c.ClipInvert = false; }
            else if (c.ClipPresets != null && i - 1 < c.ClipPresets.Count)
            {
                ClipPreset p = c.ClipPresets[i - 1];
                c.Clips = p.Regions.Select(r => r.Clone()).ToList();
                c.ClipInvert = p.Invert;
            }
            Changed();
            Reload();
        }

        private static void Changed()
        {
            App.SyncRibbon(DeviationController.Current);
            DeviationController.Apply(Job.Recolor);
        }

        private void Reload()
        {
            if (IsDisposed) return;
            _loading = true;
            try
            {
                DeviationSettings c = DeviationController.Current;
                _presets.Items.Clear();
                _presets.Items.Add("0   " + L.T("Tutti i punti"));
                if (c.ClipPresets != null)
                    for (int i = 0; i < c.ClipPresets.Count; i++)
                        _presets.Items.Add(string.Format("{0}   {1}  ({2})", i + 1, c.ClipPresets[i].Name, c.ClipPresets[i].Regions.Count));

                _regions.Items.Clear();
                foreach (ClipRegion r in c.Clips)
                    _regions.Items.Add((r.KeepInside ? "▣ " : "▢ ") + r.Name + (r.KeepInside ? "" : "  (" + L.T("punti fuori") + ")"), r.Enabled);
                _invert.Checked = c.ClipInvert;
            }
            finally { _loading = false; }
        }
    }
}
