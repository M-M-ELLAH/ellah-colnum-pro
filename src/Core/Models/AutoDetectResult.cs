namespace EllahColNum.Core.Models;

/// <summary>
/// Result of <see cref="Services.ToleranceAutoDetector.Detect"/>.
/// All distances are reported in <b>feet</b> for direct compatibility with
/// <see cref="NumberingOptions.RowToleranceFeet"/>.
///
/// <para>The detector splits the consecutive-gap distribution into two
/// populations — "in-row drift" and "between-row separation" — and proposes
/// a tolerance halfway between the largest in-row drift and the smallest
/// between-row separation.  This is the value that maximises the safety
/// margin in both directions.</para>
/// </summary>
public sealed record AutoDetectResult(
    /// <summary>Suggested row tolerance, in feet. <c>null</c> when the
    /// detector cannot find a reliable split (too few columns, no
    /// natural break in the gap histogram, etc.).</summary>
    double? SuggestedToleranceFeet,

    /// <summary>Largest gap that appears to belong to in-row drift, feet.
    /// 0 when undetermined.</summary>
    double MaxInRowDriftFeet,

    /// <summary>Smallest gap that appears to separate two distinct rows,
    /// feet. 0 when undetermined.</summary>
    double MinRowSeparationFeet,

    /// <summary>Number of zones the detector analysed.</summary>
    int ZoneCount,

    /// <summary>Number of column groups inspected.</summary>
    int GroupCount,

    /// <summary><c>true</c> when the suggestion is backed by a clear
    /// bimodal distribution (drift &lt;&lt; separation).  <c>false</c>
    /// indicates a fall-back heuristic was used.</summary>
    bool IsConfident,

    /// <summary>Short human-readable explanation, English.</summary>
    string Reason);
