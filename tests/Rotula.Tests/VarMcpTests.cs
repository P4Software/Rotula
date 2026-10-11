using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Mcp.Tools;

namespace Rotula.Tests;

/// <summary>set_variable, the previews, validation and print_label with date, counter, prompt and computed variables.</summary>
public class VarMcpTests : IDisposable
{
    private readonly McpTestEnv _env = new(s => s with { AllowPrinting = true, AllowedPrinters = ["Zebra ZD420"], MaxCopies = 5 });

    public VarMcpTests()
    {
        _env.Context.Time = FakeTime.AtLocal(2026, 3, 7, 14, 5, 9);
        _env.Label.CreateLabel(100, 50);
    }

    public void Dispose() => _env.Dispose();

    private static Dictionary<string, JsonElement> Def(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private string Zpl() => _env.Label.GenerateZpl().Zpl;

    [Fact]
    public void TheOldCall_StillMakesATextVariable()
    {
        var r = _env.Label.SetVariable("name", "Ana");
        var v = Assert.Single(r.Label.Variables);
        Assert.Equal(("name", "Ana", "text", ""), (v.Name, v.Sample, v.Kind, v.Definition));
        _env.Label.SetVariable("name", "Luis");
        Assert.Equal("Luis", Assert.Single(_env.Label.GetLabel().Label.Variables).Sample);
    }

    [Fact]
    public void ADateVariable_ThatIsTodayPlus30Days_PrintedAsGs1()
    {
        var r = _env.Label.SetVariable("expiry", kind: "date", definition: Def(new { offsets = new[] { "+30 days" }, format = "gs1" }));
        Assert.Equal("date", Assert.Single(r.Label.Variables).Kind);
        Assert.Contains("+30 days", r.Label.Variables[0].Definition);
        _env.Elements.AddText("EXP {{expiry}}", 5, 5);
        Assert.Contains("^FDEXP 260406^FS", Zpl());
        var v = _env.Label.GetLabel().Label.Variables.Single();
        Assert.Equal("gs1", _env.Context.Session.GetCopy().Document.Variables[0].Date!.Format);
        Assert.NotEmpty(v.Definition);
    }

    [Fact]
    public void TheDefinitionIsKeptWhenOnlyTheSampleChanges_AndSettingsAreMerged()
    {
        _env.Label.SetVariable("d", kind: "date", definition: Def(new { offsets = new[] { "+1 day" }, format = "iso" }));
        _env.Label.SetVariable("d", "fallback", definition: Def(new { format = "compact" }));
        var v = _env.Context.Session.GetCopy().Document.Variables.Single();
        Assert.Equal(("fallback", "compact", 1), (v.SampleValue, v.Date!.Format, v.Date.Offsets.Count));
        _env.Label.SetVariable("d", "fallback2");  // no kind, no definition: nothing else changes
        Assert.Equal(VariableKind.Date, _env.Context.Session.GetCopy().Document.Variables.Single().Kind);
        _env.Label.SetVariable("d", kind: "text");   // back to a plain variable: the definition is dropped
        var plain = _env.Context.Session.GetCopy().Document.Variables.Single();
        Assert.Equal(VariableKind.Text, plain.Kind);
        Assert.Null(plain.Date);
    }

    [Fact]
    public void AllKinds_CanBeSetAndDescribed()
    {
        _env.Label.SetVariable("julian", kind: "date", definition: Def(new { format = "julian" }));
        _env.Label.SetVariable("serial", kind: "counter", definition: Def(new { start = "100", padding = 5, prefix = "SN", remember = true }));
        _env.Label.SetVariable("shift", kind: "prompt", definition: Def(new { label = "Shift", type = "choice", choices = new[] { "A", "B" }, required = true }));
        var r = _env.Label.SetVariable("lot", kind: "computed", definition: Def(new { expression = "'L' & julian & '-' & serial" }));
        Assert.Equal(new[] { "date", "counter", "prompt", "computed" }, r.Label.Variables.Select(v => v.Kind));
        Assert.Contains("remembers", r.Label.Variables[1].Definition);
        Assert.Contains("required", r.Label.Variables[2].Definition);
        Assert.StartsWith("computed:", r.Label.Variables[3].Definition);
    }

    public static IEnumerable<object[]> Refusals() => new[]
    {
        new object[] { "d", "date", new { nope = 1 } },
        new object[] { "d", "date", new { offsets = new[] { "soon" } } },
        new object[] { "d", "date", new { offsets = new[] { "+30 fortnights" } } },
        new object[] { "d", "date", new { offsets = Enumerable.Repeat("+1 day", 40).ToArray() } },
        new object[] { "d", "date", new { offsets = new[] { "+99999999 days" } } },
        new object[] { "d", "date", new { format = "yyyy-MM-dd'unclosed" } },
        new object[] { "d", "date", new { source = "moon" } },
        new object[] { "d", "date", new { fixedDate = "yesterday" } },
        new object[] { "d", "date", new { source = "fixed" } },
        new object[] { "d", "date", new { rounding = "weekly" } },
        new object[] { "d", "date", new { holidays = new[] { "xmas" } } },
        new object[] { "c", "counter", new { start = "abc" } },
        new object[] { "c", "counter", new { padding = 99 } },
        new object[] { "c", "counter", new { prefix = new string('x', 100) } },
        new object[] { "c", "counter", new { overflow = "explode" } },
        new object[] { "p", "prompt", new { pattern = "((" } },
        new object[] { "p", "prompt", new { type = "choice" } },
        new object[] { "p", "prompt", new { type = "hologram" } },
        new object[] { "p", "prompt", new { choices = "A,B" } },
        new object[] { "p", "prompt", new { minLength = -1 } },
        new object[] { "x", "computed", new { expression = "upper(" } },
        new object[] { "x", "computed", new { expression = "nosuch(1)" } },
        new object[] { "x", "computed", new { expression = new string('a', 2000) } },
        new object[] { "x", "computed", new { expression = "x" } },   // refers to itself
        new object[] { "x", "wizard", new { } },
        new object[] { "x", "text", new { expression = "'a'" } },
    };

    [Theory, MemberData(nameof(Refusals))]
    public void BadDefinitions_AreRefused_AndNothingChanges(string name, string kind, object definition)
    {
        _env.Label.SetVariable("keep", "1");
        var ex = Assert.Throws<McpException>(() => _env.Label.SetVariable(name, kind: kind, definition: Def(definition)));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal("keep", Assert.Single(_env.Context.Session.GetCopy().Document.Variables).Name);
    }

    [Fact]
    public void SettingsWithoutAKind_OnAPlainVariable_AreRefusedWithAHint()
    {
        _env.Label.SetVariable("v", "x");
        var ex = Assert.Throws<McpException>(() => _env.Label.SetVariable("v", definition: Def(new { format = "iso" })));
        Assert.Contains("kind", ex.Message);
    }

    [Fact]
    public void ARefusedDefinition_DoesNotCountAsAnUndoStep()
    {
        _env.Label.SetVariable("a", "1");
        int before = _env.Context.Session.UndoSteps;
        Assert.Throws<McpException>(() => _env.Label.SetVariable("b", kind: "computed", definition: Def(new { expression = "upper(" })));
        Assert.Equal(before, _env.Context.Session.UndoSteps);
    }

    [Fact]
    public void ValidateLabel_ReportsExpressionProblems_AndUnknownReferences()
    {
        _env.Label.SetVariable("fine", kind: "computed", definition: Def(new { expression = "'a' & ghost" }));
        var v = _env.Label.ValidateLabel();
        Assert.Contains(v.Issues, i => i.Key == VariableMessageKeys.ExpressionUnknownVariable);
        Assert.True(v.Printable); // a column may exist at merge time: only a warning
    }

    [Fact]
    public void RenderPreview_AndGenerateZpl_UseTheResolvedValues()
    {
        _env.Label.SetVariable("expiry", kind: "date", definition: Def(new { offsets = new[] { "+30 days" }, format = "gs1" }));
        _env.Elements.AddText("EXP {{expiry}}", 5, 5);
        var preview = _env.Preview.RenderPreview();
        Assert.NotEmpty(preview.Content);
        Assert.Contains("^FDEXP 260406^FS", Zpl());
        // the plain-sample path would have printed the empty sample
        Assert.DoesNotContain("^FDEXP ^FS", Zpl());
    }

    [Fact]
    public void ThePrinterClock_BecomesClockCodesInGeneratedZpl_AndAWarningWhenItCannot()
    {
        _env.Label.SetVariable("now", kind: "date", definition: Def(new { source = "printer", format = "yyyy-MM-dd HH:mm" }));
        _env.Elements.AddText("{{now}}", 5, 5);
        Assert.Contains("^FC%^FD%Y-%m-%d %H:%M^FS", Zpl());

        _env.Label.SetVariable("eom", kind: "date", definition: Def(new { source = "printer", rounding = "EndOfMonth" }));
        _env.Elements.AddText("{{eom}}", 5, 15);
        var v = _env.Label.ValidateLabel();
        Assert.Contains(v.Issues, i => i.Key == VariableMessageKeys.PrinterRounding && i.Severity == "warning");
    }

    [Fact]
    public void MergePreview_ResolvesTheLabelsOwnVariables_PerRecord()
    {
        _env.Label.SetVariable("expiry", kind: "date", definition: Def(new { offsets = new[] { "+1 day" }, format = "iso" }));
        _env.Label.SetVariable("serial", kind: "counter", definition: Def(new { padding = 3, prefix = "N" }));
        _env.Label.SetVariable("code", kind: "computed", definition: Def(new { expression = "upper(left(name, 2)) & '-' & serial" }));
        _env.Elements.AddText("{{name}} {{expiry}} {{code}}", 5, 5);
        File.WriteAllText(Path.Combine(_env.Labels, "people.csv"), "name\nAna\nLuis\n");
        var r = _env.Preview.MergePreview("people.csv", record: 2);
        var merge = r.StructuredContent!.Value.GetProperty("merge");
        Assert.Equal("N002", merge.GetProperty("record").GetProperty("serial").GetString());
        Assert.Equal("LU-N002", merge.GetProperty("record").GetProperty("code").GetString());
        Assert.Equal("2026-03-08", merge.GetProperty("record").GetProperty("expiry").GetString());
        Assert.Contains("^FDLuis 2026-03-08 LU-N002^FS", merge.GetProperty("zpl").GetString());
        Assert.Empty(merge.GetProperty("unmapped").EnumerateArray());   // the label defines expiry, serial and code itself
    }

    // ------------------------------------------------------------------------------------ print_label

    private static string Sent(McpFakePrinter p) => Encoding.UTF8.GetString(p.Jobs.Single().Data);

    [Fact]
    public async Task Print_WorksOutDatesAndComputedValues_WhenTheJobIsSent()
    {
        _env.Label.SetVariable("expiry", kind: "date", definition: Def(new { offsets = new[] { "+30 days" }, format = "gs1" }));
        _env.Label.SetVariable("lot", kind: "computed", definition: Def(new { expression = "'L' & date('2026-03-07', 'julian')" }));
        _env.Elements.AddText("EXP {{expiry}} {{lot}}", 5, 5);

        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 2);
        Assert.Equal("dry-run", dry.Status);
        // a minute later the token still matches: dates are worked out when the job is sent, they do not make the confirmation fail
        _env.Context.Time = FakeTime.AtLocal(2026, 3, 7, 14, 6, 30);
        var sent = await _env.Printing.PrintLabel("Zebra ZD420", 2, dry.ConfirmationToken);
        Assert.Equal("sent", sent.Status);
        Assert.Contains("EXP 260406 L066", Sent(_env.Printer));
    }

    [Fact]
    public async Task Print_RefusesACounterThatRemembers()
    {
        _env.Label.SetVariable("serial", kind: "counter", definition: Def(new { remember = true }));
        _env.Elements.AddText("{{serial}}", 5, 5);
        var ex = await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1));
        Assert.Contains("remembers", ex.Message);
        Assert.Empty(_env.Printer.Jobs);

        _env.Label.SetVariable("serial", kind: "counter", definition: Def(new { remember = false, padding = 4 }));
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);
        Assert.Equal("dry-run", dry.Status);
    }

    [Fact]
    public async Task Print_ACountingLabelWithCopies_IsOneJobExactlyAsConfirmed()
    {
        _env.Label.SetVariable("serial", kind: "counter", definition: Def(new { remember = false, padding = 4 }));
        _env.Elements.AddText("{{serial}}", 5, 5);
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 5);
        var sent = await _env.Printing.PrintLabel("Zebra ZD420", 5, dry.ConfirmationToken);
        Assert.Equal("sent", sent.Status);
        // One job with the copies in ^PQ, as the dry run showed; not separate labels.
        Assert.Single(_env.Printer.Jobs);
        Assert.Contains("^PQ5,", Sent(_env.Printer));
    }

    [Fact]
    public async Task Print_NeedsAnswersForPrompts_AndChecksThem()
    {
        _env.Label.SetVariable("shift", kind: "prompt", definition: Def(new { label = "Which shift?", type = "choice", choices = new[] { "Day", "Night" }, required = true }));
        _env.Elements.AddText("{{shift}}", 5, 5);

        var ask = await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1));
        Assert.Contains("Which shift?", ask.Message);
        Assert.Contains("Day/Night", ask.Message);

        var bad = await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1, answers: new() { ["shift"] = "Dusk" }));
        Assert.Contains("shift", bad.Message);

        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1, answers: new() { ["shift"] = "night" });
        Assert.Equal("dry-run", dry.Status);
        var sent = await _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken, new() { ["shift"] = "night" });
        Assert.Equal("sent", sent.Status);
        Assert.Contains("^FDNight^FS", Sent(_env.Printer));

        // a different answer is a different job: the old token does not work
        var other = await _env.Printing.PrintLabel("Zebra ZD420", 1, answers: new() { ["shift"] = "Day" });
        Assert.NotEqual(dry.ConfirmationToken, other.ConfirmationToken);
    }

    [Fact]
    public void TheLimits_RefuseOversizedDefinitionsFromOtherSources()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(new LabelVariable("x") { Kind = VariableKind.Computed, Computed = new ComputedVariableOptions { Expression = new string('a', 5000) } });
        var ex = Assert.Throws<McpException>(() => Rotula.Mcp.Security.LimitChecks.Verify(doc, _env.Settings));
        Assert.Contains("larger than the limits", ex.Message);
    }

    [Fact]
    public void ThePreviewShowsTheNextRememberedNumber_AndNeverChangesIt()
    {
        string counters = Path.Combine(_env.Root, "counters.json");
        var store = new Rotula.Files.FileCounterStore(counters);
        _env.Context.CounterStore = store;
        _env.Label.SetVariable("serial", kind: "counter", definition: Def(new { remember = true, padding = 4 }));
        _env.Elements.AddText("{{serial}}", 5, 5);
        var doc = _env.Context.Session.GetCopy().Document;
        doc.EnsureId();
        _env.Context.Session.Replace(doc, "n", null, null, "test", dirty: true);

        var job = VariableJob.Begin(doc, new VariableContext { CounterStore = store }, 7);
        job.Complete(7);
        string before = File.ReadAllText(counters);
        Assert.Contains("^FD0008^FS", Zpl());
        _env.Preview.RenderPreview();
        Assert.Equal(before, File.ReadAllText(counters)); // previews only read
    }
}
