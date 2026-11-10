using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>The print mode model: per-target mode, the mode suggestion, the acknowledgement and the new result members.</summary>
public class PrinterOutputModeTests
{
    /// <summary>Zebra-style names and text-only queues suggest the printer language; everything else suggests the Windows driver.</summary>
    [Theory]
    [InlineData("ZDesigner ZD421-203dpi ZPL", "ZDesigner ZD421-203dpi ZPL", "USB001", PrinterOutputMode.PrinterLanguage)]
    [InlineData("Zebra GK420d", "ZDesigner GK420d", "USB001", PrinterOutputMode.PrinterLanguage)]
    [InlineData("Label printer", "Generic / Text Only", "USB002", PrinterOutputMode.PrinterLanguage)]
    [InlineData("Brother QL-820NWB", "Brother QL-820NWB", "USB003", PrinterOutputMode.WindowsDriver)]
    [InlineData("HP LaserJet Pro", "HP LaserJet Pro", "USB004", PrinterOutputMode.WindowsDriver)]
    [InlineData("DYMO LabelWriter 450", "DYMO LabelWriter 450", "USB005", PrinterOutputMode.WindowsDriver)]
    [InlineData("zebra gk420d", "zdesigner gk420d", "usb001", PrinterOutputMode.PrinterLanguage)]
    [InlineData("label printer", "generic / text only", "USB002", PrinterOutputMode.PrinterLanguage)]
    public void SuggestionFollowsTheNames(string queue, string driver, string port, PrinterOutputMode expected) =>
        Assert.Equal(expected, PrinterOutputModeSuggestion.For(queue, driver, port));

    /// <summary>
    /// settings.json stores the mode by member name (<c>"printModes": { "Queue": "WindowsDriver" }</c>). Renaming the enum type was safe because only the
    /// type name changed; renaming a member would silently send every saved printer back to the mode question, so the names are pinned here.
    /// </summary>
    [Fact]
    public void MemberNames_AreTheStoredValues_AndNeverChange() =>
        Assert.Equal(new[] { "PrinterLanguage", "WindowsDriver" }, Enum.GetNames<PrinterOutputMode>());

    /// <summary>Missing names (a driver that reports nothing) must not throw and fall back to the driver mode.</summary>
    [Fact]
    public void SuggestionToleratesNulls() =>
        Assert.Equal(PrinterOutputMode.WindowsDriver, PrinterOutputModeSuggestion.For(null!, null!, null!));

    /// <summary>A new target keeps today's behaviour: ZPL to the printer, nothing acknowledged.</summary>
    [Fact]
    public void NewTargetIsPrinterLanguage()
    {
        var target = PrinterTarget.Spooler("x");
        Assert.Equal(PrinterOutputMode.PrinterLanguage, target.Mode);
        Assert.Null(target.AcknowledgedFindings);
    }

    /// <summary>A raw TCP socket has no driver to draw through, so the mode request is ignored.</summary>
    [Fact]
    public void TcpTargetIgnoresDriverMode() =>
        Assert.Equal(PrinterOutputMode.PrinterLanguage, PrinterTarget.TcpIp("10.0.0.5").WithMode(PrinterOutputMode.WindowsDriver).Mode);

    /// <summary>The "with" methods return copies that keep every other property and leave the original alone.</summary>
    [Fact]
    public void WithModeAndWithAcknowledgementAreCopies()
    {
        var status = PrinterStatusOptions.Default;
        var original = PrinterTarget.Spooler("Brother QL").WithStatus(status);

        var driver = original.WithMode(PrinterOutputMode.WindowsDriver);
        var acknowledged = driver.WithAcknowledgement("abc123");

        Assert.Equal(PrinterOutputMode.PrinterLanguage, original.Mode);
        Assert.Null(original.AcknowledgedFindings);
        Assert.Equal(PrinterOutputMode.WindowsDriver, driver.Mode);
        Assert.Null(driver.AcknowledgedFindings);
        Assert.Equal("abc123", acknowledged.AcknowledgedFindings);
        Assert.Equal(PrinterOutputMode.WindowsDriver, acknowledged.Mode);

        // Everything else survives both copies.
        Assert.Equal(PrinterTransport.Spooler, acknowledged.Transport);
        Assert.Equal("Brother QL", acknowledged.PrinterName);
        Assert.Same(status, acknowledged.Status);
    }

    /// <summary>The acknowledgement can be cleared again with null.</summary>
    [Fact]
    public void AcknowledgementCanBeCleared() =>
        Assert.Null(PrinterTarget.Spooler("x").WithAcknowledgement("abc").WithAcknowledgement(null).AcknowledgedFindings);

    /// <summary>Switching a driver target back to the printer language works.</summary>
    [Fact]
    public void SpoolerTargetCanSwitchBack() =>
        Assert.Equal(PrinterOutputMode.PrinterLanguage, PrinterTarget.Spooler("x").WithMode(PrinterOutputMode.WindowsDriver).WithMode(PrinterOutputMode.PrinterLanguage).Mode);

    /// <summary>The new error kinds follow the derived key rule the UI maps to text.</summary>
    [Fact]
    public void ErrorKeysAreDerived()
    {
        Assert.Equal("Print.Error.NotAcknowledged", PrintError.Create(PrintErrorKind.NotAcknowledged).Key);
        Assert.Equal("Print.Error.DriverRefused", PrintError.Create(PrintErrorKind.DriverRefused, "Brother", "no page").Key);
    }

    /// <summary>Existing results report no pages; WithPages returns a copy and leaves the original alone.</summary>
    [Fact]
    public void PagesSentDefaultsToZeroAndWithPagesIsACopy()
    {
        var ok = PrintResult.Ok(5);
        Assert.Equal(0, ok.PagesSent);

        var withPages = ok.WithPages(3);
        Assert.Equal(0, ok.PagesSent);
        Assert.Equal(3, withPages.PagesSent);
        Assert.Equal(5, withPages.BytesSent);
        Assert.True(withPages.Succeeded);
    }

    /// <summary>A failure keeps its error and byte count when pages are attached, and notices survive too.</summary>
    [Fact]
    public void WithPagesKeepsErrorBytesAndNotices()
    {
        var notice = new Rotula.Core.Validation.ValidationIssue(Rotula.Core.Validation.ValidationSeverity.Warning, "Some.Code");
        var failed = PrintResult.Fail(PrintError.Create(PrintErrorKind.DriverRefused, "p", "d"), 7).WithNotices([notice]).WithPages(2);

        Assert.False(failed.Succeeded);
        Assert.Equal(PrintErrorKind.DriverRefused, failed.Error!.Kind);
        Assert.Equal(7, failed.BytesSent);
        Assert.Equal(2, failed.PagesSent);
        Assert.Single(failed.Notices);
    }

    /// <summary>WithNotices after WithPages must not drop the page count.</summary>
    [Fact]
    public void WithNoticesKeepsPages()
    {
        var notice = new Rotula.Core.Validation.ValidationIssue(Rotula.Core.Validation.ValidationSeverity.Warning, "Some.Code");
        Assert.Equal(4, PrintResult.Ok(0).WithPages(4).WithNotices([notice]).PagesSent);
    }
}

