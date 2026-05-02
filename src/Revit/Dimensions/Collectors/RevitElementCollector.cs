using Autodesk.Revit.DB;
using EllahColNum.Core.Dimensions.Models;

namespace EllahColNum.Revit.Dimensions.Collectors;

/// <summary>
/// Collects every element type that Pro Dimensions can reference.
///
/// Element types covered:
///   Grid          — new Reference(grid)
///   Structural columns  — OST_StructuralColumns, FamilyInstance (LocationPoint) or
///                         concrete columns with LocationCurve (base point used)
///   Architectural columns — OST_Columns (also FamilyInstance)
///   Structural framing / beams — OST_StructuralFraming (FamilyInstance with LocationCurve)
///                         Both endpoints stored as Beam elements so the engine can
///                         detect span directions.
///   Walls         — Wall (LocationCurve), midpoint; face refs extracted per-view
///   Doors         — OST_Doors, FamilyInstance
///   Windows       — OST_Windows, FamilyInstance
///
/// Reference strategy:
///   Point-based FamilyInstances (columns, doors, windows) → new Reference(inst).
///   Grids → new Reference(grid).
///   Beams (LocationCurve FamilyInstances) → new Reference(inst) (symbolic midpoint).
///   Walls → geometry face traversal (done fresh per-view in the helper).
/// </summary>
public class RevitElementCollector
{
    private readonly Document _doc;

    /// <summary>ElementId.Value → list of Revit Reference objects for that element.</summary>
    public Dictionary<long, List<Reference>> RefMap { get; } = new();

    public RevitElementCollector(Document doc) => _doc = doc;

    // ── Public entry point ────────────────────────────────────────────────────

    public List<ElementRefData> CollectAll()
    {
        var result = new List<ElementRefData>();
        result.AddRange(CollectGrids());
        result.AddRange(CollectColumns());   // structural + architectural
        result.AddRange(CollectBeams());     // structural framing
        result.AddRange(CollectWalls());
        result.AddRange(CollectOpenings());
        return result;
    }

    // ── Grid Lines ────────────────────────────────────────────────────────────

    public List<ElementRefData> CollectGrids()
    {
        var result = new List<ElementRefData>();

        foreach (var grid in new FilteredElementCollector(_doc)
            .OfClass(typeof(Grid))
            .Cast<Grid>())
        {
            if (grid.Curve is not Line line) continue;

            var p0  = line.GetEndPoint(0);
            var p1  = line.GetEndPoint(1);
            var mid = (p0 + p1) / 2.0;
            var dir = (p1 - p0).Normalize();

            result.Add(new ElementRefData
            {
                ElementId = grid.Id.Value,
                Name      = grid.Name,
                Category  = ElementCategory.Grid,
                X         = mid.X,
                Y         = mid.Y,
                Angle     = Math.Atan2(dir.Y, dir.X),
            });

            RefMap[grid.Id.Value] = new List<Reference> { new Reference(grid) };
        }

        return result;
    }

    // ── Structural + Architectural Columns ────────────────────────────────────

    public List<ElementRefData> CollectColumns()
    {
        var result = new List<ElementRefData>();

        // Structural columns (steel, concrete, composite)
        AddColumnInstances(result,
            BuiltInCategory.OST_StructuralColumns,
            ElementCategory.Column);

        // Architectural columns (appear in arch plans)
        AddColumnInstances(result,
            BuiltInCategory.OST_Columns,
            ElementCategory.Column);

        return result;
    }

    private void AddColumnInstances(
        List<ElementRefData>  result,
        BuiltInCategory       bic,
        ElementCategory       cat)
    {
        foreach (var inst in new FilteredElementCollector(_doc)
            .OfCategory(bic)
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>())
        {
            XYZ? pt = inst.Location switch
            {
                LocationPoint lp => lp.Point,
                LocationCurve lc => lc.Curve.Evaluate(0.0, normalized: true), // base of column
                _                => null,
            };
            if (pt == null) continue;

            result.Add(new ElementRefData
            {
                ElementId = inst.Id.Value,
                Name      = inst.Symbol?.Name ?? inst.Id.ToString(),
                Category  = cat,
                X         = pt.X,
                Y         = pt.Y,
            });

            RefMap[inst.Id.Value] = new List<Reference> { new Reference(inst) };
        }
    }

    // ── Structural Framing / Beams ────────────────────────────────────────────

    /// <summary>
    /// Collects beams (OST_StructuralFraming).  Each beam contributes its midpoint
    /// as a Column-category element so it participates in column-grid dimension strings.
    /// Diagonal beams store their angle so the engine can detect non-orthogonal axes.
    /// </summary>
    public List<ElementRefData> CollectBeams()
    {
        var result = new List<ElementRefData>();

        foreach (var inst in new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>())
        {
            if (inst.Location is not LocationCurve lc) continue;
            if (lc.Curve is not Line beamLine) continue;  // skip curved beams

            var p0  = beamLine.GetEndPoint(0);
            var p1  = beamLine.GetEndPoint(1);
            var mid = (p0 + p1) / 2.0;
            var dir = (p1 - p0).Normalize();

            result.Add(new ElementRefData
            {
                ElementId = inst.Id.Value,
                Name      = inst.Symbol?.Name ?? inst.Id.ToString(),
                Category  = ElementCategory.Beam,
                X         = mid.X,
                Y         = mid.Y,
                Angle     = Math.Atan2(dir.Y, dir.X),
            });

            RefMap[inst.Id.Value] = new List<Reference> { new Reference(inst) };
        }

        return result;
    }

    // ── Walls ─────────────────────────────────────────────────────────────────

    public List<ElementRefData> CollectWalls()
    {
        var result = new List<ElementRefData>();

        foreach (var wall in new FilteredElementCollector(_doc)
            .OfClass(typeof(Wall))
            .Cast<Wall>()
            .Where(w => w.Location is LocationCurve))
        {
            var lc  = (LocationCurve)wall.Location;
            var mid = lc.Curve.Evaluate(0.5, normalized: true);
            var dir = GetCurveDirection(lc.Curve);

            result.Add(new ElementRefData
            {
                ElementId = wall.Id.Value,
                Name      = wall.WallType?.Name ?? wall.Id.ToString(),
                Category  = ElementCategory.Wall,
                X         = mid.X,
                Y         = mid.Y,
                Angle     = Math.Atan2(dir.Y, dir.X),
            });

            // Wall face refs are extracted fresh per-view inside ProDimensionHelper,
            // because Options.View is required for valid dimensionable references.
            RefMap[wall.Id.Value] = new List<Reference>();
        }

        return result;
    }

    // ── Doors and Windows ─────────────────────────────────────────────────────

    public List<ElementRefData> CollectOpenings()
    {
        var result = new List<ElementRefData>();

        foreach (var bic in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows })
        {
            foreach (var inst in new FilteredElementCollector(_doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>())
            {
                if (inst.Location is not LocationPoint lp) continue;
                var pt = lp.Point;

                result.Add(new ElementRefData
                {
                    ElementId = inst.Id.Value,
                    Name      = inst.Symbol?.Name ?? inst.Id.ToString(),
                    Category  = ElementCategory.Opening,
                    X         = pt.X,
                    Y         = pt.Y,
                });

                RefMap[inst.Id.Value] = new List<Reference> { new Reference(inst) };
            }
        }

        return result;
    }

    // ── Plan views + dimension type readers ───────────────────────────────────

    public Dictionary<string, List<(long Id, string Name)>> ReadPlanViewsByDiscipline()
    {
        var all = new List<(long Id, string Name, string Discipline)>();

        foreach (var view in new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate))
        {
            try { all.Add((view.Id.Value, view.Name, DisciplineName(view))); }
            catch { }
        }

        var result = new Dictionary<string, List<(long, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["All"] = all.OrderBy(v => v.Name).Select(v => (v.Id, v.Name)).ToList()
        };

        foreach (var grp in all.GroupBy(v => v.Discipline))
            result[grp.Key] = grp.OrderBy(v => v.Name).Select(v => (v.Id, v.Name)).ToList();

        return result;
    }

    public List<(long Id, string Name)> ReadDimensionTypes()
    {
        return new FilteredElementCollector(_doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .OrderBy(dt => dt.Name)
            .Select(dt => (dt.Id.Value, dt.Name))
            .ToList();
    }

    // ── Small helpers ─────────────────────────────────────────────────────────

    private static XYZ GetCurveDirection(Curve curve)
    {
        var p0 = curve.GetEndPoint(0);
        var p1 = curve.GetEndPoint(1);
        var d  = p1 - p0;
        return d.GetLength() > 1e-6 ? d.Normalize() : XYZ.BasisX;
    }

    private static string DisciplineName(View view)
    {
        try
        {
            return view.Discipline switch
            {
                ViewDiscipline.Structural    => "Structural",
                ViewDiscipline.Architectural => "Architectural",
                ViewDiscipline.Mechanical    => "Mechanical",
                ViewDiscipline.Electrical    => "Electrical",
                ViewDiscipline.Plumbing      => "Plumbing",
                ViewDiscipline.Coordination  => "Coordination",
                _                            => "Other",
            };
        }
        catch { return "Other"; }
    }
}
