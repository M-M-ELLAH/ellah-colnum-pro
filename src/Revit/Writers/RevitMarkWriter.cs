using Autodesk.Revit.DB;
using EllahColNum.Core.Interfaces;
using EllahColNum.Core.Models;

namespace EllahColNum.Revit.Writers;

/// <summary>
/// Writes AssignedMark values back to Revit elements.
/// Must be called inside an open Transaction.
/// When a targetParameterName is provided the mark is written to that named
/// parameter; if the parameter is not found or is read-only the built-in
/// ALL_MODEL_MARK ("Mark") is used as a fallback.
/// </summary>
public class RevitMarkWriter : IMarkWriter
{
    private readonly Document _doc;
    private readonly string   _targetParameterName;

    /// <param name="doc">Active Revit document.</param>
    /// <param name="targetParameterName">
    /// Name of the parameter to write into.
    /// Pass null or empty string to use the default built-in Mark parameter.
    /// </param>
    public RevitMarkWriter(Document doc, string targetParameterName = "")
    {
        _doc                 = doc;
        _targetParameterName = targetParameterName?.Trim() ?? "";
    }

    /// <summary>
    /// Writes each column's AssignedMark to the chosen parameter.
    /// Skips columns with empty AssignedMark.
    /// Returns number of successfully updated elements.
    /// </summary>
    public int WriteMarks(IEnumerable<ColumnData> columns)
    {
        int count = 0;

        foreach (var col in columns)
        {
            if (string.IsNullOrWhiteSpace(col.AssignedMark)) continue;

            var element = _doc.GetElement(new ElementId(col.ElementId));
            if (element == null) continue;

            var param = ResolveParameter(element);
            if (param == null || param.IsReadOnly) continue;

            param.Set(col.AssignedMark);
            count++;
        }

        return count;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private Parameter? ResolveParameter(Element element)
    {
        // Try the user-chosen named parameter first
        if (!string.IsNullOrEmpty(_targetParameterName))
        {
            var named = element.LookupParameter(_targetParameterName);
            if (named != null && named.StorageType == StorageType.String && !named.IsReadOnly)
                return named;
        }

        // Fall back to the built-in Mark parameter
        return element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
    }
}
