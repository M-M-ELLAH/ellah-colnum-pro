using EllahColNum.Core.Models;

namespace EllahColNum.Core.Interfaces;

/// <summary>
/// Contract for writing marks back to any target (Revit, test mock, export file).
/// Separates write logic from core numbering logic.
/// </summary>
public interface IMarkWriter
{
    /// <summary>
    /// Writes the AssignedMark of each column back to the data source.
    /// Returns the number of columns successfully updated.
    /// </summary>
    int WriteMarks(IEnumerable<ColumnData> columns);
}
