namespace EllahColNum.Core.Dimensions.Models;

/// <summary>
/// Output of DimensionEngine.BuildPlan — two sorted lists of grid lines,
/// one per axis, ready for dimension string creation.
/// </summary>
public class DimensionPlan
{
    /// <summary>N-S (vertical) grids, sorted by X position left → right.</summary>
    public List<GridLineData> VerticalGrids { get; set; } = new();

    /// <summary>E-W (horizontal) grids, sorted by Y position bottom → top.</summary>
    public List<GridLineData> HorizontalGrids { get; set; } = new();
}
