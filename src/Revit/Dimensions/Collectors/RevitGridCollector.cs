using Autodesk.Revit.DB;
using EllahColNum.Core.Dimensions.Models;

namespace EllahColNum.Revit.Dimensions.Collectors;

/// <summary>
/// Reads grid lines, plan views, and dimension types from the Revit document
/// for use by the Smart Grid Dimensions command.
/// </summary>
public class RevitGridCollector
{
    private readonly Document _doc;

    public RevitGridCollector(Document doc) => _doc = doc;

    // ── Grids ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all straight (linear) Grid elements as GridLineData objects,
    /// plus a fast lookup map from ElementId.Value → Grid.
    /// Arc/spline grids are intentionally skipped — NewDimension only works with straight references.
    /// </summary>
    public (List<GridLineData> grids, Dictionary<long, Grid> gridMap) ReadAllGrids()
    {
        var grids   = new List<GridLineData>();
        var gridMap = new Dictionary<long, Grid>();

        var elements = new FilteredElementCollector(_doc)
            .OfClass(typeof(Grid))
            .Cast<Grid>();

        foreach (var grid in elements)
        {
            if (grid.Curve is not Line line) continue;

            var p0    = line.GetEndPoint(0);
            var p1    = line.GetEndPoint(1);
            var delta = p1 - p0;

            bool   isVertical = Math.Abs(delta.Y) >= Math.Abs(delta.X);
            double position   = isVertical
                ? (p0.X + p1.X) / 2.0
                : (p0.Y + p1.Y) / 2.0;

            grids.Add(new GridLineData
            {
                ElementId  = grid.Id.Value,
                Name       = grid.Name,
                IsVertical = isVertical,
                Position   = position,
            });

            gridMap[grid.Id.Value] = grid;
        }

        return (grids, gridMap);
    }

    // ── Plan views ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all non-template ViewPlan elements, keyed by discipline.
    /// Always includes an "All" key containing every view.
    /// Each value is a list of (ElementId.Value, Name) pairs, sorted by name.
    /// </summary>
    public Dictionary<string, List<(long Id, string Name)>> ReadPlanViewsByDiscipline()
    {
        var all = new List<(long Id, string Name, string Discipline)>();

        foreach (var view in new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate))
        {
            try { all.Add((view.Id.Value, view.Name, DisciplineName(view))); }
            catch { /* skip views that throw on property access */ }
        }

        var result = new Dictionary<string, List<(long, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["All"] = all.OrderBy(v => v.Name).Select(v => (v.Id, v.Name)).ToList()
        };

        foreach (var grp in all.GroupBy(v => v.Discipline))
        {
            result[grp.Key] = grp
                .OrderBy(v => v.Name)
                .Select(v => (v.Id, v.Name))
                .ToList();
        }

        return result;
    }

    // ── Dimension types ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns all linear DimensionType elements in the project, sorted by name.
    /// NOTE: OfClass(typeof(DimensionType)) must be used — OfCategory(OST_Dimensions)
    /// does NOT work because DimensionType.Category is null.
    /// </summary>
    public List<(long Id, string Name)> ReadDimensionTypes()
    {
        return new FilteredElementCollector(_doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .OrderBy(dt => dt.Name)
            .Select(dt => (dt.Id.Value, dt.Name))
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
