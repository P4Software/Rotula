using Rotula.Files.Station;

namespace Rotula.Tests;

/// <summary>The print station's exit PIN hash and the scanner logic (terminators, print on last scan, scan to select), without any window.</summary>
public class StationScanTests
{
    // The smallest iteration count the hash accepts keeps these tests fast; the format and the comparison are the same.
    private const int FastIterations = 10_000;

    // ------------------------------------------------------------------------------------ PIN

    [Fact]
    public void APin_IsStoredAsASaltedHash_AndOnlyTheRightPinMatches()
    {
        string hash = StationPin.Hash("4711", FastIterations);
        Assert.StartsWith("pbkdf2-sha256$10000$", hash);
        Assert.DoesNotContain("4711", hash);
        Assert.True(StationPin.IsHash(hash));
        Assert.True(StationPin.Verify("4711", hash));
        Assert.True(StationPin.Verify(" 4711 ", hash)); // surrounding spaces are ignored, as when it was made
        Assert.False(StationPin.Verify("4712", hash));
        Assert.False(StationPin.Verify("", hash));
        Assert.False(StationPin.Verify(null, hash));
        // A new salt every time: the same PIN never gives the same line twice.
        Assert.NotEqual(hash, StationPin.Hash("4711", FastIterations));
    }

    [Fact]
    public void TheDefaultHash_UsesManyIterations()
    {
        string hash = StationPin.Hash("2468");
        Assert.StartsWith("pbkdf2-sha256$600000$", hash);
        Assert.True(StationPin.Verify("2468", hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("pbkdf2-sha256$10000$abc")]
    [InlineData("pbkdf2-sha1$10000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$10$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // too few iterations
    [InlineData("pbkdf2-sha256$99999999$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // would freeze the station
    [InlineData("pbkdf2-sha256$10000$not base64!$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$10000$AAAAAAAAAAAAAAAAAAAAAA==$AAAA")] // wrong hash length
    public void AMalformedHash_IsRefused_AndNeverMatches(string? stored)
    {
        Assert.False(StationPin.IsHash(stored));
        Assert.False(StationPin.Verify("1234", stored));
    }

    [Theory]
    [InlineData("123", false)]
    [InlineData("1234", true)]
    [InlineData("    ", false)]
    [InlineData("12\t34", false)]
    [InlineData("abcdefghijabcdefghijabcdefghij12", true)]
    [InlineData("abcdefghijabcdefghijabcdefghij123", false)]
    public void APin_Needs4To32Characters(string pin, bool valid)
    {
        Assert.Equal(valid, StationPin.IsValidPin(pin));
        if (!valid) Assert.Throws<ArgumentException>(() => StationPin.Hash(pin, FastIterations));
    }

    // ------------------------------------------------------------------------------------ questions

    [Fact]
    public void EnterAndTab_MoveToTheNextField_AndShiftTabGoesBack()
    {
        var s = new ScanSequencer(printOnLastScan: true, scanToSelect: true);
        Assert.Equal(new ScanStep(ScanStepKind.MoveTo, 1), s.Next(0, 3, ScanTerminator.Enter, true));
        Assert.Equal(new ScanStep(ScanStepKind.MoveTo, 2), s.Next(1, 3, ScanTerminator.Tab, true));
        Assert.Equal(new ScanStep(ScanStepKind.MoveTo, 0), s.Next(1, 3, ScanTerminator.ShiftTab, true));
        Assert.Equal(new ScanStep(ScanStepKind.MoveTo, 0), s.Next(0, 3, ScanTerminator.ShiftTab, true));
        // Going back is allowed even when the field's answer is refused.
        Assert.Equal(new ScanStep(ScanStepKind.MoveTo, 0), s.Next(1, 3, ScanTerminator.ShiftTab, false));
    }

    [Fact]
    public void TheLastScan_Prints_WhenPrintOnLastScanIsOn()
    {
        var on = new ScanSequencer(printOnLastScan: true, scanToSelect: true);
        Assert.Equal(ScanStepKind.Print, on.Next(2, 3, ScanTerminator.Enter, true).Kind);
        Assert.Equal(ScanStepKind.Print, on.Next(2, 3, ScanTerminator.Tab, true).Kind);
        var off = new ScanSequencer(printOnLastScan: false, scanToSelect: true);
        Assert.Equal(ScanStepKind.FocusPrint, off.Next(2, 3, ScanTerminator.Enter, true).Kind);
    }

    [Fact]
    public void ARefusedScan_StaysInItsField_AndNeverPrints()
    {
        var s = new ScanSequencer(printOnLastScan: true, scanToSelect: true);
        Assert.Equal(new ScanStep(ScanStepKind.Stay, 0), s.Next(0, 3, ScanTerminator.Enter, false));
        Assert.Equal(new ScanStep(ScanStepKind.Stay, 2), s.Next(2, 3, ScanTerminator.Enter, false));
    }

    [Fact]
    public void ALabelWithoutQuestions_PrintsOnEnter_OrGoesToPrint()
    {
        var s = new ScanSequencer(printOnLastScan: true, scanToSelect: true);
        Assert.Equal(ScanStepKind.Print, s.Next(0, 0, ScanTerminator.Enter, true).Kind);
        Assert.Equal(ScanStepKind.FocusPrint, s.Next(0, 0, ScanTerminator.Tab, true).Kind);
    }

    // ------------------------------------------------------------------------------------ choosing a label

    private sealed record L(string Name, string? Key);

    private static readonly L[] All = { new("Pallet", "PAL-01"), new("Case", "CASE-07"), new("Carton", null) };

    [Fact]
    public void ScanningALabelsKey_OpensIt_IgnoringCaseAndScannerExtras()
    {
        var s = new ScanSequencer(true, scanToSelect: true);
        Assert.Equal(LabelScanKind.Open, s.SelectLabel("  pal-01\r", All, l => l.Key, Array.Empty<L>(), out var hit));
        Assert.Equal("Pallet", hit!.Name);
        Assert.Equal(LabelScanKind.Open, s.SelectLabel("\u001DCASE-07\n", All, l => l.Key, Array.Empty<L>(), out hit));
        Assert.Equal("Case", hit!.Name);
    }

    [Fact]
    public void ASearchThatLeavesOneLabel_OpensIt_AndAnythingElseIsUnknown()
    {
        var s = new ScanSequencer(true, scanToSelect: true);
        Assert.Equal(LabelScanKind.Open, s.SelectLabel("cart", All, l => l.Key, new[] { All[2] }, out var hit));
        Assert.Equal("Carton", hit!.Name);
        Assert.Equal(LabelScanKind.Unknown, s.SelectLabel("ca", All, l => l.Key, new[] { All[1], All[2] }, out hit));
        Assert.Null(hit);
        Assert.Equal(LabelScanKind.Unknown, s.SelectLabel("XYZ", All, l => l.Key, Array.Empty<L>(), out _));
        Assert.Equal(LabelScanKind.None, s.SelectLabel(" \r\n", All, l => l.Key, All, out _));
    }

    [Fact]
    public void WithScanToSelectOff_AKeyDoesNotOpenALabel()
    {
        var s = new ScanSequencer(true, scanToSelect: false);
        Assert.Equal(LabelScanKind.Unknown, s.SelectLabel("PAL-01", All, l => l.Key, Array.Empty<L>(), out _));
    }

    [Fact]
    public void Clean_KeepsInnerSeparators()
    {
        Assert.Equal("01\u001D10ABC", ScanSequencer.Clean("\u001D01\u001D10ABC\r\n"));
        Assert.Equal(string.Empty, ScanSequencer.Clean(null));
    }
}
