using Autodesk.Revit.DB;
using EllahColNum.Core.Dimensions.Models;

namespace EllahColNum.Revit.Dimensions.Helpers;

/// <summary>
/// Creates multi-layer, multi-string dimension chains for Pro Dimensions (Phase 2).
/// Must be called inside an open Transaction.
///
/// ── Key design decisions ────────────────────────────────────────────────────
///
/// 1. PER-ROW / PER-COLUMN grouping (not just 4 exterior strings)
///    Elements are grouped by their position perpendicular to the dimension axis.
///    Each group produces ONE dimension string.  A 4×3 column grid produces
///    4 horizontal strings + 3 vertical strings + 2 exterior total strings = 9,
///    instead of the old 2.
///
/// 2. BUILDING-AXIS DETECTION
///    Grid line angles define the building's primary axes.  For a rotated building
///    all dimension lines are also rotated so they align with the grid.
///
/// 3. COORDINATE PROJECTION
///    All element positions are projected onto the two building axes (P1, P2).
///    P1 is the coordinate along axis 1 (dominant grid direction).
///    P2 is along axis 2 (perpendicular).  For orthogonal buildings P1≈X, P2≈Y.
///
/// 4. REFERENCE TYPES
///    • Grid      → new Reference(grid) from pre-built map
///    • Column/Beam/Opening → new Reference(inst) collected per-view
///    • Wall      → face.Reference from Options { View = view } ← REQUIRED
///
/// 5. PER-VIEW ELEMENT COLLECTION
///    FilteredElementCollector(doc, view.Id) ensures only visible elements.
///
/// 6. DIMENSIONTYPE FALLBACK
///    If a user-selected type is rejected, we retry with null (project default).
/// </summary>
public class ProDimensionHelper
{
    private readonly Document _doc;

    // Elements within this many feet perpendicular to the dimension axis
    // are considered to be in the same row/column.
    private const double RowToleranceFt = 2.0;

    public ProDimensionHelper(Document doc) => _doc = doc;

    // ── Public API ────────────────────────────────────────────────────────────

    public int CreateDimensions(
        ProDimensionPlan                  plan,
        Dictionary<long, List<Reference>> gridRefMap,
        List<ViewPlan>                    views,
        BoundingBoxXYZ                    buildingBounds)
    {
        int created = 0;
        var typeCache = BuildTypeCache(plan);

        // ── Detect building primary axes from grid lines ───────────────────
        double axis1  = DetectPrimaryAxis(plan);   // angle in radians
        double axis2  = axis1 + Math.PI / 2.0;
        var    u1     = UnitVec(axis1);            // along dominant grid direction
        var    u2     = UnitVec(axis2);            // perpendicular

        foreach (var view in views)
        {
            // ── Collect raw (world X,Y) PosRefs per category ───────────────
            var gridRaw    = plan.Grids.Elements.Count    >= 2 ? GetGridPosRefs(plan, gridRefMap) : null;
            var colRaw     = plan.Columns.Elements.Count  >= 2 ? GetColumnPosRefsForView(view)    : null;
            var wallRaw    = plan.Walls.Elements.Count    >= 2 ? GetWallPosRefsForView(view)      : null;
            var openRaw    = plan.Openings.Elements.Count >= 2 ? GetOpeningPosRefsForView(view)   : null;

            // ── Project onto building axes (P1 = along u1, P2 = along u2) ─
            var gridPRs = gridRaw?   .Select(r => Project(r, u1, u2)).ToList();
            var colPRs  = colRaw?    .Select(r => Project(r, u1, u2)).ToList();
            var wallPRs = wallRaw?   .Select(r => Project(r, u1, u2)).ToList();
            var openPRs = openRaw?   .Select(r => Project(r, u1, u2)).ToList();

            // ── Grid extents in projected space ────────────────────────────
            (double minP1, double maxP1, double minP2, double maxP2) = ComputeExtents(
                gridPRs ?? colPRs ?? new List<PosRef>(), buildingBounds, u1, u2);

            // ── Create strings for each enabled layer ──────────────────────
            var layers = new (ElementCategory cat, List<PosRef>? prs, double offset)[]
            {
                (ElementCategory.Grid,    gridPRs, plan.Grids.OffsetFeet),
                (ElementCategory.Column,  colPRs,  plan.Columns.OffsetFeet),
                (ElementCategory.Wall,    wallPRs, plan.Walls.OffsetFeet),
                (ElementCategory.Opening, openPRs, plan.Openings.OffsetFeet),
            };

            foreach (var (cat, prs, offset) in layers)
            {
                if (prs == null || prs.Count < 2) continue;
                var dimType = typeCache.GetValueOrDefault(cat);

                // ── Per-row strings (measuring spacing along u1) ───────────
                // Group elements by P2 (position along axis 2).
                foreach (var row in GroupByPosition(prs, r => r.P2))
                {
                    var sorted = row.OrderBy(r => r.P1).ToList();
                    if (sorted.Count < 2) continue;

                    double rowP2  = row.Average(r => r.P2);
                    double lineP2 = rowP2 + offset;
                    var    ra     = ToRA(sorted);
                    var    line   = LineAlongU1(sorted.First().P1 - 1, sorted.Last().P1 + 1, lineP2, u1, u2);
                    if (TryDim(view, line, ra, dimType)) created++;
                }

                // One full exterior string ABOVE all elements (along u1 direction)
                created += ExteriorString1(view, prs, maxP2 + offset, u1, u2, dimType);
                // Mirror below
                created += ExteriorString1(view, prs, minP2 - offset, u1, u2, dimType);

                // ── Per-column strings (measuring spacing along u2) ─────────
                // Group elements by P1 (position along axis 1).
                foreach (var col in GroupByPosition(prs, r => r.P1))
                {
                    var sorted = col.OrderBy(r => r.P2).ToList();
                    if (sorted.Count < 2) continue;

                    double colP1  = col.Average(r => r.P1);
                    double lineP1 = colP1 - offset;
                    var    ra     = ToRA(sorted);
                    var    line   = LineAlongU2(lineP1, sorted.First().P2 - 1, sorted.Last().P2 + 1, u1, u2);
                    if (TryDim(view, line, ra, dimType)) created++;
                }

                // One full exterior string LEFT of all elements (along u2 direction)
                created += ExteriorString2(view, prs, minP1 - offset, u1, u2, dimType);
                // Mirror right
                created += ExteriorString2(view, prs, maxP1 + offset, u1, u2, dimType);
            }
        }

        return created;
    }

    // ── Exterior summary strings ──────────────────────────────────────────────

    private int ExteriorString1(ViewPlan view, List<PosRef> prs, double fixedP2,
        XYZ u1, XYZ u2, DimensionType? dt)
    {
        var unique = prs.GroupBy(r => Math.Round(r.P1, 1)).Select(g => g.First())
                        .OrderBy(r => r.P1).ToList();
        if (unique.Count < 2) return 0;
        var ra   = ToRA(unique);
        var line = LineAlongU1(unique.First().P1 - 1, unique.Last().P1 + 1, fixedP2, u1, u2);
        return TryDim(view, line, ra, dt) ? 1 : 0;
    }

    private int ExteriorString2(ViewPlan view, List<PosRef> prs, double fixedP1,
        XYZ u1, XYZ u2, DimensionType? dt)
    {
        var unique = prs.GroupBy(r => Math.Round(r.P2, 1)).Select(g => g.First())
                        .OrderBy(r => r.P2).ToList();
        if (unique.Count < 2) return 0;
        var ra   = ToRA(unique);
        var line = LineAlongU2(fixedP1, unique.First().P2 - 1, unique.Last().P2 + 1, u1, u2);
        return TryDim(view, line, ra, dt) ? 1 : 0;
    }

    // ── Building-axis detection ───────────────────────────────────────────────

    private double DetectPrimaryAxis(ProDimensionPlan plan)
    {
        if (plan.Grids.Elements.Count == 0) return 0.0;

        var angles = new List<double>();
        foreach (var elem in plan.Grids.Elements)
        {
            if (_doc.GetElement(new ElementId(elem.ElementId)) is not Grid grid) continue;
            if (grid.Curve is not Line line) continue;
            var dir = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
            // Normalise to [0, π/2]: we don't distinguish positive/negative direction
            double a = Math.Atan2(Math.Abs(dir.Y), Math.Abs(dir.X));
            angles.Add(a);
        }

        if (angles.Count == 0) return 0.0;

        // Majority vote within a 5° window
        const double tol = 5 * Math.PI / 180.0;
        double best = angles[0]; int bestN = 0;
        foreach (var c in angles)
        {
            int n = angles.Count(a => Math.Abs(a - c) < tol);
            if (n > bestN) { bestN = n; best = c; }
        }
        return best;
    }

    // ── Per-view collectors ───────────────────────────────────────────────────

    /// <summary>Returns raw (world X, Y) PosRefs for all grids in the plan.</summary>
    private static List<PosRef> GetGridPosRefs(
        ProDimensionPlan                  plan,
        Dictionary<long, List<Reference>> gridRefMap)
    {
        var result = new List<PosRef>();
        foreach (var elem in plan.Grids.Elements)
        {
            if (!gridRefMap.TryGetValue(elem.ElementId, out var refs) || refs.Count == 0) continue;
            result.Add(new PosRef(elem.X, elem.Y, refs[0]));
        }
        return result;
    }

    private List<PosRef> GetColumnPosRefsForView(ViewPlan view)
    {
        var prs = new List<PosRef>();

        // Structural columns (steel, concrete — any FamilyInstance in this category)
        foreach (var bic in new[] { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns })
        {
            foreach (var inst in new FilteredElementCollector(_doc, view.Id)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>())
            {
                XYZ? pt = inst.Location switch
                {
                    LocationPoint lp => lp.Point,
                    LocationCurve lc => lc.Curve.Evaluate(0.0, normalized: true),
                    _                => null,
                };
                if (pt == null) continue;
                try { prs.Add(new PosRef(pt.X, pt.Y, new Reference(inst))); } catch { }
            }
        }

        // Structural framing (beams) — midpoints contribute to the column grid
        foreach (var inst in new FilteredElementCollector(_doc, view.Id)
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>())
        {
            if (inst.Location is not LocationCurve lc) continue;
            var mid = lc.Curve.Evaluate(0.5, normalized: true);
            try { prs.Add(new PosRef(mid.X, mid.Y, new Reference(inst))); } catch { }
        }

        return prs;
    }

    private List<PosRef> GetOpeningPosRefsForView(ViewPlan view)
    {
        var prs = new List<PosRef>();
        foreach (var bic in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows })
        {
            foreach (var inst in new FilteredElementCollector(_doc, view.Id)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>())
            {
                if (inst.Location is not LocationPoint lp) continue;
                var pt = lp.Point;
                try { prs.Add(new PosRef(pt.X, pt.Y, new Reference(inst))); } catch { }
            }
        }
        return prs;
    }

    private List<PosRef> GetWallPosRefsForView(ViewPlan view)
    {
        var opts = new Options
        {
            ComputeReferences        = true,
            IncludeNonVisibleObjects = false,
            View                     = view,   // CRITICAL — reference must be view-bound
        };

        var prs = new List<PosRef>();

        foreach (var wall in new FilteredElementCollector(_doc, view.Id)
            .OfClass(typeof(Wall))
            .Cast<Wall>()
            .Where(w => w.Location is LocationCurve lc && lc.Curve is Line))
        {
            var lc  = (LocationCurve)wall.Location;
            var ln  = (Line)lc.Curve;
            var dir = (ln.GetEndPoint(1) - ln.GetEndPoint(0)).Normalize();
            var mid = ln.Evaluate(0.5, normalized: true);

            try
            {
                Reference? best = null;
                foreach (var obj in wall.get_Geometry(opts))
                {
                    if (obj is not Solid solid || solid.Volume < 1e-9) continue;
                    foreach (Face face in solid.Faces)
                    {
                        if (face.Reference == null) continue;
                        var n   = face.ComputeNormal(new UV(0.5, 0.5));
                        double dot = Math.Abs(n.X * dir.X + n.Y * dir.Y);
                        if (dot < 0.2) { best = face.Reference; break; }
                    }
                    if (best != null) break;
                }
                if (best != null)
                    prs.Add(new PosRef(mid.X, mid.Y, best));
            }
            catch { }
        }

        return prs;
    }

    // ── Grouping ──────────────────────────────────────────────────────────────

    private static List<List<PosRef>> GroupByPosition(IEnumerable<PosRef> items, Func<PosRef, double> key)
    {
        var sorted  = items.OrderBy(key).ToList();
        var groups  = new List<List<PosRef>>();
        if (sorted.Count == 0) return groups;

        var cur      = new List<PosRef> { sorted[0] };
        double ctr   = key(sorted[0]);

        for (int i = 1; i < sorted.Count; i++)
        {
            double k = key(sorted[i]);
            if (Math.Abs(k - ctr) <= RowToleranceFt)
            {
                cur.Add(sorted[i]);
                ctr = cur.Average(key);
            }
            else
            {
                groups.Add(cur);
                cur = new List<PosRef> { sorted[i] };
                ctr = k;
            }
        }
        groups.Add(cur);
        return groups;
    }

    // ── Coordinate helpers ────────────────────────────────────────────────────

    private static PosRef Project(PosRef raw, XYZ u1, XYZ u2)
    {
        var pt = new XYZ(raw.P1, raw.P2, 0);
        return new PosRef(Dot(pt, u1), Dot(pt, u2), raw.Ref);
    }

    private static double Dot(XYZ a, XYZ b) => a.X * b.X + a.Y * b.Y;

    private static XYZ UnitVec(double angleRad) =>
        new XYZ(Math.Cos(angleRad), Math.Sin(angleRad), 0);

    /// <summary>Line parallel to u1, at fixed P2 position, spanning P1 start→end.</summary>
    private static Line LineAlongU1(double p1Start, double p1End, double fixedP2, XYZ u1, XYZ u2)
    {
        var s = u1 * p1Start + u2 * fixedP2;
        var e = u1 * p1End   + u2 * fixedP2;
        if ((e - s).GetLength() < 0.01) e = s + u1 * 0.1;
        return Line.CreateBound(s, e);
    }

    /// <summary>Line parallel to u2, at fixed P1 position, spanning P2 start→end.</summary>
    private static Line LineAlongU2(double fixedP1, double p2Start, double p2End, XYZ u1, XYZ u2)
    {
        var s = u1 * fixedP1 + u2 * p2Start;
        var e = u1 * fixedP1 + u2 * p2End;
        if ((e - s).GetLength() < 0.01) e = s + u2 * 0.1;
        return Line.CreateBound(s, e);
    }

    private static ReferenceArray ToRA(IEnumerable<PosRef> items)
    {
        var ra = new ReferenceArray();
        foreach (var r in items) ra.Append(r.Ref);
        return ra;
    }

    private static (double, double, double, double) ComputeExtents(
        List<PosRef> prs, BoundingBoxXYZ bb, XYZ u1, XYZ u2)
    {
        if (prs.Count == 0)
        {
            double p1min = Dot(bb.Min, u1), p1max = Dot(bb.Max, u1);
            double p2min = Dot(bb.Min, u2), p2max = Dot(bb.Max, u2);
            return (p1min, p1max, p2min, p2max);
        }
        return (prs.Min(r => r.P1), prs.Max(r => r.P1),
                prs.Min(r => r.P2), prs.Max(r => r.P2));
    }

    // ── DimensionType cache ───────────────────────────────────────────────────

    private Dictionary<ElementCategory, DimensionType?> BuildTypeCache(ProDimensionPlan plan)
    {
        var allTypes = new FilteredElementCollector(_doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .GroupBy(dt => dt.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        DimensionType? Resolve(string name) =>
            string.IsNullOrWhiteSpace(name) ? null : allTypes.GetValueOrDefault(name);

        return new Dictionary<ElementCategory, DimensionType?>
        {
            [ElementCategory.Grid]    = Resolve(plan.Grids.DimTypeName),
            [ElementCategory.Column]  = Resolve(plan.Columns.DimTypeName),
            [ElementCategory.Wall]    = Resolve(plan.Walls.DimTypeName),
            [ElementCategory.Opening] = Resolve(plan.Openings.DimTypeName),
        };
    }

    // ── Revit API wrapper ─────────────────────────────────────────────────────

    private bool TryDim(ViewPlan view, Line line, ReferenceArray ra, DimensionType? dt)
    {
        if (line.Length < 0.01 || ra.Size < 2) return false;
        try
        {
            return (dt != null
                ? _doc.Create.NewDimension(view, line, ra, dt)
                : _doc.Create.NewDimension(view, line, ra)) != null;
        }
        catch
        {
            if (dt == null) return false;
            try { return _doc.Create.NewDimension(view, line, ra) != null; }
            catch { return false; }
        }
    }

    // ── Building bounds ───────────────────────────────────────────────────────

    public static BoundingBoxXYZ ComputeBuildingBounds(List<ElementRefData> elements)
    {
        if (elements.Count == 0)
        {
            var z = new BoundingBoxXYZ(); z.Min = new XYZ(-1,-1,0); z.Max = new XYZ(1,1,0); return z;
        }
        var bb = new BoundingBoxXYZ();
        bb.Min = new XYZ(elements.Min(e => e.X), elements.Min(e => e.Y), 0);
        bb.Max = new XYZ(elements.Max(e => e.X), elements.Max(e => e.Y), 0);
        return bb;
    }

    // ── Internal record ───────────────────────────────────────────────────────

    /// <summary>
    /// Position along the two building axes (P1 = along axis 1, P2 = along axis 2)
    /// plus the Revit Reference for this element.
    /// Before projection: P1 = world X, P2 = world Y.
    /// After projection: P1 and P2 are rotated-axis coordinates.
    /// </summary>
    private record PosRef(double P1, double P2, Reference Ref);
}
