using EllahColNum.Core.Geometry;
using EllahColNum.Core.Models;

namespace EllahColNum.Core.Services;

/// <summary>
/// Suggests a row-grouping tolerance value by inspecting the actual
/// distribution of column positions in the model.
///
/// <para>The principle is engineering-grade rather than heuristic: in any
/// well-modelled structural plan the gaps between adjacent column centroids
/// (along EITHER local axis) form a bimodal distribution.  Small gaps belong
/// to in-row modelling drift; large gaps mark transitions between rows.
/// The natural tolerance sits a configurable margin above the largest
/// in-row drift, well below the smallest between-row separation.</para>
///
/// <para>Because Revit modelling drift is essentially isotropic — the same
/// engineer with the same precision tools produces similar drift in X and
/// in Y — the detector analyses BOTH axes simultaneously and uses the
/// larger confident drift value as the global drift estimate.  This makes
/// the suggested tolerance stable across sort directions: a building's
/// "looseness" doesn't change just because the user counts left-to-right
/// instead of bottom-to-top.</para>
///
/// <para>The detector operates per zone (orthogonal vs. tilted) so a
/// mixed-axis building gets the most demanding zone's value.  Single-zone
/// projects fall through to the same code path with one zone of id 0.</para>
///
/// <para>Industry-standard sanity caps:
///   • Lower bound 30 cm (≈ half the smallest typical concrete column).
///   • Upper bound 200 cm (≈ one third of the universal 6 m minimum bay).
/// These are derived from published structural-engineering references and
/// real-world Revit modelling practice (Autodesk forums, SteelFlo,
/// Eng-Tips).</para>
/// </summary>
public static class ToleranceAutoDetector
{
    /// <summary>Minimum gap (feet) used as duplicate filter.  Two columns
    /// that share the same XY (multi-story stack) are excluded from the
    /// drift histogram.</summary>
    private const double DuplicateGapFeet = 0.033;     // ≈1 cm

    /// <summary>Minimum absolute jump between consecutive sorted gaps to
    /// qualify as a "break" between drift and row-separation.</summary>
    private const double MinBreakAbsoluteFeet = 0.98;  // ≈30 cm

    /// <summary>Minimum ratio between consecutive sorted gaps to qualify
    /// as a "break".  Default 1.8 = the larger gap must be ≥80% wider
    /// than the smaller.</summary>
    private const double MinBreakRatio = 1.8;

    /// <summary>Lower clamp for the suggested tolerance.  Approximately
    /// half the smallest typical concrete column (60 cm).</summary>
    private const double MinSuggestionFeet = 0.98;     // ≈30 cm

    /// <summary>Upper clamp.  Approximately one third of the universal
    /// minimum bay width (6 m); above this the suggestion would risk
    /// merging structurally distinct rows.</summary>
    private const double MaxSuggestionFeet = 6.56;     // ≈200 cm

    /// <summary>Headroom multiplier applied to the largest observed
    /// in-row drift.  Empirically tuned to match real-project data:
    /// 30% margin absorbs minor noise and rotation residuals while
    /// staying clear of the next-row separation band.</summary>
    private const double DriftHeadroom = 1.3;

    /// <summary>
    /// Runs the detector.
    /// </summary>
    /// <param name="groups">Column groups whose XY centroids are used.
    /// Multi-story stacks count once.</param>
    /// <param name="sortDirection">The sort direction the user has chosen.
    /// Determines which axis takes priority for the secondary metric and
    /// for the textual hint, but BOTH axes are analysed under the hood.</param>
    /// <param name="zoneByElementId">Optional zone-id per column.
    /// <c>null</c> ⇒ all groups treated as one zone.</param>
    /// <param name="rotationByElementId">Optional rotation (deg) per
    /// column.  Used to project the centroid into the zone frame so the
    /// detector measures along the BUILDING's local axis, not the
    /// project axis.  <c>null</c> ⇒ no rotation.</param>
    public static AutoDetectResult Detect(
        IReadOnlyList<ColumnGroup>      groups,
        SortDirection                   sortDirection,
        IReadOnlyDictionary<long, int>? zoneByElementId     = null,
        IReadOnlyDictionary<long, double>? rotationByElementId = null)
    {
        if (groups == null || groups.Count < 4)
        {
            return new AutoDetectResult(
                SuggestedToleranceFeet: null,
                MaxInRowDriftFeet:      0,
                MinRowSeparationFeet:   0,
                ZoneCount:              0,
                GroupCount:             groups?.Count ?? 0,
                IsConfident:            false,
                Reason:                 "too few columns for a reliable estimate");
        }

        // Analyse BOTH axes — the building's modelling drift is the same
        // regardless of which axis we sort along, so combining both gives
        // a more reliable global estimate than relying on one alone.
        var xResult = AnalyseAxis(groups, useXAxis: true,  zoneByElementId, rotationByElementId);
        var yResult = AnalyseAxis(groups, useXAxis: false, zoneByElementId, rotationByElementId);

        // Pick the global drift estimate.  Strategy:
        //   1) If at least one axis is confident, use the LARGER confident
        //      drift — modelling drift is isotropic so the demanding axis
        //      sets the safe lower bound.
        //   2) If neither axis is confident, fall back to the axis matching
        //      the user's chosen sort direction (its drift is what they're
        //      asking about right now).
        bool   anyConfident;
        double drift;
        double separation;
        string axisLabel;

        if (xResult.Confident || yResult.Confident)
        {
            anyConfident = true;
            // Choose the axis whose drift is larger AND confident.
            var pickX = xResult.Confident && (!yResult.Confident || xResult.Drift >= yResult.Drift);
            var picked = pickX ? xResult : yResult;
            drift      = picked.Drift;
            separation = picked.Separation;
            axisLabel  = pickX ? "X" : "Y";
        }
        else
        {
            anyConfident = false;
            bool useX = AxisIsX(sortDirection);
            var picked = useX ? xResult : yResult;
            drift      = picked.Drift;
            separation = picked.Separation;
            axisLabel  = useX ? "X" : "Y";
        }

        if (xResult.ZoneCount == 0 && yResult.ZoneCount == 0)
        {
            return new AutoDetectResult(
                SuggestedToleranceFeet: null,
                MaxInRowDriftFeet:      0,
                MinRowSeparationFeet:   0,
                ZoneCount:              0,
                GroupCount:             groups.Count,
                IsConfident:            false,
                Reason:                 "no usable zone with at least four columns");
        }

        // Apply formula.
        double suggestion;
        if (anyConfident)
        {
            double tight    = drift * DriftHeadroom;
            double midpoint = (drift + separation) / 2.0;
            suggestion      = Math.Min(tight, midpoint);
        }
        else
        {
            suggestion = drift > 0 ? drift * DriftHeadroom : MinSuggestionFeet;
        }

        // Industry-grade clamps.
        double clamped = Math.Clamp(suggestion, MinSuggestionFeet, MaxSuggestionFeet);

        string reason = anyConfident
            ? $"clear gap split on {axisLabel}-axis: drift ≤ {ToCm(drift):F0} cm, separation ≥ {ToCm(separation):F0} cm"
            : $"no clear bimodal split — using {axisLabel}-axis 95th-percentile drift ({ToCm(drift):F0} cm)";

        return new AutoDetectResult(
            SuggestedToleranceFeet: clamped,
            MaxInRowDriftFeet:      drift,
            MinRowSeparationFeet:   separation,
            ZoneCount:              Math.Max(xResult.ZoneCount, yResult.ZoneCount),
            GroupCount:             groups.Count,
            IsConfident:            anyConfident,
            Reason:                 reason);
    }

    // ── Internal types & helpers ───────────────────────────────────────────

    private readonly record struct AxisAnalysis(
        bool   Confident,
        double Drift,
        double Separation,
        int    ZoneCount);

    /// <summary>
    /// Runs the per-zone bimodal-split analysis on a single axis (X or Y).
    /// Returns the LARGEST suggestion-driving drift across all zones.
    /// </summary>
    private static AxisAnalysis AnalyseAxis(
        IReadOnlyList<ColumnGroup>          groups,
        bool                                useXAxis,
        IReadOnlyDictionary<long, int>?     zoneByElementId,
        IReadOnlyDictionary<long, double>?  rotationByElementId)
    {
        var byZone = new Dictionary<int, List<double>>();

        foreach (var g in groups)
        {
            if (g.Columns.Count == 0) continue;
            long elemId = g.Columns[0].ElementId;

            int zone = (zoneByElementId != null && zoneByElementId.TryGetValue(elemId, out var z))
                ? z : 0;
            double rotDeg = (rotationByElementId != null && rotationByElementId.TryGetValue(elemId, out var r))
                ? r : 0.0;

            var (lx, ly) = RotationTransform.Rotate(g.X, g.Y, -rotDeg);
            double axisVal = useXAxis ? lx : ly;

            if (!byZone.TryGetValue(zone, out var list))
                byZone[zone] = list = new List<double>();
            list.Add(axisVal);
        }

        bool   anyConfident   = false;
        double bestDrift      = 0;
        double bestSeparation = 0;
        int    analysedZones  = 0;
        bool   anyResult      = false;

        foreach (var (_, axisVals) in byZone)
        {
            if (axisVals.Count < 4) continue;
            analysedZones++;

            axisVals.Sort();
            var gaps = new List<double>(axisVals.Count - 1);
            for (int i = 1; i < axisVals.Count; i++)
            {
                double g = axisVals[i] - axisVals[i - 1];
                if (g > DuplicateGapFeet) gaps.Add(g);
            }
            if (gaps.Count < 3) continue;

            gaps.Sort();

            double driftCandidate;
            double sepCandidate;
            bool   confident;
            if (TryFindBimodalBreak(gaps, out var below, out var above))
            {
                driftCandidate = below;
                sepCandidate   = above;
                confident      = true;
            }
            else if (gaps[0] >= MinBreakAbsoluteFeet)
            {
                // Pristine model: even the smallest gap is large, which
                // means there is no in-row drift to speak of.  Reporting
                // a tiny drift makes the global cap fall back to its
                // industry-standard floor (30 cm).
                driftCandidate = 0;
                sepCandidate   = gaps[0];
                confident      = false;
            }
            else
            {
                int idx = (int)Math.Round(0.95 * (gaps.Count - 1));
                driftCandidate = gaps[Math.Max(0, idx)];
                sepCandidate   = 0;
                confident      = false;
            }

            // Pick the most demanding (largest drift) confident result —
            // or accept the first result we see if nothing is confident yet.
            bool replace = !anyResult
                        || (confident && !anyConfident)
                        || (confident == anyConfident && driftCandidate > bestDrift);
            if (replace)
            {
                anyConfident   = anyConfident || confident;
                bestDrift      = driftCandidate;
                bestSeparation = sepCandidate;
                anyResult      = true;
            }
        }

        return new AxisAnalysis(anyConfident, bestDrift, bestSeparation, analysedZones);
    }

    /// <summary>
    /// Searches the sorted gaps array for the largest absolute+ratio break
    /// between consecutive entries.  Returns <c>true</c> if found.
    /// </summary>
    private static bool TryFindBimodalBreak(List<double> sortedGaps, out double below, out double above)
    {
        below = 0; above = 0;
        double bestScore = 0;
        for (int i = 0; i < sortedGaps.Count - 1; i++)
        {
            double a = sortedGaps[i];
            double b = sortedGaps[i + 1];
            if (b - a < MinBreakAbsoluteFeet) continue;
            double ratio = a > 0 ? b / a : double.PositiveInfinity;
            if (ratio < MinBreakRatio) continue;

            double score = (b - a) * Math.Min(ratio, 100.0);
            if (score > bestScore)
            {
                bestScore = score;
                below     = a;
                above     = b;
            }
        }
        return bestScore > 0;
    }

    /// <summary>
    /// Returns <c>true</c> when the row-perpendicular axis is X — i.e. the
    /// numbering walks columns vertically (rows = constant-X bands).
    /// </summary>
    private static bool AxisIsX(SortDirection s) => s switch
    {
        SortDirection.BottomToTop    => true,
        SortDirection.TopToBottom    => true,
        SortDirection.BottomTopLeft  => true,
        SortDirection.TopBottomLeft  => true,
        _                            => false,
    };

    private static double ToCm(double feet) => feet * 30.48;
}
