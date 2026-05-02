using EllahColNum.Core.Geometry;
using EllahColNum.Core.Models;
using EllahColNum.Core.Text;

namespace EllahColNum.Core.Services;

/// <summary>
/// Assigns marks to column groups based on NumberingOptions.
/// Supports three modes:
///   SmartContinue — detect existing pattern, keep numbered groups, continue for new ones
///   Override       — renumber everything from scratch
///   AddOnly        — only assign marks to completely unnumbered groups
/// </summary>
public class NumberingEngine
{
    private readonly NumberingOptions _options;
    private readonly MarkAnalyzer _analyzer;

    public NumberingEngine(NumberingOptions options)
    {
        _options  = options;
        _analyzer = new MarkAnalyzer();
    }

    /// <summary>
    /// Main entry point. Analyzes existing marks, then assigns marks to all groups.
    /// Returns the groups with AssignedMark populated and a summary of what was done.
    ///
    /// SPECIFIC FLOOR mode short-circuits the ContinuationMode switch:
    /// when the user is renumbering a single isolated floor there is no vertical
    /// continuity to inherit from, so any existing marks (left over from a previous
    /// full-project run) are deliberately ignored and the floor gets a fresh
    /// sequential numbering starting at <see cref="NumberingOptions.StartNumber"/>.
    /// This mirrors how the Reference Floor is numbered consecutively at the start
    /// of a full-project run.
    /// </summary>
    public NumberingResult AssignMarks(List<ColumnGroup> groups)
    {
        var analysis = _analyzer.Analyze(groups);
        var sorted   = Sort(groups);

        // ── SPECIFIC FLOOR — always renumber from scratch ────────────────────
        // Engineering rationale: a single floor in isolation has no vertical
        // continuity; existing marks left from prior full-project runs would
        // otherwise be preserved by SmartContinue and produce gaps (e.g. 100,
        // 103, 107) instead of the consecutive 1, 2, 3 the engineer expects.
        if (!string.IsNullOrWhiteSpace(_options.SpecificFloorName))
        {
            AssignSpecificFloor(sorted);
            // Propagate mark to every column element inside each group
            foreach (var group in sorted)
                foreach (var col in group.Columns)
                    col.AssignedMark = group.AssignedMark;

            // Replace the analysis counts so the UI badges honestly reflect
            // that every group is being renumbered (no "kept" / "conflict"
            // badges should appear in single-floor mode).
            var renumbered = new MarkAnalysis
            {
                NotNumberedCount       = sorted.Count,
                FullyNumberedCount     = 0,
                PartiallyNumberedCount = 0,
                ConflictingCount       = 0,
                DetectedPattern        = analysis.DetectedPattern,
                MaxExistingNumber      = analysis.MaxExistingNumber,
            };
            return new NumberingResult { Groups = sorted, Analysis = renumbered };
        }

        switch (_options.ContinuationMode)
        {
            case ContinuationMode.Override:
                AssignOverride(sorted);
                break;

            case ContinuationMode.AddOnly:
                AssignAddOnly(sorted, analysis);
                break;

            case ContinuationMode.SmartContinue:
            default:
                AssignSmartContinue(sorted, analysis);
                break;
        }

        // Propagate assigned mark to every column element inside each group
        foreach (var group in sorted)
            foreach (var col in group.Columns)
                col.AssignedMark = group.AssignedMark;

        return new NumberingResult
        {
            Groups   = sorted,
            Analysis = analysis,
        };
    }

    // ── Mode: SmartContinue ──────────────────────────────────────────────────

    private void AssignSmartContinue(List<ColumnGroup> groups, MarkAnalysis analysis)
    {
        // Determine the prefix and starting number to use
        string prefix = _options.Prefix;
        int nextNumber = _options.StartNumber;

        if (analysis.HasExistingNumbering && analysis.DetectedPattern != null)
        {
            // Inherit the detected prefix and continue from the max number + 1
            prefix     = analysis.DetectedPattern.Prefix;
            nextNumber = analysis.MaxExistingNumber + 1;
        }

        foreach (var group in groups)
        {
            switch (group.NumberingStatus)
            {
                case GroupNumberingStatus.FullyNumbered:
                    // Already has a consistent mark → keep it exactly as-is
                    group.AssignedMark = group.ExistingMark!;
                    break;

                case GroupNumberingStatus.PartiallyNumbered:
                    // Some floors have a mark, some don't → complete with existing mark
                    group.AssignedMark = group.ExistingMark!;
                    break;

                case GroupNumberingStatus.Conflicting:
                    // Different marks on different floors → take the most common one, flag it
                    group.AssignedMark = group.ExistingMark!;
                    break;

                case GroupNumberingStatus.NotNumbered:
                    // Brand new → assign next number (or grid mark if in Grid mode)
                    group.AssignedMark = _options.Mode == NumberingMode.GridBased
                        ? BuildGridMark(group)
                        : BuildMark(prefix, nextNumber, _options);
                    if (_options.Mode != NumberingMode.GridBased) nextNumber++;
                    break;
            }
        }
    }

    // ── Mode: Override ───────────────────────────────────────────────────────

    private void AssignOverride(List<ColumnGroup> groups)
    {
        int n = _options.StartNumber;
        foreach (var group in groups)
        {
            group.AssignedMark = _options.Mode == NumberingMode.GridBased
                ? BuildGridMark(group)
                : BuildMark(_options.Prefix, n++, _options);
        }
    }

    // ── Mode: SpecificFloor — single-floor renumber ──────────────────────────

    /// <summary>
    /// Numbers a single isolated floor consecutively from <see cref="NumberingOptions.StartNumber"/>.
    ///
    /// Differs from <see cref="AssignOverride"/> in two important ways:
    ///   1. Each group's <see cref="ColumnGroup.NumberingStatus"/> is reset to
    ///      <see cref="GroupNumberingStatus.NotNumbered"/> so the UI honestly
    ///      reflects that every group will receive a brand-new mark — no
    ///      misleading "KEEP" or "CONFLICT" badges.
    ///   2. <see cref="ColumnGroup.ExistingMark"/> is cleared so downstream
    ///      consumers cannot accidentally inherit prior marks.
    ///
    /// Called only when <see cref="NumberingOptions.SpecificFloorName"/> is set.
    /// Honors <see cref="NumberingOptions.Mode"/> (Sequential / GridBased) just
    /// like the other assignment methods.
    /// </summary>
    private void AssignSpecificFloor(List<ColumnGroup> groups)
    {
        int n = _options.StartNumber;
        foreach (var group in groups)
        {
            group.NumberingStatus = GroupNumberingStatus.NotNumbered;
            group.ExistingMark    = null;
            group.AssignedMark    = _options.Mode == NumberingMode.GridBased
                ? BuildGridMark(group)
                : BuildMark(_options.Prefix, n++, _options);
        }
    }

    // ── Mode: AddOnly ────────────────────────────────────────────────────────

    private void AssignAddOnly(List<ColumnGroup> groups, MarkAnalysis analysis)
    {
        string prefix = _options.Prefix;
        int nextNumber = _options.StartNumber;

        if (analysis.HasExistingNumbering && analysis.DetectedPattern != null)
        {
            prefix     = analysis.DetectedPattern.Prefix;
            nextNumber = analysis.MaxExistingNumber + 1;
        }

        foreach (var group in groups)
        {
            if (group.NumberingStatus == GroupNumberingStatus.NotNumbered)
            {
                group.AssignedMark = BuildMark(prefix, nextNumber++, _options);
            }
            else
            {
                // Keep whatever mark exists
                group.AssignedMark = group.ExistingMark ?? "";
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildMark(string prefix, int number, NumberingOptions opts)
    {
        var numStr = opts.PadWithZeros
            ? number.ToString().PadLeft(opts.PadLength, '0')
            : number.ToString();
        return $"{prefix}{numStr}{opts.Suffix}";
    }

    private static string BuildGridMark(ColumnGroup group)
    {
        if (!string.IsNullOrWhiteSpace(group.GridRow) &&
            !string.IsNullOrWhiteSpace(group.GridColumn))
            return $"{group.GridRow}{group.GridColumn}";

        return ""; // Grid info not available
    }

    private List<ColumnGroup> Sort(List<ColumnGroup> groups)
    {
        // ── Multi-zone building-axis transform ───────────────────────────────
        // The engine has THREE coordinate frames it juggles per column:
        //
        //   • PROJECT frame  — the raw XY Revit returns.  Used for vertical
        //                      stacking, Revit identity, mark writing, and for
        //                      ORDERING clusters relative to each other (so
        //                      "top of the plan" stays top regardless of which
        //                      zone a row belongs to).
        //
        //   • ZONE frame     — project XY rotated by  −zone.RotationDegrees.
        //                      Used to CLUSTER columns into rows or columns
        //                      WITHIN a zone, so a tilted zone's row members
        //                      share the same rotated Y and an orthogonal
        //                      zone's row members share the same project Y.
        //
        //   • REFERENCE-anchor — when ReferenceFloorName is set the column's
        //                      position on that specific floor wins over the
        //                      group's average position.  Applied before the
        //                      zone rotation so the sort key always reflects
        //                      the floor the engineer is anchoring on.
        //
        // For single-zone projects the per-column rotation/zone lookups fall
        // back to the global BuildingRotationDegrees and zone 0 — matching the
        // legacy behaviour exactly.
        int Zone(ColumnGroup g)
        {
            if (_options.ColumnZoneByElementId != null && g.Columns.Count > 0)
            {
                if (_options.ColumnZoneByElementId.TryGetValue(g.Columns[0].ElementId, out var z))
                    return z;
            }
            return 0;
        }

        double Rotation(ColumnGroup g)
        {
            if (_options.ColumnRotationByElementId != null && g.Columns.Count > 0)
            {
                if (_options.ColumnRotationByElementId.TryGetValue(g.Columns[0].ElementId, out var r))
                    return r;
            }
            return _options.BuildingRotationDegrees;
        }

        (double X, double Y) ProjectXY(ColumnGroup g)
        {
            double x = g.X, y = g.Y;
            if (!string.IsNullOrWhiteSpace(_options.ReferenceFloorName))
            {
                var col = g.GetColumnAtFloor(_options.ReferenceFloorName);
                if (col != null) { x = col.X; y = col.Y; }
            }
            return (x, y);
        }

        (double X, double Y) ZoneXY(ColumnGroup g)
        {
            var (x, y) = ProjectXY(g);
            return RotationTransform.Rotate(x, y, -Rotation(g));
        }

        double SortX(ColumnGroup g)    => ZoneXY(g).X;
        double SortY(ColumnGroup g)    => ZoneXY(g).Y;
        double ProjectXVal(ColumnGroup g) => ProjectXY(g).X;
        double ProjectYVal(ColumnGroup g) => ProjectXY(g).Y;

        double tol = Math.Max(_options.RowToleranceFeet, 0.05); // at least ~1.5 cm

        // Cap the cluster span only where the local frame is essentially the
        // project frame (orthogonal-ish zone).  In tilted zones the rotation
        // may carry small residuals that would otherwise inflate local-axis
        // span and falsely split real lines.
        //
        // Threshold sits below BuildingZoneClassifier's 3° zone separation,
        // so a tilted zone (≥3°) never qualifies, but a "physically
        // orthogonal" zone whose grids drift by 1°–2° still gets the cap.
        bool IsOrthogonalZone(ColumnGroup g) => Math.Abs(Rotation(g)) < 2.0;

        List<ColumnGroup> sorted;

        // Cluster ordering rule (cross-zone safe):
        //   • Inside each cluster all groups share a zone → ordering by the
        //     ZONE-frame coordinate is meaningful and matches what the
        //     engineer sees inside that zone.
        //   • Between clusters from different zones we order by the PROJECT
        //     frame so "top of the plan" / "leftmost on the plan" still
        //     means what the eye expects across the whole drawing.
        switch (_options.SortBy)
        {
            // ── Row-by-row sorts ──────────────────────────────────────────
            case SortDirection.TopLeftToRight:
                sorted = ClusterByZoneAndAxis(groups, Zone, SortY, tol, IsOrthogonalZone)
                    .OrderByDescending(row => row.Average(ProjectYVal))
                    .SelectMany(row => row.OrderBy(SortX))
                    .ToList();
                break;

            case SortDirection.RightToLeft:
                sorted = ClusterByZoneAndAxis(groups, Zone, SortY, tol, IsOrthogonalZone)
                    .OrderByDescending(row => row.Average(ProjectYVal))
                    .SelectMany(row => row.OrderByDescending(SortX))
                    .ToList();
                break;

            case SortDirection.LeftToRight:
                sorted = ClusterByZoneAndAxis(groups, Zone, SortY, tol, IsOrthogonalZone)
                    .OrderBy(row => row.Average(ProjectYVal))
                    .SelectMany(row => row.OrderBy(SortX))
                    .ToList();
                break;

            case SortDirection.RightToLeftUp:
                sorted = ClusterByZoneAndAxis(groups, Zone, SortY, tol, IsOrthogonalZone)
                    .OrderBy(row => row.Average(ProjectYVal))
                    .SelectMany(row => row.OrderByDescending(SortX))
                    .ToList();
                break;

            // ── Column-by-column sorts ────────────────────────────────────
            case SortDirection.TopToBottom:
                sorted = ClusterByZoneAndAxis(groups, Zone, SortX, tol, IsOrthogonalZone)
                    .OrderBy(col => col.Average(ProjectXVal))
                    .SelectMany(col => col.OrderByDescending(SortY))
                    .ToList();
                break;

            case SortDirection.BottomToTop:
                sorted = ClusterByZoneAndAxis(groups, Zone, SortX, tol, IsOrthogonalZone)
                    .OrderBy(col => col.Average(ProjectXVal))
                    .SelectMany(col => col.OrderBy(SortY))
                    .ToList();
                break;

            case SortDirection.TopBottomLeft:
                sorted = ClusterByZoneAndAxis(groups, Zone, SortX, tol, IsOrthogonalZone)
                    .OrderByDescending(col => col.Average(ProjectXVal))
                    .SelectMany(col => col.OrderByDescending(SortY))
                    .ToList();
                break;

            case SortDirection.BottomTopLeft:
                sorted = ClusterByZoneAndAxis(groups, Zone, SortX, tol, IsOrthogonalZone)
                    .OrderByDescending(col => col.Average(ProjectXVal))
                    .SelectMany(col => col.OrderBy(SortY))
                    .ToList();
                break;

            default:
                sorted = ClusterByZoneAndAxis(groups, Zone, SortY, tol, IsOrthogonalZone)
                    .OrderByDescending(row => row.Average(ProjectYVal))
                    .SelectMany(row => row.OrderBy(SortX))
                    .ToList();
                break;
        }

        // When a reference floor is selected, re-order so that all groups that have
        // a column on that floor come first (preserving their relative sort order),
        // followed by all remaining groups.  This guarantees the reference floor
        // receives fully consecutive numbers with no gaps.
        //
        // Multi-story columns that span THROUGH the reference floor are recognised
        // via IsGroupOnReferenceFloor — see that method for the full predicate.
        if (!string.IsNullOrWhiteSpace(_options.ReferenceFloorName))
        {
            var onRef  = sorted.Where(IsGroupOnReferenceFloor).ToList();
            var offRef = sorted.Where(g => !IsGroupOnReferenceFloor(g)).ToList();
            sorted     = [.. onRef, .. offRef];
        }

        // If an anchor element is set, rotate the sorted list so the anchor's group is first.
        if (_options.StartAnchorElementId.HasValue)
        {
            var anchorIdx = sorted.FindIndex(g =>
                g.Columns.Any(c => c.ElementId == _options.StartAnchorElementId.Value));
            if (anchorIdx > 0)
                sorted = [.. sorted[anchorIdx..], .. sorted[..anchorIdx]];
        }

        return sorted;
    }

    /// <summary>
    /// Returns true when <paramref name="g"/> contains at least one column that
    /// Revit would show on the reference floor's plan view.
    ///
    /// A column is considered to be on the reference floor in any of these cases:
    ///   • Its <see cref="ColumnData.BaseLevelName"/> matches by name (column starts there).
    ///   • Its <see cref="ColumnData.TopLevelName"/> matches by name (column ends there).
    ///   • Its base or top elevation is within tolerance of the reference floor's elevation
    ///     (defends against Hebrew/RTL string-encoding mismatches between separate API calls).
    ///   • Its base elevation is BELOW and top elevation is ABOVE the reference floor —
    ///     the multi-story / span-through case.  These columns are physically present on
    ///     the reference floor and must be numbered together with it.
    ///
    /// This predicate mirrors the SpecificFloor filter applied in the Revit command, so
    /// reference-floor partitioning and specific-floor selection always agree on what
    /// "being on a floor" means.  Without span-through detection, picking a middle floor
    /// (e.g. Floor 5 in a 7-storey building) would mis-partition the column list: most
    /// continuous columns model their <see cref="ColumnData.BaseLevelName"/> as Floor 1,
    /// so almost no group would qualify and the reference floor would receive a sparse,
    /// non-sequential set of marks.
    /// </summary>
    private bool IsGroupOnReferenceFloor(ColumnGroup g)
    {
        var name = _options.ReferenceFloorName;
        var elev = _options.ReferenceFloorElevation;
        const double tol = 0.1;  // ~3 cm in Revit internal feet — same tolerance used by SpecificFloor

        foreach (var c in g.Columns)
        {
            // BiDi-tolerant name comparisons: Hebrew level names returned by
            // Revit may carry invisible RTL/LRM marks that break ordinary
            // ordinal-ignore-case equality.  See BidiText for the rationale.
            if (BidiText.EqualsIgnoreBidi(c.BaseLevelName, name)) return true;
            if (BidiText.EqualsIgnoreBidi(c.TopLevelName,  name)) return true;
            if (elev > 0 && Math.Abs(c.BaseLevelElevation - elev) <= tol) return true;
            if (elev > 0 && Math.Abs(c.TopLevelElevation  - elev) <= tol) return true;
            if (elev > 0 &&
                c.BaseLevelElevation < elev - tol &&
                c.TopLevelElevation  > elev + tol) return true;
        }
        return false;
    }

    /// <summary>
    /// Zone-aware adjacent-gap row/column clustering.  Two groups are placed
    /// in the same cluster only if (a) they belong to the same zone and (b)
    /// their adjacent (sorted) axis values lie within tolerance.
    ///
    /// Adjacent-gap is local and deterministic: each group only needs to be
    /// "close enough to its nearest sorted neighbour", which matches how
    /// engineers visually group columns into rows or columns on a plan.
    /// When all groups share zone 0 (single-zone or legacy code path) the
    /// zone gate is a no-op and the test reduces to a pure adjacent-gap.
    /// </summary>
    private static List<List<ColumnGroup>> ClusterByZoneAndAxis(
        IEnumerable<ColumnGroup>   groups,
        Func<ColumnGroup, int>     zone,
        Func<ColumnGroup, double>  axis,
        double                     tolerance,
        Func<ColumnGroup, bool>    capPredicate)
    {
        var ordered = groups
            .OrderBy(zone)
            .ThenBy(axis)
            .ToList();

        var clusters = new List<List<ColumnGroup>>();
        if (ordered.Count == 0) return clusters;

        var current        = new List<ColumnGroup> { ordered[0] };
        int currentZone    = zone(ordered[0]);
        double firstAxis   = axis(ordered[0]);
        bool currentCap    = capPredicate(ordered[0]);

        // Span cap prevents "chain bridging": a long sequence of small gaps
        // (each ≤ tolerance) that would otherwise merge visually distinct
        // rows whose endpoints are far apart.  A real row reflects column
        // centroids deviating up to ±tolerance from a grid line, so the
        // total span of one row cannot exceed 2·tolerance.
        //
        // The cap is only enforced where it is geometrically safe — namely
        // in orthogonal zones (capPredicate == true).  In tilted zones the
        // local axis frame is reconstructed from the detected rotation, and
        // any residual tilt error inflates the local-axis span of a single
        // physical line; capping there would falsely split that line into
        // sub-clusters which then re-interleave by ProjectX.
        double maxSpan = 2.0 * tolerance;

        for (int i = 1; i < ordered.Count; i++)
        {
            int    z    = zone(ordered[i]);
            double a    = axis(ordered[i]);
            double gap  = a - axis(ordered[i - 1]);
            double span = a - firstAxis;

            bool spanOk = !currentCap || span <= maxSpan;

            if (z == currentZone && gap <= tolerance && spanOk)
            {
                current.Add(ordered[i]);
            }
            else
            {
                clusters.Add(current);
                current     = [ordered[i]];
                currentZone = z;
                firstAxis   = a;
                currentCap  = capPredicate(ordered[i]);
            }
        }
        clusters.Add(current);
        return clusters;
    }
}

/// <summary>Result returned from AssignMarks.</summary>
public class NumberingResult
{
    public List<ColumnGroup> Groups   { get; set; } = [];
    public MarkAnalysis      Analysis { get; set; } = new();
}
