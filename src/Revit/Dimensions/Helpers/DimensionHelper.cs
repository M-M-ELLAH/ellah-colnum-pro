using Autodesk.Revit.DB;
using EllahColNum.Core.Dimensions.Models;

namespace EllahColNum.Revit.Dimensions.Helpers;

/// <summary>
/// Creates dimension strings in Revit views by calling doc.Create.NewDimension().
/// This class owns ALL calls to the Revit dimensioning API and must be invoked
/// inside an open Transaction.
///
/// Critical rule (from Revit API research):
///   Use new Reference(grid)  — REFERENCE_TYPE_NONE   ← CORRECT
///   NOT grid.Curve.Reference — REFERENCE_TYPE_SURFACE ← throws InvalidOperationException
/// </summary>
public class DimensionHelper
{
    private readonly Document _doc;

    public DimensionHelper(Document doc) => _doc = doc;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Iterates over all selected views and creates dimension strings for each
    /// grid axis group defined in the plan. Returns the total number of
    /// Dimension elements successfully created across all views.
    /// Must be called inside an open Transaction.
    /// </summary>
    public int CreateDimensions(
        DimensionPlan           plan,
        Dictionary<long, Grid>  gridMap,
        List<ViewPlan>          views,
        DimensionOptions        options,
        DimensionType?          dimType)
    {
        int created = 0;

        foreach (var view in views)
        {
            // Horizontal dimension string across N-S (vertical) grids
            if (options.DimensionVerticalGrids && plan.VerticalGrids.Count >= 2)
            {
                var grids = ResolveGrids(plan.VerticalGrids, gridMap);
                if (grids.Count >= 2 &&
                    TryCreateHorizontalDimension(view, grids, dimType, options.OffsetFromGridFeet) != null)
                    created++;
            }

            // Vertical dimension string across E-W (horizontal) grids
            if (options.DimensionHorizontalGrids && plan.HorizontalGrids.Count >= 2)
            {
                var grids = ResolveGrids(plan.HorizontalGrids, gridMap);
                if (grids.Count >= 2 &&
                    TryCreateVerticalDimension(view, grids, dimType, options.OffsetFromGridFeet) != null)
                    created++;
            }
        }

        return created;
    }

    // ── Dimension creation ────────────────────────────────────────────────────

    /// <summary>
    /// Places a horizontal dimension string above all N-S grid lines.
    /// The dimension line Y coordinate = max grid extent Y + offsetFeet.
    /// All grids must be sorted by X position (left → right).
    /// </summary>
    private Dimension? TryCreateHorizontalDimension(
        ViewPlan       view,
        List<Grid>     sortedByX,
        DimensionType? dimType,
        double         offsetFeet)
    {
        // CRITICAL: new Reference(grid) gives REFERENCE_TYPE_NONE — only valid type for grids
        var ra = new ReferenceArray();
        foreach (var g in sortedByX)
            ra.Append(new Reference(g));

        // Place dimension line above the topmost grid endpoint extent
        double maxY = sortedByX.Max(g =>
            Math.Max(g.Curve.GetEndPoint(0).Y, g.Curve.GetEndPoint(1).Y));
        double dimY = maxY + offsetFeet;

        double x1 = MidX(sortedByX.First());
        double x2 = MidX(sortedByX.Last());

        if (Math.Abs(x2 - x1) < 1e-6) return null;

        var dimLine = Line.CreateBound(new XYZ(x1, dimY, 0), new XYZ(x2, dimY, 0));
        return TryNewDimension(view, dimLine, ra, dimType);
    }

    /// <summary>
    /// Places a vertical dimension string to the left of all E-W grid lines.
    /// The dimension line X coordinate = min grid extent X - offsetFeet.
    /// All grids must be sorted by Y position (bottom → top).
    /// </summary>
    private Dimension? TryCreateVerticalDimension(
        ViewPlan       view,
        List<Grid>     sortedByY,
        DimensionType? dimType,
        double         offsetFeet)
    {
        var ra = new ReferenceArray();
        foreach (var g in sortedByY)
            ra.Append(new Reference(g));

        // Place dimension line to the left of the leftmost grid endpoint extent
        double minX = sortedByY.Min(g =>
            Math.Min(g.Curve.GetEndPoint(0).X, g.Curve.GetEndPoint(1).X));
        double dimX = minX - offsetFeet;

        double y1 = MidY(sortedByY.First());
        double y2 = MidY(sortedByY.Last());

        if (Math.Abs(y2 - y1) < 1e-6) return null;

        var dimLine = Line.CreateBound(new XYZ(dimX, y1, 0), new XYZ(dimX, y2, 0));
        return TryNewDimension(view, dimLine, ra, dimType);
    }

    private Dimension? TryNewDimension(
        ViewPlan       view,
        Line           dimLine,
        ReferenceArray ra,
        DimensionType? dimType)
    {
        try
        {
            return dimType != null
                ? _doc.Create.NewDimension(view, dimLine, ra, dimType)
                : _doc.Create.NewDimension(view, dimLine, ra);
        }
        catch
        {
            // Swallow per-view failures — a bad view should not abort the whole batch
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<Grid> ResolveGrids(List<GridLineData> gridData, Dictionary<long, Grid> gridMap)
        => gridData
            .Select(gd => gridMap.GetValueOrDefault(gd.ElementId))
            .Where(g => g != null)
            .Cast<Grid>()
            .ToList();

    private static double MidX(Grid g) =>
        (g.Curve.GetEndPoint(0).X + g.Curve.GetEndPoint(1).X) / 2.0;

    private static double MidY(Grid g) =>
        (g.Curve.GetEndPoint(0).Y + g.Curve.GetEndPoint(1).Y) / 2.0;
}
