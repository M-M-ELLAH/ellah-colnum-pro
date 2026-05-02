using EllahColNum.Core.Models;

namespace EllahColNum.Core.Services;

/// <summary>
/// Groups columns by their X,Y position within a configurable tolerance.
/// Columns at the same plan position across different floors form one ColumnGroup.
/// </summary>
public class ColumnGrouper
{
    private readonly double _tolerance;

    public ColumnGrouper(double toleranceFeet)
    {
        _tolerance = toleranceFeet;
    }

    /// <summary>
    /// Processes a flat list of columns and returns a list of ColumnGroups.
    /// Each group represents one column stack (same position, possibly multiple floors).
    /// </summary>
    public List<ColumnGroup> Group(IEnumerable<ColumnData> columns)
    {
        var groups = new List<ColumnGroup>();

        foreach (var col in columns)
        {
            var match = FindMatchingGroup(col.X, col.Y, groups);

            if (match != null)
            {
                col.GroupKey = match.Key;
                match.Columns.Add(col);
            }
            else
            {
                var key = $"{col.X:F3}_{col.Y:F3}";
                col.GroupKey = key;

                var newGroup = new ColumnGroup
                {
                    Key     = key,
                    Columns = [col],
                    // Parse grid info from the first column we encounter at this position
                    GridRow    = ParseGridRow(col.ColumnLocationMark),
                    GridColumn = ParseGridColumn(col.ColumnLocationMark),
                };

                groups.Add(newGroup);
            }
        }

        return groups;
    }

    private ColumnGroup? FindMatchingGroup(double x, double y, List<ColumnGroup> groups)
    {
        foreach (var g in groups)
        {
            if (Math.Abs(x - g.X) <= _tolerance &&
                Math.Abs(y - g.Y) <= _tolerance)
                return g;
        }
        return null;
    }

    /// <summary>
    /// Parses the row grid name from Revit's Column Location Mark.
    /// Example: "A(64)-1(-46)" → "A"
    /// </summary>
    private static string ParseGridRow(string locationMark)
    {
        if (string.IsNullOrWhiteSpace(locationMark)) return "";
        var parenIdx = locationMark.IndexOf('(');
        return parenIdx > 0
            ? locationMark[..parenIdx].Trim()
            : locationMark.Split('-')[0].Trim();
    }

    /// <summary>
    /// Parses the column grid name from Revit's Column Location Mark.
    /// Example: "A(64)-1(-46)" → "1"
    /// </summary>
    private static string ParseGridColumn(string locationMark)
    {
        if (string.IsNullOrWhiteSpace(locationMark)) return "";
        // Format: "A(64)-1(-46)" — find the second segment after the dash between grids
        var dashIdx = locationMark.IndexOf(")-");
        if (dashIdx < 0) return "";
        var after = locationMark[(dashIdx + 2)..];
        var parenIdx = after.IndexOf('(');
        return parenIdx > 0
            ? after[..parenIdx].Trim()
            : after.Trim();
    }
}
