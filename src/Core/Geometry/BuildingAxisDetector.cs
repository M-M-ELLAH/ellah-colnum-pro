namespace EllahColNum.Core.Geometry;

/// <summary>
/// Result of analysing a set of column positions for the building's primary axis.
/// </summary>
/// <param name="AngleDegrees">
/// Signed angle of the building's primary (long) axis relative to project east,
/// normalised to the range [-45°, +45°].  A perfectly orthogonal building yields 0°.
/// To align column coordinates with that axis you rotate them by <c>-AngleDegrees</c>.
/// </param>
/// <param name="Confidence">
/// 0 → no clear axis (square footprint or too few samples).
/// 1 → perfectly elongated point cloud (every point on a single line).
/// Computed as (λ₁ − λ₂) / λ₁ from the covariance eigenvalues.
/// Use this together with <see cref="AngleDegrees"/> to decide whether to apply
/// the rotation: low-confidence detections should be ignored.
/// </param>
/// <param name="SampleCount">Number of points consumed by the analysis.</param>
/// <param name="ConflictDetected">
/// Set to <c>true</c> by detectors that have positive evidence of a
/// MULTI-AXIS building (e.g. Revit Grids split between an orthogonal zone
/// and a tilted zone).  A conflict means "do NOT rotate, AND do NOT fall
/// back to a different detector" — the building is intentionally
/// non-uniform and any single rotation we apply would break one of the
/// zones.  Pure-confidence detectors (PCA) do not set this flag; they
/// simply return zero confidence when the answer is unclear.
/// </param>
public sealed record BuildingAxisAnalysis(
    double AngleDegrees,
    double Confidence,
    int    SampleCount,
    bool   ConflictDetected = false);

/// <summary>
/// Pure-PCA detector for the dominant horizontal axis of a building, given a set
/// of column XY positions.  Used by the numbering engine to align row/column
/// clustering with the architect's intended axes — required for slanted /
/// non-orthogonal grids where a project-axis sort produces visibly wrong results.
///
/// The detector is deliberately unaware of Revit: it operates on plain (X, Y)
/// tuples so it can be exercised by unit tests and reused for any future feature
/// that needs a building-axis frame (dimensions, schedules, exports, etc.).
///
/// Algorithm summary (Principal Component Analysis):
///   1. Centre the points on their centroid.
///   2. Compute the 2×2 covariance matrix S.
///   3. Solve for eigenvalues  λ₁ ≥ λ₂ ≥ 0  and the eigenvector for λ₁.
///   4. The dominant eigenvector points along the building's long axis;
///      its angle relative to project east is the building's rotation.
///   5. Normalise to [-45°, +45°] modulo 90° (since 90° rotation gives an
///      indistinguishable orthogonal building).
///   6. Confidence = (λ₁ − λ₂) / λ₁ — 0 for square footprints, 1 for perfectly
///      collinear points.
/// </summary>
public static class BuildingAxisDetector
{
    /// <summary>Minimum number of points before PCA is meaningful.</summary>
    private const int MinSampleCount = 3;

    /// <summary>
    /// Returns the dominant axis of <paramref name="points"/>.
    /// Always returns a non-null result.  Sample counts below
    /// <see cref="MinSampleCount"/> yield (0°, 0 confidence).
    /// </summary>
    public static BuildingAxisAnalysis Detect(IEnumerable<(double X, double Y)> points)
    {
        var pts = points.ToList();
        if (pts.Count < MinSampleCount)
            return new BuildingAxisAnalysis(0.0, 0.0, pts.Count);

        // Centroid
        double mx = 0, my = 0;
        foreach (var (x, y) in pts) { mx += x; my += y; }
        mx /= pts.Count; my /= pts.Count;

        // Covariance accumulators
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var (x, y) in pts)
        {
            double dx = x - mx;
            double dy = y - my;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }
        sxx /= pts.Count; syy /= pts.Count; sxy /= pts.Count;

        // Eigenvalues of [[sxx, sxy], [sxy, syy]]
        double trace = sxx + syy;
        double det   = sxx * syy - sxy * sxy;
        double disc  = Math.Sqrt(Math.Max(0, trace * trace - 4 * det));
        double l1    = (trace + disc) / 2;   // larger eigenvalue
        double l2    = (trace - disc) / 2;   // smaller eigenvalue

        // Dominant eigenvector — special-case the diagonal (no covariance) path
        // to avoid the degenerate atan2(0, 0).
        double angleDeg;
        if (Math.Abs(sxy) < 1e-12)
        {
            angleDeg = sxx >= syy ? 0.0 : 90.0;
        }
        else
        {
            // From the first row of (S − λ₁ I) v = 0:
            //     (sxx − λ₁)·vx + sxy·vy = 0  ⇒  vy/vx = (λ₁ − sxx) / sxy
            // Choosing vx = sxy gives the well-conditioned form  v = (sxy, λ₁ − sxx).
            double vx = sxy;
            double vy = l1 - sxx;
            angleDeg = Math.Atan2(vy, vx) * 180.0 / Math.PI;
        }

        // Normalise to [-45, +45] modulo 90° — a 90° rotation yields the same
        // orthogonal building, so any angle outside that range is equivalent.
        double normalised = angleDeg;
        while (normalised >  45.0) normalised -= 90.0;
        while (normalised < -45.0) normalised += 90.0;

        // Confidence: 1 when one direction completely dominates (collinear),
        // 0 when both directions are equal (square / circular cloud).
        double confidence = l1 > 1e-12
            ? Math.Clamp((l1 - l2) / l1, 0.0, 1.0)
            : 0.0;

        return new BuildingAxisAnalysis(normalised, confidence, pts.Count);
    }

    /// <summary>
    /// Cluster a set of grid-line angles (in degrees, already folded into
    /// <c>[0°, 90°)</c>) into orientation groups, then decide whether a single
    /// global rotation is justified.
    ///
    /// Pure logic, deliberately Revit-free, so the tilted/orthogonal/mixed
    /// classification can be unit-tested.  Used by
    /// <c>RevitGridAxisDetector</c> after it has extracted angles from Revit
    /// Grid elements.
    /// </summary>
    /// <param name="anglesInZeroNinety">
    /// Folded grid angles.  Caller is responsible for the ((deg % 90) + 90) % 90
    /// folding — this method assumes valid inputs in <c>[0, 90)</c>.
    /// </param>
    /// <param name="clusterToleranceDeg">Two angles within this many degrees join the same cluster.</param>
    /// <param name="minDominantShare">Minimum dominant-cluster share to apply rotation (e.g. 0.85).</param>
    /// <param name="significantSecondaryShare">
    /// Any secondary cluster reaching this share signals a multi-axis building
    /// and forces <see cref="BuildingAxisAnalysis.ConflictDetected"/> to true.
    /// </param>
    public static BuildingAxisAnalysis ClassifyGridAngles(
        IEnumerable<double> anglesInZeroNinety,
        double              clusterToleranceDeg       = 0.5,
        double              minDominantShare          = 0.85,
        double              significantSecondaryShare = 0.10)
    {
        var angles = anglesInZeroNinety.ToList();
        angles.Sort();
        if (angles.Count == 0)
            return new BuildingAxisAnalysis(0, 0, 0);

        // Greedy 1-D clustering by adjacent gap.
        var clusters = new List<List<double>>();
        var current  = new List<double> { angles[0] };
        for (int i = 1; i < angles.Count; i++)
        {
            if (angles[i] - angles[i - 1] <= clusterToleranceDeg)
                current.Add(angles[i]);
            else
            {
                clusters.Add(current);
                current = [angles[i]];
            }
        }
        clusters.Add(current);

        // Wrap-around: angles near 89° are physically the same orientation as
        // angles near 0° in the folded space — merge them.
        if (clusters.Count >= 2)
        {
            var first = clusters[0];
            var last  = clusters[^1];
            if ((90.0 - last[^1]) + first[0] <= clusterToleranceDeg)
            {
                var shifted = last.Select(a => a - 90.0).ToList();
                first.InsertRange(0, shifted);
                clusters.RemoveAt(clusters.Count - 1);
            }
        }

        var ordered  = clusters.OrderByDescending(c => c.Count).ToList();
        var dominant = ordered[0];
        double share = (double)dominant.Count / angles.Count;

        bool hasConflict = false;
        for (int i = 1; i < ordered.Count; i++)
        {
            if ((double)ordered[i].Count / angles.Count >= significantSecondaryShare)
            {
                hasConflict = true;
                break;
            }
        }

        if (hasConflict || share < minDominantShare)
            return new BuildingAxisAnalysis(0, 0, angles.Count, ConflictDetected: hasConflict);

        double meanAngle = dominant.Average();
        double normalised = meanAngle;
        while (normalised >  45.0) normalised -= 90.0;
        while (normalised < -45.0) normalised += 90.0;

        double tightnessPenalty = dominant.Count > 1
            ? Math.Min(1.0, clusterToleranceDeg / Math.Max(1e-6, dominant.Max() - dominant.Min()))
            : 1.0;
        double confidence = share * tightnessPenalty;

        return new BuildingAxisAnalysis(normalised, confidence, angles.Count);
    }
}
