using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PointDeviation
{
    /// <summary>
    /// Legenda del profilo colore attivo: barra verticale (sfumata o a fasce) con le tacche dei valori,
    /// etichette delle classi e colore della nuvola senza modello. Ridimensionabile.
    /// </summary>
    internal sealed class LegendForm : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UiTheme.Apply(this);
        }

        private readonly Button _edit = new Button { Text = L.T("Profili…"), AutoSize = true };

        public LegendForm()
        {
            Text = L.T("Legenda");
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(230, 480);
            MinimumSize = new Size(180, 260);
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 8.5f);

            _edit.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _edit.Location = new Point(ClientSize.Width - _edit.PreferredSize.Width - 8, ClientSize.Height - _edit.PreferredSize.Height - 8);
            _edit.Click += (s, e) => DeviationController.ShowProfiles();
            Controls.Add(_edit);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DeviationSettings s = DeviationController.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            if (s.Display != DisplayMode.Discrepanze)
            {
                g.DrawString(L.T("La legenda è disponibile\nin modalità Discrepanze."), Font, Brushes.Black, 10, 10);
                return;
            }

            ColorProfile p = s.Profile;
            DrawProfile(g, p, s.Missing, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height - _edit.Height - 12), Font, true);
        }

        /// <summary>Disegna la legenda di un profilo nel rettangolo dato (usato anche dall'editor per l'anteprima).</summary>
        public static void DrawProfile(Graphics g, ColorProfile p, MissingMode missing, Rectangle area, Font font, bool title)
        {
            int y0 = area.Top + 8;
            if (title)
            {
                using (var bold = new Font(font, FontStyle.Bold))
                {
                    SizeF sz = g.MeasureString(p.Name, bold, area.Width - 16);
                    g.DrawString(p.Name, bold, Brushes.Black, new RectangleF(area.Left + 8, y0, area.Width - 16, sz.Height));
                    y0 += (int)Math.Ceiling(sz.Height) + 2;
                }
                g.DrawString(p.Absolute ? L.T("valore assoluto, mm") : L.T("mm  (+ davanti, − dietro)"), font, Brushes.Gray, area.Left + 8, y0);
                y0 += font.Height + 8;
            }

            if (p.IsSelection)
            {
                DrawSelectionLegend(g, p, new Rectangle(area.Left, y0, area.Width, area.Bottom - y0), font);
                return;
            }

            if (p.HideMissing) missing = MissingMode.Nascondi;
            int missingH = missing == MissingMode.Nascondi ? 0 : 44;
            int top = y0 + 6, bottom = area.Bottom - 10 - missingH;
            int h = Math.Max(40, bottom - top);
            int x0 = area.Left + 14, w = 26;

            double mn, mx;
            p.GetRange(out mn, out mx);

            // barra: dall'alto (massimo) al basso (minimo), da una bitmap in cache
            g.DrawImageUnscaled(BarBitmap(p, w, h), x0, top);
            g.DrawRectangle(Pens.DimGray, x0, top, w, h);

            // tacche, evitando etichette sovrapposte
            Func<double, int> yOf = v => top + (int)Math.Round((mx - v) / (mx - mn) * h);
            int lastY = int.MinValue;
            List<double> ticks = p.Ticks();
            ticks.Reverse();
            foreach (double v in ticks)
            {
                int y = yOf(v);
                g.DrawLine(Pens.DimGray, x0 + w, y, x0 + w + 5, y);
                if (Math.Abs(y - lastY) < font.Height) continue;
                g.DrawString(Format(v, p.Absolute), font, Brushes.Black, x0 + w + 8, y - font.Height / 2);
                lastY = y;
            }

            // etichette delle classi, a metà di ciascuna fascia
            if (p.Kind == ProfileKind.Classi)
            {
                foreach (ColorClass k in p.Classes)
                {
                    if (string.IsNullOrEmpty(k.Label)) continue;
                    int ya = yOf(Math.Max(k.FromMm, k.ToMm)), yb = yOf(Math.Min(k.FromMm, k.ToMm));
                    if (yb - ya < font.Height) continue;
                    g.DrawString(k.Label, font, Brushes.DimGray, x0 + w + 62, (ya + yb) / 2 - font.Height / 2);
                }
            }

            if (missingH > 0)
            {
                Color c = missing == MissingMode.Evidenzia
                    ? Color.FromArgb(ColorScale.MissingRgb[0], ColorScale.MissingRgb[1], ColorScale.MissingRgb[2])
                    : Color.FromArgb(150, 150, 150);
                int ym = bottom + 16;
                using (var br = new SolidBrush(c)) g.FillRectangle(br, x0, ym, w, 16);
                g.DrawRectangle(Pens.DimGray, x0, ym, w, 16);
                g.DrawString(string.Format(L.T("Nessun modello\nentro {0:0.#} mm"), p.SearchRangeMm), font, Brushes.Black, x0 + w + 8, ym - 4);
            }
        }

        /// <summary>Schema per completezza e interferenze: una parete in sezione e le zone evidenziate.</summary>
        private static void DrawSelectionLegend(Graphics g, ColorProfile p, Rectangle a, Font font)
        {
            Color hi = Color.FromArgb(p.HighlightArgb), ins = Color.FromArgb(p.InsideArgb);
            int x0 = a.Left + 14, w = Math.Max(80, a.Width - 28);
            int top = a.Top + 4, h = Math.Max(120, Math.Min(260, a.Height - 130));
            int mid = top + h / 2, wall = Math.Max(12, h / 9), gap = Math.Max(14, h / 5);
            var wallRect = new Rectangle(x0, mid - wall / 2, w, wall);

            using (var fill = new SolidBrush(Color.FromArgb(150, hi)))
            using (var dash = new Pen(Color.DimGray) { DashStyle = DashStyle.Dash })
            {
                if (p.Kind == ProfileKind.Completezza)
                {
                    g.FillRectangle(fill, x0, top, w, wallRect.Top - gap - top);
                    g.FillRectangle(fill, x0, wallRect.Bottom + gap, w, top + h - wallRect.Bottom - gap);
                    g.DrawLine(dash, x0, wallRect.Top - gap, x0 + w, wallRect.Top - gap);
                    g.DrawLine(dash, x0, wallRect.Bottom + gap, x0 + w, wallRect.Bottom + gap);
                }
                else
                {
                    g.FillRectangle(fill, x0, wallRect.Top - gap, w, wall + 2 * gap);
                    g.DrawLine(dash, x0, wallRect.Top - gap, x0 + w, wallRect.Top - gap);
                    g.DrawLine(dash, x0, wallRect.Bottom + gap, x0 + w, wallRect.Bottom + gap);
                }
            }

            // parete tratteggiata (per le interferenze l'interno ha il colore della compenetrazione)
            using (var hatch = new HatchBrush(HatchStyle.WideDownwardDiagonal, Color.FromArgb(60, 60, 60),
                       p.Kind == ProfileKind.Interferenze ? Color.FromArgb(200, ins) : Color.White))
                g.FillRectangle(hatch, wallRect);
            g.DrawLine(Pens.Black, wallRect.Left, wallRect.Top, wallRect.Right, wallRect.Top);
            g.DrawLine(Pens.Black, wallRect.Left, wallRect.Bottom, wallRect.Right, wallRect.Bottom);

            // quota della soglia
            int qx = x0 + 10;
            g.DrawLine(Pens.Black, qx, wallRect.Top - gap, qx, wallRect.Top);
            g.DrawLine(Pens.Black, qx - 4, wallRect.Top - gap, qx + 4, wallRect.Top - gap);
            g.DrawLine(Pens.Black, qx - 4, wallRect.Top, qx + 4, wallRect.Top);
            g.DrawString(string.Format("{0:0.#} mm", p.ThresholdMm), font, Brushes.Black, qx + 6, wallRect.Top - gap / 2 - font.Height / 2);

            // voci
            int y = top + h + 12;
            Action<Color, string> item = (c, text) =>
            {
                using (var br = new SolidBrush(c)) g.FillRectangle(br, x0, y + 2, 18, 12);
                g.DrawRectangle(Pens.DimGray, x0, y + 2, 18, 12);
                SizeF sz = g.MeasureString(text, font, w - 26);
                g.DrawString(text, font, Brushes.Black, new RectangleF(x0 + 26, y, w - 26, sz.Height + 2));
                y += Math.Max(18, (int)sz.Height + 6);
            };
            if (p.Kind == ProfileKind.Completezza)
                item(hi, string.Format(L.T("Più di {0:0.#} mm da qualsiasi superficie del modello"), p.ThresholdMm));
            else
            {
                item(hi, string.Format(L.T("Entro {0:0.#} mm dal modello (da entrambi i lati)"), p.ThresholdMm));
                item(ins, L.T("Dentro il modello (compenetrazione)"));
            }
            if (p.ShowOthers) item(Color.FromArgb(150, 150, 150), L.T("Altri punti"));
        }

        private static Bitmap _bar;
        private static string _barKey;

        private static Bitmap BarBitmap(ColorProfile p, int w, int h)
        {
            string key;
            using (var ms = new System.IO.MemoryStream())
            {
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ColorProfile)).WriteObject(ms, p);
                key = Convert.ToBase64String(ms.ToArray()) + "|" + w + "x" + h;
            }
            if (_bar != null && key == _barKey) return _bar;

            double mn, mx;
            p.GetRange(out mn, out mx);
            var bmp = new Bitmap(w, h);
            for (int y = 0; y < h; y++)
            {
                double v = mx - (mx - mn) * (y + 0.5) / h;
                byte r, gg, b;
                Color c = p.TryColor(v, out r, out gg, out b) ? Color.FromArgb(r, gg, b) : Color.White;
                for (int x = 0; x < w; x++) bmp.SetPixel(x, y, c);
            }
            if (_bar != null) _bar.Dispose();
            _bar = bmp;
            _barKey = key;
            return bmp;
        }

        private static string Format(double v, bool absolute)
        {
            return absolute ? string.Format("{0:0.##}", v) : string.Format("{0:+0.##;−0.##;0}", v);
        }
    }
}
