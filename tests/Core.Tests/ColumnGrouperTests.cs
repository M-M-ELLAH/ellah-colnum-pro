using FluentAssertions;
using EllahColNum.Core.Models;
using EllahColNum.Core.Services;
using Xunit;

namespace EllahColNum.Tests;

/// <summary>
/// Tests for ColumnGrouper — runs entirely without Revit.
/// </summary>
public class ColumnGrouperTests
{
    private readonly NumberingOptions _defaultOptions = new();

    [Fact]
    public void Group_SamePosition_ShouldProduceOneGroup()
    {
        var columns = new List<ColumnData>
        {
            new() { ElementId = 1, X = 10.0, Y = 20.0, BaseLevelName = "Floor 1" },
            new() { ElementId = 2, X = 10.0, Y = 20.0, BaseLevelName = "Floor 2" },
            new() { ElementId = 3, X = 10.0, Y = 20.0, BaseLevelName = "Floor 3" },
        };

        var grouper = new ColumnGrouper(_defaultOptions.PositionToleranceFeet);
        var groups  = grouper.Group(columns);

        groups.Should().HaveCount(1);
        groups[0].FloorCount.Should().Be(3);
    }

    [Fact]
    public void Group_DifferentPositions_ShouldProduceSeparateGroups()
    {
        var columns = new List<ColumnData>
        {
            new() { ElementId = 1, X = 0.0,  Y = 0.0  },
            new() { ElementId = 2, X = 10.0, Y = 0.0  },
            new() { ElementId = 3, X = 0.0,  Y = 10.0 },
        };

        var grouper = new ColumnGrouper(_defaultOptions.PositionToleranceFeet);
        var groups  = grouper.Group(columns);

        groups.Should().HaveCount(3);
    }

    [Fact]
    public void Group_SlightlyOffPosition_ShouldGroupWithinTolerance()
    {
        // Columns at almost the same spot — within 0.3 ft tolerance
        var columns = new List<ColumnData>
        {
            new() { ElementId = 1, X = 10.00, Y = 20.00 },
            new() { ElementId = 2, X = 10.10, Y = 20.05 }, // 0.1 ft off — within default 0.5
        };

        var grouper = new ColumnGrouper(_defaultOptions.PositionToleranceFeet);
        var groups  = grouper.Group(columns);

        groups.Should().HaveCount(1, "small modeling offsets should be tolerated");
    }

    [Fact]
    public void Group_ParsesGridRowAndColumn_FromLocationMark()
    {
        var columns = new List<ColumnData>
        {
            new() { ElementId = 1, X = 0, Y = 0, ColumnLocationMark = "A(64)-1(-46)" },
        };

        var grouper = new ColumnGrouper(_defaultOptions.PositionToleranceFeet);
        var groups  = grouper.Group(columns);

        groups[0].GridRow.Should().Be("A");
        groups[0].GridColumn.Should().Be("1");
    }
}
