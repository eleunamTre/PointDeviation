using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Drawing.Color;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using View = Autodesk.Revit.DB.View;

namespace PointDeviation
{
    // =====================================================================================
    //  Coda di operazioni da eseguire nel contesto dell'API di Revit (dalle finestre non modali)
    // =====================================================================================

    internal sealed class ApiQueue : IExternalEventHandler
    {
        private static ApiQueue _handler;
        private static ExternalEvent _event;
        private readonly ConcurrentQueue<Action<UIApplication>> _q = new ConcurrentQueue<Action<UIApplication>>();

        public static void Init()
        {
            if (_event != null) return;
            _handler = new ApiQueue();
            _event = ExternalEvent.Create(_handler);
        }

        public static void Run(Action<UIApplication> a)
        {
            if (_event == null) return;
            _handler._q.Enqueue(a);
            _event.Raise();
        }

        public string GetName() { return "PointDeviation API"; }

        public void Execute(UIApplication app)
        {
            Action<UIApplication> a;
            while (_q.TryDequeue(out a))
            {
                try { a(app); }
                catch (Exception ex) { TaskDialog.Show("Point Deviation", ex.Message); }
            }
        }
    }

    // =====================================================================================
    //  Pannello Visibilità: una riga per nuvola, con spunta (visibile nella vista attiva) e colore
    // =====================================================================================

    internal sealed class VisibilityForm : Form
    {
        private readonly ListView _list = new ListView
        {
            View = System.Windows.Forms.View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            OwnerDraw = true
        };
        private bool _loading;
        private readonly Label _diag = new Label { AutoEllipsis = true };
        private readonly Timer _timer = new Timer { Interval = 1000 };

        private CloudLayer Current()
        {
            ListViewItem it = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0] : (_list.Items.Count > 0 ? _list.Items[0] : null);
            return it != null ? it.Tag as CloudLayer : null;
        }

        /// <summary>Riga di diagnostica della nuvola selezionata (o della prima): disegno, caricamento, posizione.</summary>
        private void UpdateDiag()
        {
            try
            {
                CloudLayer l = Current();
                if (l == null) { _diag.Text = ""; return; }
                int loaded, pending, failed;
                l.LoadStats(out loaded, out pending, out failed);
                bool recent = Environment.TickCount - l.StatTick < 5000;
                XYZ a, b;
                l.ModelBounds(out a, out b);
                XYZ c = (a + b) / 2;
                _diag.Text = string.Format(L.T("{0}: disegnati {1:N0} punti in {2} blocchi su {3} richiesti{4}. In memoria {5}/{6}, in lettura {7}, errori {8}.\nCentro nel progetto (m): {9:F1}, {10:F1}, {11:F1}{12}"),
                    l.Name, recent ? l.StatPoints : 0, recent ? l.StatNodes : 0, recent ? l.StatWanted : 0,
                    recent ? "" : L.T(" (non disegnata di recente)"),
                    loaded, l.Tiles.Length, pending, failed,
                    c.X * 0.3048, c.Y * 0.3048, c.Z * 0.3048, (l.IsOctree ? " · octree" : "") + (l.StatBudgetHit ? L.T(" · limite di punti raggiunto") : ""));
            }
            catch { }
        }

        /// <summary>Inquadra la nuvola selezionata (o tutte) nella vista attiva.</summary>
        private void ZoomTo()
        {
            CloudLayer only = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as CloudLayer : null;
            ApiQueue.Run(app =>
            {
                UIDocument uidoc = app.ActiveUIDocument;
                DocumentData d = DeviationController.ActiveData;
                if (uidoc == null || d == null) return;
                XYZ mn = null, mx = null;
                foreach (CloudLayer l in d.Layers)
                {
                    if (only != null && !ReferenceEquals(l, only)) continue;
                    XYZ a, b;
                    l.ModelBounds(out a, out b);
                    mn = mn == null ? a : new XYZ(Math.Min(mn.X, a.X), Math.Min(mn.Y, a.Y), Math.Min(mn.Z, a.Z));
                    mx = mx == null ? b : new XYZ(Math.Max(mx.X, b.X), Math.Max(mx.Y, b.Y), Math.Max(mx.Z, b.Z));
                }
                if (mn == null) return;
                UIView uiv = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id);
                if (uiv != null) uiv.ZoomAndCenterRectangle(mn, mx);
            });
        }

        public VisibilityForm()
        {
            Text = L.T("Visibilità nuvole");
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 260);
            BackColor = UiTheme.Surface;

            _list.Columns.Add("#", 40);
            _list.Columns.Add(L.T("Colore"), 60);
            _list.Columns.Add(L.T("Nome"), 230);
            _list.Columns.Add(L.T("Punti"), 90, HorizontalAlignment.Right);
            _list.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            _list.DrawSubItem += DrawSubItem;
            _list.ItemChecked += OnChecked;
            _list.MouseClick += OnClick;
            // mouse sopra un nome: la nuvola si illumina del suo colore
            _list.MouseMove += (s, e) =>
            {
                ListViewItem it = _list.GetItemAt(e.X, e.Y);
                var l = it != null ? it.Tag as CloudLayer : null;
                CloudHighlight.Set(l != null ? l.Folder : null);
            };
            _list.MouseLeave += (s, e) => CloudHighlight.Set(null);

            var hint = new Label
            {
                Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(8, 4, 8, 0), ForeColor = UiTheme.Muted,
                Text = L.T("Spunta = visibile nella vista attiva (come Nascondi elemento). Mouse sul nome: la nuvola si illumina. Clic sul colore per cambiarlo.")
            };
            // diagnostica e zoom sulla nuvola
            var bottom = new System.Windows.Forms.Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(8, 4, 8, 4) };
            var zoom = new Button { Text = L.T("Zoom sulla nuvola"), AutoSize = true, Dock = DockStyle.Right };
            zoom.Click += (s, e) => ZoomTo();
            var log = new Button { Text = L.T("Registro"), AutoSize = true, Dock = DockStyle.Right };
            log.Click += (s, e) =>
            {
                try
                {
                    if (!System.IO.File.Exists(Log.FilePath)) Log.Write("Registro", "aperto");
                    System.Diagnostics.Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
                }
                catch { }
            };
            _diag.Dock = DockStyle.Fill;
            _diag.ForeColor = UiTheme.Muted;
            bottom.Controls.Add(_diag);
            bottom.Controls.Add(zoom);
            bottom.Controls.Add(log);
            Controls.Add(_list);
            Controls.Add(hint);
            Controls.Add(bottom);
            _timer.Tick += (s, e) => UpdateDiag();
            _timer.Start();

            DeviationController.ActiveChanged += Reload;
            DeviationController.SettingsChanged += Reload;
            FormClosed += (s, e) =>
            {
                DeviationController.ActiveChanged -= Reload;
                DeviationController.SettingsChanged -= Reload;
                CloudHighlight.Set(null);
                _timer.Dispose();
            };
            Deactivate += (s, e) => CloudHighlight.Set(null);
            UiTheme.Apply(this);
            Shown += (s, e) => Reload(); // i dati arrivano dal thread di Revit: la finestra deve già esistere
        }

        public void Reload()
        {
            if (IsDisposed || !IsHandleCreated) return;
            ApiQueue.Run(app =>
            {
                UIDocument uidoc = app.ActiveUIDocument;
                DocumentData d = DeviationController.ActiveData;
                if (uidoc == null || d == null || IsDisposed) return;
                View view = uidoc.ActiveView;
                var rows = d.Layers.Select((l, i) =>
                {
                    Element e = uidoc.Document.GetElement(l.InstanceId);
                    bool visible = e != null && !e.IsHidden(view);
                    return new { Index = i + 1, Layer = l, Visible = visible };
                }).ToList();

                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    _loading = true;
                    try
                    {
                        _list.Items.Clear();
                        foreach (var r in rows)
                        {
                            var it = new ListViewItem(r.Index.ToString()) { Checked = r.Visible, Tag = r.Layer };
                            it.SubItems.Add("");
                            it.SubItems.Add(r.Layer.Name);
                            it.SubItems.Add(r.Layer.Info.PointCount.ToString("N0"));
                            _list.Items.Add(it);
                        }
                    }
                    finally { _loading = false; }
                }));
            });
        }

        private static int ColorOf(CloudLayer l)
        {
            int c;
            return DeviationController.Current.CloudColors.TryGetValue(l.Folder, out c) ? c : CloudPalette.For(l.Folder);
        }

        private void DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != 1) { e.DrawDefault = true; return; }
            e.DrawBackground();
            var l = e.Item.Tag as CloudLayer;
            if (l == null) return;
            var r = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 3, 28, e.Bounds.Height - 6);
            using (var br = new SolidBrush(Color.FromArgb(ColorOf(l)))) e.Graphics.FillRectangle(br, r);
            e.Graphics.DrawRectangle(Pens.DimGray, r);
        }

        private void OnClick(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo hit = _list.HitTest(e.Location);
            if (hit.Item == null || hit.SubItem == null || hit.Item.SubItems.IndexOf(hit.SubItem) != 1) return;
            var l = hit.Item.Tag as CloudLayer;
            if (l == null) return;
            using (var dlg = new ColorDialog { Color = Color.FromArgb(ColorOf(l)), FullOpen = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                DeviationSettings s = DeviationController.Current;
                var map = new Dictionary<string, int>(s.CloudColors, StringComparer.OrdinalIgnoreCase);
                map[l.Folder] = dlg.Color.ToArgb();
                s.CloudColors = map;
                _list.Invalidate();
                DeviationController.Apply(Job.Recolor);
            }
        }

        private void OnChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_loading) return;
            var l = e.Item.Tag as CloudLayer;
            if (l == null) return;
            bool hide = !e.Item.Checked;
            ElementId id = l.InstanceId;
            ApiQueue.Run(app =>
            {
                UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                NativeSync.SetHidden(uidoc.Document, uidoc.ActiveView, new List<ElementId> { id }, hide);
            });
        }
    }

    // =====================================================================================
    //  Misura punti: clic sulla nuvola -> marcatori a cui si agganciano quote e misure di Revit
    // =====================================================================================

    internal static class MeasureTool
    {
        public static void Run(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null) return;
            DocumentData d = DeviationController.GetData(uidoc.Document);
            if (d == null || !d.HasCloud) return;
            View view = uidoc.ActiveView;

            var v3 = view as View3D;
            if (v3 != null && v3.IsPerspective)
            {
                TaskDialog.Show("Point Deviation", L.T("Misura punti funziona nelle viste ortogonali: passa a una vista 3D non prospettica, una pianta o una sezione."));
                return;
            }
            UIView uiv = uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == view.Id);
            if (uiv == null) return;

            Autodesk.Revit.DB.Rectangle wr = uiv.GetWindowRectangle();
            IList<XYZ> corners = uiv.GetZoomCorners();
            XYZ origin = view.Origin, right = view.RightDirection, up = view.UpDirection;
            XYZ forward = v3 != null ? v3.GetOrientation().ForwardDirection : view.ViewDirection.Negate();
            Func<XYZ, double> U = p => (p - origin).DotProduct(right);
            Func<XYZ, double> V = p => (p - origin).DotProduct(up);
            double u0 = U(corners[0]), v0 = V(corners[0]), u1 = U(corners[1]), v1 = V(corners[1]);
            var screen = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
            double w = screen.Width, h = screen.Height;
            double pixel = Math.Abs(u1 - u0) / Math.Max(1, w);

            var visible = d.Layers.Where(l => l.VisibleIn(view)).ToList();
            var picked = new List<XYZ>();
            Style st = d.Style;

            Func<PointF, bool> pick = p =>
            {
                double u = u0 + p.X / w * (u1 - u0), v = v1 - p.Y / h * (v1 - v0);
                XYZ o = origin + right * u + up * v - forward * 1e4; // origine del raggio ben davanti alla vista
                double bestT = double.MaxValue;
                XYZ best = null;
                foreach (CloudLayer l in visible) l.Pick(o, forward, pixel * 7, st, ref bestT, ref best);
                if (best == null) return false;
                picked.Add(best);
                return true;
            };

            using (var draw = new FenceDrawLayer(screen))
            using (var input = new MeasureInputLayer(screen, draw, pick))
            {
                draw.Show(new WindowOwner(app.MainWindowHandle));
                input.ShowDialog(new WindowOwner(app.MainWindowHandle));
                draw.Close();
            }
            if (picked.Count == 0) return;

            using (var t = new Transaction(uidoc.Document, L.T("Misura punti")))
            {
                t.Start();
                int n = new FilteredElementCollector(uidoc.Document).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                    .Count(x => x.ApplicationId == MarkerAppId);
                foreach (XYZ p in picked) CreateMarker(uidoc.Document, p, ++n);
                t.Commit();
            }
        }

        public const string MarkerAppId = "PointDeviation.Marker";

        /// <summary>Croce 3D di tre segmenti: Revit si aggancia agli estremi e all'incrocio con quote e misure.</summary>
        private static void CreateMarker(Document doc, XYZ p, int number)
        {
            double r = 0.05 / 0.3048;
            var shape = new List<GeometryObject>
            {
                Line.CreateBound(p - XYZ.BasisX * r, p + XYZ.BasisX * r),
                Line.CreateBound(p - XYZ.BasisY * r, p + XYZ.BasisY * r),
                Line.CreateBound(p - XYZ.BasisZ * r, p + XYZ.BasisZ * r)
            };
            DirectShape ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
            ds.ApplicationId = MarkerAppId;
            ds.ApplicationDataId = number.ToString();
            ds.SetShape(shape);
            try { ds.SetName(L.T("Punto misurato") + " " + number); } catch { }
            try
            {
                Parameter cm = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (cm != null && !cm.IsReadOnly)
                    cm.Set(string.Format("X={0:F3} Y={1:F3} Z={2:F3} m", p.X * 0.3048, p.Y * 0.3048, p.Z * 0.3048));
            }
            catch { }
        }
    }

    /// <summary>Strato di input di Misura punti: ogni clic cerca il punto della nuvola sotto il cursore.</summary>
    internal sealed class MeasureInputLayer : Form
    {
        private readonly FenceDrawLayer _draw;
        private readonly Func<PointF, bool> _pick;
        private readonly List<PointF> _marks = new List<PointF>();

        public MeasureInputLayer(Rectangle bounds, FenceDrawLayer draw, Func<PointF, bool> pick)
        {
            _draw = draw;
            _pick = pick;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            Opacity = 0.01;
            KeyPreview = true;
            TopMost = true;
            Cursor = Cursors.Cross;
            _draw.IsClosed = true;
            Banner(L.T("Clicca sui punti della nuvola  |  INVIO o ESC termina"));
        }

        private void Banner(string text)
        {
            _draw.Banner = text;
            _draw.Marks = new List<PointF>(_marks); // ogni punto preso è segnato da una piccola croce
            _draw.Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Escape || k == Keys.Enter) { DialogResult = DialogResult.OK; Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.OK; Close(); return; }
            if (e.Button != MouseButtons.Left) return;
            var p = new PointF(e.X, e.Y);
            if (_pick(p))
            {
                _marks.Add(p);
                Banner(string.Format(L.T("{0} punti presi  |  INVIO o ESC termina"), _marks.Count));
            }
            else Banner(L.T("Nessun punto della nuvola qui: avvicinati o clicca su una zona più densa"));
        }
    }

    // =====================================================================================
    //  Coordinate di rilievo: le coordinate condivise del progetto diventano quelle originali di una nuvola
    // =====================================================================================

    internal static class SurveyTool
    {
        public static void Run(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null) return;
            Document doc = uidoc.Document;
            DocumentData d = DeviationController.GetData(doc);
            if (d == null || !d.HasCloud) return;

            // nuvola selezionata, altrimenti la prima (con più nuvole si chiede di selezionarla)
            CloudLayer layer = null;
            foreach (ElementId id in uidoc.Selection.GetElementIds())
            {
                layer = d.Layers.Find(l => l.InstanceId == id);
                if (layer != null) break;
            }
            if (layer == null)
            {
                if (d.Layers.Count > 1)
                {
                    TaskDialog.Show("Point Deviation", L.T("Ci sono più nuvole: seleziona quella da usare come riferimento e riprova."));
                    return;
                }
                layer = d.Layers[0];
            }

            var td = new TaskDialog("Point Deviation")
            {
                MainInstruction = L.T("Impostare le coordinate condivise del progetto su quelle della nuvola?"),
                MainContent = string.Format(L.T("Dopo l'operazione le coordinate condivise (rilievo) del progetto coincideranno con quelle originali di «{0}». Si può annullare con Annulla."), layer.Name),
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel
            };
            if (td.Show() != TaskDialogResult.Ok) return;

            string err;
            using (var t = new Transaction(doc, L.T("Imposta coordinate di rilievo")))
            {
                t.Start();
                err = Apply(doc, layer.T);
                if (err == null) t.Commit(); else t.RollBack();
            }
            TaskDialog.Show("Point Deviation", err ?? L.T("Coordinate condivise impostate. Le nuove nuvole inserite con «Coordinate condivise» cadranno nella stessa posizione."));
        }

        /// <summary>
        /// Imposta la posizione di progetto in modo che (coordinate condivise -> interne) coincida con il posizionamento
        /// della nuvola. Invece di affidarsi alle convenzioni di segno di ProjectPosition, misura l'effetto dei parametri
        /// sulla trasformazione risultante e risolve: rotazione con prova dei due segni, traslazione con un sistema 3x3.
        /// </summary>
        private static string Apply(Document doc, double[] pl)
        {
            ProjectLocation loc = doc.ActiveProjectLocation;
            Transform target = NativeClouds.ToTransform(pl);
            if (Math.Abs(target.BasisZ.Z - 1) > 1e-6) return L.T("La nuvola è inclinata: si possono impostare solo rotazioni attorno all'asse verticale.");
            double theta = Math.Atan2(target.BasisX.Y, target.BasisX.X);

            double angle = double.NaN;
            foreach (double a in new[] { theta, -theta })
            {
                loc.SetProjectPosition(XYZ.Zero, new ProjectPosition(0, 0, 0, a));
                Transform got = loc.GetTotalTransform();
                if (got.BasisX.IsAlmostEqualTo(target.BasisX, 1e-6) && got.BasisY.IsAlmostEqualTo(target.BasisY, 1e-6)) { angle = a; break; }
            }
            if (double.IsNaN(angle)) return L.T("Non è stato possibile allineare la rotazione delle coordinate.");

            Func<double, double, double, XYZ> origin = (ew, ns, el) =>
            {
                loc.SetProjectPosition(XYZ.Zero, new ProjectPosition(ew, ns, el, angle));
                return loc.GetTotalTransform().Origin;
            };
            XYZ o0 = origin(0, 0, 0);
            XYZ dx = origin(1, 0, 0) - o0, dy = origin(0, 1, 0) - o0, dz = origin(0, 0, 1) - o0;
            XYZ need = target.Origin - o0;

            // need = a*dx + b*dy + c*dz  (Cramer)
            double det = dx.DotProduct(dy.CrossProduct(dz));
            if (Math.Abs(det) < 1e-12) return L.T("Non è stato possibile allineare la posizione delle coordinate.");
            double pa = need.DotProduct(dy.CrossProduct(dz)) / det;
            double pb = dx.DotProduct(need.CrossProduct(dz)) / det;
            double pc = dx.DotProduct(dy.CrossProduct(need)) / det;
            XYZ final = origin(pa, pb, pc);
            if (!final.IsAlmostEqualTo(target.Origin, 1e-4)) return L.T("Non è stato possibile allineare la posizione delle coordinate.");
            return null;
        }
    }
}
