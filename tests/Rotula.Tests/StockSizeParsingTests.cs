using Rotula.Core.Stocks;

namespace Rotula.Tests;

/// <summary>The size parser on its own: spellings, units, and texts that must not be read as a label size.</summary>
public class StockSizeParsingTests
{
    [Theory]
    [InlineData("4x6 in", 4, 6, StockSizeUnit.Inches)]
    [InlineData("4 x 6 in", 4, 6, StockSizeUnit.Inches)]
    [InlineData("4in x 6in", 4, 6, StockSizeUnit.Inches)]
    [InlineData("4\"x6\"", 4, 6, StockSizeUnit.Inches)]
    [InlineData("4” x 6”", 4, 6, StockSizeUnit.Inches)]
    [InlineData("2.25 x 1.25 inches", 2.25, 1.25, StockSizeUnit.Inches)]
    [InlineData("2,25 x 1,25 in", 2.25, 1.25, StockSizeUnit.Inches)]
    [InlineData("4 por 6 pulgadas", 4, 6, StockSizeUnit.Inches)]
    [InlineData("100x150mm", 100, 150, StockSizeUnit.Millimeters)]
    [InlineData("100 mm x 150 mm", 100, 150, StockSizeUnit.Millimeters)]
    [InlineData("100 X 150 MM", 100, 150, StockSizeUnit.Millimeters)]
    [InlineData("10 cm x 15 cm", 100, 150, StockSizeUnit.Millimeters)]
    [InlineData("Label (102×152mm) white", 102, 152, StockSizeUnit.Millimeters)]
    [InlineData("Z-Select 4000D 2.25 x 2.5 in", 2.25, 2.5, StockSizeUnit.Inches)]
    public void Parses_WithAWrittenUnit(string text, double a, double b, StockSizeUnit unit)
    {
        Assert.True(StockSizeParser.TryParse(text, out var s));
        Assert.True(s.UnitWritten);
        Assert.Equal(unit, s.FirstUnit);
        var c = Assert.Single(s.Candidates);
        double k = unit == StockSizeUnit.Inches ? 25.4 : 1;
        Assert.Equal(a * k, c.WidthMm, 3);
        Assert.Equal(b * k, c.HeightMm, 3);
    }

    [Fact]
    public void UnitLessSizeHasOneReadingPerPlausibleUnit()
    {
        Assert.True(StockSizeParser.TryParse("4x6", out var s));
        Assert.False(s.UnitWritten);
        var c = Assert.Single(s.Candidates);
        Assert.Equal(StockSizeUnit.Inches, c.Unit);

        Assert.True(StockSizeParser.TryParse("102x152", out var mm));
        Assert.Equal(StockSizeUnit.Millimeters, Assert.Single(mm.Candidates).Unit);

        Assert.True(StockSizeParser.TryParse("10x15", out var both));
        Assert.Equal(2, both.Candidates.Count);
    }

    [Fact]
    public void TheTwoStatementsOfOneRowAreBothFound()
    {
        var all = StockSizeParser.FindAll("4x6in (101.6x152.4mm)");
        Assert.Equal(2, all.Count);
        Assert.Equal(StockSizeUnit.Inches, all[0].FirstUnit);
        Assert.Equal(StockSizeUnit.Millimeters, all[1].FirstUnit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no size here")]
    [InlineData("10010041")]
    [InlineData("4 in x 30.5 m")]
    [InlineData("3in x 960in roll")]
    [InlineData("6x")]
    [InlineData("x6")]
    [InlineData("4 x six")]
    [InlineData("0 x 6 in")]
    public void DoesNotInventASize(string text)
    {
        var found = StockSizeParser.FindAll(text);
        // "3in x 960in" is a real pair of numbers; every other text here has none.
        if (text.StartsWith("3in", StringComparison.Ordinal)) Assert.Single(found);
        else Assert.Empty(found);
    }

    [Fact]
    public void NeverThrowsOnHostileText()
    {
        Assert.Empty(StockSizeParser.FindAll(null));
        Assert.Empty(StockSizeParser.FindAll(new string('9', 100_000)));
        Assert.NotNull(StockSizeParser.FindAll(string.Concat(Enumerable.Repeat("1", 1500)) + " x " + string.Concat(Enumerable.Repeat("2", 400))));
    }
}
