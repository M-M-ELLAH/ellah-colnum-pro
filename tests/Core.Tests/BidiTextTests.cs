using FluentAssertions;
using EllahColNum.Core.Text;
using Xunit;

namespace EllahColNum.Tests;

/// <summary>
/// Tests for BidiText — guards the Hebrew/RTL-safe string comparisons used when
/// matching Revit level/view names that may carry invisible BiDi control marks.
/// </summary>
public class BidiTextTests
{
    [Fact]
    public void NormalizeForCompare_StripsRleAndPdf()
    {
        // RLE (U+202B) ... PDF (U+202C) wrappers are commonly added by Revit
        // when level names are surrounded by RTL text in tooltips and views.
        var withMarks = "\u202bקומה ז'\u202c";
        BidiText.NormalizeForCompare(withMarks).Should().Be("קומה ז'");
    }

    [Fact]
    public void NormalizeForCompare_StripsLrmAndRlm()
    {
        BidiText.NormalizeForCompare("\u200eFloor 5\u200f").Should().Be("Floor 5");
    }

    [Fact]
    public void NormalizeForCompare_StripsBidiIsolates()
    {
        // FSI (U+2068) ... PDI (U+2069)
        BidiText.NormalizeForCompare("\u2068קומה ב'\u2069").Should().Be("קומה ב'");
    }

    [Fact]
    public void NormalizeForCompare_TrimsSurroundingWhitespace()
    {
        BidiText.NormalizeForCompare("  \u202bקומה ה'\u202c  ").Should().Be("קומה ה'");
    }

    [Fact]
    public void NormalizeForCompare_NullOrEmpty_ReturnsEmpty()
    {
        BidiText.NormalizeForCompare(null).Should().Be("");
        BidiText.NormalizeForCompare("").Should().Be("");
    }

    [Fact]
    public void NormalizeForCompare_PlainText_PassesThrough()
    {
        BidiText.NormalizeForCompare("Floor 1").Should().Be("Floor 1");
        BidiText.NormalizeForCompare("קומה א'").Should().Be("קומה א'");
    }

    [Fact]
    public void EqualsIgnoreBidi_MatchesAcrossRtlMarkers()
    {
        BidiText.EqualsIgnoreBidi("\u202bקומה ז'\u202c", "קומה ז'").Should().BeTrue();
        BidiText.EqualsIgnoreBidi("\u200eFloor 7\u200f", "floor 7").Should().BeTrue();
    }

    [Fact]
    public void EqualsIgnoreBidi_DistinctNames_StillNotEqual()
    {
        BidiText.EqualsIgnoreBidi("קומה ז'", "קומה ה'").Should().BeFalse();
        BidiText.EqualsIgnoreBidi("Floor 5", "Floor 7").Should().BeFalse();
    }

    [Fact]
    public void EqualsIgnoreBidi_HandlesNullsGracefully()
    {
        BidiText.EqualsIgnoreBidi(null, null).Should().BeTrue();
        BidiText.EqualsIgnoreBidi(null, "Floor 1").Should().BeFalse();
        BidiText.EqualsIgnoreBidi("Floor 1", null).Should().BeFalse();
    }
}
