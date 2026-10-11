using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Prompts;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>Print-time prompts: what is asked, how answers are checked and normalized, and how they combine with data records.</summary>
public class VarPromptTests
{
    private static PromptValidation Check(Action<PromptVariableOptions> configure, string? answer)
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Prompt("p", configure));
        var set = PromptSet.Build(doc);
        return set.Validate(answer is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["p"] = answer });
    }

    private static readonly List<object?[]> Cases = new()
    {
        // configure, answer, expected value, expected key (null = valid), expected args after the name
        new object?[] { (Action<PromptVariableOptions>)(p => { }), "hello", "hello", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => { }), "  padded  ", "padded", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => { }), "line1\r\nline2\ttab", "line1 line2 tab", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => { }), "bell\u0007char", "bellchar", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => { }), null, "", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => p.Required = true), "", "", VariableMessageKeys.PromptRequired, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.Required = true), "   ", "", VariableMessageKeys.PromptRequired, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.Required = true), null, "", VariableMessageKeys.PromptRequired, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.Uppercase = true), "abc-é", "ABC-É", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => p.MinLength = 3), "ab", "ab", VariableMessageKeys.PromptTooShort, new[] { "3" } },
        new object?[] { (Action<PromptVariableOptions>)(p => p.MinLength = 3), "abc", "abc", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => p.MaxLength = 4), "abcde", "abcd", VariableMessageKeys.PromptTooLong, new[] { "4" } },
        new object?[] { (Action<PromptVariableOptions>)(p => p.MaxLength = 4), "abcd", "abcd", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => p.Pattern = "[A-Z]{3}[0-9]{2}"), "ABC12", "ABC12", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => p.Pattern = "[A-Z]{3}[0-9]{2}"), "ABC123", "ABC123", VariableMessageKeys.PromptPattern, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.Pattern = "[0-9]+"), "12abc", "12abc", VariableMessageKeys.PromptPattern, new string[0] }, // whole answer, not a substring
        new object?[] { (Action<PromptVariableOptions>)(p => p.Pattern = "(a"), "a", "a", VariableMessageKeys.PromptPatternInvalid, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.Pattern = @"(?<=a)b"), "ab", "ab", VariableMessageKeys.PromptPatternInvalid, new string[0] }, // look-behind is not supported by the safe engine
        new object?[] { (Action<PromptVariableOptions>)(p => p.Pattern = @"(a)\1"), "aa", "aa", VariableMessageKeys.PromptPatternInvalid, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => { p.Uppercase = true; p.Pattern = "[A-Z]+"; }), "abc", "ABC", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Number), "42", "42", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Number), "-3.25", "-3.25", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Number), "1,5", "1,5", VariableMessageKeys.PromptNotNumber, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Number), "12e3", "12e3", VariableMessageKeys.PromptNotNumber, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Number), ".5", ".5", VariableMessageKeys.PromptNotNumber, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Number), "NaN", "NaN", VariableMessageKeys.PromptNotNumber, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Date), "2026-03-07", "2026-03-07", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Date), "20260307", "2026-03-07", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => { p.InputType = PromptInputType.Date; p.DateFormat = "gs1"; }), "2026-03-07", "260307", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Date), "2026-02-30", "2026-02-30", VariableMessageKeys.PromptNotDate, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Date), "07/03/2026", "07/03/2026", VariableMessageKeys.PromptNotDate, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => { p.InputType = PromptInputType.Choice; p.Choices.AddRange(new[] { "Red", "Green" }); }), "green", "Green", null, null },
        new object?[] { (Action<PromptVariableOptions>)(p => { p.InputType = PromptInputType.Choice; p.Choices.AddRange(new[] { "Red", "Green" }); }), "Blue", "Blue", VariableMessageKeys.PromptNotChoice, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => p.InputType = PromptInputType.Choice), "x", "x", VariableMessageKeys.PromptNoChoices, new string[0] },
        new object?[] { (Action<PromptVariableOptions>)(p => { p.InputType = PromptInputType.Choice; p.Choices.Add("A"); }), "", "", null, null },
    };

    /// <summary>Delegates cannot be serialized by the test runner, so each row is addressed by its position.</summary>
    public static IEnumerable<object[]> Table() => Enumerable.Range(0, Cases.Count).Select(i => new object[] { i });

    [Theory, MemberData(nameof(Table))]
    public void ValidationTable(int row)
    {
        var configure = (Action<PromptVariableOptions>)Cases[row][0]!;
        var answer = (string?)Cases[row][1];
        var expectedValue = (string)Cases[row][2]!;
        var expectedKey = (string?)Cases[row][3];
        var extraArgs = (string[]?)Cases[row][4];
        var result = Check(configure, answer);
        Assert.Equal(expectedValue, result.Values["p"]);
        if (expectedKey is null)
        {
            Assert.True(result.IsValid, string.Join(";", result.Issues));
            return;
        }
        var issue = Assert.Single(result.Issues);
        Assert.Equal(expectedKey, issue.MessageKey);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Equal(new[] { "p" }.Concat(extraArgs!), issue.Args);
    }

    [Fact]
    public void AHostilePattern_CannotHangTheProgram()
    {
        // Catastrophic for a backtracking engine (exponential time); the non-backtracking engine answers at once.
        var result = Check(p => p.Pattern = "(a+)+$", new string('a', 40) + "!");
        Assert.Equal(VariableMessageKeys.PromptPattern, Assert.Single(result.Issues).MessageKey);
        Assert.False(PromptRegex.IsValid(new string('a', VariableLimits.MaxPatternLength + 1)));
    }

    [Fact]
    public void TheSetListsWhatToAsk_InDeclarationOrder_WithInitialValues()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(new LabelVariable("plain", "x"));
        doc.Variables.Add(Vars.Prompt("lot", p => { p.Label = "Lot number"; p.Default = "L1"; p.Required = true; p.HelpText = "Printed on the box"; p.Uppercase = true; p.MaxLength = 8; }));
        doc.Variables.Add(Vars.Prompt("color", p => { p.InputType = PromptInputType.Choice; p.Choices.AddRange(new[] { "R", "G" }); }, sample: "G"));
        var set = PromptSet.Build(doc);
        Assert.False(set.IsEmpty);
        Assert.Equal(new[] { "lot", "color" }, set.Items.Select(i => i.Name));
        var lot = set.Items[0];
        Assert.Equal(("Lot number", "L1", true, "Printed on the box", true, 8), (lot.Label, lot.InitialValue, lot.Required, lot.HelpText, lot.Uppercase, lot.MaxLength));
        Assert.Equal(("color", "G", PromptInputType.Choice), (set.Items[1].Label, set.Items[1].InitialValue, set.Items[1].InputType));
        Assert.Equal(new[] { "R", "G" }, set.Items[1].Choices);
        Assert.True(PromptSet.Build(new LabelDocument()).IsEmpty);
    }

    [Fact]
    public void ARecordColumnWins_AndTheQuestionIsNotAsked_UnlessConfiguredOtherwise()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Prompt("batch", p => p.Default = "D"));
        doc.Variables.Add(Vars.Prompt("shift", p => { p.Default = "D"; p.OverrideRecord = true; }));
        var record = new Dictionary<string, string> { ["batch"] = "FROM-DATA", ["shift"] = "FROM-DATA" };

        var set = PromptSet.Build(doc, record);
        Assert.Equal(new[] { "shift" }, set.Items.Select(i => i.Name));

        var answers = new Dictionary<string, string> { ["batch"] = "typed", ["shift"] = "typed" };
        var r = VariableEngine.Resolve(doc, new VariableContext { Record = record, PromptAnswers = answers });
        Assert.Equal("FROM-DATA", r.Values["batch"]);   // the data wins by default
        Assert.Equal("typed", r.Values["shift"]);        // the prompt wins only when configured
        // an empty data cell does not count as supplying a value
        var empty = VariableEngine.Resolve(doc, new VariableContext { Record = new Dictionary<string, string> { ["batch"] = "" }, PromptAnswers = answers });
        Assert.Equal("typed", empty.Values["batch"]);
    }

    [Fact]
    public void WithoutAnswers_TheDefaultStandsIn_ForPreviews_AndIsAnErrorWhenPrinting()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Prompt("a", p => p.Default = "dflt", sample: "smp"));
        doc.Variables.Add(Vars.Prompt("b", null, sample: "smp"));
        var preview = VariableEngine.Resolve(doc, new VariableContext());
        Assert.Equal(("dflt", "smp"), (preview.Values["a"], preview.Values["b"]));
        Assert.Empty(preview.Issues);

        var print = VariableEngine.Resolve(doc, new VariableContext { RequirePromptAnswers = true });
        Assert.Equal(new[] { VariableMessageKeys.PromptMissingAnswer, VariableMessageKeys.PromptMissingAnswer }, print.Issues.Select(i => i.MessageKey));
        var ok = VariableEngine.Resolve(doc, new VariableContext { RequirePromptAnswers = true, PromptAnswers = new Dictionary<string, string> { ["a"] = "1", ["b"] = "" } });
        Assert.Empty(ok.Issues);
        Assert.Equal("", ok.Values["b"]);
    }

    [Fact]
    public void ValidatedAnswers_FlowIntoTheLabel_AndCombineWithRecordAndCounter()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Prompt("shift", p => { p.Uppercase = true; p.Required = true; }));
        doc.Variables.Add(Vars.Counter("n", c => c.Padding = 3));
        doc.Variables.Add(Vars.Computed("code", "shift & '-' & sku & '-' & n"));
        var set = PromptSet.Build(doc);
        var v = set.Validate(new Dictionary<string, string> { ["shift"] = " b " });
        Assert.True(v.IsValid);
        var ctx = new VariableContext { PromptAnswers = v.Values, Record = new Dictionary<string, string> { ["sku"] = "S9" }, ItemIndex = 4 };
        Assert.Equal("B-S9-005", VariableEngine.Resolve(doc, ctx).Values["code"]);
    }

    [Fact]
    public void RememberLast_OffersTheLastAnswer_NextTime()
    {
        var doc = new LabelDocument();
        doc.EnsureId();
        doc.Variables.Add(Vars.Prompt("operator", p => { p.RememberLast = true; p.Default = "nobody"; }));
        doc.Variables.Add(Vars.Prompt("note", p => p.Default = "d"));
        var memory = new MemoryPromptMemory();

        var set = PromptSet.Build(doc, null, memory);
        Assert.Equal("nobody", set.Items[0].InitialValue);
        var v = set.Validate(new Dictionary<string, string> { ["operator"] = "Ana", ["note"] = "n" });
        set.Remember(v.Values, memory);

        var next = PromptSet.Build(doc, null, memory);
        Assert.Equal("Ana", next.Items[0].InitialValue);
        Assert.Equal("d", next.Items[1].InitialValue); // only prompts that ask for it are remembered

        // a label without id has nothing to key the memory on
        var anon = new LabelDocument();
        anon.Variables.Add(Vars.Prompt("operator", p => p.RememberLast = true));
        var s2 = PromptSet.Build(anon, null, memory);
        s2.Remember(s2.Validate(new Dictionary<string, string> { ["operator"] = "Zed" }).Values, memory);
        Assert.Equal("", PromptSet.Build(anon, null, memory).Items[0].InitialValue);
    }

    [Fact]
    public void ThePromptMemoryFile_SurvivesRestarts_AndDamage()
    {
        string path = Path.Combine(Path.GetTempPath(), "rotula-prompts-" + Guid.NewGuid().ToString("N"), "prompts.json");
        try
        {
            var m1 = new FilePromptMemory(path);
            m1.Remember("lbl", new Dictionary<string, string> { ["Operator"] = "Ana" });
            Assert.Equal("Ana", new FilePromptMemory(path).Get("lbl", "operator"));
            File.WriteAllText(path, "{ nonsense");
            Assert.Null(new FilePromptMemory(path).Get("lbl", "operator"));              // damaged: no suggestion, no crash
            new FilePromptMemory(path).Remember("lbl", new Dictionary<string, string> { ["x"] = "1" });
            Assert.Equal("1", new FilePromptMemory(path).Get("lbl", "x"));               // and it heals itself
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void DesignTimeChecks_FindBadPatternsAndEmptyChoices()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Prompt("a", p => p.Pattern = "(("));
        doc.Variables.Add(Vars.Prompt("b", p => p.InputType = PromptInputType.Choice));
        doc.Variables.Add(Vars.Prompt("c", p => { p.InputType = PromptInputType.Date; p.DateFormat = "'unclosed"; }));
        var keys = VariableEngine.ValidateDefinitions(doc).Select(i => i.MessageKey).ToList();
        Assert.Equal(new[] { VariableMessageKeys.PromptPatternInvalid, VariableMessageKeys.PromptNoChoices, VariableMessageKeys.DateFormatInvalid }, keys);
    }
}
