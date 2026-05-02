using Autodesk.Revit.DB;

namespace EllahColNum.Revit.Helpers;

/// <summary>
/// Utility for working with Revit Grid lines.
/// Used to validate and enrich grid-based numbering.
/// </summary>
public static class GridHelper
{
    /// <summary>
    /// Returns all Grid elements in the document, keyed by their name.
    /// </summary>
    public static Dictionary<string, Grid> GetAllGrids(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Grid))
            .Cast<Grid>()
            .ToDictionary(g => g.Name, g => g);
    }

    /// <summary>
    /// Parses Revit's Column Location Mark format: "A(64)-1(-46)"
    /// Returns (rowGrid: "A", colGrid: "1") or empty strings if parsing fails.
    /// The numbers in parentheses are offsets in mm from the grid line center.
    /// </summary>
    public static (string Row, string Col) ParseLocationMark(string locationMark)
    {
        if (string.IsNullOrWhiteSpace(locationMark))
            return ("", "");

        // Format: "ROWGRID(offset)-COLGRID(offset)"
        // Find the separator between the two grids: ")- "
        var sepIdx = locationMark.IndexOf(")-");
        if (sepIdx < 0)
            return (locationMark.Trim(), "");

        var rowPart = locationMark[..sepIdx];
        var colPart = locationMark[(sepIdx + 2)..];

        var row = StripOffset(rowPart);
        var col = StripOffset(colPart);

        return (row, col);
    }

    /// <summary>
    /// Removes the offset in parentheses: "A(64)" → "A"
    /// </summary>
    private static string StripOffset(string part)
    {
        var parenIdx = part.IndexOf('(');
        return parenIdx > 0
            ? part[..parenIdx].Trim()
            : part.Trim();
    }
}
