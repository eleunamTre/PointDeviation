using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;

namespace PointDeviation
{
    /// <summary>
    /// Operazioni che richiedono l'API di Revit (thread principale, dentro un ExternalEvent):
    /// estrazione dei punti dalle nuvole ReCap e calcolo del posizionamento all'inserimento.
    /// </summary>
    internal static class RevitCloudOps
    {
        private const double FtPerM = 1.0 / 0.3048;
        private const int CallCap = 500000;   // punti massimi chiesti a Revit per ogni chiamata GetPoints

        // ===================================================================== estrazione RCP / RCS

        /// <summary>
        /// Estrae i punti di una nuvola ReCap in un file grezzo, pronto per il convertitore della libreria.
        /// La nuvola viene inserita in una transazione che alla fine si annulla: il progetto non cambia.
        /// existingType: tipo di una nuvola già inserita nel progetto (in alternativa al file).
        /// </summary>
        public static RawPointWriter ExtractToRaw(Document doc, string filePath, ElementId existingType,
            double spacingM, string rawPath, Action<string> progress)
        {
            using (var t = new Transaction(doc, "PointDeviation: lettura nuvola"))
            {
                t.Start();
                try
                {
                    PointCloudType type;
                    if (existingType != null && existingType != ElementId.InvalidElementId)
                    {
                        type = doc.GetElement(existingType) as PointCloudType;
                        if (type == null) throw new InvalidOperationException(L.T("Tipo di nuvola non trovato."));
                    }
                    else
                    {
                        string engine = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(); // "rcp" o "rcs"
                        type = PointCloudType.Create(doc, engine, filePath);
                    }

                    // inserita con trasformazione identità: coordinate di modello = coordinate della nuvola
                    PointCloudInstance inst = PointCloudInstance.Create(doc, type.Id, Transform.Identity);
                    doc.Regenerate();

                    BoundingBoxXYZ bb = inst.get_BoundingBox(null);
                    if (bb == null) throw new InvalidOperationException(L.T("Revit non restituisce l'ingombro della nuvola."));

                    XYZ offset = XYZ.Zero;
                    try { offset = type.Offset; } catch { }

                    XYZ bmin = bb.Min, bmax = bb.Max;
                    XYZ mid = (bmin + bmax) / 2.0;
                    double spacingFt = Math.Max(0.0005, spacingM) * FtPerM;
                    // Revit tiene le nuvole ReCap vicino all'origine e mette lo spostamento (es. UTM) nell'offset del tipo:
                    // per avere le coordinate originali della scansione, come in ReCap, lo si somma ai punti.
                    XYZ toOriginal = !offset.IsZeroLength() && mid.GetLength() < offset.GetLength() * 0.5 ? offset : XYZ.Zero;
                    XYZ midOrig = mid + toOriginal;
                    var writer = new RawPointWriter(rawPath, new[] { midOrig.X / FtPerM, midOrig.Y / FtPerM, midOrig.Z / FtPerM })
                    {
                        FormatName = existingType != null && existingType != ElementId.InvalidElementId ? L.T("RCP (dal progetto)") : Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant(),
                        SourcePath = filePath ?? type.Name
                    };

                    try
                    {
                        // ------------------------------------------------- in che sistema lavorano filtro e punti?
                        // L'API non dichiara in modo inequivocabile se filtro e punti restituiti sono in coordinate di
                        // modello o locali della nuvola (che differiscono di PointCloudType.Offset). Si prova e si osserva.
                        bool filterLocal = false;
                        PointCollection probe = SafeGetPoints(inst, BoxFilter(bmin, bmax, XYZ.Zero), spacingFt * 50, 2000);
                        if (Count(probe) == 0 && !offset.IsZeroLength())
                        {
                            filterLocal = true;
                            probe = SafeGetPoints(inst, BoxFilter(bmin, bmax, offset), spacingFt * 50, 2000);
                        }
                        if (Count(probe) == 0)
                            throw new InvalidOperationException(L.T("Revit non restituisce punti per questa nuvola (è caricata? è vuota?)."));

                        // ------------------------------------------------- attesa del caricamento
                        // Revit carica le scansioni ReCap in background: chiedendo i punti subito se ne ottiene solo una parte.
                        // Si aspetta che il numero di punti (a densità bassa) smetta di crescere.
                        {
                            PointCloudFilter whole = BoxFilter(bmin, bmax, filterLocal ? offset : XYZ.Zero);
                            double probeSp = spacingFt * 20;
                            int prev = -1, stable = 0;
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            while (sw.Elapsed.TotalSeconds < 180)
                            {
                                int k = Count(SafeGetPoints(inst, whole, probeSp, CallCap));
                                if (k >= CallCap * 0.98) { probeSp *= 2; prev = -1; stable = 0; continue; }
                                if (prev >= 0 && Math.Abs(k - prev) <= Math.Max(10, prev * 0.002)) { if (++stable >= 3) break; }
                                else stable = 0;
                                prev = k;
                                if (progress != null)
                                    progress(string.Format(L.T("Attendo che Revit carichi la nuvola... ({0:N0} punti di controllo, {1:F0} s)"), k, sw.Elapsed.TotalSeconds));
                                System.Threading.Thread.Sleep(800);
                            }
                        }

                        // ------------------------------------------------- da coordinate della nuvola a coordinate di modello
                        // GetPoints dà i punti «nelle coordinate della nuvola», che per i ReCap possono avere un'altra scala
                        // e un'altra origine rispetto al modello (il filtro invece è in coordinate di modello).
                        // Si ricava la corrispondenza confrontando l'ingombro dei punti con quello dell'elemento.
                        double sc = 1.0;
                        XYZ o = XYZ.Zero;
                        {
                            PointCollection big = null;
                            double sp = spacingFt * 20;
                            for (int tries = 0; tries < 12; tries++)
                            {
                                big = SafeGetPoints(inst, BoxFilter(bmin, bmax, filterLocal ? offset : XYZ.Zero), sp, CallCap);
                                if (Count(big) < CallCap * 0.98) break;
                                sp *= 2;
                            }
                            double[] vmn = { double.MaxValue, double.MaxValue, double.MaxValue }, vmx = { double.MinValue, double.MinValue, double.MinValue };
                            if (big != null)
                                foreach (CloudPoint cp in big)
                                {
                                    if (cp.X < vmn[0]) vmn[0] = cp.X; if (cp.X > vmx[0]) vmx[0] = cp.X;
                                    if (cp.Y < vmn[1]) vmn[1] = cp.Y; if (cp.Y > vmx[1]) vmx[1] = cp.Y;
                                    if (cp.Z < vmn[2]) vmn[2] = cp.Z; if (cp.Z > vmx[2]) vmx[2] = cp.Z;
                                }
                            double[] bm = { bmin.X, bmin.Y, bmin.Z }, bM = { bmax.X, bmax.Y, bmax.Z };
                            var ratios = new List<double>();
                            for (int k = 0; k < 3; k++)
                                if (vmx[k] - vmn[k] > 1e-6 && bM[k] - bm[k] > 1e-6) ratios.Add((bM[k] - bm[k]) / (vmx[k] - vmn[k]));
                            if (ratios.Count > 0)
                            {
                                ratios.Sort();
                                double r = ratios[ratios.Count / 2];
                                // valori tipici: stessa unità, metri, «due metri» (osservato con file ReCap)
                                bool snapped = false;
                                foreach (double nice in new[] { 1.0, FtPerM, 2 * FtPerM, 0.5 * FtPerM, 1 / FtPerM })
                                    if (Math.Abs(r / nice - 1) < 0.03) { r = nice; snapped = true; break; }
                                if (Math.Abs(r - 1) > 0.03) sc = r;
                                o = new XYZ((bm[0] + bM[0]) / 2 - sc * (vmn[0] + vmx[0]) / 2,
                                            (bm[1] + bM[1]) / 2 - sc * (vmn[1] + vmx[1]) / 2,
                                            (bm[2] + bM[2]) / 2 - sc * (vmn[2] + vmx[2]) / 2);
                                // con una scala «tonda» (cambio di unità) l'origine è la stessa: il piccolo scarto tra i centri
                                // viene solo dal campione di punti, che non arriva fino agli estremi dell'ingombro
                                double diag = (bmax - bmin).GetLength();
                                if (o.GetLength() < 0.066 || (snapped && o.GetLength() < diag * 0.02)) o = XYZ.Zero;
                                Log.Write("Estrazione", string.Format("rapporti ingombro modello/punti: {0}; scala usata {1:F5}; origine (ft) {2:F3}, {3:F3}, {4:F3}",
                                    string.Join(" ", ratios.Select(x => x.ToString("F5"))), sc, o.X, o.Y, o.Z));
                            }
                        }
                        // anche la distanza tra i punti chiesta a GetPoints è nelle coordinate della nuvola
                        double spacingCloud = spacingFt / sc;
                        XYZ toModel = XYZ.Zero;

                        writer.Notes = string.Format(
                            L.T("Offset tipo (ft): {0:F3}, {1:F3}, {2:F3}\nFiltro in coordinate {3}; punti in coordinate {4}.\n") +
                            L.T("Ingombro di modello (m): {5:F3}, {6:F3}, {7:F3}  ->  {8:F3}, {9:F3}, {10:F3}\nPasso richiesto: {11:F1} mm") +
                            string.Format(L.T("\nScala nuvola -> modello: {0:F5}"), sc) +
                            (toOriginal.IsZeroLength() ? "" : L.T("\nCoordinate originali = punti + offset del tipo.")),
                            offset.X, offset.Y, offset.Z, filterLocal ? L.T("locali") : L.T("di modello"), toModel.IsZeroLength() ? L.T("di modello") : L.T("locali"),
                            bmin.X / FtPerM, bmin.Y / FtPerM, bmin.Z / FtPerM, bmax.X / FtPerM, bmax.Y / FtPerM, bmax.Z / FtPerM, spacingM * 1000);

                        // ------------------------------------------------- estrazione a scatole che si suddividono
                        // Revit non sempre dà tutta la densità chiesta in una sola richiesta su una zona grande:
                        // si divide una scatola quando ha restituito il massimo, oppure quando le sue 8 metà insieme
                        // danno sensibilmente più punti di lei (allora si usano quelle).
                        bool anyColor = false;
                        XYZ shiftF = filterLocal ? offset : XYZ.Zero;
                        var stack = new Stack<ExtractBox>();
                        stack.Push(new ExtractBox { A = bmin, B = bmax, Depth = 0 });
                        int boxes = 0;
                        double totalVol = Volume(bmin, bmax), doneVol = 0;
                        double minBox = spacingFt * 64;

                        while (stack.Count > 0)
                        {
                            ExtractBox item = stack.Pop();
                            XYZ a = item.A, b = item.B;
                            PointCollection pts = item.Points ?? FetchStable(inst, BoxFilter(a, b, shiftF), spacingCloud);
                            int n = Count(pts);
                            double size = Math.Max(b.X - a.X, Math.Max(b.Y - a.Y, b.Z - a.Z));

                            ExtractBox[] kids = null;
                            if (item.Depth < 14 && n > 0 && size > minBox)
                            {
                                kids = Halves(a, b, item.Depth + 1);
                                if (n < CallCap * 0.98)
                                {
                                    if (n < 2000) kids = null;
                                    else
                                    {
                                        long sum = 0;
                                        foreach (ExtractBox k in kids)
                                        {
                                            k.Points = SafeGetPoints(inst, BoxFilter(k.A, k.B, shiftF), spacingCloud, CallCap);
                                            sum += Count(k.Points);
                                        }
                                        if (sum <= n * 1.08 + 100) kids = null; // nessun guadagno: vanno bene i punti della scatola intera
                                        else foreach (ExtractBox k in kids) if (Count(k.Points) > 200000) k.Points = null; // memoria: si rileggono dopo
                                    }
                                }
                            }
                            if (kids != null)
                            {
                                foreach (ExtractBox k in kids) stack.Push(k);
                                continue;
                            }

                            if (pts != null)
                            {
                                foreach (CloudPoint cp in pts)
                                {
                                    double x = sc * cp.X + o.X, y = sc * cp.Y + o.Y, z = sc * cp.Z + o.Z;
                                    // scatole semiaperte: un punto sul bordo comune non viene scritto due volte
                                    if (!HalfOpen(x, y, z, a, b, bmax)) continue;
                                    int c = cp.Color;
                                    byte r = (byte)(c & 0xFF), g = (byte)((c >> 8) & 0xFF), bl = (byte)((c >> 16) & 0xFF);
                                    if (c != 0) anyColor = true;
                                    writer.Add((x + toOriginal.X) / FtPerM, (y + toOriginal.Y) / FtPerM, (z + toOriginal.Z) / FtPerM, r, g, bl);
                                }
                            }

                            boxes++;
                            doneVol += Volume(a, b);
                            if (progress != null && (boxes & 7) == 1)
                                progress(string.Format(L.T("Lettura punti da Revit... {0:P0}  ({1:N0} punti)"), Math.Min(1.0, doneVol / totalVol), writer.Count));
                        }

                        writer.HasRgb = anyColor;
                        Log.Write("Estrazione", string.Format("{0}: {1:N0} punti in {2} zone, passo {3:F1} mm, offset applicato: {4}",
                            type.Name, writer.Count, boxes, spacingM * 1000, !toOriginal.IsZeroLength()));
                        return writer;
                    }
                    catch
                    {
                        writer.Dispose(); // chiude il file grezzo, così può essere cancellato
                        throw;
                    }
                }
                finally
                {
                    // nulla di quanto inserito resta nel progetto
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                }
            }
        }

        private sealed class ExtractBox
        {
            public XYZ A, B;
            public int Depth;
            public PointCollection Points;   // punti già letti (null = da leggere)
        }

        private static ExtractBox[] Halves(XYZ a, XYZ b, int depth)
        {
            XYZ m = (a + b) / 2.0;
            var res = new ExtractBox[8];
            for (int i = 0; i < 8; i++)
                res[i] = new ExtractBox
                {
                    A = new XYZ((i & 1) == 0 ? a.X : m.X, (i & 2) == 0 ? a.Y : m.Y, (i & 4) == 0 ? a.Z : m.Z),
                    B = new XYZ((i & 1) == 0 ? m.X : b.X, (i & 2) == 0 ? m.Y : b.Y, (i & 4) == 0 ? m.Z : b.Z),
                    Depth = depth
                };
            return res;
        }

        /// <summary>Punti di una zona; se una seconda richiesta ne dà di più, la zona si stava ancora caricando e si ripete.</summary>
        private static PointCollection FetchStable(PointCloudInstance inst, PointCloudFilter f, double spacingFt)
        {
            PointCollection pts = SafeGetPoints(inst, f, spacingFt, CallCap);
            int n = Count(pts);
            for (int retry = 0; retry < 8 && n < CallCap * 0.98; retry++)
            {
                System.Threading.Thread.Sleep(retry == 0 ? 50 : 400);
                PointCollection again = SafeGetPoints(inst, f, spacingFt, CallCap);
                int m = Count(again);
                if (m <= n * 1.005 + 5) break;
                pts = again; n = m;
            }
            return pts;
        }

        private static PointCloudFilter BoxFilter(XYZ min, XYZ max, XYZ shift)
        {
            XYZ a = min - shift, b = max - shift;
            var planes = new List<Plane>
            {
                Plane.CreateByNormalAndOrigin(XYZ.BasisX, a),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisX, b),
                Plane.CreateByNormalAndOrigin(XYZ.BasisY, a),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisY, b),
                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, a),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisZ, b)
            };
            return PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
        }

        private static PointCollection SafeGetPoints(PointCloudInstance inst, PointCloudFilter f, double spacingFt, int cap)
        {
            try { return inst.GetPoints(f, spacingFt, cap); }
            catch (Exception ex) { Log.Write("Estrazione", "GetPoints non riuscito: " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        private static int Count(PointCollection pc)
        {
            return pc == null ? 0 : pc.Count;
        }

        private static bool Inside(double x, double y, double z, XYZ min, XYZ max, XYZ shift)
        {
            const double tol = 0.5; // piedi
            return x >= min.X - shift.X - tol && x <= max.X - shift.X + tol
                && y >= min.Y - shift.Y - tol && y <= max.Y - shift.Y + tol
                && z >= min.Z - shift.Z - tol && z <= max.Z - shift.Z + tol;
        }

        private static bool HalfOpen(double x, double y, double z, XYZ a, XYZ b, XYZ globalMax)
        {
            const double e = 1e-9;
            bool okX = x >= a.X - e && (x < b.X || b.X >= globalMax.X - e);
            bool okY = y >= a.Y - e && (y < b.Y || b.Y >= globalMax.Y - e);
            bool okZ = z >= a.Z - e && (z < b.Z || b.Z >= globalMax.Z - e);
            return okX && okY && okZ;
        }

        private static double Volume(XYZ a, XYZ b)
        {
            return Math.Max(1e-9, (b.X - a.X) * (b.Y - a.Y) * (b.Z - a.Z));
        }

        /// <summary>Nuvole ReCap inserite nel progetto: selezionata, altrimenti tutte.</summary>
        public static List<PointCloudInstance> FindInstances(Autodesk.Revit.UI.UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            var sel = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id) as PointCloudInstance)
                .Where(e => e != null).ToList();
            if (sel.Count > 0) return sel;
            return new FilteredElementCollector(doc).OfClass(typeof(PointCloudInstance)).Cast<PointCloudInstance>().ToList();
        }

        // ===================================================================== posizionamento

        /// <summary>Posizionamento (vedi Placement) per la modalità scelta.</summary>
        public static double[] ComputePlacement(Document doc, CloudInfo info, PlacementMode mode, double[] last, out string note)
        {
            note = "";
            switch (mode)
            {
                case PlacementMode.OrigineInterna:
                    return Placement.Identity();

                case PlacementMode.Condivise:
                {
                    // p_interno = T · p_condiviso   (T = trasformazione totale della posizione di progetto attiva)
                    Transform tr = doc.ActiveProjectLocation.GetTotalTransform();
                    return new[]
                    {
                        tr.BasisX.X, tr.BasisY.X, tr.BasisZ.X,
                        tr.BasisX.Y, tr.BasisY.Y, tr.BasisZ.Y,
                        tr.BasisX.Z, tr.BasisY.Z, tr.BasisZ.Z,
                        tr.Origin.X, tr.Origin.Y, tr.Origin.Z
                    };
                }

                case PlacementMode.UltimaInserita:
                    if (Placement.IsValid(last)) return (double[])last.Clone();
                    note = L.T("Nessuna nuvola inserita prima in questo progetto: uso centro su centro.");
                    goto default;

                default:
                {
                    XYZ c = ModelCenter(doc);
                    double[] m = info.MidM;
                    return Placement.Translation(c.X - m[0] * FtPerM, c.Y - m[1] * FtPerM, c.Z - m[2] * FtPerM);
                }
            }
        }

        /// <summary>Centro dell'ingombro degli elementi di modello del progetto (origine interna se vuoto).</summary>
        private static XYZ ModelCenter(Document doc)
        {
            double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] mx = { double.MinValue, double.MinValue, double.MinValue };
            bool any = false;
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (e.Category == null || e.Category.CategoryType != CategoryType.Model) continue;
                if (e is PointCloudInstance || e is RevitLinkInstance || e is ImportInstance) continue;
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                any = true;
                mn[0] = Math.Min(mn[0], bb.Min.X); mn[1] = Math.Min(mn[1], bb.Min.Y); mn[2] = Math.Min(mn[2], bb.Min.Z);
                mx[0] = Math.Max(mx[0], bb.Max.X); mx[1] = Math.Max(mx[1], bb.Max.Y); mx[2] = Math.Max(mx[2], bb.Max.Z);
            }
            return any ? new XYZ((mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2) : XYZ.Zero;
        }
    }
}
