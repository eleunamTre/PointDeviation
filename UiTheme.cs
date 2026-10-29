using System;
using System.Drawing;
using System.Windows.Forms;

namespace PointDeviation
{
    /// <summary>
    /// Aspetto comune di tutte le finestre: stesso carattere, colori, pulsanti piatti con bordo sottile
    /// e un colore d'accento per l'azione principale. Si applica alla fine del costruttore di ogni finestra.
    /// </summary>
    internal static class UiTheme
    {
        public static readonly Color Accent = Color.FromArgb(31, 114, 207);
        public static readonly Color AccentHover = Color.FromArgb(24, 94, 172);
        public static readonly Color Ink = Color.FromArgb(44, 62, 80);
        public static readonly Color Muted = Color.FromArgb(110, 120, 130);
        public static readonly Color Border = Color.FromArgb(200, 206, 214);
        public static readonly Color Surface = Color.White;
        public static readonly Color Panel = Color.FromArgb(244, 246, 248);
        public static readonly Color Hover = Color.FromArgb(232, 240, 250);

        public static Font Base { get { return new Font("Segoe UI", 9f); } }

        /// <summary>Applica il tema alla finestra e a tutti i controlli contenuti.</summary>
        public static void Apply(Form f, Button primary = null)
        {
            f.Font = Base;
            if (f.BackColor == SystemColors.Control) f.BackColor = Surface;
            f.ForeColor = Ink;
            ApplyTo(f.Controls, primary ?? f.AcceptButton as Button);
        }

        private static void ApplyTo(Control.ControlCollection controls, Button primary)
        {
            foreach (Control c in controls)
            {
                var b = c as Button;
                if (b != null && b.BackColor == SystemColors.Control || b != null && b.UseVisualStyleBackColor)
                    StyleButton(b, ReferenceEquals(b, primary));
                else if (c is Panel || c is TableLayoutPanel || c is FlowLayoutPanel)
                {
                    // sfondo pieno, mai trasparente: i TrackBar su sfondo trasparente entrano in un ciclo di
                    // ridisegno continuo che blocca la finestra e rallenta tutta l'interfaccia di Revit
                    if (c.BackColor == SystemColors.Control || c.BackColor == Color.Transparent) c.BackColor = Surface;
                }
                else if (c is TrackBar)
                {
                    c.BackColor = Surface;
                }
                else if (c is TextBox || c is NumericUpDown || c is ComboBox)
                {
                    if (c is ComboBox) ((ComboBox)c).FlatStyle = FlatStyle.Flat;
                }
                else if (c is Label && c.ForeColor == SystemColors.GrayText)
                {
                    c.ForeColor = Muted;
                }

                if (c.HasChildren) ApplyTo(c.Controls, primary);
            }
        }

        public static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.BorderSize = 1;
            b.Cursor = Cursors.Hand;
            if (b.AutoSize) b.Padding = new Padding(8, 1, 8, 1);
            if (primary)
            {
                b.BackColor = Accent;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderColor = Accent;
                b.FlatAppearance.MouseOverBackColor = AccentHover;
                b.FlatAppearance.MouseDownBackColor = AccentHover;
            }
            else
            {
                b.BackColor = Surface;
                b.ForeColor = Ink;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = Hover;
                b.FlatAppearance.MouseDownBackColor = Hover;
            }
        }

        /// <summary>Etichetta di sezione (titoletto) usata nelle finestre con più gruppi di opzioni.</summary>
        public static Label Section(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 9.5f),
                ForeColor = Ink,
                Margin = new Padding(0, 10, 0, 4)
            };
        }
    }
}
