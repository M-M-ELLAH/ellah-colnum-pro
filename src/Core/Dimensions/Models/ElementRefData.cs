namespace EllahColNum.Core.Dimensions.Models;

/// <summary>
/// Revit-free snapshot of one dimensionable element.
/// Carries only the data the pure-logic engine needs — no Revit types.
/// The actual Revit Reference object is stored separately in the collector's map,
/// keyed by ElementId.
/// </summary>
public class ElementRefData
{
    /// <summary>Revit ElementId.Value for reverse-lookup in the Reference map.</summary>
    public long ElementId { get; set; }

    /// <summary>Human-readable label (family name, type name, or grid name).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Broad category used to assign the element to a dimension layer.
    /// </summary>
    public ElementCategory Category { get; set; }

    /// <summary>
    /// X coordinate of the element's effective reference point, in Revit internal units (feet).
    /// For columns: centrepoint.  For walls: midpoint of location curve.
    /// For doors/windows: centrepoint of the opening.
    /// Used to sort elements left → right when building a horizontal dimension string.
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// Y coordinate of the element's effective reference point, in Revit internal units (feet).
    /// Used to sort elements bottom → top when building a vertical dimension string.
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    /// Orientation angle in radians (0 = horizontal/E-W, π/2 = vertical/N-S).
    /// Meaningful for Grid and Beam. Used to detect building primary axes.
    /// Default 0 for point elements.
    /// </summary>
    public double Angle { get; set; }
}

/// <summary>Broad dimension-layer category.</summary>
public enum ElementCategory
{
    Grid,
    Column,    // structural or architectural column
    Wall,
    Opening,   // door or window
    Beam,      // structural framing / beam
}
