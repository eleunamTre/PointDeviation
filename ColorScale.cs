using System;

namespace PointDeviation
{
    /// <summary>Scale colore condivise da rendering e legenda.</summary>
    internal static class ColorScale
    {
        private static readonly byte[][] ElevStops =
        {
            new byte[] { 30, 60, 230 },   // blu
            new byte[] { 0, 200, 230 },   // ciano
            new byte[] { 40, 190, 60 },   // verde
            new byte[] { 255, 220, 0 },   // giallo
            new byte[] { 230, 20, 20 }    // rosso
        };

        private static byte Lerp(byte a, byte b, double t)
        {
            return (byte)Math.Round(a + (b - a) * Math.Max(0.0, Math.Min(1.0, t)));
        }

        private static void Mix(byte r0, byte g0, byte b0, byte r1, byte g1, byte b1, double t,
            out byte r, out byte g, out byte b)
        {
            r = Lerp(r0, r1, t); g = Lerp(g0, g1, t); b = Lerp(b0, b1, t);
        }

        /// <summary>Rampa per quota: t in [0,1], dal blu (basso) al rosso (alto).</summary>
        public static void Elevation(double t, out byte r, out byte g, out byte b)
        {
            if (double.IsNaN(t)) t = 0.0;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double f = t * (ElevStops.Length - 1);
            int i = (int)Math.Floor(f);
            if (i >= ElevStops.Length - 1) i = ElevStops.Length - 2;
            double u = f - i;
            r = Lerp(ElevStops[i][0], ElevStops[i + 1][0], u);
            g = Lerp(ElevStops[i][1], ElevStops[i + 1][1], u);
            b = Lerp(ElevStops[i][2], ElevStops[i + 1][2], u);
        }

        /// <summary>Colore usato per i punti senza modello vicino quando sono evidenziati.</summary>
        public static readonly byte[] MissingRgb = { 190, 0, 190 };

        /// <summary>Luminosità e contrasto (entrambi da -100 a 100) applicati a un colore RGB.</summary>
        public static void Adjust(ref byte r, ref byte g, ref byte b, double brightness, double contrast)
        {
            if (brightness == 0.0 && contrast == 0.0) return;
            double f = 1.0 + contrast / 100.0;
            double off = 128.0 + brightness * 2.55;
            r = Clamp((r - 128.0) * f + off);
            g = Clamp((g - 128.0) * f + off);
            b = Clamp((b - 128.0) * f + off);
        }

        private static byte Clamp(double v)
        {
            return (byte)Math.Max(0.0, Math.Min(255.0, Math.Round(v)));
        }
    }
}
