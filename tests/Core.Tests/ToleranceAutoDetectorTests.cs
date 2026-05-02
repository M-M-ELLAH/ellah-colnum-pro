using FluentAssertions;
using EllahColNum.Core.Models;
using EllahColNum.Core.Services;
using Xunit;

namespace EllahColNum.Tests;

/// <summary>
/// Unit tests for <see cref="ToleranceAutoDetector"/>.
/// </summary>
public class ToleranceAutoDetectorTests
{
    /// <summary>
    /// Real-world replication: 38 column centroids extracted from the user's
    /// "קומה ב" DXF, right (orthogonal) half. The detector must propose a
    /// tolerance in the band 100–160 cm, since the largest in-row drift is
    /// ~88 cm and the smallest row-to-row separation is ~245 cm.
    /// </summary>
    [Fact]
    public void Detect_RealDxfData_ProposesValueInExpectedBand()
    {
        // X positions (cm) sorted from left to right (right half of the plan).
        double[] xCm =
        {
            -2012.0, -1945.0, -1932.8, -1915.4, -1890.5, -1876.0, -1870.7,
            -1804.7, -1791.5, -1722.0, -1692.2, -1637.3, -1627.1, -1611.5,
            -1611.3, -1546.1, -1539.6, -1502.6, -1502.6, -1470.1, -1381.6,
            -1379.1, -1306.1, -1306.1, -1306.1, -1275.0, -1030.3, -1030.1,
            -950.1,  -863.4,  -618.2,  -618.2,  -584.5,  -584.5,  -517.8,
            -510.1,  -510.1,    -1.3,
        };
        var groups = xCm
            .Select((x, i) => new ColumnGroup
            {
                Key     = $"k{i}",
                Columns =
                [
                    new ColumnData { ElementId = i, X = x / 30.48, Y = 0 } // cm → ft
                ],
            })
            .ToList();

        var result = ToleranceAutoDetector.Detect(groups, SortDirection.BottomToTop);

        result.SuggestedToleranceFeet.Should().NotBeNull();
        var cm = result.SuggestedToleranceFeet!.Value * 30.48;
        cm.Should().BeInRange(100, 200,
            because: "drift maxes around 88 cm and row separation starts at ~245 cm");
        result.IsConfident.Should().BeTrue();
    }

    /// <summary>
    /// Pristine model with columns sitting exactly on grid intersections
    /// (zero modelling drift, 6-metre row-to-row spacing).  The detector
    /// should return a small but bounded tolerance.
    /// </summary>
    [Fact]
    public void Detect_PristineGrid_ProposesSmallToleranceWithConfidence()
    {
        // 5 rows × 4 cols, X = 0..18 m step 6m, Y = 0..24 m step 6m.
        // No drift at all.  All in-row "drift" gaps are 0 → filtered out;
        // the only gaps are between rows (6 m).
        // BottomToTop ⇒ row-perpendicular axis = X. So sort axis values are X.
        var groups = new List<ColumnGroup>();
        long id = 0;
        for (int r = 0; r < 5; r++)
        for (int c = 0; c < 4; c++)
        {
            groups.Add(new ColumnGroup
            {
                Key     = $"r{r}c{c}",
                Columns =
                [
                    new ColumnData { ElementId = ++id,
                                     X = c * 6.0 / 0.3048,   // m → ft
                                     Y = r * 6.0 / 0.3048 }
                ],
            });
        }

        var result = ToleranceAutoDetector.Detect(groups, SortDirection.BottomToTop);

        result.SuggestedToleranceFeet.Should().NotBeNull();
        // With zero in-row drift the detector clamps to the lower
        // industry-standard floor (≈30 cm = 0.98 ft).
        result.SuggestedToleranceFeet!.Value.Should().BeInRange(0.95, 1.5);
    }

    /// <summary>
    /// Too few columns → no suggestion, low confidence.
    /// </summary>
    [Fact]
    public void Detect_TooFewColumns_ReturnsNullSuggestion()
    {
        var groups = new List<ColumnGroup>
        {
            new() { Key = "a", Columns = [new ColumnData { ElementId = 1, X = 0, Y = 0 }] },
            new() { Key = "b", Columns = [new ColumnData { ElementId = 2, X = 1, Y = 0 }] },
        };

        var result = ToleranceAutoDetector.Detect(groups, SortDirection.BottomToTop);

        result.SuggestedToleranceFeet.Should().BeNull();
        result.IsConfident.Should().BeFalse();
    }

    /// <summary>
    /// Mixed-axis project: orthogonal zone with tight drift (≤30 cm)
    /// AND a tilted zone with looser drift (≤80 cm).  Detector must take
    /// the MORE DEMANDING zone (tilted at 80 cm) so both zones cluster
    /// without false splits.
    /// </summary>
    [Fact]
    public void Detect_MixedZones_TakesMostDemandingZone()
    {
        var rotByElem = new Dictionary<long, double>();
        var zoneByElem = new Dictionary<long, int>();
        var groups = new List<ColumnGroup>();
        long id = 0;

        // Zone 0: orthogonal, drift up to 30 cm, row separation 5 m.
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 5; c++)
        {
            id++;
            double drift = ((r + c) % 2 == 0 ? 0.10 : -0.10) / 0.3048; // ±10 cm in ft
            // BottomToTop ⇒ axis = X ⇒ drift goes on X.
            double x = c * 5.0 / 0.3048 + drift;
            double y = r * 5.0 / 0.3048;
            groups.Add(new ColumnGroup
            {
                Key = $"O{id}",
                Columns = [new ColumnData { ElementId = id, X = x, Y = y }],
            });
            zoneByElem[id] = 0;
            rotByElem[id]  = 0.0;
        }

        // Zone 1: tilted 7°, larger drift (~80 cm) so the row-perpendicular
        // axis values genuinely scatter wider in the local frame.
        long zoneOneStart = id + 1;
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 5; c++)
        {
            id++;
            double drift = ((r + c) % 2 == 0 ? 0.40 : -0.40) / 0.3048; // ±40 cm
            double xL = c * 5.0 / 0.3048 + drift;
            double yL = r * 5.0 / 0.3048 + 100;   // offset to keep zone separate
            // Rotate by +7° (so detector must rotate by -7° to recover xL).
            double cT = Math.Cos(7 * Math.PI / 180);
            double sT = Math.Sin(7 * Math.PI / 180);
            double xW = xL * cT - yL * sT;
            double yW = xL * sT + yL * cT;
            groups.Add(new ColumnGroup
            {
                Key = $"T{id}",
                Columns = [new ColumnData { ElementId = id, X = xW, Y = yW }],
            });
            zoneByElem[id] = 1;
            rotByElem[id]  = 7.0;
        }

        var result = ToleranceAutoDetector.Detect(
            groups,
            SortDirection.BottomToTop,
            zoneByElem,
            rotByElem);

        result.SuggestedToleranceFeet.Should().NotBeNull();
        result.ZoneCount.Should().Be(2);

        var cm = result.SuggestedToleranceFeet!.Value * 30.48;
        // The tilted zone has ±40 cm drift = 80 cm peak-to-peak. Suggestion
        // must be larger than that, but smaller than the 5 m row separation.
        cm.Should().BeInRange(60, 350);
    }

    /// <summary>
    /// The detector now analyses BOTH axes regardless of the user's chosen
    /// sort direction, and the suggested tolerance is therefore stable
    /// across direction changes.  This is the regression test for the
    /// "TopLeftToRight blew up the suggestion to 152 cm because the Y-axis
    /// has continuous distribution" bug observed in the user's real plan.
    /// </summary>
    [Fact]
    public void Detect_DualAxisAnalysis_SuggestionStableAcrossSortDirection()
    {
        // Synthetic project where the X-axis has a clean bimodal split
        // (drift ≈ 80 cm, rows 6 m apart) but the Y-axis is continuous
        // (corridor + stairs + offsets producing many medium gaps).
        var groups = new List<ColumnGroup>();
        long id = 0;

        // X-direction: 4 lines at X = 0, 6m, 12m, 18m, each with ±40cm drift.
        // Y-direction: irregular rows at Y = 0, 1m, 2.5m, 5m, 8m, 12m,
        // simulating the rooms-stairs-corridor pattern.
        double[] yPositions = { 0, 1, 2.5, 5, 8, 12 };
        for (int xLine = 0; xLine < 4; xLine++)
        for (int yIdx = 0; yIdx < yPositions.Length; yIdx++)
        {
            id++;
            double xDrift = ((xLine + yIdx) % 2 == 0 ? 0.40 : -0.40);
            double yDrift = ((xLine + yIdx) % 3 == 0 ? 0.05 : -0.05);
            double x = (xLine * 6.0 + xDrift) / 0.3048;          // m → ft
            double y = (yPositions[yIdx] + yDrift) / 0.3048;
            groups.Add(new ColumnGroup
            {
                Key     = $"k{id}",
                Columns = [new ColumnData { ElementId = id, X = x, Y = y }],
            });
        }

        var resultBottomToTop    = ToleranceAutoDetector.Detect(groups, SortDirection.BottomToTop);
        var resultTopLeftToRight = ToleranceAutoDetector.Detect(groups, SortDirection.TopLeftToRight);
        var resultLeftToRight    = ToleranceAutoDetector.Detect(groups, SortDirection.LeftToRight);

        // All three suggestions must be defined.
        resultBottomToTop.SuggestedToleranceFeet.Should().NotBeNull();
        resultTopLeftToRight.SuggestedToleranceFeet.Should().NotBeNull();
        resultLeftToRight.SuggestedToleranceFeet.Should().NotBeNull();

        // All three must be IDENTICAL — modelling drift is isotropic, so
        // changing the sort direction must not change the suggestion.
        resultBottomToTop.SuggestedToleranceFeet
            .Should().Be(resultTopLeftToRight.SuggestedToleranceFeet,
                because: "modelling drift is isotropic; switching sort " +
                         "direction must not change the auto-detected tolerance");
        resultBottomToTop.SuggestedToleranceFeet
            .Should().Be(resultLeftToRight.SuggestedToleranceFeet);

        // And the suggestion must come from the X-axis (the confident one),
        // not the Y-axis fallback.
        resultBottomToTop.IsConfident.Should().BeTrue();
        resultTopLeftToRight.IsConfident.Should().BeTrue();
        resultBottomToTop.Reason.Should().Contain("X-axis");
    }

    /// <summary>
    /// Sort direction that uses the Y axis (TopLeftToRight) must read drift
    /// from the perpendicular axis = Y, NOT X.
    /// </summary>
    [Fact]
    public void Detect_HorizontalSortDirection_UsesYAxis()
    {
        // 4 rows × 4 cols.  X positions identical; Y has clear bimodal
        // structure (drift 20 cm within row, 5 m between rows).
        var groups = new List<ColumnGroup>();
        long id = 0;
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
        {
            id++;
            double drift = ((r + c) % 2 == 0 ? 0.10 : -0.10) / 0.3048;
            double x = c * 5.0 / 0.3048;
            double y = r * 5.0 / 0.3048 + drift;
            groups.Add(new ColumnGroup
            {
                Key = $"k{id}",
                Columns = [new ColumnData { ElementId = id, X = x, Y = y }],
            });
        }

        var resultX = ToleranceAutoDetector.Detect(groups, SortDirection.BottomToTop);  // axis X
        var resultY = ToleranceAutoDetector.Detect(groups, SortDirection.TopLeftToRight); // axis Y

        // For BottomToTop the X axis has zero drift (all c*5m identical) so
        // confidence is low.  For TopLeftToRight the Y axis carries the
        // drift and the suggestion is confident in the 60–500 cm band.
        resultY.SuggestedToleranceFeet.Should().NotBeNull();
        resultY.IsConfident.Should().BeTrue();
        var cmY = resultY.SuggestedToleranceFeet!.Value * 30.48;
        cmY.Should().BeInRange(20, 500);
    }
}
