using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Stocks;
using static Rotula.Tests.StockTestSupport;

namespace Rotula.Tests;

/// <summary>Applying a library stock to a label.</summary>
public class StockApplierTests
{
    private static StockEntry Stock(string id) => Bundled.Find(id) ?? throw new InvalidOperationException("missing " + id);

    private static bool Has(StockApplyResult r, string key) => r.Warnings.Any(w => w.MessageKey == key);

    [Theory]
    [InlineData(PrinterDpi.Dpi203, 812, 1218)]
    [InlineData(PrinterDpi.Dpi300, 1200, 1800)]
    [InlineData(PrinterDpi.Dpi600, 2400, 3600)]
    public void FourBySixInch_GivesTheExactDotSize(PrinterDpi dpi, int w, int h)
    {
        var r = StockApplier.Apply(null, Stock("std-4x6in"), dpi);
        Assert.True(r.CreatedNew);
        Assert.Equal(dpi, r.Document.Dpi);
        Assert.Equal(w, r.Document.WidthDots);
        Assert.Equal(h, r.Document.HeightDots);
        Assert.Equal("std-4x6in", r.Document.StockId);
        Assert.Equal(MediaTracking.Web, r.Document.MediaTracking);
        Assert.Equal(LabelOrientation.Normal, r.Document.Orientation);
        Assert.True(r.HeightApplied);
    }

    [Fact]
    public void MillimetreStock_KeepsMillimetres()
    {
        var r = StockApplier.Apply(null, Stock("iso-102x152mm"), PrinterDpi.Dpi203);
        Assert.Equal(LengthUnit.Millimeters, r.Document.Width.Unit);
        Assert.Equal(101.6, r.Document.Width.Value);
        Assert.Equal(152.4, r.Document.Height.Value);
    }

    [Fact]
    public void UnknownGap_IsReportedForDieCutRolls()
    {
        var stock = Stock("zebra-10028310");
        Assert.Null(stock.GapMm);
        var r = StockApplier.Apply(null, stock, PrinterDpi.Dpi203);
        var w = Assert.Single(r.Warnings, x => x.MessageKey == StockMessageKeys.ApplyGapUnknown);
        Assert.Equal(new[] { "zebra-10028310" }, w.Args.ToArray());
        Assert.Equal(MediaType.DirectThermal, r.Document.MediaType);
        Assert.Equal(3.18, r.CornerRadiusMm);
        Assert.False(Has(r, StockMessageKeys.ApplyUnverified));
    }

    [Fact]
    public void UnverifiedEntry_Warns()
    {
        var r = StockApplier.Apply(null, Stock("zebra-10011696"), PrinterDpi.Dpi203);
        Assert.True(Has(r, StockMessageKeys.ApplyUnverified));
    }

    [Fact]
    public void ThermalTransferStock_SetsTheMediaType()
    {
        var stock = Bundled.Entries.First(e => e.Media == StockMedia.ThermalTransfer && e.Format == StockFormat.Roll);
        var r = StockApplier.Apply(new LabelDocument { MediaType = MediaType.DirectThermal }, stock, PrinterDpi.Dpi203);
        Assert.Equal(MediaType.ThermalTransfer, r.Document.MediaType);
    }

    [Fact]
    public void EitherMedia_LeavesTheLabelsMediaType()
    {
        var stock = Bundled.Entries.First(e => e.Media == StockMedia.Either);
        var r = StockApplier.Apply(new LabelDocument { MediaType = MediaType.DirectThermal }, stock, PrinterDpi.Dpi203);
        Assert.Equal(MediaType.DirectThermal, r.Document.MediaType);
        var r2 = StockApplier.Apply(new LabelDocument { MediaType = MediaType.ThermalTransfer }, stock, PrinterDpi.Dpi203);
        Assert.Equal(MediaType.ThermalTransfer, r2.Document.MediaType);
    }

    [Fact]
    public void Wristband_BundledEntryPassesThroughUnchangedAndRotatesFields()
    {
        var stock = Stock("zebra-10003852");
        Assert.True(stock.WidthMm < stock.HeightMm);
        var r = StockApplier.Apply(null, stock, PrinterDpi.Dpi203);
        Assert.False(r.SidesSwapped);
        Assert.True(r.FieldsRotated);
        Assert.Equal(FieldRotation.Rotated90, r.Document.DefaultRotation);
        Assert.Equal(LabelOrientation.Normal, r.Document.Orientation);
        Assert.Equal(203, r.Document.WidthDots);
        Assert.Equal(1421, r.Document.HeightDots);
        Assert.Equal(MediaType.DirectThermal, r.Document.MediaType);
    }

    [Fact]
    public void Wristband_LongSideAsWidthIsSwapped()
    {
        var hand = new StockEntry
        {
            Id = "hand-1", Name = "Hand made band", Category = "wristband",
            WidthMm = 279.4, HeightMm = 25.4, WidthIn = 11, HeightIn = 1, Sources = new[] { "x" },
        };
        var r = StockApplier.Apply(null, hand, PrinterDpi.Dpi300);
        Assert.True(r.SidesSwapped);
        Assert.Equal(Measure.Inches(1), r.Document.Width);
        Assert.Equal(Measure.Inches(11), r.Document.Height);
        Assert.Equal(300, r.Document.WidthDots);
        Assert.Equal(3300, r.Document.HeightDots);
    }

    [Fact]
    public void Wristband_ExistingContentKeepsItsRotation()
    {
        var doc = new LabelDocument();
        doc.Elements.Add(new TextElement { Text = "keep me" });
        var r = StockApplier.Apply(doc, Stock("zebra-10003852"), PrinterDpi.Dpi203);
        Assert.False(r.FieldsRotated);
        Assert.Equal(FieldRotation.Normal, r.Document.DefaultRotation);
        Assert.True(Has(r, StockMessageKeys.ApplyRotationKept));
        Assert.True(Has(r, StockMessageKeys.ApplyElementsMayClip));
        Assert.Single(r.Document.Elements);
    }

    [Fact]
    public void Wristband_RotationCanBeSwitchedOff()
    {
        var r = StockApplier.Apply(null, Stock("zebra-10003852"), PrinterDpi.Dpi203, new StockApplyOptions { RotateWristbandFields = false });
        Assert.Equal(FieldRotation.Normal, r.Document.DefaultRotation);
        Assert.False(r.FieldsRotated);
    }

    [Fact]
    public void ContinuousRoll_ChoosesContinuousTrackingAndKeepsTheHeight()
    {
        var stock = Stock("zebra-10001965");
        var doc = new LabelDocument { Height = Measure.Millimeters(80) };
        var r = StockApplier.Apply(doc, stock, PrinterDpi.Dpi203);
        Assert.Equal(MediaTracking.Continuous, r.Document.MediaTracking);
        Assert.False(r.HeightApplied);
        Assert.Equal(Measure.Millimeters(80), r.Document.Height);
        Assert.Equal(Measure.Millimeters(101.6), r.Document.Width.Unit == LengthUnit.Millimeters ? r.Document.Width : Measure.Millimeters(r.Document.Width.Value * 25.4));
        var w = Assert.Single(r.Warnings, x => x.MessageKey == StockMessageKeys.ApplyContinuousNoHeight);
        Assert.Equal("22.555", w.Args[1]);
        Assert.False(Has(r, StockMessageKeys.ApplyGapUnknown));
        Assert.Equal(22.555, r.RollLengthM);
    }

    [Fact]
    public void BlackMarkStock_ChoosesMarkTracking()
    {
        var r = StockApplier.Apply(null, Stock("zebra-10010062"), PrinterDpi.Dpi203);
        Assert.Equal(MediaTracking.Mark, r.Document.MediaTracking);
        Assert.False(Has(r, StockMessageKeys.ApplyGapUnknown));
    }

    [Fact]
    public void MultiUp_IsReportedNotApplied()
    {
        var stock = Bundled.Entries.First(e => e.Columns > 1 && e.Layout == StockLayout.Roll);
        var r = StockApplier.Apply(null, stock, PrinterDpi.Dpi203);
        Assert.True(r.Columns > 1);
        Assert.True(Has(r, StockMessageKeys.ApplyMultiUpNotSupported));
        // One label only: the document is the size of a single label.
        Assert.Equal(stock.WidthMm, r.Document.Width.Unit == LengthUnit.Inches ? r.Document.Width.Value * 25.4 : r.Document.Width.Value, 1);
        Assert.Equal(stock.HGapMm, r.HGapMm);
    }

    [Fact]
    public void SheetStock_ReportsLayoutLimit()
    {
        var r = StockApplier.Apply(null, Stock("avery-5160"), PrinterDpi.Dpi300);
        Assert.True(Has(r, StockMessageKeys.ApplySheetLayoutNotSupported));
        Assert.Equal(3, r.Columns);
        Assert.Equal(10, r.Rows);
    }

    [Fact]
    public void ExistingLabel_IsNotModifiedAndKeepsItsContent()
    {
        var doc = new LabelDocument { Dpi = PrinterDpi.Dpi203, Width = Measure.Dots(800), Height = Measure.Dots(400) };
        doc.Elements.Add(new TextElement { Text = "Hello", X = 100, Y = 50 });
        doc.StockId = "old";
        var r = StockApplier.Apply(doc, Stock("std-4x6in"), PrinterDpi.Dpi203);
        Assert.NotSame(doc, r.Document);
        Assert.Equal(Measure.Dots(800), doc.Width);
        Assert.Equal("old", doc.StockId);
        Assert.Equal("std-4x6in", r.Document.StockId);
        Assert.Equal("Hello", ((TextElement)Assert.Single(r.Document.Elements)).Text);
        Assert.False(r.CreatedNew);
    }

    [Fact]
    public void ExistingLabel_KeepsEveryOtherSetting()
    {
        // Apply works on the copy that LabelDpiConverter makes; a label property that converter forgets would vanish
        // here, so every simple settable property is set to a non-default value and must survive.
        var doc = new LabelDocument();
        var set = new List<System.Reflection.PropertyInfo>();
        foreach (var p in typeof(LabelDocument).GetProperties().Where(p => p.CanWrite && p.GetIndexParameters().Length == 0))
        {
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            object? v = null;
            if (t == typeof(bool)) v = true;
            else if (t == typeof(int)) v = 7;
            else if (t == typeof(decimal)) v = 5m;
            else if (t.IsEnum) v = Enum.GetValues(t).Cast<object>().Last();
            if (v is null) continue;
            p.SetValue(doc, v);
            set.Add(p);
        }
        Assert.Contains(set, p => p.Name == "Encoding" || p.Name == "MediaTracking");
        var r = StockApplier.Apply(doc, Stock("std-4x6in"), doc.Dpi);
        var skipped = new[] { nameof(LabelDocument.MediaTracking), nameof(LabelDocument.MediaType), nameof(LabelDocument.Dpi) };
        foreach (var p in set.Where(p => !skipped.Contains(p.Name)))
            Assert.True(Equals(p.GetValue(doc), p.GetValue(r.Document)), "LabelDocument." + p.Name + " was lost by Apply");
    }

    [Fact]
    public void ExistingLabelAtAnotherResolution_IsConvertedWithItsWarnings()
    {
        var doc = new LabelDocument { Dpi = PrinterDpi.Dpi203, Width = Measure.Dots(406), Height = Measure.Dots(203) };
        doc.Elements.Add(new TextElement { Text = "Hi", X = 203, Y = 100 });
        var r = StockApplier.Apply(doc, Stock("std-4x6in"), PrinterDpi.Dpi300);
        Assert.Equal(PrinterDpi.Dpi300, r.Document.Dpi);
        Assert.Equal(PrinterDpi.Dpi203, doc.Dpi);
        Assert.Equal(300, ((TextElement)r.Document.Elements[0]).X);
        Assert.Equal(1200, r.Document.WidthDots);
    }

    [Fact]
    public void RoundTrip_GeneratedZplUsesTheStockSize()
    {
        var r = StockApplier.Apply(null, Stock("std-4x6in"), PrinterDpi.Dpi203);
        string zpl = Rotula.Core.Zpl.ZplGenerator.Generate(r.Document).Zpl;
        Assert.Contains("^PW812", zpl);
        Assert.Contains("^LL1218", zpl);
    }

    [Fact]
    public void InvalidStock_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => StockApplier.Apply(null, null!, PrinterDpi.Dpi203));
        var bad = new StockEntry { Id = "x", Name = "x", WidthMm = 0 };
        Assert.Throws<ArgumentException>(() => StockApplier.Apply(null, bad, PrinterDpi.Dpi203));
    }

    [Fact]
    public void EveryBundledStock_CanBeApplied()
    {
        foreach (var e in Bundled.Entries)
        {
            var r = StockApplier.Apply(null, e, PrinterDpi.Dpi203);
            Assert.True(r.Document.WidthDots > 0 && r.Document.HeightDots > 0, e.Id);
            Assert.Equal(e.Id, r.Document.StockId);
        }
    }
}
