using Rotula.Core.Barcodes;
using Rotula.Core.Model;
using Xunit;

namespace Rotula.Tests;

/// <summary>
/// Tests for the minimum module width table: the check that decides whether a barcode drawn as a picture by the Windows driver
/// is still wide enough to scan at the target printer's resolution.
/// </summary>
public class BarcodeMinimumModuleTests
{
    /// <summary>Resolutions the check has to hold for: the usual thermal heads plus the office-laser 1200 dpi that motivates it.</summary>
    private static readonly int[] Resolutions = { 203, 300, 600, 1200 };

    /// <summary>Every symbology of the model, so a member added later without a rule is caught here.</summary>
    public static IEnumerable<object[]> AllSymbologies() =>
        Enum.GetValues<BarcodeSymbology>().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(AllSymbologies))]
    public void EverySymbologyHasAPositiveFiniteMinimum(BarcodeSymbology symbology)
    {
        double mils = BarcodeSizing.MinimumModuleMils(symbology);
        Assert.True(mils > 0, $"{symbology} minimum must be positive, was {mils}");
        Assert.True(double.IsFinite(mils), $"{symbology} minimum must be finite, was {mils}");
    }

    [Theory]
    [MemberData(nameof(AllSymbologies))]
    public void EverySymbologyNamesItsSource(BarcodeSymbology symbology)
    {
        // The value has to be traceable: a table entry or a labelled family fallback, never an unexplained number.
        Assert.False(string.IsNullOrWhiteSpace(BarcodeSizing.MinimumModuleSourceOf(symbology)));
        Assert.False(string.IsNullOrWhiteSpace(BarcodeSizing.MinimumModuleSourceOf(symbology, gs1DigitalLink: true)));
    }

    [Theory]
    [MemberData(nameof(AllSymbologies))]
    public void OneDeviceDotIsAlwaysSingleDot(BarcodeSymbology symbology)
    {
        // One dot has no tolerance for dot gain whatever the symbology, so it is reported as its own case at every resolution.
        foreach (int dpi in Resolutions)
            Assert.Equal(ModuleSafety.SingleDot, BarcodeSizing.ModuleSafetyOf(symbology, 1, dpi));
    }

    [Theory]
    [InlineData(BarcodeSymbology.Code128)]
    [InlineData(BarcodeSymbology.QrCode)]
    [InlineData(BarcodeSymbology.Ean13)]
    [InlineData(BarcodeSymbology.DataMatrix)]
    public void ModuleAtTheMinimumIsSafeAndOneDotLessIsBelow(BarcodeSymbology symbology)
    {
        const int dpi = 1200;
        // The smallest whole-dot module that reaches the minimum width.
        int safe = (int)Math.Ceiling(BarcodeSizing.MinimumModuleMils(symbology) * dpi / 1000.0);
        Assert.Equal(ModuleSafety.Safe, BarcodeSizing.ModuleSafetyOf(symbology, safe, dpi));
        // One dot narrower must fall below it (a 1-dot module is the separate SingleDot case, so only check from 2 dots up).
        if (safe - 1 >= 2)
            Assert.Equal(ModuleSafety.BelowMinimum, BarcodeSizing.ModuleSafetyOf(symbology, safe - 1, dpi));
    }

    [Fact]
    public void OfficeLaserExampleNotPassedByDotCount()
    {
        // A 2-dot module looks fine to a dot-count rule, but at 1200 dpi it is only 1.7 mils wide.
        Assert.Equal(ModuleSafety.BelowMinimum, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.Code128, 2, 1200));
    }

    [Fact]
    public void ThreeDotCode128AtOfficeLaserResolutionIsBelowMinimum()
    {
        // 3 dots at 1200 dpi is 2.5 mils: still far below what a Code 128 reader needs (the picture-printing mode relies on this warning).
        Assert.True(BarcodeSizing.MinimumModuleMils(BarcodeSymbology.Code128) > 2.5);
        Assert.Equal(ModuleSafety.BelowMinimum, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.Code128, 3, 1200));
    }

    [Fact]
    public void CommonThermalSettingsAreSafe()
    {
        // The everyday thermal configurations must not raise a false alarm: Code 128 at 3 dots / 203 dpi (14.8 mils),
        // EAN-13 at 2 dots / 203 dpi (9.85 mils, the 75 percent magnification GS1 allows for on-demand printing),
        // a 5-dot QR Code at 300 dpi (16.7 mils, safe even as a GS1 Digital Link) and a generic 4-dot QR Code at 300 dpi (13.3 mils).
        Assert.Equal(ModuleSafety.Safe, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.Code128, 3, 203));
        Assert.Equal(ModuleSafety.Safe, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.Ean13, 2, 203));
        Assert.Equal(ModuleSafety.Safe, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.QrCode, 5, 300));
        Assert.Equal(ModuleSafety.Safe, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.QrCode, 5, 300, gs1DigitalLink: true));
        Assert.Equal(ModuleSafety.Safe, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.QrCode, 4, 300));
    }

    [Fact]
    public void ZeroOrNegativeResolutionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.Code128, 3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.QrCode, 3, 0, gs1DigitalLink: true));
    }

    // ---- QR Code: generic content and GS1 Digital Link content have different minimums

    [Fact]
    public void AGenericQrTakesTheMatrixFallbackBecauseIsoStatesNoMinimum()
    {
        // ISO/IEC 18004 states no minimum module, so a generic QR (a web address, Wi-Fi, a contact card) gets the matrix-2D family fallback,
        // the GS1 DataMatrix figure of 0.255 mm, and says so: a "Fallback", never a figure presented as the standard's own.
        Assert.Equal(0.255 / 0.0254, BarcodeSizing.MinimumModuleMils(BarcodeSymbology.QrCode), 9);
        Assert.Equal(BarcodeSizing.MinimumModuleMils(BarcodeSymbology.DataMatrix), BarcodeSizing.MinimumModuleMils(BarcodeSymbology.QrCode), 9);
        string source = BarcodeSizing.MinimumModuleSourceOf(BarcodeSymbology.QrCode);
        Assert.StartsWith("Fallback", source);
        Assert.Contains("ISO/IEC 18004", source);
        // The old signatures are the generic ones.
        Assert.Equal(BarcodeSizing.MinimumModuleMils(BarcodeSymbology.QrCode, gs1DigitalLink: false), BarcodeSizing.MinimumModuleMils(BarcodeSymbology.QrCode));
        Assert.Equal(BarcodeSizing.MinimumModuleSourceOf(BarcodeSymbology.QrCode, gs1DigitalLink: false), source);
    }

    [Fact]
    public void AGs1DigitalLinkQrKeepsTheGs1Figure()
    {
        // GS1 General Specifications symbol specification table 1: GS1 QR Code (a GS1 Digital Link URI) 0.375 mm.
        Assert.Equal(0.375 / 0.0254, BarcodeSizing.MinimumModuleMils(BarcodeSymbology.QrCode, gs1DigitalLink: true), 9);
        string source = BarcodeSizing.MinimumModuleSourceOf(BarcodeSymbology.QrCode, gs1DigitalLink: true);
        Assert.Contains("GS1", source);
        Assert.Contains("0.375", source);
        Assert.False(source.StartsWith("Fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void AFourDotQrAt300DpiIsSafeUnlessItCarriesAGs1DigitalLink()
    {
        // 4 dots at 300 dpi is 13.3 mils (0.339 mm): above the 10.0 mils fallback, under the 14.8 mils GS1 asks for.
        Assert.Equal(ModuleSafety.Safe, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.QrCode, 4, 300));
        Assert.Equal(ModuleSafety.Safe, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.QrCode, 4, 300, gs1DigitalLink: false));
        Assert.Equal(ModuleSafety.BelowMinimum, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.QrCode, 4, 300, gs1DigitalLink: true));
    }

    [Fact]
    public void AGs1DigitalLinkQrAtTheMinimumIsSafeAndOneDotLessIsBelow()
    {
        const int dpi = 1200;
        double minimum = BarcodeSizing.MinimumModuleMils(BarcodeSymbology.QrCode, gs1DigitalLink: true);
        int safe = (int)Math.Ceiling(minimum * dpi / 1000.0);
        Assert.Equal(ModuleSafety.Safe, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.QrCode, safe, dpi, gs1DigitalLink: true));
        Assert.Equal(ModuleSafety.BelowMinimum, BarcodeSizing.ModuleSafetyOf(BarcodeSymbology.QrCode, safe - 1, dpi, gs1DigitalLink: true));
    }

    [Theory]
    [MemberData(nameof(AllSymbologies))]
    public void TheGs1DigitalLinkFlagChangesOnlyQrCode(BarcodeSymbology symbology)
    {
        // Data Matrix keeps its single GS1 figure (0.255 mm) whatever its content; every other symbology has no Digital Link form at all.
        if (symbology == BarcodeSymbology.QrCode) return;
        Assert.Equal(BarcodeSizing.MinimumModuleMils(symbology), BarcodeSizing.MinimumModuleMils(symbology, gs1DigitalLink: true));
        Assert.Equal(BarcodeSizing.MinimumModuleSourceOf(symbology), BarcodeSizing.MinimumModuleSourceOf(symbology, gs1DigitalLink: true));
    }

    [Fact]
    public void GS1DigitalLinkContentIsRecognisedByTheOptionOrByTheContentBuilder()
    {
        // The Digital Link option (the data is checked as a GS1 Digital Link URI) and the content builder's GS1 Digital Link kind both mean it.
        var plain = new Rotula.Core.Elements.BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "https://example.com" };
        var option = new Rotula.Core.Elements.BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "https://id.gs1.org/01/09506000134352", DigitalLink = true };
        var builder = new Rotula.Core.Elements.BarcodeElement
        {
            Symbology = BarcodeSymbology.QrCode,
            Data = "https://id.gs1.org/01/09506000134352",
            QrContentSpec = Rotula.Core.Barcodes.QrContent.QrContentSpec.Create(Rotula.Core.Barcodes.QrContent.QrContentKind.GS1DigitalLink, ("gtin", "09506000134352")).ToJson(),
        };
        var url = new Rotula.Core.Elements.BarcodeElement
        {
            Symbology = BarcodeSymbology.QrCode,
            Data = "https://example.com",
            QrContentSpec = Rotula.Core.Barcodes.QrContent.QrContentSpec.Create(Rotula.Core.Barcodes.QrContent.QrContentKind.Url, ("url", "https://example.com")).ToJson(),
        };
        var broken = new Rotula.Core.Elements.BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "x", QrContentSpec = "{not json" };

        Assert.False(BarcodeSizing.HasGs1DigitalLinkContent(plain));
        Assert.True(BarcodeSizing.HasGs1DigitalLinkContent(option));
        Assert.True(BarcodeSizing.HasGs1DigitalLinkContent(builder));
        Assert.False(BarcodeSizing.HasGs1DigitalLinkContent(url));
        Assert.False(BarcodeSizing.HasGs1DigitalLinkContent(broken));
    }
}
