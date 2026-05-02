using EllahColNum.Core.Models;

namespace EllahColNum.Core.Interfaces;

/// <summary>
/// Contract for reading columns from any source (Revit, test mock, future SaaS).
/// Core logic depends on this interface — NOT on Revit API directly.
/// </summary>
public interface IColumnReader
{
    /// <summary>Returns all structural columns in the current document/model.</summary>
    List<ColumnData> ReadAllColumns();
}
