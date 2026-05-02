using EllahColNum.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace EllahColNum.Core.Tests;

/// <summary>
/// Unit tests for <see cref="BuildingAxisDetector"/>.  These cover the four
/// real-world scenarios that decide whether the numbering engine should switch
/// to building-frame coordinates:
///   1. Orthogonal building → angle ≈ 0°, do NOT rotate.
///   2. Slanted building → matching angle, high confidence, DO rotate.
///   3. Square / circular footprint → low confidence, do NOT rotate.
///   4. Pathological inputs (single point, two points, collinear) → safe defaults.
/// </summary>
public class BuildingAxisDetectorTests
{
    private const double AngleTolerance = 0.05;   // 0.05° — well below the engine's 1° gate

    /// <summary>Builds a tilted rectangular grid of (rows × cols) points, spacing 5m × 8m.</summary>
    private static List<(double X, double Y)> TiltedGrid(int rows, int cols, double tiltDeg)
    {
        var pts = new List<(double, double)>(rows * cols);
        double rad = tiltDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            double x = c * 8.0;
            double y = r * 5.0;
            pts.Add((x * cos - y * sin, x * sin + y * cos));
        }
        return pts;
    }

    [Fact]
    public void Orthogonal_building_returns_zero_angle()
    {
        var pts = TiltedGrid(rows: 3, cols: 6, tiltDeg: 0);

        var result = BuildingAxisDetector.Detect(pts);

        result.AngleDegrees.Should().BeApproximately(0.0, AngleTolerance);
        result.SampleCount.Should().Be(18);
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(-5.0)]
    [InlineData(12.5)]
    [InlineData(30.0)]
    [InlineData(-30.0)]
    public void Slanted_rectangular_building_recovers_tilt_angle(double tilt)
    {
        var pts = TiltedGrid(rows: 4, cols: 8, tiltDeg: tilt);

        var result = BuildingAxisDetector.Detect(pts);

        result.AngleDegrees.Should().BeApproximately(tilt, AngleTolerance);
        result.Confidence.Should().BeGreaterThan(0.3,
            "an elongated 4×8 footprint must give the engine enough certainty to apply rotation");
    }

    [Fact]
    public void Tilt_at_90_degrees_normalises_back_into_minus45_to_plus45()
    {
        var pts = TiltedGrid(rows: 4, cols: 8, tiltDeg: 90);

        var result = BuildingAxisDetector.Detect(pts);

        // 90° rotation produces an indistinguishable orthogonal building, so the
        // detector must collapse the angle into the canonical [-45°, +45°] band.
        result.AngleDegrees.Should().BeInRange(-45.0, 45.0);
        Math.Abs(result.AngleDegrees).Should().BeLessThan(AngleTolerance);
    }

    [Fact]
    public void Square_footprint_yields_low_confidence()
    {
        // 5×5 square grid with equal spacing — neither axis dominates.
        var pts = new List<(double, double)>();
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 5; c++)
                pts.Add((c * 6.0, r * 6.0));

        var result = BuildingAxisDetector.Detect(pts);

        result.Confidence.Should().BeLessThan(0.05,
            "a perfectly square cloud must NOT be reported as having a meaningful axis");
    }

    [Fact]
    public void Collinear_points_yield_unit_confidence()
    {
        var pts = new List<(double, double)>
        {
            (0, 0), (10, 0), (20, 0), (30, 0), (40, 0)
        };

        var result = BuildingAxisDetector.Detect(pts);

        result.AngleDegrees.Should().BeApproximately(0.0, AngleTolerance);
        result.Confidence.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public void Too_few_points_returns_zero_with_zero_confidence()
    {
        BuildingAxisDetector.Detect(Array.Empty<(double, double)>())
            .Should().BeEquivalentTo(new BuildingAxisAnalysis(0, 0, 0));

        BuildingAxisDetector.Detect(new[] { (1.0, 2.0), (3.0, 4.0) })
            .Should().BeEquivalentTo(new BuildingAxisAnalysis(0, 0, 2));
    }

    // ── ClassifyGridAngles — orthogonal vs uniform-tilted vs mixed ──────────
    //
    // These tests mirror the three real-world classes a Revit project can fall
    // into and lock in the conservative behaviour the user explicitly asked
    // for: rotation must ONLY be applied to a clearly uniform tilted building.

    [Fact]
    public void ClassifyGridAngles_OrthogonalProject_ReturnsZero()
    {
        // Six grids at 0° + four grids at 90° — folded to [0°, 90°) they all
        // share the SAME orientation cluster (90° wraps back to 0°).  This is
        // the textbook orthogonal building.
        var angles = new[] { 0.0, 0.05, 0.0, 89.95, 0.1, 89.9, 0.0, 89.95, 0.0, 0.0 };

        var result = BuildingAxisDetector.ClassifyGridAngles(angles);

        result.AngleDegrees.Should().BeApproximately(0.0, 0.1);
        result.ConflictDetected.Should().BeFalse();
    }

    [Fact]
    public void ClassifyGridAngles_UniformTiltedProject_ReturnsAngle()
    {
        // A uniformly-tilted building: rows at 5°, cols at 95°.  Both fold to 5°
        // in [0°, 90°), so every grid lands in the same orientation cluster.
        // This is what the caller sees from RevitGridAxisDetector after the
        // folding step.
        var angles = new[]
        {
            4.98, 5.0, 5.02, 5.0, 5.0, 4.99, 5.01, 5.0,
            5.0,  4.99, 5.01, 5.0, 4.98, 5.02
        };

        var result = BuildingAxisDetector.ClassifyGridAngles(angles);

        result.ConflictDetected.Should().BeFalse();
        result.AngleDegrees.Should().BeApproximately(5.0, 0.1);
    }

    [Fact]
    public void ClassifyGridAngles_MixedTiltedAndOrthogonalZones_FlagsConflictAndZeroAngle()
    {
        // The user's real building: top-left zone tilted 7°, bottom-right zone
        // orthogonal.  Roughly 9 tilted grids + 5 orthogonal grids → 64 % vs
        // 36 %.  We expect ConflictDetected = true and angle = 0 so that the
        // command keeps everything in project frame and the orthogonal zone
        // continues to number correctly.
        var angles = new[]
        {
            // Tilted zone — 7° in both directions (rows + cols folded the same)
            6.95, 7.0, 7.05, 7.0, 7.02, 6.98,
            // Tilted cols at 97° fold to 7°
            6.98, 7.01, 7.0,
            // Orthogonal zone — 0° rows + 90° cols (fold to 0°)
            0.05, 0.0, 89.95, 0.0, 89.95
        };

        var result = BuildingAxisDetector.ClassifyGridAngles(angles);

        result.ConflictDetected.Should().BeTrue(
            "a building whose grids are split between two orientations must be " +
            "treated as multi-axis — applying any single rotation would break " +
            "one of the zones");
        result.AngleDegrees.Should().Be(0.0,
            "no rotation should be applied for a multi-axis building");
    }

    [Fact]
    public void ClassifyGridAngles_NearUniform_WithSparseNoise_DoesNotFlagConflict()
    {
        // 18 grids at 5°, plus 1 stray grid at 30° (≈ 5 % share).
        // Expected: dominant share = 95 %, secondary share = 5 % < 10 %
        // threshold → no conflict, rotation applied.
        var angles = Enumerable.Repeat(5.0, 18).Append(30.0).ToList();

        var result = BuildingAxisDetector.ClassifyGridAngles(angles);

        result.ConflictDetected.Should().BeFalse();
        result.AngleDegrees.Should().BeApproximately(5.0, 0.1);
    }

    [Fact]
    public void Real_world_noise_does_not_swing_angle_significantly()
    {
        // Simulate columns built at slightly imperfect positions on a 7° tilted grid.
        var rng = new Random(42);
        var pts = TiltedGrid(rows: 3, cols: 7, tiltDeg: 7.0);
        for (int i = 0; i < pts.Count; i++)
        {
            var (x, y) = pts[i];
            pts[i] = (x + (rng.NextDouble() - 0.5) * 0.05,   // ±2.5cm jitter
                     y + (rng.NextDouble() - 0.5) * 0.05);
        }

        var result = BuildingAxisDetector.Detect(pts);

        result.AngleDegrees.Should().BeApproximately(7.0, 0.5,
            "construction tolerances must not throw the detector off by more than half a degree");
    }
}
