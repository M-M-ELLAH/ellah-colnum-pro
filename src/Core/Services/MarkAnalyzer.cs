using System.Text.RegularExpressions;
using EllahColNum.Core.Models;

namespace EllahColNum.Core.Services;

/// <summary>
/// Analyzes existing Mark values on column groups.
/// Detects manual numbering patterns and determines continuation strategy.
/// 
/// Handles three real-world scenarios:
///   1. Column fully numbered on all floors (same mark everywhere) → keep as-is
///   2. Column numbered on some floors, empty on others → complete to all floors
///   3. Column not numbered at all → assign next available number
/// </summary>
public class MarkAnalyzer
{
    // Matches patterns like "C-1", "COL-23", "P001", "A1" — prefix then digits
    private static readonly Regex _markRegex = new(@"^(.*?)(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// Analyzes all groups, classifies each one, and detects the existing pattern.
    /// Returns a full analysis that the NumberingEngine uses to decide continuation.
    /// </summary>
    public MarkAnalysis Analyze(List<ColumnGroup> groups)
    {
        ClassifyGroups(groups);

        var existingMarks = groups
            .Where(g => !string.IsNullOrWhiteSpace(g.ExistingMark))
            .Select(g => g.ExistingMark!)
            .ToList();

        var pattern = DetectPattern(existingMarks);
        var maxNumber = pattern != null
            ? GetMaxNumber(existingMarks, pattern.Prefix)
            : 0;

        return new MarkAnalysis
        {
            DetectedPattern  = pattern,
            MaxExistingNumber = maxNumber,
            FullyNumberedCount     = groups.Count(g => g.NumberingStatus == GroupNumberingStatus.FullyNumbered),
            PartiallyNumberedCount = groups.Count(g => g.NumberingStatus == GroupNumberingStatus.PartiallyNumbered),
            ConflictingCount       = groups.Count(g => g.NumberingStatus == GroupNumberingStatus.Conflicting),
            NotNumberedCount       = groups.Count(g => g.NumberingStatus == GroupNumberingStatus.NotNumbered),
        };
    }

    /// <summary>
    /// Classifies each group and sets ExistingMark where applicable.
    /// </summary>
    private static void ClassifyGroups(List<ColumnGroup> groups)
    {
        foreach (var group in groups)
        {
            var allMarks = group.Columns
                .Select(c => c.CurrentMark?.Trim() ?? "")
                .ToList();

            var nonEmpty = allMarks.Where(m => m.Length > 0).Distinct().ToList();

            if (nonEmpty.Count == 0)
            {
                group.NumberingStatus = GroupNumberingStatus.NotNumbered;
            }
            else if (nonEmpty.Count == 1)
            {
                group.ExistingMark = nonEmpty[0];
                group.NumberingStatus = allMarks.Any(m => m.Length == 0)
                    ? GroupNumberingStatus.PartiallyNumbered  // some floors missing the mark
                    : GroupNumberingStatus.FullyNumbered;
            }
            else
            {
                // Multiple different marks on different floors of the same column → conflict
                group.ExistingMark = nonEmpty[0];
                group.NumberingStatus = GroupNumberingStatus.Conflicting;
            }
        }
    }

    /// <summary>
    /// Tries to detect a common prefix pattern from a list of mark strings.
    /// Example: ["C-1", "C-2", "C-5"] → prefix "C-"
    /// Returns null if no clear pattern found.
    /// </summary>
    public MarkPattern? DetectPattern(List<string> marks)
    {
        if (marks.Count == 0) return null;

        var parsed = marks
            .Select(m => _markRegex.Match(m))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();

        if (parsed.Count == 0) return null;

        var dominantPrefix = parsed
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .First().Key;

        return new MarkPattern { Prefix = dominantPrefix };
    }

    /// <summary>
    /// Finds the highest number used among all marks with the given prefix.
    /// Example: ["C-1", "C-5", "C-3"] → 5
    /// </summary>
    public int GetMaxNumber(List<string> marks, string prefix)
    {
        int max = 0;
        foreach (var mark in marks)
        {
            var m = _markRegex.Match(mark);
            if (m.Success && m.Groups[1].Value == prefix)
            {
                if (int.TryParse(m.Groups[2].Value, out var num) && num > max)
                    max = num;
            }
        }
        return max;
    }
}

/// <summary>Result of analyzing existing marks in the model.</summary>
public class MarkAnalysis
{
    public MarkPattern? DetectedPattern { get; set; }
    public int MaxExistingNumber { get; set; }
    public int FullyNumberedCount { get; set; }
    public int PartiallyNumberedCount { get; set; }
    public int ConflictingCount { get; set; }
    public int NotNumberedCount { get; set; }

    /// <summary>True when there are existing marks we should try to continue from.</summary>
    public bool HasExistingNumbering =>
        FullyNumberedCount > 0 || PartiallyNumberedCount > 0;
}

/// <summary>Detected prefix pattern from existing marks.</summary>
public class MarkPattern
{
    /// <summary>The text before the number, e.g. "C-" or "COL-"</summary>
    public string Prefix { get; set; } = "";
}
