using Autodesk.Revit.DB;
using EllahColNum.Core.Geometry;

namespace EllahColNum.Revit.Helpers;

/// <summary>
/// Determines the building's primary horizontal axis from a Revit document.
///
/// Why this exists
/// ───────────────
/// The numbering engine needs to know whether the project's structural grid is
/// rotated relative to project east, so it can perform row/column clustering in
/// the building's own frame.  For orthogonal projects the answer is "no rotation"
/// and the detector must say so confidently — otherwise we risk regressing the
/// well-tested orthogonal code path.
///
/// Strategy (two layers, most reliable first)
/// ──────────────────────────────────────────
///   Layer 1 — Revit Grids:
///       The architect explicitly draws Grid lines along the building's intended
///       axes.  We collect every linear Grid, take its 2-D direction, fold it
///       into [0°, 90°) (because a 90° rotation gives an indistinguishable
///       orthogonal building), then group similar angles within a 0.5°
///       tolerance.  The largest group wins.  When it accounts for ≥ 60 % of
///       all grids and its mean angle is ≥ 1° from orthogonal, we use it.
///
///   Layer 2 — PCA on column positions  (caller's responsibility):
///       When grids are missing, sparse, or inconsistent, the caller falls back
///       to <see cref="BuildingAxisDetector"/> over the column XY positions.
///
/// Returning <c>(0°, 0 confidence)</c> tells the caller "no reliable axis from
/// grids — try the fallback".  This is intentional: the engine treats any value
/// below ~1° as no rotation, so unreliable detections are cost-free.
/// </summary>
public static class RevitGridAxisDetector
{
    /// <summary>Two grid angles within this many degrees are merged into one cluster.</summary>
    private const double AngleClusterToleranceDeg = 0.5;

    /// <summary>
    /// Required share of the total grids that must lie in the dominant
    /// orientation cluster before we trust a global rotation.  Set high on
    /// purpose: real-world buildings often combine an orthogonal zone with a
    /// tilted zone, and a single global rotation breaks one of them.  If the
    /// dominant cluster doesn't reach this share, we explicitly REFUSE to
    /// rotate and signal a conflict so the caller doesn't fall back to PCA.
    ///
    /// 0.85 was picked from real plans:
    ///   • Uniform-tilted building → 100 % → passes easily.
    ///   • Pure orthogonal building → 100 % at 0° → no rotation applied.
    ///   • Mixed building (e.g. 15 tilted grids + 4 orthogonal) → 79 % → fails
    ///     and triggers the conflict path → no rotation, exactly the legacy
    ///     behaviour that already worked for those projects.
    /// </summary>
    private const double MinDominantClusterShare = 0.85;

    /// <summary>
    /// Below this many grids overall we don't even try — a single grid line
    /// gives no information about the building's axis.
    /// </summary>
    private const int MinGridSampleCount = 4;

    /// <summary>
    /// A secondary cluster larger than this share is treated as positive
    /// evidence of an intentional multi-axis building.  In that case we set
    /// <see cref="BuildingAxisAnalysis.ConflictDetected"/> so the command
    /// suppresses both the global rotation AND the PCA fallback.
    /// </summary>
    private const double SignificantSecondaryShare = 0.10;

    /// <summary>
    /// Detect the building's axis from <paramref name="doc"/>'s Grid elements.
    /// Always returns a non-null result.  Confidence = 0 means "no reliable
    /// answer — caller should fall back to a different detector".
    /// </summary>
    public static BuildingAxisAnalysis Detect(Document doc)
    {
        if (doc == null) return new BuildingAxisAnalysis(0, 0, 0);

        // Collect every Grid whose curve is a straight line.  Curved/elliptical
        // grids do exist (rarely) but they don't have a single direction we can
        // average — skip them.
        var angles = new List<double>();
        var collector = new FilteredElementCollector(doc).OfClass(typeof(Grid));
        foreach (Grid g in collector)
        {
            if (g.Curve is not Line line) continue;
            var dir = line.Direction;
            // Project onto XY plane.  Vertical / near-vertical 3-D directions
            // are non-physical for plan-view grids and degrade the average.
            double horizMag = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            if (horizMag < 1e-9) continue;

            // Angle in degrees, folded into [0°, 90°) so that horizontal,
            // vertical, and antiparallel grids contribute to the same cluster.
            double deg = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
            deg = ((deg % 90.0) + 90.0) % 90.0;   // [0, 90)
            angles.Add(deg);
        }

        if (angles.Count < MinGridSampleCount)
            return new BuildingAxisAnalysis(0, 0, angles.Count);

        // Hand off the actual classification to the pure-C# helper in Core so
        // it stays unit-testable without a Revit document.
        return BuildingAxisDetector.ClassifyGridAngles(
            angles,
            clusterToleranceDeg:       AngleClusterToleranceDeg,
            minDominantShare:          MinDominantClusterShare,
            significantSecondaryShare: SignificantSecondaryShare);
    }
}
