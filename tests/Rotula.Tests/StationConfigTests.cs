using System.Xml.Linq;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Files;
using Rotula.Files.Station;

namespace Rotula.Tests;

/// <summary>The print station configuration (station.json): where it is read from, what is checked, and the message keys of every problem.</summary>
public class StationConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rotula-station-" + Guid.NewGuid().ToString("N"));

    public StationConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Label(string name, string? folder = null)
    {
        string dir = folder is null ? _dir : Path.Combine(_dir, folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".rotula");
        RotulaFileSerializer.Save(new LabelDocument(), path);
        return path;
    }

    private StationLoadResult Parse(string json) => StationConfigLoader.Parse(json, Path.Combine(_dir, StationConfig.FileName));

    private static void HasError(StationLoadResult r, string key, params string[] args)
    {
        Assert.False(r.CanOpen);
        var issue = Assert.Single(r.Issues, i => i.MessageKey == key);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        if (args.Length > 0) Assert.Equal(args, issue.Args);
    }

    [Fact]
    public void AFolderWithoutConfiguration_OffersEveryLabelByName_WithTheDesignersPrinter()
    {
        Label("pallet");
        Label("Box");
        Label("nested", "sub");
        var r = StationConfigLoader.Load(_dir);
        Assert.True(r.CanOpen);
        var c = r.Config!;
        Assert.Null(c.ConfigPath);
        Assert.Equal(new[] { "Box", "pallet" }, c.Labels.Select(l => l.Name));
        Assert.All(c.Labels, l => Assert.Null(l.Printer));
        Assert.All(c.Labels, l => Assert.Equal(StationConfig.DefaultMaxCopies, l.MaxCopies));
        Assert.Null(c.ExitPinHash);
        Assert.True(c.PrintOnLastScan);
        Assert.True(c.ScanToSelect);
        Assert.True(c.FullScreen);
    }

    [Fact]
    public void AFullConfiguration_IsReadWithItsPrintersOverridesAndKeys()
    {
        Label("pallet", "labels");
        Label("case", "labels");
        string hash = StationPin.Hash("2468", 10_000);
        File.WriteAllText(Path.Combine(_dir, StationConfig.FileName), $$"""
            // Packing line 2
            {
              "formatVersion": 1,
              "title": "Packing line 2",
              "labelFolder": "labels",
              "printers": {
                "line": { "host": "192.168.1.50", "dpi": 300, "darkness": 20, "speed": 4 },
                "desk": { "windowsPrinter": "Desk printer", "checkStatus": false },
              },
              "defaultPrinter": "line",
              "defaultCopies": 2,
              "maxCopies": 50,
              "keepValues": false,
              "exitPinHash": "{{hash}}",
              "printOnLastScan": false,
              "scanToSelect": true,
              "fullScreen": false,
              "labels": [
                { "file": "pallet.rotula", "name": "Pallet", "key": "PAL-01", "maxCopies": 5, "defaultCopies": 1, "keepFields": ["lot", " "] },
                { "file": "case.rotula", "printer": "desk", "allowDataMerge": true, "keepValues": true }
              ]
            }
            """);
        var r = StationConfigLoader.Load(_dir);
        Assert.True(r.CanOpen, string.Join(", ", r.Issues.Select(i => i.MessageKey)));
        var c = r.Config!;
        Assert.Equal(Path.Combine(_dir, StationConfig.FileName), c.ConfigPath);
        Assert.Equal("Packing line 2", c.Title);
        Assert.Equal(hash, c.ExitPinHash);
        Assert.False(c.PrintOnLastScan);
        Assert.False(c.FullScreen);

        var pallet = c.Labels[0];
        Assert.Equal("Pallet", pallet.Name);
        Assert.Equal("PAL-01", pallet.Key);
        Assert.Equal(Path.Combine(_dir, "labels", "pallet.rotula"), pallet.Path);
        Assert.Equal(5, pallet.MaxCopies);
        Assert.Equal(1, pallet.DefaultCopies);
        Assert.Equal(new[] { "lot" }, pallet.KeepFields);
        Assert.False(pallet.AllowDataMerge);
        var line = pallet.Printer!;
        Assert.Equal(("192.168.1.50", 9100, 300, 20, 4, true), (line.Host, line.Port, line.Dpi, line.Darkness, line.Speed, line.CheckStatus));
        Assert.Null(line.WindowsPrinter);

        var box = c.Labels[1];
        Assert.Equal("case", box.Name);
        Assert.Null(box.Key);
        Assert.Equal(50, box.MaxCopies);
        Assert.Equal(2, box.DefaultCopies);
        Assert.True(box.AllowDataMerge);
        Assert.True(box.KeepAllValues);
        Assert.Equal("Desk printer", box.Printer!.WindowsPrinter);
        Assert.False(box.Printer.CheckStatus);
    }

    [Fact]
    public void TheOnlyPrinter_IsTheDefault()
    {
        Label("a");
        var r = Parse("""{ "printers": { "p": { "host": "printer.local", "port": 6101 } } }""");
        Assert.True(r.CanOpen);
        Assert.Equal(("printer.local", 6101), (r.Config!.Labels[0].Printer!.Host, r.Config.Labels[0].Printer!.Port));
    }

    [Fact]
    public void AMisspeltSetting_IsAnError_WithItsLine()
    {
        Label("a");
        var r = Parse("{\n  \"maxCopy\": 5\n}");
        HasError(r, StationKeys.InvalidJson);
        Assert.Equal("2", r.Issues[0].Args[1]);
        Assert.Contains("maxCopy", r.Issues[0].Args[2]);
        HasError(Parse("{ \"maxCopies\": \"many\" }"), StationKeys.InvalidJson);
        HasError(Parse("{ not json"), StationKeys.InvalidJson);
    }

    [Fact]
    public void PrinterMistakes_AreReportedByName()
    {
        Label("a");
        HasError(Parse("""{ "printers": { "p": { } } }"""), StationKeys.PrinterTarget, "p");
        HasError(Parse("""{ "printers": { "p": { "host": "h", "windowsPrinter": "w" } } }"""), StationKeys.PrinterTarget, "p");
        HasError(Parse("""{ "printers": { "p": { "host": "h", "port": 0 } } }"""), StationKeys.PrinterPort, "p");
        HasError(Parse("""{ "printers": { "p": { "host": "h", "dpi": 250 } } }"""), StationKeys.PrinterDpi, "p");
        HasError(Parse("""{ "printers": { "p": { "host": "h", "darkness": 31 } } }"""), StationKeys.PrinterProfile, "p");
        HasError(Parse("""{ "printers": { "p": { "host": "h", "speed": 0 } } }"""), StationKeys.PrinterProfile, "p");
        HasError(Parse("""{ "printers": { "p": { "host": "h" } }, "defaultPrinter": "q" }"""), StationKeys.UnknownPrinter, "q");
        HasError(Parse("""{ "labels": [ { "file": "a.rotula", "printer": "nope" } ] }"""), StationKeys.UnknownPrinter, "nope");
    }

    [Fact]
    public void LabelMistakes_AreReported_AndAMissingLabelIsOnlyLeftOut()
    {
        Label("a");
        Label("b");
        HasError(Parse("""{ "labels": [ { "file": "a.rotula", "key": "X" }, { "file": "b.rotula", "key": " x " } ] }"""), StationKeys.DuplicateKey, "x");
        HasError(Parse("""{ "labels": [ { "file": "a.txt" } ] }"""), StationKeys.LabelNotRotula, "a.txt");
        HasError(Parse("""{ "labels": [ { "name": "no file" } ] }"""), StationKeys.LabelNoFile, "1");

        var missing = Parse("""{ "labels": [ { "file": "a.rotula" }, { "file": "gone.rotula" } ] }""");
        Assert.True(missing.CanOpen);
        Assert.Single(missing.Config!.Labels);
        var warning = Assert.Single(missing.Warnings);
        Assert.Equal(StationKeys.LabelMissing, warning.MessageKey);
        Assert.Equal("gone.rotula", warning.Args[0]);

        HasError(Parse("""{ "labels": [ { "file": "gone.rotula" } ] }"""), StationKeys.NoLabels);
        HasError(Parse("""{ "labelFolder": "empty-but-missing" }"""), StationKeys.NotFound);
    }

    [Fact]
    public void CopiesAndPin_AreChecked()
    {
        Label("a");
        HasError(Parse("""{ "maxCopies": 0 }"""), StationKeys.Copies);
        HasError(Parse("""{ "maxCopies": 5, "defaultCopies": 6 }"""), StationKeys.Copies);
        HasError(Parse("""{ "labels": [ { "file": "a.rotula", "name": "A", "maxCopies": 2, "defaultCopies": 3 } ] }"""), StationKeys.LabelCopies, "A");
        HasError(Parse("""{ "exitPinHash": "1234" }"""), StationKeys.PinHash);
        HasError(Parse("""{ "formatVersion": 99 }"""), StationKeys.NewerVersion, "99");
    }

    [Fact]
    public void ALabelFile_OpensThatLabelOnly_WithTheSettingsNextToIt()
    {
        string a = Label("a");
        Label("b");
        File.WriteAllText(Path.Combine(_dir, StationConfig.FileName), """{ "maxCopies": 7, "labels": [ { "file": "b.rotula" } ] }""");
        var r = StationConfigLoader.Load(a);
        Assert.True(r.CanOpen);
        var only = Assert.Single(r.Config!.Labels);
        Assert.Equal(a, only.Path);
        Assert.Equal(7, only.MaxCopies);

        // Without a configuration next to it the label opens with the defaults.
        string c = Label("c", "alone");
        Assert.Equal(c, Assert.Single(StationConfigLoader.Load(c).Config!.Labels).Path);
    }

    [Fact]
    public void AConfigurationFile_CanBeGivenDirectly_AndAMissingPathIsExplained()
    {
        Label("a");
        string json = Path.Combine(_dir, "line2.json");
        File.WriteAllText(json, """{ "title": "Line 2" }""");
        Assert.Equal("Line 2", StationConfigLoader.Load(json).Config!.Title);
        HasError(StationConfigLoader.Load(Path.Combine(_dir, "nothing-here")), StationKeys.NotFound);
    }

    [Fact]
    public void EveryStationKey_HasAnIssueMessage_InEveryLanguage()
    {
        var keys = typeof(StationKeys).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.True(keys.Count >= 15);
        Assert.Empty(UiStrings.MissingInAnyLanguage(keys.Select(k => "Issue." + k)));
    }
}
