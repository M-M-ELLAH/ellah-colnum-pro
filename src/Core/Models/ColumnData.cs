namespace EllahColNum.Core.Models;

/// <summary>
/// Represents a single structural column element from the Revit model.
/// Completely decoupled from Revit API — usable in tests and future SaaS integration.
/// </summary>
public class ColumnData
{
    /// <summary>Revit ElementId (long since Revit 2024+)</summary>
    public long ElementId { get; set; }

    /// <summary>Center point X coordinate (Revit internal feet)</summary>
    public double X { get; set; }

    /// <summary>Center point Y coordinate (Revit internal feet)</summary>
    public double Y { get; set; }

    /// <summary>Name of the level this column starts at (e.g. "קומה א")</summary>
    public string BaseLevelName { get; set; } = "";

    /// <summary>Name of the level this column ends at (e.g. "הקומה הטיפוסית")</summary>
    public string TopLevelName { get; set; } = "";

    /// <summary>Elevation of the base level in feet</summary>
    public double BaseLevelElevation { get; set; }

    /// <summary>Elevation of the top level in feet</summary>
    public double TopLevelElevation { get; set; }

    /// <summary>Additional offset from the base level in feet</summary>
    public double BaseOffset { get; set; }

    /// <summary>Additional offset from the top level in feet</summary>
    public double TopOffset { get; set; }

    /// <summary>Family name (e.g. "M_Concrete-Rectangular-Column")</summary>
    public string FamilyName { get; set; } = "";

    /// <summary>Type name / section (e.g. "30/75")</summary>
    public string TypeName { get; set; } = "";

    /// <summary>Structural material (e.g. "Concrete, Cast-in-Place")</summary>
    public string StructuralMaterial { get; set; } = "";

    /// <summary>
    /// Revit's automatic grid intersection label (e.g. "A(64)-1(-46)").
    /// The numbers in parentheses are offsets in mm from the grid line.
    /// </summary>
    public string ColumnLocationMark { get; set; } = "";

    /// <summary>The current Mark value in the model (may be empty)</summary>
    public string CurrentMark { get; set; } = "";

    /// <summary>The mark assigned by our numbering engine — written back to Revit</summary>
    public string AssignedMark { get; set; } = "";

    /// <summary>
    /// Key shared by all columns at the same X,Y position (within tolerance).
    /// Set by ColumnGrouper.
    /// </summary>
    public string GroupKey { get; set; } = "";

    /// <summary>True if the column is attached to grid lines (Moves With Grids)</summary>
    public bool MovesWithGrids { get; set; }
}
