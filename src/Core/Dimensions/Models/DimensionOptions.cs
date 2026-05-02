namespace EllahColNum.Core.Dimensions.Models;

/// <summary>
/// User-configured options for the Smart Grid Dimensions command.
/// Passed from the dialog → engine → helper, containing everything needed to run.
/// </summary>
public class DimensionOptions
{
    /// <summary>Revit ElementId.Value (long) for each selected ViewPlan.</summary>
    public List<long> SelectedViewIds { get; set; } = new();

    /// <summary>Display name of the Revit DimensionType to use. Empty = Revit default.</summary>
    public string DimensionTypeName { get; set; } = "";

    /// <summary>Distance from the outermost grid endpoint to the dimension string, in Revit internal units (feet).</summary>
    public double OffsetFromGridFeet { get; set; } = 3.281; // ≈ 1 metre

    /// <summary>Create a horizontal dimension string across all N-S (vertical) grid lines.</summary>
    public bool DimensionVerticalGrids { get; set; } = true;

    /// <summary>Create a vertical dimension string across all E-W (horizontal) grid lines.</summary>
    public bool DimensionHorizontalGrids { get; set; } = true;
}
