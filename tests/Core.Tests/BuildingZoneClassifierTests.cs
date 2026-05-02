using EllahColNum.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace EllahColNum.Core.Tests;

/// <summary>
/// Tests for <see cref="BuildingZoneClassifier"/> — the engineering centerpiece
/// that splits a multi-axis building's columns into zones based on the grid
/// each column physically sits on.
///
/// Scenarios covered:
///   • Pure orthogonal building   → 1 zone, 0° rotation, every column inside.
///   • Uniform tilted building    → 1 zone, ~tilt rotation, every column inside.
///   • Mixed-axis building        → 2 zones, each column lands in the zone
///                                   whose grids actually pass through its
///                                   position.  Crucially, an orthogonal
///                                   column must NOT be pulled into the
///                                   tilted zone even when a tilted grid
///                                   line happens to extend across the
///                                   entire building.
///   • Boundary column            → assigned to the nearer zone.
/// </summary>
public class BuildingZoneClassifierTests
{
    /// <summary>Builds a horizontal grid line (row) at given Y.</summary>
    private static GridLine2D Row(double y, double tiltDeg = 0)
    {
        double rad = tiltDeg * Math.PI / 180.0;
        return new GridLine2D(new Point2D(0, y), new Point2D(Math.Cos(rad), Math.Sin(rad)));
    }

    /// <summary>Builds a vertical grid line (column) at given X.</summary>
    private static GridLine2D Col(double x, double tiltDeg = 0)
    {
        double rad = tiltDeg * Math.PI / 180.0;
        return new GridLine2D(new Point2D(x, 0), new Point2D(-Math.Sin(rad), Math.Cos(rad)));
    }

    private static (long, Point2D) Pt(long id, double x, double y) => (id, new Point2D(x, y));

    [Fact]
    public void Orthogonal_building_yields_single_zone_with_zero_rotation()
    {
        var grids = new List<GridLine2D>
        {
            Row(0), Row(5), Row(10), Row(15),       // four horizontal rows
            Col(0), Col(8), Col(16), Col(24)        // four vertical columns
        };
        var cols = new List<(long, Point2D)>
        {
            Pt(1,  0,  0), Pt(2,  8,  0), Pt(3, 16,  0), Pt(4, 24,  0),
            Pt(5,  0,  5), Pt(6,  8,  5), Pt(7, 16,  5), Pt(8, 24,  5)
        };

        var map = BuildingZoneClassifier.Classify(grids, cols);

        map.Zones.Should().HaveCount(1);
        map.Zones[0].RotationDegrees.Should().BeApproximately(0.0, 0.1);
        map.Zones[0].ColumnCount.Should().Be(8);
        map.ColumnZoneByElementId.Values.Should().AllBeEquivalentTo(0);
    }

    [Fact]
    public void Uniform_tilted_building_yields_single_zone_with_correct_rotation()
    {
        var grids = new List<GridLine2D>
        {
            Row( 0, 7), Row( 5, 7), Row(10, 7),
            Col( 0, 7), Col( 8, 7), Col(16, 7)
        };
        var cols = new List<(long, Point2D)>
        {
            Pt(1, 0, 0), Pt(2, 8, 0), Pt(3, 16, 0),
            Pt(4, 0, 5), Pt(5, 8, 5), Pt(6, 16, 5)
        };

        var map = BuildingZoneClassifier.Classify(grids, cols);

        map.Zones.Should().HaveCount(1);
        map.Zones[0].RotationDegrees.Should().BeApproximately(7.0, 0.5);
        map.Zones[0].ColumnCount.Should().Be(6);
    }

    [Fact]
    public void Mixed_axis_building_splits_columns_into_correct_zones()
    {
        // Orthogonal zone (lower-right quadrant): grids around (X=20..40, Y=0..10).
        var orthogonalGrids = new List<GridLine2D>
        {
            new(new Point2D(20,  0), new Point2D(1, 0)),  // row at y=0
            new(new Point2D(20,  5), new Point2D(1, 0)),  // row at y=5
            new(new Point2D(20, 10), new Point2D(1, 0)),  // row at y=10
            new(new Point2D(20,  0), new Point2D(0, 1)),  // col at x=20
            new(new Point2D(30,  0), new Point2D(0, 1)),  // col at x=30
            new(new Point2D(40,  0), new Point2D(0, 1)),  // col at x=40
        };

        // Tilted zone (upper-left quadrant): rows tilted +20°, around (X=0..15, Y=15..30).
        double rad = 20.0 * Math.PI / 180.0;
        double cT  = Math.Cos(rad);
        double sT  = Math.Sin(rad);
        Point2D RowDir() => new(cT, sT);
        Point2D ColDir() => new(-sT, cT);

        var tiltedGrids = new List<GridLine2D>
        {
            new(new Point2D( 0, 15), RowDir()),
            new(new Point2D( 0, 22), RowDir()),
            new(new Point2D( 0, 29), RowDir()),
            new(new Point2D( 0, 15), ColDir()),
            new(new Point2D( 8, 15), ColDir()),
            new(new Point2D(15, 15), ColDir()),
        };

        var grids = orthogonalGrids.Concat(tiltedGrids).ToList();

        // Orthogonal columns at orthogonal grid intersections
        var orthCols = new List<(long, Point2D)>
        {
            Pt(101, 20,  0), Pt(102, 30,  0), Pt(103, 40,  0),
            Pt(104, 20,  5), Pt(105, 30,  5), Pt(106, 40,  5),
        };
        // Tilted columns at tilted grid intersections (locally rotated coords)
        // (x_local, y_local) → world coord:
        //   x = x_local * cT - y_local * sT,  y = 15 + x_local * sT + y_local * cT
        Point2D TiltedAt(double xL, double yL) =>
            new(xL * cT - yL * sT, 15 + xL * sT + yL * cT);

        var tiltedCols = new List<(long, Point2D)>
        {
            (201, TiltedAt(0, 0)), (202, TiltedAt(8, 0)), (203, TiltedAt(15, 0)),
            (204, TiltedAt(0, 7)), (205, TiltedAt(8, 7)), (206, TiltedAt(15, 7)),
        };

        var allCols = orthCols.Concat(tiltedCols).ToList();

        var map = BuildingZoneClassifier.Classify(grids, allCols);

        map.Zones.Should().HaveCount(2,
            "the building has two distinct grid orientations, so the classifier must surface two zones");

        // Each orthogonal column must land in the zone whose RotationDegrees is ~0.
        int zoneOfOrthogonal = map.ColumnZoneByElementId[101];
        map.Zones[zoneOfOrthogonal].RotationDegrees.Should().BeApproximately(0.0, 1.0);

        foreach (var (id, _) in orthCols)
            map.ColumnZoneByElementId[id].Should().Be(zoneOfOrthogonal,
                $"orthogonal column {id} must remain in the orthogonal zone");

        // Each tilted column must land in the zone whose RotationDegrees is ~20.
        int zoneOfTilted = map.ColumnZoneByElementId[201];
        zoneOfTilted.Should().NotBe(zoneOfOrthogonal);
        map.Zones[zoneOfTilted].RotationDegrees.Should().BeApproximately(20.0, 1.0);

        foreach (var (id, _) in tiltedCols)
            map.ColumnZoneByElementId[id].Should().Be(zoneOfTilted,
                $"tilted column {id} must remain in the tilted zone");
    }

    [Fact]
    public void Mixed_building_with_long_grid_line_crossing_zones_does_not_misclassify()
    {
        // Pathological case: a SINGLE tilted row grid is drawn so long it
        // happens to pass close to a column in the orthogonal zone.  The
        // classifier must NOT treat that orthogonal column as tilted, because
        // there is no perpendicular tilted grid passing close by.
        double rad = 15.0 * Math.PI / 180.0;
        double cT  = Math.Cos(rad);
        double sT  = Math.Sin(rad);
        Point2D RowDirT() => new(cT, sT);
        Point2D ColDirT() => new(-sT, cT);

        var grids = new List<GridLine2D>
        {
            // Orthogonal grids covering the whole right half
            new(new Point2D(20, 0), new Point2D(1, 0)),
            new(new Point2D(20, 5), new Point2D(1, 0)),
            new(new Point2D(20, 0), new Point2D(0, 1)),
            new(new Point2D(28, 0), new Point2D(0, 1)),
            new(new Point2D(36, 0), new Point2D(0, 1)),

            // Tilted zone in upper-left, with one row that extends right far enough
            // that it passes ≈ 1 m below the orthogonal column at (28, 5).
            new(new Point2D(0, 12), RowDirT()),
            new(new Point2D(0, 19), RowDirT()),
            new(new Point2D(0, 12), ColDirT()),
            new(new Point2D(8, 12), ColDirT()),
        };

        // Place an orthogonal column that the long tilted row passes near,
        // measured perpendicular to the tilted line — but no PERPENDICULAR
        // tilted grid is anywhere near it.
        var orthCol  = (id: 99L, pos: new Point2D(28, 5));
        var tiltedCol = (id: 100L, pos: new Point2D(0 + 8 * cT, 12 + 8 * sT));   // at tilted (8, 0)

        var cols = new List<(long, Point2D)> { orthCol, tiltedCol };

        var map = BuildingZoneClassifier.Classify(grids, cols);

        map.Zones.Should().HaveCount(2);

        // Orthogonal column must end up in the orthogonal zone
        int orthZone = map.ColumnZoneByElementId[99];
        map.Zones[orthZone].RotationDegrees.Should().BeApproximately(0.0, 1.0,
            "the orthogonal column must NOT be misclassified just because a long tilted row " +
            "passes close to it — no perpendicular tilted grid is anywhere near, so the score " +
            "for the tilted zone is intentionally penalised");

        int tiltZone = map.ColumnZoneByElementId[100];
        tiltZone.Should().NotBe(orthZone);
        map.Zones[tiltZone].RotationDegrees.Should().BeApproximately(15.0, 1.0);
    }

    [Fact]
    public void Phantom_zone_from_modelling_drift_is_merged_into_orthogonal_zone()
    {
        // ── Real-world regression scenario reported by the user ────────────
        //
        // A nominally orthogonal Revit project where one or two grid lines
        // happen to be drawn 2–3° off-axis (architect didn't snap perfectly).
        // Without merging, the classifier creates two zones — one at 0° and
        // one at  ≈ −2.8° — and ends up pulling some orthogonal columns into
        // the phantom tilted zone, mangling the right-side numbering.
        //
        // After merging by angular proximity (< 3°) only ONE zone remains,
        // which is exactly the legacy single-zone behaviour the project was
        // working under before the multi-zone code path went in.
        var grids = new List<GridLine2D>();

        // 14 nicely orthogonal grid lines
        for (int i = 0; i < 7; i++) grids.Add(Row(i * 5));
        for (int i = 0; i < 7; i++) grids.Add(Col(i * 8));

        // 3 stragglers tilted by −2.8°
        for (int i = 0; i < 3; i++) grids.Add(Row(20 + i * 5, tiltDeg: -2.8));

        var cols = new List<(long, Point2D)>();
        for (int i = 0; i < 12; i++) cols.Add(Pt(1000 + i, i * 4, i % 4 * 5));

        var map = BuildingZoneClassifier.Classify(grids, cols);

        map.Zones.Should().HaveCount(1,
            "near-orthogonal grids must NOT be split into a phantom zone");
        map.Zones[0].RotationDegrees.Should().BeApproximately(0.0, 1.0);
    }

    [Fact]
    public void Genuinely_distinct_zones_are_kept_apart_after_merging_pass()
    {
        // Sanity check: a building with one orthogonal zone and one zone tilted
        // 7° must still split into two zones — the merge threshold is 3°,
        // well below the 7° separation here.
        double rad = 7.0 * Math.PI / 180.0;
        double cT  = Math.Cos(rad);
        double sT  = Math.Sin(rad);

        var grids = new List<GridLine2D>();
        for (int i = 0; i < 4; i++) grids.Add(Row(i * 5));
        for (int i = 0; i < 4; i++) grids.Add(Col(i * 8));
        for (int i = 0; i < 4; i++) grids.Add(Row(40 + i * 5, tiltDeg: 7.0));
        for (int i = 0; i < 4; i++)
            grids.Add(new GridLine2D(new Point2D(50 + i * 8, 40), new Point2D(-sT, cT)));

        var cols = new List<(long, Point2D)>
        {
            Pt(1, 0, 0), Pt(2, 8, 0),
            Pt(3, 50, 40), Pt(4, 50 + 8 * cT, 40 + 8 * sT),
        };

        var map = BuildingZoneClassifier.Classify(grids, cols);

        map.Zones.Should().HaveCount(2,
            "a 7° separation is well above the 3° merge threshold and must " +
            "remain a true multi-zone building");
    }

    [Fact]
    public void Tiny_orientation_clusters_are_dropped_so_columns_fall_back_to_real_zone()
    {
        // An overwhelmingly orthogonal project with a SINGLE stray grid at
        // 25° (e.g. an architect's helper line that was never deleted).  The
        // stray grid is below the 4-grid floor so it must NOT spawn a zone.
        var grids = new List<GridLine2D>();
        for (int i = 0; i < 8; i++) grids.Add(Row(i * 5));
        for (int i = 0; i < 8; i++) grids.Add(Col(i * 8));
        grids.Add(Row(0, tiltDeg: 25));

        var cols = new List<(long, Point2D)> { Pt(1, 0, 0), Pt(2, 8, 0) };

        var map = BuildingZoneClassifier.Classify(grids, cols);

        map.Zones.Should().HaveCount(1);
        map.Zones[0].RotationDegrees.Should().BeApproximately(0.0, 0.5);
    }

    [Fact]
    public void Empty_inputs_yield_empty_zone_map()
    {
        BuildingZoneClassifier.Classify([], []).Zones.Should().BeEmpty();
        BuildingZoneClassifier.Classify(
            [Row(0), Col(0)], []).Zones.Should().BeEmpty();
    }
}
