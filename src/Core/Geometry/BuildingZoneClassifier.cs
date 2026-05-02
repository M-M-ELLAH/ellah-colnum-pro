namespace EllahColNum.Core.Geometry;

/// <summary>
/// One axis-system zone of a building.  A "zone" groups every grid line that
/// shares an orientation (folded modulo 90°) and every column that visually
/// belongs to the cell pattern formed by those grids.
/// </summary>
/// <param name="Id">Stable index of the zone (0 = first orientation cluster).</param>
/// <param name="RotationDegrees">
/// Rotation of the zone's primary axis relative to project east, normalised to
/// <c>[-45°, +45°]</c>.  Sorting and clustering for columns belonging to this
/// zone are performed in coordinates rotated by  <c>−RotationDegrees</c>.
/// </param>
/// <param name="GridCount">Number of grid lines that defined this zone.</param>
/// <param name="ColumnCount">Number of columns assigned to this zone.</param>
public sealed record BuildingZone(int Id, double RotationDegrees, int GridCount, int ColumnCount);

/// <summary>
/// Output of <see cref="BuildingZoneClassifier"/>.  Lists the detected zones
/// and gives the engine a way to look up which zone any given column belongs
/// to via its Revit ElementId.
/// </summary>
/// <param name="Zones">All detected zones, ordered by Id.</param>
/// <param name="ColumnZoneByElementId">
/// Per-column assignment.  Missing keys mean the column was not seen by the
/// classifier (e.g. it has no nearby grid).  Callers should treat missing
/// columns as "zone 0 / no rotation" rather than failing.
/// </param>
public sealed record BuildingZoneMap(
    IReadOnlyList<BuildingZone>     Zones,
    IReadOnlyDictionary<long, int>  ColumnZoneByElementId);

/// <summary>
/// Splits a building's columns into zones based on the orientation of the
/// nearest Revit grid intersection.  This is the engineering-correct way to
/// number a building whose grid system is not uniform — e.g. a tower core
/// whose rooms tilt at +5° while the parking podium below stays orthogonal.
///
/// Algorithm
/// ─────────
/// 1.  Cluster grid lines by FOLDED orientation (angle modulo 90°).  Two
///     perpendicular grids (rows + columns of the same axis system) fold to
///     the same value, so a cluster represents an entire axis system, not a
///     single direction.
///
/// 2.  Inside each cluster, separate grids into "row" and "column" by their
///     un-folded angle: lines closer to the cluster's mean angle become rows,
///     lines closer to mean+90° become columns.  This split lets us measure
///     how WELL a column sits at a true grid INTERSECTION inside the cluster
///     instead of merely being close to one stray grid line.
///
/// 3.  For each column, compute a score per cluster:
///         score = 1 / (1 + d_nearest_row + d_nearest_col)
///     where the distances are perpendicular distances to the nearest row /
///     col grid line in that cluster.  A column at a real grid intersection
///     yields  d_row ≈ 0  and  d_col ≈ 0  → score near 1.  A column that is
///     accidentally close to a single long grid that passes through a
///     foreign zone gets penalised because the perpendicular grid in the
///     same cluster is far away.
///
/// 4.  Assign the column to the cluster with the highest score.
///
/// Behaviour for degenerate inputs (uniform building, no grids, very few
/// columns) is documented on <see cref="Classify"/>.  In all such cases the
/// classifier returns a single zone or an empty map so the rest of the
/// pipeline falls back to legacy single-axis behaviour.
/// </summary>
public static class BuildingZoneClassifier
{
    /// <summary>Two grids whose folded angle differs by ≤ this join the same cluster.</summary>
    public const double DefaultClusterToleranceDeg = 0.5;

    /// <summary>
    /// Two adjacent orientation clusters whose mean angles differ by less than
    /// this many degrees are MERGED into a single zone.  Modelling drift in
    /// real Revit projects routinely produces grids that are nominally
    /// orthogonal but actually sit at  −0.4°, +0.7°, +2.6° …  Without merging,
    /// every drifted grid would spawn a phantom zone and pull nearby orthogonal
    /// columns into a slightly-rotated frame, mangling the numbering.
    ///
    /// 3° is well above the noise floor of careful modelling and well below
    /// the 5–7° a real architect would use to mark an intentionally tilted
    /// sub-zone of a building.
    /// </summary>
    public const double DefaultMinZoneAngularSeparationDeg = 3.0;

    /// <summary>
    /// Orientation clusters with fewer grids than this are NOT promoted to
    /// zones.  Their grids stay in the input pool but the cluster itself is
    /// discarded so columns near a few stray grids fall back to whichever
    /// real zone is closest geometrically.  Real-world zones — even small
    /// ones — invariably ship with at least two row grids and two column
    /// grids = 4 lines.
    /// </summary>
    public const int DefaultMinGridsPerZone = 4;

    /// <summary>
    /// Below this perpendicular distance (project units) we treat a column as
    /// "on the grid".  Used only as a clamp for score saturation, never as a
    /// hard threshold.
    /// </summary>
    private const double DistanceFloor = 0.05;

    /// <summary>
    /// Multi-zone classifier entry point.
    ///
    /// <para>Returns <see cref="BuildingZoneMap.Zones"/> = empty when there are
    /// fewer than two grids or no columns.  Returns a single-zone map when all
    /// grids share one orientation cluster.  Otherwise returns one zone per
    /// orientation cluster with each column assigned to its best-scoring zone.
    /// </para>
    /// </summary>
    public static BuildingZoneMap Classify(
        IReadOnlyList<GridLine2D>                          gridLines,
        IReadOnlyList<(long ElementId, Point2D Position)>  columns,
        double                                             clusterToleranceDeg          = DefaultClusterToleranceDeg,
        double                                             minZoneAngularSeparationDeg  = DefaultMinZoneAngularSeparationDeg,
        int                                                minGridsPerZone              = DefaultMinGridsPerZone)
    {
        if (gridLines == null || columns == null || columns.Count == 0)
            return new BuildingZoneMap([], new Dictionary<long, int>());

        // ── Step 1: cluster grids by folded orientation ──────────────────────
        var orientationClusters = ClusterGridsByOrientation(gridLines, clusterToleranceDeg);

        // ── Step 1b: merge clusters whose orientations are too close to be ──
        //            distinct axis systems.  This is what keeps modelling
        //            drift (one stray grid at −2.8°) from masquerading as a
        //            second building zone.
        orientationClusters = MergeClustersByAngularProximity(
            orientationClusters, minZoneAngularSeparationDeg);

        // ── Step 1c: drop clusters that are too small to credibly define a ──
        //            zone.  Their grids effectively disappear from the
        //            classifier's view, so any column that happens to be near
        //            one of them now scores against the next-best zone.
        orientationClusters = orientationClusters
            .Where(c => c.Count >= minGridsPerZone)
            .ToList();

        if (orientationClusters.Count == 0)
            return new BuildingZoneMap([], new Dictionary<long, int>());

        // Single orientation → single zone covering every column.
        if (orientationClusters.Count == 1)
        {
            var single = BuildSingleZone(orientationClusters[0], columns.Count);
            var assignAll = columns.ToDictionary(c => c.ElementId, _ => single.Id);
            return new BuildingZoneMap([single], assignAll);
        }

        // ── Step 2: precompute zone metadata (rotation + row/col grid lists) ─
        var zoneInfo = orientationClusters
            .Select((cluster, i) => BuildZoneInfo(i, cluster))
            .ToList();

        // ── Step 3 + 4: score each column against every zone, assign best ────
        var assignment = new Dictionary<long, int>(columns.Count);
        var counts     = new int[zoneInfo.Count];

        foreach (var col in columns)
        {
            int    bestZone  = 0;
            double bestScore = double.NegativeInfinity;

            for (int z = 0; z < zoneInfo.Count; z++)
            {
                double score = ScoreColumnForZone(col.Position, zoneInfo[z]);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestZone  = z;
                }
            }

            assignment[col.ElementId] = bestZone;
            counts[bestZone]++;
        }

        var zones = zoneInfo
            .Select((z, i) => new BuildingZone(
                Id:              i,
                RotationDegrees: z.RotationDegrees,
                GridCount:       z.AllGrids.Count,
                ColumnCount:     counts[i]))
            .ToList();

        return new BuildingZoneMap(zones, assignment);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    /// <summary>
    /// Greedy 1-D clustering of grids by their folded angle, with the same
    /// 89°↔0° wrap-around as <c>BuildingAxisDetector.ClassifyGridAngles</c>.
    /// </summary>
    private static List<List<GridLine2D>> ClusterGridsByOrientation(
        IReadOnlyList<GridLine2D> grids,
        double clusterToleranceDeg)
    {
        var entries = grids
            .Select(g => (Grid: g, Folded: g.FoldedAngleDegrees()))
            .OrderBy(e => e.Folded)
            .ToList();

        if (entries.Count == 0) return [];

        var clusters = new List<List<GridLine2D>>();
        var current  = new List<GridLine2D> { entries[0].Grid };
        double currentMean = entries[0].Folded;

        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Folded - entries[i - 1].Folded <= clusterToleranceDeg)
            {
                current.Add(entries[i].Grid);
                currentMean = current.Average(g => g.FoldedAngleDegrees());
            }
            else
            {
                clusters.Add(current);
                current = [entries[i].Grid];
                currentMean = entries[i].Folded;
            }
        }
        clusters.Add(current);

        // Wrap-around 89° ↔ 0°
        if (clusters.Count >= 2)
        {
            var first = clusters[0];
            var last  = clusters[^1];
            double firstMin = first.Min(g => g.FoldedAngleDegrees());
            double lastMax  = last .Max(g => g.FoldedAngleDegrees());
            if ((90.0 - lastMax) + firstMin <= clusterToleranceDeg)
            {
                first.AddRange(last);
                clusters.RemoveAt(clusters.Count - 1);
            }
        }

        return clusters;
    }

    /// <summary>
    /// Iteratively merges any two clusters whose mean folded angles differ
    /// by less than <paramref name="minSeparationDeg"/>.  Operates on the
    /// circular angle space (mod 90°) so a 89.7° cluster and a 0.2° cluster
    /// are recognised as nearly-identical orientations.
    ///
    /// Order independence: the loop runs until no more merges are possible,
    /// so the final clustering is stable regardless of input ordering.
    /// </summary>
    private static List<List<GridLine2D>> MergeClustersByAngularProximity(
        List<List<GridLine2D>> clusters,
        double                 minSeparationDeg)
    {
        if (minSeparationDeg <= 0 || clusters.Count < 2)
            return clusters;

        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < clusters.Count && !merged; i++)
            {
                double a1 = CircularMeanFoldedAngle(clusters[i]);
                for (int j = i + 1; j < clusters.Count; j++)
                {
                    double a2   = CircularMeanFoldedAngle(clusters[j]);
                    double diff = Math.Abs(a1 - a2);
                    diff = Math.Min(diff, 90.0 - diff);   // wrap-around in [0°, 90°)

                    if (diff < minSeparationDeg)
                    {
                        clusters[i].AddRange(clusters[j]);
                        clusters.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }
        } while (merged);

        return clusters;
    }

    private static BuildingZone BuildSingleZone(List<GridLine2D> grids, int columnCount)
    {
        double meanFolded = CircularMeanFoldedAngle(grids);
        double normalised = NormaliseToMinus45Plus45(meanFolded);
        return new BuildingZone(0, normalised, grids.Count, columnCount);
    }

    /// <summary>
    /// Circular (vector-sum) mean of folded grid angles.  Required because
    /// folded angles live on a 90°-periodic circle — a naïve arithmetic mean
    /// of 0° and 88° is 44° (wrong), while the circular mean is 89° (correct,
    /// signing back to −1° once we normalise into [-45°, +45°]).
    ///
    /// Implementation: map [0°, 90°) → [0°, 360°) by doubling, sum unit
    /// vectors, take atan2, halve back into [0°, 90°).  Standard technique
    /// for averaging directional data.
    /// </summary>
    private static double CircularMeanFoldedAngle(IReadOnlyCollection<GridLine2D> grids)
    {
        if (grids.Count == 0) return 0;
        double sumX = 0, sumY = 0;
        foreach (var g in grids)
        {
            double rad = g.FoldedAngleDegrees() * 2 * Math.PI / 90.0;
            sumX += Math.Cos(rad);
            sumY += Math.Sin(rad);
        }
        double meanRad = Math.Atan2(sumY, sumX);
        if (meanRad < 0) meanRad += 2 * Math.PI;
        return meanRad * 90.0 / (2 * Math.PI);
    }

    /// <summary>Per-zone working data used during the scoring pass.</summary>
    private sealed record ZoneInfo(
        int                       Id,
        double                    RotationDegrees,
        IReadOnlyList<GridLine2D> AllGrids,
        IReadOnlyList<GridLine2D> RowGrids,
        IReadOnlyList<GridLine2D> ColGrids);

    private static ZoneInfo BuildZoneInfo(int id, List<GridLine2D> grids)
    {
        double meanFolded     = CircularMeanFoldedAngle(grids);
        double rowAngleDeg    = meanFolded;                 // row direction in [0°, 90°)
        double colAngleDeg    = (meanFolded + 90.0) % 180.0; // col direction in [0°, 180°)

        var rows = new List<GridLine2D>();
        var cols = new List<GridLine2D>();

        foreach (var g in grids)
        {
            double a = g.UnfoldedAngleDegrees();              // [0°, 180°)
            double dRow = AngularDistance(a, rowAngleDeg);
            double dCol = AngularDistance(a, colAngleDeg);
            if (dRow <= dCol) rows.Add(g);
            else              cols.Add(g);
        }

        return new ZoneInfo(
            Id:              id,
            RotationDegrees: NormaliseToMinus45Plus45(meanFolded),
            AllGrids:        grids,
            RowGrids:        rows,
            ColGrids:        cols);
    }

    /// <summary>Smallest separation between two angles modulo 180°.</summary>
    private static double AngularDistance(double a, double b)
    {
        double diff = Math.Abs(a - b) % 180.0;
        return Math.Min(diff, 180.0 - diff);
    }

    /// <summary>
    /// Score = 1 / (1 + d_row + d_col).  Columns at real grid intersections
    /// score near 1; columns that are merely close to a single stray grid
    /// score much less because the perpendicular direction's distance is
    /// large.  Zero grids in a direction degrades the score symmetrically.
    /// </summary>
    private static double ScoreColumnForZone(Point2D col, ZoneInfo zone)
    {
        double dRow = NearestPerpendicularDistance(col, zone.RowGrids);
        double dCol = NearestPerpendicularDistance(col, zone.ColGrids);

        // If a direction is missing from the zone, only penalise once with a
        // single distance.  This keeps degenerate "rows-only" zones usable.
        double sum;
        if (zone.RowGrids.Count == 0 && zone.ColGrids.Count > 0) sum = dCol;
        else if (zone.ColGrids.Count == 0 && zone.RowGrids.Count > 0) sum = dRow;
        else if (zone.RowGrids.Count == 0 && zone.ColGrids.Count == 0) sum = double.PositiveInfinity;
        else sum = dRow + dCol;

        sum = Math.Max(sum, DistanceFloor);
        return 1.0 / (1.0 + sum);
    }

    private static double NearestPerpendicularDistance(Point2D p, IReadOnlyList<GridLine2D> grids)
    {
        if (grids.Count == 0) return double.PositiveInfinity;
        double best = double.PositiveInfinity;
        for (int i = 0; i < grids.Count; i++)
        {
            double d = grids[i].DistanceTo(p);
            if (d < best) best = d;
        }
        return best;
    }

    private static double NormaliseToMinus45Plus45(double folded)
    {
        double r = folded;
        while (r >  45.0) r -= 90.0;
        while (r < -45.0) r += 90.0;
        return r;
    }
}
