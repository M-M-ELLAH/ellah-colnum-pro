using EllahColNum.Core.Dimensions.Models;

namespace EllahColNum.Core.Dimensions.Services;

/// <summary>
/// Pure-logic dimension planning engine — no Revit dependency.
/// Separates grid lines by axis direction and sorts them by position,
/// producing a DimensionPlan that the Revit layer can execute.
/// </summary>
public class DimensionEngine
{
    /// <summary>
    /// Splits <paramref name="grids"/> into vertical and horizontal groups
    /// and sorts each group by position.
    /// Vertical grids (N-S) are sorted by X coordinate, left → right.
    /// Horizontal grids (E-W) are sorted by Y coordinate, bottom → top.
    /// </summary>
    public DimensionPlan BuildPlan(IReadOnlyList<GridLineData> grids, DimensionOptions options)
    {
        var plan = new DimensionPlan();

        if (options.DimensionVerticalGrids)
            plan.VerticalGrids = grids
                .Where(g => g.IsVertical)
                .OrderBy(g => g.Position)
                .ToList();

        if (options.DimensionHorizontalGrids)
            plan.HorizontalGrids = grids
                .Where(g => !g.IsVertical)
                .OrderBy(g => g.Position)
                .ToList();

        return plan;
    }
}
