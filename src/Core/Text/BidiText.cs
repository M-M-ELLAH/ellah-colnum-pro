namespace EllahColNum.Core.Text;

/// <summary>
/// Helpers for safely comparing strings that may contain bidirectional control
/// characters (RTL/LTR markers, isolates, embeddings).  Hebrew level and view
/// names returned by Revit sometimes carry invisible BiDi marks that cause
/// ordinary <see cref="string.Equals(string, string, System.StringComparison)"/>
/// comparisons to fail even when the visible text is identical.
///
/// Real-world example that motivated this helper:
///   Level.Name             = "קומה ז'"               (plain)
///   ViewPlan.GenLevel.Name = "\u202bקומה ז'\u202c"   (wrapped in RLE/PDF marks)
///   Equals → false, even though the user sees the same text.
///
/// This module is pure C# and lives in Core so it can be exercised by unit tests
/// and reused by any future feature that compares Revit-sourced names.
/// </summary>
public static class BidiText
{
    /// <summary>
    /// Returns <paramref name="value"/> with all bidirectional control
    /// characters removed and surrounding whitespace trimmed.
    /// Safe to call on null (returns empty string).
    /// </summary>
    public static string NormalizeForCompare(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // Pre-allocate at exact length — normalisation can only shrink the string.
        var buffer = new char[value.Length];
        int n = 0;

        foreach (var ch in value)
        {
            if (IsBidiControl(ch)) continue;
            buffer[n++] = ch;
        }

        return new string(buffer, 0, n).Trim();
    }

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are equal after
    /// stripping BiDi control characters, using ordinal-ignore-case comparison
    /// (matching the rest of the plugin's level-name comparisons).
    /// </summary>
    public static bool EqualsIgnoreBidi(string? a, string? b)
    {
        return string.Equals(
            NormalizeForCompare(a),
            NormalizeForCompare(b),
            System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for Unicode codepoints that affect bidirectional rendering but are
    /// otherwise invisible.  Reference: Unicode 15.1 BiDi formatting characters.
    /// </summary>
    private static bool IsBidiControl(char ch) => ch switch
    {
        '\u200E' => true,                     // LEFT-TO-RIGHT MARK (LRM)
        '\u200F' => true,                     // RIGHT-TO-LEFT MARK (RLM)
        >= '\u202A' and <= '\u202E' => true,  // LRE / RLE / PDF / LRO / RLO
        >= '\u2066' and <= '\u2069' => true,  // LRI / RLI / FSI / PDI
        _        => false,
    };
}
