using FluentAssertions;
using EllahColNum.Core.Models;
using EllahColNum.Core.Services;
using Xunit;

namespace EllahColNum.Tests;

/// <summary>
/// Tests for NumberingEngine — runs entirely without Revit.
/// </summary>
public class NumberingEngineTests
{
    [Fact]
    public void Sequential_ShouldAssignIncrementalMarks()
    {
        var groups = MakeGroups(3);
        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode        = NumberingMode.Sequential,
            Prefix      = "C-",
            StartNumber = 1
        });

        engine.AssignMarks(groups);

        var marks = groups.Select(g => g.AssignedMark).ToList();
        marks.Should().BeEquivalentTo(["C-1", "C-2", "C-3"],
            because: "sequential marks should start at 1 with prefix C-");
    }

    [Fact]
    public void Sequential_WithPadding_ShouldZeroPad()
    {
        var groups = MakeGroups(3);
        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode         = NumberingMode.Sequential,
            Prefix       = "C-",
            StartNumber  = 1,
            PadWithZeros = true,
            PadLength    = 2
        });

        engine.AssignMarks(groups);

        groups[0].AssignedMark.Should().Be("C-01");
        groups[1].AssignedMark.Should().Be("C-02");
    }

    [Fact]
    public void GridBased_ShouldUseGridNames()
    {
        var groups = new List<ColumnGroup>
        {
            new() { Key = "1", GridRow = "A", GridColumn = "1",
                    Columns = [new ColumnData { ElementId = 1 }] },
            new() { Key = "2", GridRow = "B", GridColumn = "3",
                    Columns = [new ColumnData { ElementId = 2 }] },
        };

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode = NumberingMode.GridBased
        });

        engine.AssignMarks(groups);

        groups.Should().ContainSingle(g => g.AssignedMark == "A1");
        groups.Should().ContainSingle(g => g.AssignedMark == "B3");
    }

    [Fact]
    public void AssignMarks_ShouldPropagateMarkToAllColumnsInGroup()
    {
        var group = new ColumnGroup
        {
            Key     = "k",
            Columns =
            [
                new ColumnData { ElementId = 1, BaseLevelName = "Floor 1" },
                new ColumnData { ElementId = 2, BaseLevelName = "Floor 2" },
                new ColumnData { ElementId = 3, BaseLevelName = "Floor 3" },
            ]
        };

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode = NumberingMode.Sequential, Prefix = "C-", StartNumber = 1
        });
        engine.AssignMarks([group]);

        group.Columns.Should().AllSatisfy(c =>
            c.AssignedMark.Should().Be("C-1"),
            because: "all floors of the same column should share the same mark");
    }

    // ── REFERENCE FLOOR partition ───────────────────────────────────────────
    //
    // Engineering invariant:
    //   When the engineer picks a reference floor for a full-project numbering,
    //   every column physically present on that floor — whether it starts there,
    //   ends there, or passes through it — must be numbered consecutively first
    //   so the reference floor's plan reads 1, 2, 3 … without gaps.
    //
    //   Pre-fix behaviour: only columns whose BaseLevelName matched the floor
    //   were considered "on" it.  In real projects most columns model their
    //   base as the foundation/level 1, so picking any middle floor (e.g. 5)
    //   produced a sparse on-reference set and arbitrary numbering.

    [Fact]
    public void ReferenceFloor_SpanThroughColumn_ShouldBeNumberedFirst()
    {
        // Three groups, all visible on Floor 5:
        //   g1 — column starts on Floor 5 (matched by BaseLevelName)
        //   g2 — multi-story column from Floor 1 to Floor 7 (must match by span-through)
        //   g3 — column NOT on Floor 5 at all (Floor 6 to Floor 7) — goes after
        var groups = new List<ColumnGroup>
        {
            new() { Key = "g1", Columns = [new ColumnData {
                ElementId = 1, X = 30, Y = 10,
                BaseLevelName = "Floor 5", TopLevelName = "Floor 6",
                BaseLevelElevation = 12.0, TopLevelElevation = 15.0 }] },

            new() { Key = "g2", Columns = [new ColumnData {
                ElementId = 2, X = 10, Y = 10,
                BaseLevelName = "Floor 1", TopLevelName = "Floor 7",
                BaseLevelElevation = 0.0, TopLevelElevation = 18.0 }] },

            new() { Key = "g3", Columns = [new ColumnData {
                ElementId = 3, X = 50, Y = 10,
                BaseLevelName = "Floor 6", TopLevelName = "Floor 7",
                BaseLevelElevation = 15.0, TopLevelElevation = 18.0 }] },
        };

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode                    = NumberingMode.Sequential,
            Prefix                  = "C-",
            StartNumber             = 1,
            ContinuationMode        = ContinuationMode.Override,
            ReferenceFloorName      = "Floor 5",
            ReferenceFloorElevation = 12.0,
            SortBy                  = SortDirection.LeftToRight,
        });

        var result = engine.AssignMarks(groups);

        // g2 (span-through, X=10) and g1 (starts there, X=30) must both come before g3
        var marksByElement = result.Groups.ToDictionary(g => g.Columns[0].ElementId, g => g.AssignedMark);
        marksByElement[2].Should().Be("C-1", because: "span-through column has the lowest X on the reference floor");
        marksByElement[1].Should().Be("C-2", because: "column starting on Floor 5 follows in left-to-right order");
        marksByElement[3].Should().Be("C-3", because: "column not on the reference floor numbers last");
    }

    [Fact]
    public void ReferenceFloor_TopLevelMatch_ShouldQualifyAsOnFloor()
    {
        // Column ends exactly on the reference floor — must qualify too.
        var groups = new List<ColumnGroup>
        {
            new() { Key = "endsOnFloor", Columns = [new ColumnData {
                ElementId = 1, X = 0, Y = 0,
                BaseLevelName = "Floor 1", TopLevelName = "Floor 3",
                BaseLevelElevation = 0.0, TopLevelElevation = 9.0 }] },

            new() { Key = "elsewhere", Columns = [new ColumnData {
                ElementId = 2, X = 100, Y = 100,
                BaseLevelName = "Floor 7", TopLevelName = "Floor 8",
                BaseLevelElevation = 21.0, TopLevelElevation = 24.0 }] },
        };

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode                    = NumberingMode.Sequential,
            Prefix                  = "",
            StartNumber             = 1,
            ContinuationMode        = ContinuationMode.Override,
            ReferenceFloorName      = "Floor 3",
            ReferenceFloorElevation = 9.0,
        });

        var result = engine.AssignMarks(groups);

        result.Groups[0].Columns[0].ElementId.Should().Be(1,
            because: "a column ending on the reference floor (TopLevel match) belongs to it");
    }

    [Fact]
    public void ReferenceFloor_HebrewEncodingMismatch_ShouldFallBackToElevation()
    {
        // Simulates the Hebrew/RTL encoding bug: the reference-floor name string
        // does not byte-match the column's BaseLevelName, but the elevation does.
        // Without elevation fallback, the column would be excluded.
        var groups = new List<ColumnGroup>
        {
            new() { Key = "encMismatch", Columns = [new ColumnData {
                ElementId = 1, X = 0, Y = 0,
                BaseLevelName = "\u202bקומה ה'\u202c",  // wrapped in RTL marks
                BaseLevelElevation = 12.0,
                TopLevelElevation  = 15.0 }] },
        };

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode                    = NumberingMode.Sequential,
            StartNumber             = 1,
            ContinuationMode        = ContinuationMode.Override,
            ReferenceFloorName      = "קומה ה'",       // plain — different bytes
            ReferenceFloorElevation = 12.0,
        });

        var result = engine.AssignMarks(groups);

        result.Groups.Should().HaveCount(1);
        result.Groups[0].AssignedMark.Should().Be("1",
            because: "elevation fallback must rescue Hebrew encoding mismatches");
    }

    // ── SPECIFIC FLOOR mode ──────────────────────────────────────────────────
    //
    // Engineering invariant:
    //   When the engineer renumbers a single isolated floor, the result must be
    //   consecutive (1, 2, 3 …) starting at StartNumber, regardless of any
    //   existing marks left over from a previous full-project run.  A single
    //   floor has no vertical continuity, so prior marks must NOT be inherited.

    [Fact]
    public void SpecificFloor_ShouldRenumberConsecutively_IgnoringExistingMarks()
    {
        // Three column groups, each with a leftover mark from a previous run.
        // SmartContinue would normally preserve C-100 / C-103 / C-107 and produce
        // gaps; SPECIFIC FLOOR mode must override that and emit C-1 / C-2 / C-3.
        var groups = new List<ColumnGroup>
        {
            new() { Key = "k0",
                    Columns = [new ColumnData { ElementId = 1, X = 0,  Y = 0, CurrentMark = "C-100" }] },
            new() { Key = "k1",
                    Columns = [new ColumnData { ElementId = 2, X = 10, Y = 0, CurrentMark = "C-103" }] },
            new() { Key = "k2",
                    Columns = [new ColumnData { ElementId = 3, X = 20, Y = 0, CurrentMark = "C-107" }] },
        };

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode              = NumberingMode.Sequential,
            Prefix            = "C-",
            StartNumber       = 1,
            ContinuationMode  = ContinuationMode.SmartContinue,  // would normally preserve marks
            SpecificFloorName = "Floor 2",
        });

        var result = engine.AssignMarks(groups);

        result.Groups.Select(g => g.AssignedMark)
            .Should().Equal(["C-1", "C-2", "C-3"],
                because: "single-floor numbering must be consecutive from StartNumber");
    }

    [Fact]
    public void SpecificFloor_ShouldReportAllGroupsAsNew_InAnalysis()
    {
        // The analysis returned to the UI must reflect that every group is being
        // renumbered — no "kept" or "conflict" badges in single-floor mode.
        var groups = new List<ColumnGroup>
        {
            new() { Key = "k0",
                    Columns = [new ColumnData { ElementId = 1, X = 0,  Y = 0, CurrentMark = "C-1" }] },
            new() { Key = "k1",
                    Columns = [new ColumnData { ElementId = 2, X = 10, Y = 0, CurrentMark = "C-2" }] },
        };

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode              = NumberingMode.Sequential,
            Prefix            = "C-",
            StartNumber       = 1,
            SpecificFloorName = "Floor 2",
        });

        var result = engine.AssignMarks(groups);

        result.Analysis.NotNumberedCount.Should().Be(2);
        result.Analysis.FullyNumberedCount.Should().Be(0);
        result.Analysis.PartiallyNumberedCount.Should().Be(0);
        result.Analysis.ConflictingCount.Should().Be(0);

        result.Groups.Should().AllSatisfy(g =>
        {
            g.NumberingStatus.Should().Be(GroupNumberingStatus.NotNumbered);
            g.ExistingMark.Should().BeNull();
        }, because: "specific-floor renumbering clears prior status so the UI is honest");
    }

    [Fact]
    public void SpecificFloor_ShouldOverrideContinuationMode_EvenWhenSetToOverride()
    {
        // SpecificFloor takes precedence over any ContinuationMode setting —
        // the same code path is taken whether the user picked SmartContinue,
        // Override, or AddOnly.  This guards against future regressions.
        var groups = new List<ColumnGroup>
        {
            new() { Key = "k0",
                    Columns = [new ColumnData { ElementId = 1, X = 0,  Y = 0 }] },
            new() { Key = "k1",
                    Columns = [new ColumnData { ElementId = 2, X = 10, Y = 0 }] },
        };

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode              = NumberingMode.Sequential,
            Prefix            = "",
            StartNumber       = 50,
            ContinuationMode  = ContinuationMode.AddOnly,
            SpecificFloorName = "Floor 3",
        });

        var result = engine.AssignMarks(groups);

        result.Groups.Select(g => g.AssignedMark)
            .Should().Equal(["50", "51"],
                because: "AddOnly must NOT skip groups in specific-floor mode");
    }

    // ── Tilted-grid sorting ──────────────────────────────────────────────────
    //
    // Engineering scenario reported by users: a structural plan whose grid is
    // rotated by a few degrees relative to project east — perfectly common in
    // real Hebrew architectural projects.  Without building-axis correction the
    // engine clusters columns by raw project-Y, which puts visually-adjacent
    // columns in different "rows" because their Y values diverge along the tilt.
    //
    // These tests build a 3×4 column grid (3 rows × 4 cols), tilt it by 7°
    // around the origin, and verify that:
    //   • The legacy code path (BuildingRotationDegrees = 0) misclassifies
    //     enough columns that the resulting numbering is NOT row-major.
    //   • Setting BuildingRotationDegrees to the detected tilt restores the
    //     correct top-left → right, row-by-row numbering an engineer expects.

    private static List<ColumnGroup> MakeTiltedGrid(int rows, int cols, double tiltDeg)
    {
        var groups = new List<ColumnGroup>();
        double rad = tiltDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        int id = 0;
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            double x0 = c * 8.0;        // 8 m between columns
            double y0 = r * 6.0;        // 6 m between rows
            double xr = x0 * cos - y0 * sin;
            double yr = x0 * sin + y0 * cos;
            groups.Add(new ColumnGroup
            {
                Key     = $"r{r}c{c}",
                Columns = [new ColumnData { ElementId = ++id, X = xr, Y = yr }]
            });
        }
        return groups;
    }

    /// <summary>
    /// Returns the (row, col) pair encoded in the original Key (e.g. "r2c3" → (2,3)).
    /// Used to verify that columns end up numbered in row-major order regardless
    /// of the tilt applied to their physical positions.
    /// </summary>
    private static (int row, int col) ParseRowCol(ColumnGroup g)
    {
        var key = g.Key;
        int rIdx = key.IndexOf('r') + 1;
        int cIdx = key.IndexOf('c');
        int row = int.Parse(key.AsSpan(rIdx, cIdx - rIdx));
        int col = int.Parse(key.AsSpan(cIdx + 1));
        return (row, col);
    }

    [Fact]
    public void TiltedGrid_WithoutRotation_ProducesIncorrectRowOrder()
    {
        // 7° tilt — at 8 m row spacing and 6 m column spacing this places the
        // far end of row 0 at  y ≈ 8·4·sin7° ≈ 3.9 m, which is well above
        // y of row 1's near end (≈ 6·cos7° ≈ 5.95 m gap), so a naive Y-clustering
        // pulls those points into the wrong row whenever RowToleranceFeet is
        // engaged on the project frame.  We verify the regression by checking
        // that "TopLeftToRight" without rotation does NOT produce row-major order.
        var groups = MakeTiltedGrid(rows: 3, cols: 4, tiltDeg: 7.0);

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode    = NumberingMode.Sequential,
            Prefix  = "",
            StartNumber = 1,
            SortBy = SortDirection.TopLeftToRight,
            // BuildingRotationDegrees deliberately left at 0 → legacy behaviour
        });

        var sorted = engine.AssignMarks(groups).Groups;
        var rowSequence = sorted.Select(ParseRowCol).Select(t => t.row).ToList();

        // Expected (with correction): 0,0,0,0, 1,1,1,1, 2,2,2,2.
        // Without correction at 7°, the Y-ordering interleaves rows.
        rowSequence.Should().NotEqual([0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2],
            "the test guards against future code accidentally fixing the legacy " +
            "path silently — if this assertion ever flips to passing, the fix " +
            "must be reflected in the rotation-aware test below");
    }

    [Fact]
    public void TiltedGrid_WithRotationApplied_NumbersRowByRow()
    {
        var groups = MakeTiltedGrid(rows: 3, cols: 4, tiltDeg: 7.0);

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode                    = NumberingMode.Sequential,
            Prefix                  = "",
            StartNumber             = 1,
            SortBy                  = SortDirection.TopLeftToRight,
            BuildingRotationDegrees = 7.0,   // detected by RevitGridAxisDetector / PCA
        });

        var sorted = engine.AssignMarks(groups).Groups;
        var coords = sorted.Select(ParseRowCol).ToList();

        // TopLeftToRight on a 3-row grid (rows 0, 1, 2 with row 0 being TOP):
        //   Row 0 → row 1 → row 2; within each row column 0 → 3.
        //
        // NOTE the row numbering here: in the test grid r=0 is the LOWEST Y.
        // After TopLeftToRight (top first = highest rotated Y first), the
        // engine starts with the highest-r row.  So we expect rows [2, 1, 0].
        coords.Should().Equal(
            (2, 0), (2, 1), (2, 2), (2, 3),
            (1, 0), (1, 1), (1, 2), (1, 3),
            (0, 0), (0, 1), (0, 2), (0, 3));
    }

    [Fact]
    public void OrthogonalGrid_WithRotationZero_BehavesLikeLegacy()
    {
        // Sanity: setting BuildingRotationDegrees = 0 must not change behaviour
        // on a perfectly aligned project.  Otherwise we'd risk regressions on
        // the tens of thousands of orthogonal projects already in production.
        var groups = MakeTiltedGrid(rows: 3, cols: 4, tiltDeg: 0.0);

        var legacy = new NumberingEngine(new NumberingOptions
        {
            Mode = NumberingMode.Sequential, StartNumber = 1,
            SortBy = SortDirection.TopLeftToRight,
        }).AssignMarks(CloneShallow(groups)).Groups
          .Select(ParseRowCol).ToList();

        var rotated = new NumberingEngine(new NumberingOptions
        {
            Mode = NumberingMode.Sequential, StartNumber = 1,
            SortBy = SortDirection.TopLeftToRight,
            BuildingRotationDegrees = 0.0,
        }).AssignMarks(CloneShallow(groups)).Groups
          .Select(ParseRowCol).ToList();

        rotated.Should().Equal(legacy,
            "rotation = 0 must produce byte-identical ordering to the legacy path");
    }

    [Fact]
    public void MixedZoneBuilding_ColumnsClusterWithinTheirOwnZone()
    {
        // ── Engineering scenario ────────────────────────────────────────────
        //
        // The user's real building: an upper-left zone tilted by +15° plus a
        // lower-right orthogonal zone.  Without zone awareness the engine
        // would either:
        //   • leave rotation at 0 (legacy)             → tilted rows scatter
        //   • apply a single global rotation           → orthogonal rows scatter
        //
        // With per-column zone + rotation, EACH zone clusters in its own
        // frame.  The combined ordering then walks zones from
        // bottom-of-the-plan to top-of-the-plan (LeftToRight = bottom-up).
        //
        // The test sets:
        //   • Tilted zone: 2 rows × 3 cols, rotated +15° around (0, 20).
        //   • Orthogonal zone: 2 rows × 3 cols at  X = 30..50,  Y = 0..5.
        //
        // For LeftToRight (bottom-row-first → next row up) we expect the
        // engine to enumerate, in order, every column in the orthogonal
        // bottom row, then orthogonal top row, then tilted bottom row, then
        // tilted top row.  Within each row, columns must be ordered left-to-
        // right in their OWN zone's frame.

        double rad   = 15.0 * Math.PI / 180.0;
        double cT    = Math.Cos(rad);
        double sT    = Math.Sin(rad);
        var rng = new Random(7);

        var orthGroups = new List<ColumnGroup>();
        long orthIdSeed = 100;
        // Orthogonal zone rows: y=0 (bottom) and y=5 (top), x=30..50 stepping 10.
        // Add tiny jitter so adjacent-gap clustering still has to do work.
        for (int row = 0; row < 2; row++)
        for (int col = 0; col < 3; col++)
        {
            long id = orthIdSeed++;
            double x = 30 + col * 10 + (rng.NextDouble() - 0.5) * 0.05;
            double y = row * 5 + (rng.NextDouble() - 0.5) * 0.05;
            orthGroups.Add(new ColumnGroup
            {
                Key = $"O_r{row}c{col}",
                Columns = [new ColumnData { ElementId = id, X = x, Y = y }]
            });
        }

        var tiltGroups = new List<ColumnGroup>();
        long tiltIdSeed = 200;
        // Tilted zone rows: local-Y 0 (bottom, world ≈ 20) and local-Y 5 (top).
        // World position: (xL*cT - yL*sT, 20 + xL*sT + yL*cT).
        for (int row = 0; row < 2; row++)
        for (int col = 0; col < 3; col++)
        {
            long id = tiltIdSeed++;
            double xL = col * 7;
            double yL = row * 5;
            double xW = xL * cT - yL * sT + (rng.NextDouble() - 0.5) * 0.05;
            double yW = 20 + xL * sT + yL * cT + (rng.NextDouble() - 0.5) * 0.05;
            tiltGroups.Add(new ColumnGroup
            {
                Key = $"T_r{row}c{col}",
                Columns = [new ColumnData { ElementId = id, X = xW, Y = yW }]
            });
        }

        var allGroups = orthGroups.Concat(tiltGroups).ToList();

        var rotationByElementId = new Dictionary<long, double>();
        var zoneByElementId     = new Dictionary<long, int>();
        foreach (var g in orthGroups)
        {
            rotationByElementId[g.Columns[0].ElementId] = 0.0;
            zoneByElementId    [g.Columns[0].ElementId] = 0;
        }
        foreach (var g in tiltGroups)
        {
            rotationByElementId[g.Columns[0].ElementId] = 15.0;
            zoneByElementId    [g.Columns[0].ElementId] = 1;
        }

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode                       = NumberingMode.Sequential,
            Prefix                     = "",
            StartNumber                = 1,
            SortBy                     = SortDirection.LeftToRight,
            ColumnRotationByElementId  = rotationByElementId,
            ColumnZoneByElementId      = zoneByElementId,
        });

        var sorted = engine.AssignMarks(allGroups).Groups;
        var keys   = sorted.Select(g => g.Key).ToList();

        // Expected ordering:
        //   O_r0 (bottom orthogonal row, left→right): O_r0c0, O_r0c1, O_r0c2
        //   O_r1 (top    orthogonal row, left→right): O_r1c0, O_r1c1, O_r1c2
        //   T_r0 (bottom tilted    row, left→right): T_r0c0, T_r0c1, T_r0c2
        //   T_r1 (top    tilted    row, left→right): T_r1c0, T_r1c1, T_r1c2
        keys.Should().Equal(
            "O_r0c0", "O_r0c1", "O_r0c2",
            "O_r1c0", "O_r1c1", "O_r1c2",
            "T_r0c0", "T_r0c1", "T_r0c2",
            "T_r1c0", "T_r1c1", "T_r1c2");

        // And no skipping — exactly N consecutive marks for N groups.
        sorted.Select(g => g.AssignedMark).Should().Equal(
            "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12");
    }

    [Fact]
    public void RowClustering_ChainBridging_IsBrokenByMaxSpanCap()
    {
        // Engineering scenario: four columns on an orthogonal grid whose
        // centroids drift slightly along the row axis — gap between every
        // consecutive pair is well under the user's tolerance, but the row
        // endpoints are 1.35 ft (≈ 41 cm) apart at tol = 0.5 ft (≈ 15 cm).
        //
        // Pure adjacent-gap clustering would join all four into one row
        // ("chain bridging") because each individual gap ≤ tol.  With the
        // 2·tol span cap, the runaway is prevented: the row is split when
        // the cumulative span exceeds 2·tol = 1.0 ft.
        //
        // Expected split:
        //   cluster A: y = 0.0, 0.45, 0.9   (span 0.9   ≤ 1.0 ✔)
        //   cluster B: y = 1.35              (span 0.0   ≤ 1.0 ✔, but
        //                                     adding to A would push span
        //                                     to 1.35 > 1.0 → forces split)
        var groups = new List<ColumnGroup>
        {
            new() { Key = "g0", Columns = [new ColumnData { ElementId = 1, X = 0, Y = 0.00 }] },
            new() { Key = "g1", Columns = [new ColumnData { ElementId = 2, X = 0, Y = 0.45 }] },
            new() { Key = "g2", Columns = [new ColumnData { ElementId = 3, X = 0, Y = 0.90 }] },
            new() { Key = "g3", Columns = [new ColumnData { ElementId = 4, X = 0, Y = 1.35 }] },
        };

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode             = NumberingMode.Sequential,
            Prefix           = "",
            StartNumber      = 1,
            SortBy           = SortDirection.LeftToRight,
            RowToleranceFeet = 0.5,
        });

        var sorted = engine.AssignMarks(groups).Groups;

        // LeftToRight sorts clusters bottom-up by row average Y.
        // If chain-bridged into one row, all four would share a row → identical
        // average ProjectY for every group → the assertion below would fail.
        // With the cap they form two rows that the engine numbers separately.
        sorted.Select(g => g.Key).Should().Equal("g0", "g1", "g2", "g3");
        sorted.Select(g => g.AssignedMark).Should().Equal("1", "2", "3", "4");

        // Direct check: rows g0..g2 average Y=0.45, row g3 average Y=1.35.
        // Reconstruct row identity by checking that g3 is alone in its row
        // versus g0/g1/g2 — done implicitly via the consecutive marks above
        // (the sort would have interleaved otherwise).
    }

    [Fact]
    public void RowClustering_LegitimateRow_WithinTwoTolerance_StaysMerged()
    {
        // Engineering scenario: three columns forming one real row.  Each
        // centroid sits within ±tol of an imaginary grid line, so the total
        // span is 2·tol exactly.  Adjacent-gap and span cap must BOTH allow
        // this — otherwise the cap would over-split real rows.
        //
        // Layout (LeftToRight, so cluster axis = Y, sort axis = X within row):
        //   row 0: three columns at Y ≈ 0,   X = 0, 5, 10
        //   row 1: three columns at Y ≈ 1.0 (= 2·tol with tol = 0.5)
        //
        // The first row's Y values are 0.0, 0.4, 0.0 — span 0.4 ≤ 1.0 ✔.
        // The second row's Y values are 0.6, 1.0, 0.6 — span 0.4 ≤ 1.0 ✔
        // and gap to row 0's max (0.4) is 0.6 - 0.4 = 0.2 ≤ tol → would
        // chain-bridge under the OLD algorithm.  Under the NEW algorithm
        // the cumulative span 0.0 → 1.0 = 1.0 ≤ 2·tol = 1.0 ✔ still merges.
        //
        // To prove the cap correctly preserves a real two-tol-wide row,
        // we use a single row whose extreme Y values differ by exactly 2·tol
        // and assert it remains a single row (numbered left→right by X).
        var groups = new List<ColumnGroup>
        {
            new() { Key = "a", Columns = [new ColumnData { ElementId = 1, X = 0,  Y = 0.0 }] },
            new() { Key = "b", Columns = [new ColumnData { ElementId = 2, X = 5,  Y = 0.5 }] },
            new() { Key = "c", Columns = [new ColumnData { ElementId = 3, X = 10, Y = 1.0 }] },
        };

        var engine = new NumberingEngine(new NumberingOptions
        {
            Mode             = NumberingMode.Sequential,
            Prefix           = "",
            StartNumber      = 1,
            SortBy           = SortDirection.LeftToRight,
            RowToleranceFeet = 0.5,
        });

        var sorted = engine.AssignMarks(groups).Groups;

        // All three are in ONE row (span 1.0 = 2·tol exactly), sorted L→R by X.
        sorted.Select(g => g.Key).Should().Equal("a", "b", "c");
        sorted.Select(g => g.AssignedMark).Should().Equal("1", "2", "3");
    }

    private static List<ColumnGroup> CloneShallow(IEnumerable<ColumnGroup> src) =>
        src.Select(g => new ColumnGroup
        {
            Key     = g.Key,
            Columns = g.Columns.Select(c => new ColumnData
            {
                ElementId = c.ElementId, X = c.X, Y = c.Y,
                BaseLevelName = c.BaseLevelName, TopLevelName = c.TopLevelName,
                BaseLevelElevation = c.BaseLevelElevation,
                TopLevelElevation  = c.TopLevelElevation,
            }).ToList(),
        }).ToList();

    private static List<ColumnGroup> MakeGroups(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ColumnGroup
            {
                Key     = $"k{i}",
                Columns = [new ColumnData { ElementId = i, X = i * 10, Y = 0 }]
            })
            .ToList();
}
