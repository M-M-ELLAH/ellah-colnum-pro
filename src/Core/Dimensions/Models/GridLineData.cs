namespace EllahColNum.Core.Dimensions.Models;

/// <summary>
/// Revit-free representation of a single straight grid line.
/// IsVertical = true  → grid runs predominantly N-S; Position is its X coordinate (measures E-W spacing).
/// IsVertical = false → grid runs predominantly E-W; Position is its Y coordinate (measures N-S spacing).
/// </summary>
public class GridLineData
{
    public long   ElementId  { get; set; }
    public string Name       { get; set; } = "";
    public bool   IsVertical { get; set; }

    /// <summary>
    /// X coordinate for vertical grids, Y coordinate for horizontal grids — in Revit internal units (feet).
    /// Used for sorting and grouping.
    /// </summary>
    public double Position { get; set; }
}
