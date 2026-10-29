using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace PointDeviation
{
    /// <summary>
    /// Estrae i triangoli degli elementi di modello (coordinate Revit, piedi) e costruisce il BVH.
    /// Può includere i modelli collegati: la loro geometria viene portata nel sistema del modello ospite
    /// con la trasformazione totale dell'istanza di link.
    /// </summary>
    internal static class ModelMeshExtractor
    {
        public static TriangleBvh Build(Document doc, View view, ICollection<ElementId> selection,
            Outline limit, bool includeLinks,
            out int elementCount, out int triangleCount, out int linkCount)
        {
            elementCount = 0;
            triangleCount = 0;
            linkCount = 0;

            var opt = new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            var verts = new List<double>(1 << 20);
            int tris = 0;
            int elems = 0;

            // ------------------------------------------------------------ modello ospite
            IEnumerable<Element> source;
            if (selection != null)
            {
                var list = new List<Element>();
                foreach (ElementId id in selection)
                {
                    Element e = doc.GetElement(id);
                    if (e != null) list.Add(e);
                }
                source = list;
            }
            else
            {
                if (view == null || view is ViewSheet || view is ViewSchedule)
                    throw new InvalidOperationException(L.T("Attiva una vista 3D, una pianta o una sezione."));

                FilteredElementCollector col = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
                if (limit != null) col = col.WherePasses(new BoundingBoxIntersectsFilter(limit));
                source = col;
            }

            foreach (Element e in source)
            {
                int n = AddElement(e, opt, null, verts);
                if (n > 0) { tris += n; elems++; }
            }

            // ------------------------------------------------------------ modelli collegati
            // In modalità selezione si usano solo i link selezionati; altrimenti tutti, se richiesto.
            if (includeLinks || selection != null)
            {
                foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                {
                    var li = el as RevitLinkInstance;
                    if (li == null) continue;

                    bool wanted = selection != null ? selection.Contains(li.Id) : includeLinks;
                    if (!wanted) continue;

                    Document linkDoc = li.GetLinkDocument();
                    if (linkDoc == null) continue; // link non caricato

                    Transform xf = li.GetTotalTransform();

                    FilteredElementCollector lc = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType();
                    if (limit != null) lc = lc.WherePasses(new BoundingBoxIntersectsFilter(ToLinkOutline(limit, xf)));

                    int before = tris;
                    foreach (Element le in lc)
                    {
                        int n = AddElement(le, opt, xf, verts);
                        if (n > 0) { tris += n; elems++; }
                    }
                    if (tris > before) linkCount++;
                }
            }

            elementCount = elems;
            triangleCount = tris;
            if (tris == 0) return null;
            return new TriangleBvh(verts.ToArray(), tris);
        }

        /// <summary>Bounding box del limite espresso nel sistema di coordinate del link (assi allineati al link).</summary>
        private static Outline ToLinkOutline(Outline o, Transform xf)
        {
            Transform inv = xf.Inverse;
            XYZ a = o.MinimumPoint, b = o.MaximumPoint;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                XYZ p = inv.OfPoint(new XYZ(
                    (i & 1) == 0 ? a.X : b.X,
                    (i & 2) == 0 ? a.Y : b.Y,
                    (i & 4) == 0 ? a.Z : b.Z));
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }
            return new Outline(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        }

        private static int AddElement(Element e, Options opt, Transform xf, List<double> verts)
        {
            if (e.Category == null || e.Category.CategoryType != CategoryType.Model) return 0;
            if (e is RevitLinkInstance || e is PointCloudInstance) return 0;

            GeometryElement ge = null;
            try { ge = e.get_Geometry(opt); } catch { }
            if (ge == null) return 0;

            return Collect(ge, verts, xf);
        }

        /// <summary>Normale esterna di una faccia curva nel baricentro del triangolo, null se non calcolabile.</summary>
        private static XYZ CurvedFaceNormal(Face face, XYZ a, XYZ b, XYZ c)
        {
            try
            {
                XYZ centroid = (a + b + c) / 3.0;
                IntersectionResult ir = face.Project(centroid);
                return ir != null ? face.ComputeNormal(ir.UVPoint) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Controllo finale del verso dei triangoli di un solido: con le normali verso l'esterno il volume
        /// con segno (somma dei tetraedri dall'origine) è positivo. Se è negativo, le normali di Revit
        /// erano rivolte verso l'interno e si girano tutti i triangoli del solido. Così il segno
        /// della distanza non dipende dalla convenzione delle normali delle facce.
        /// </summary>
        private static void OrientOutward(List<double> v, int start)
        {
            int end = v.Count;
            if (end - start < 9 * 4) return; // meno di 4 triangoli: non è un solido chiuso

            // origine locale vicina al solido, per non perdere precisione con coordinate grandi
            double ox = v[start], oy = v[start + 1], oz = v[start + 2];
            double vol = 0.0;
            for (int o = start; o < end; o += 9)
            {
                double ax = v[o] - ox, ay = v[o + 1] - oy, az = v[o + 2] - oz;
                double bx = v[o + 3] - ox, by = v[o + 4] - oy, bz = v[o + 5] - oz;
                double cx = v[o + 6] - ox, cy = v[o + 7] - oy, cz = v[o + 8] - oz;
                vol += ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx);
            }
            if (vol >= 0.0) return;

            for (int o = start; o < end; o += 9)
            {
                for (int k = 0; k < 3; k++)
                {
                    double tmp = v[o + 3 + k];
                    v[o + 3 + k] = v[o + 6 + k];
                    v[o + 6 + k] = tmp;
                }
            }
        }

        private static int Collect(GeometryElement ge, List<double> verts, Transform xf)
        {
            int count = 0;
            foreach (GeometryObject go in ge)
            {
                var solid = go as Solid;
                if (solid != null)
                {
                    if (solid.Faces.Size == 0) continue;
                    int solidStart = verts.Count;
                    foreach (Face face in solid.Faces)
                    {
                        Mesh mesh = null;
                        try { mesh = face.Triangulate(); } catch { }
                        if (mesh == null) continue;

                        // Il segno della distanza viene dalla normale del triangolo, ma Face.Triangulate()
                        // non garantisce che l'ordine dei vertici segua la normale esterna della faccia
                        // (dipende da OrientationMatchesSurfaceOrientation). Ogni triangolo viene quindi
                        // riorientato sulla normale esterna: positivo = fuori dal solido, davanti alla superficie.
                        var planar = face as PlanarFace;
                        XYZ planarNormal = null;
                        if (planar != null)
                        {
                            try { planarNormal = planar.FaceNormal; } catch { }
                        }

                        for (int i = 0; i < mesh.NumTriangles; i++)
                        {
                            MeshTriangle t = mesh.get_Triangle(i);
                            XYZ a = t.get_Vertex(0), b = t.get_Vertex(1), c = t.get_Vertex(2);

                            XYZ outward = planarNormal ?? CurvedFaceNormal(face, a, b, c);

                            if (xf != null)
                            {
                                a = xf.OfPoint(a); b = xf.OfPoint(b); c = xf.OfPoint(c);
                                if (outward != null) outward = xf.OfVector(outward);
                            }

                            if (outward != null && (b - a).CrossProduct(c - a).DotProduct(outward) < 0.0)
                            {
                                XYZ tmp = b; b = c; c = tmp;
                            }

                            verts.Add(a.X); verts.Add(a.Y); verts.Add(a.Z);
                            verts.Add(b.X); verts.Add(b.Y); verts.Add(b.Z);
                            verts.Add(c.X); verts.Add(c.Y); verts.Add(c.Z);
                            count++;
                        }
                    }
                    OrientOutward(verts, solidStart);
                    continue;
                }

                var inst = go as GeometryInstance;
                if (inst != null)
                {
                    // già trasformata nelle coordinate del documento (progetto o link)
                    GeometryElement sub = null;
                    try { sub = inst.GetInstanceGeometry(); } catch { }
                    if (sub != null) count += Collect(sub, verts, xf);
                    continue;
                }

                var nested = go as GeometryElement;
                if (nested != null) count += Collect(nested, verts, xf);
            }
            return count;
        }
    }
}
