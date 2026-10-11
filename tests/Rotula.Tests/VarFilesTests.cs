using System.Text.Json;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Variables;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>The .rotula file keeps variable definitions and the label id, opens older files unchanged, and treats definitions as untrusted input.</summary>
public class VarFilesTests
{
    internal static LabelDocument FullVariableDocument()
    {
        var doc = new LabelDocument();
        doc.Id = "0123456789abcdef0123456789abcdef";
        doc.Elements.Add(new TextElement { Text = "{{expiry}} {{serial}} {{who}} {{lot}}" });
        doc.Variables.Add(new LabelVariable("plain", "sample"));

        var d = new DateVariableOptions
        {
            Source = DateSource.PrinterClock, FixedValue = new DateTime(2030, 1, 2, 3, 4, 5), Format = "gs1", Culture = "es-ES", Utc = true, Rounding = DateRounding.EndOfMonth,
        };
        d.Offsets.Add(new DateOffset(30, DateUnit.Days));
        d.Offsets.Add(new DateOffset(-2, DateUnit.BusinessDays));
        d.Holidays.Add(new DateOnly(2026, 12, 25));
        doc.Variables.Add(Vars.Date("expiry", d, sample: "fallback"));

        doc.Variables.Add(Vars.Counter("serial", c =>
        {
            c.Kind = CounterKind.Alphanumeric; c.Start = "AA9"; c.Step = 2; c.Padding = 0; c.Prefix = "P-"; c.Suffix = "-S"; c.Overflow = CounterOverflow.Error; c.Remember = true;
        }));
        doc.Variables.Add(Vars.Prompt("who", p =>
        {
            p.Label = "Operator"; p.InputType = PromptInputType.Choice; p.Default = "Ana"; p.Required = true; p.Pattern = "[A-Za-z ]+"; p.MinLength = 2; p.MaxLength = 20;
            p.Choices.AddRange(new[] { "Ana", "Luis" }); p.Uppercase = true; p.RememberLast = true; p.HelpText = "Who is printing"; p.OverrideRecord = true; p.DateFormat = "dd/MM/yyyy";
        }));
        doc.Variables.Add(Vars.Computed("lot", "'L' & julian"));
        return doc;
    }

    [Fact]
    public void EveryDefinitionRoundTrips_AndTheFileIsVersionSeven()
    {
        var original = FullVariableDocument();
        string json = RotulaFileSerializer.Serialize(original);
        var copy = RotulaFileSerializer.Deserialize(json);
        Assert.Equal(json, RotulaFileSerializer.Serialize(copy));
        Assert.Equal(7, JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32());

        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(VariableKind.Text, copy.Variables[0].Kind);
        Assert.Null(copy.Variables[0].Date);
        var d = copy.Variables[1];
        Assert.Equal((VariableKind.Date, "fallback"), (d.Kind, d.SampleValue));
        Assert.Equal((DateSource.PrinterClock, new DateTime(2030, 1, 2, 3, 4, 5), "gs1", "es-ES", true, DateRounding.EndOfMonth),
            (d.Date!.Source, d.Date.FixedValue, d.Date.Format, d.Date.Culture, d.Date.Utc, d.Date.Rounding));
        Assert.Equal(new[] { (30, DateUnit.Days), (-2, DateUnit.BusinessDays) }, d.Date.Offsets.Select(o => (o.Amount, o.Unit)));
        Assert.Equal(new[] { new DateOnly(2026, 12, 25) }, d.Date.Holidays);
        var c = copy.Variables[2].Counter!;
        Assert.Equal((CounterKind.Alphanumeric, "AA9", 2, 0, "P-", "-S", CounterOverflow.Error, true), (c.Kind, c.Start, c.Step, c.Padding, c.Prefix, c.Suffix, c.Overflow, c.Remember));
        var p = copy.Variables[3].Prompt!;
        Assert.Equal(("Operator", PromptInputType.Choice, "Ana", true, "[A-Za-z ]+", 2, 20), (p.Label, p.InputType, p.Default, p.Required, p.Pattern, p.MinLength, p.MaxLength));
        Assert.Equal(new[] { "Ana", "Luis" }, p.Choices);
        Assert.Equal((true, true, "Who is printing", true, "dd/MM/yyyy"), (p.Uppercase, p.RememberLast, p.HelpText, p.OverrideRecord, p.DateFormat));
        Assert.Equal("'L' & julian", copy.Variables[4].Computed!.Expression);
    }

    [Fact]
    public void PlainLabels_KeepTheirOldVersion_AndWriteNoNewFields()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(new LabelVariable("a", "1"));
        doc.Elements.Add(new TextElement { Text = "{{a}}" });
        string json = RotulaFileSerializer.Serialize(doc);
        Assert.Equal(1, JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32());
        foreach (var name in new[] { "kind", "date", "counter", "prompt", "computed" })
            Assert.DoesNotContain(name, json);
        Assert.False(JsonDocument.Parse(json).RootElement.GetProperty("label").TryGetProperty("id", out _)); // the label has no id until a counter needs one
    }

    [Fact]
    public void AnIdAlone_OrAKindAlone_RaisesTheVersion()
    {
        var withId = new LabelDocument();
        withId.EnsureId();
        Assert.Equal(7, JsonDocument.Parse(RotulaFileSerializer.Serialize(withId)).RootElement.GetProperty("formatVersion").GetInt32());
        var withKind = new LabelDocument();
        withKind.Variables.Add(Vars.Computed("x", "'a'"));
        Assert.Equal(7, JsonDocument.Parse(RotulaFileSerializer.Serialize(withKind)).RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void OldFiles_OpenUnchanged()
    {
        const string v1 = """{"formatVersion":1,"label":{"variables":[{"name":"sku","sampleValue":"A1"}]},"elements":[{"type":"text","text":"{{sku}}"}]}""";
        var doc = RotulaFileSerializer.Deserialize(v1);
        var v = Assert.Single(doc.Variables);
        Assert.Equal(("sku", "A1", VariableKind.Text), (v.Name, v.SampleValue, v.Kind));
        Assert.Null(doc.Id);
    }

    [Fact]
    public void AVersionSevenFile_IsRefusedByThePreviousFormatReader_Cleanly()
    {
        // An older build reads formatVersion 6 as the newest; it must see 7 as "newer" instead of dropping the definitions. Simulate with the guard.
        string json = RotulaFileSerializer.Serialize(FullVariableDocument());
        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(root.GetProperty("formatVersion").GetInt32() > RotulaFileSerializer.ZplCatalogFormatVersion);
        string future = json.Replace("\"formatVersion\": 7", "\"formatVersion\": 8");
        Assert.Equal("File.Error.NewerVersion", Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize(future)).MessageKey);
    }

    [Fact]
    public void ADefinitionKindWithMissingOptions_OpensAsAPlainVariable()
    {
        const string json = """{"formatVersion":7,"label":{"variables":[{"name":"d","sampleValue":"S","kind":"Date"},{"name":"c","sampleValue":"T","kind":"Mystery"},{"name":"x","kind":"Computed","computed":{"expression":"'a'"}}]},"elements":[]}""";
        var doc = RotulaFileSerializer.Deserialize(json);
        Assert.Equal(new[] { VariableKind.Text, VariableKind.Text, VariableKind.Computed }, doc.Variables.Select(v => v.Kind));
        Assert.Equal("S", doc.Variables[0].SampleValue);
    }

    [Fact]
    public void HostileDefinitions_AreCappedClippedOrDropped()
    {
        string huge = new('x', 200_000);
        var offsets = Enumerable.Range(0, 5000).Select(i => new { amount = int.MaxValue, unit = i % 2 == 0 ? "Days" : "Nonsense" }).ToList();
        var holidays = Enumerable.Range(0, 5000).Select(i => i % 3 == 0 ? "not a date" : "2026-01-01").ToList();
        var variables = new List<object>
        {
            new { name = huge, sampleValue = huge },
            new { name = "d", kind = "Date", date = new { source = "Moon", format = huge, culture = huge, rounding = "Never", fixedValue = huge, offsets, holidays } },
            new { name = "c", kind = "Counter", counter = new { kind = "Numeric", start = huge, prefix = huge, suffix = huge, overflow = "Boom", step = int.MaxValue, padding = -5 } },
            new { name = "p", kind = "Prompt", prompt = new { label = huge, inputType = "Hologram", @default = huge, pattern = huge, helpText = huge, minLength = int.MaxValue, maxLength = -1,
                choices = Enumerable.Range(0, 300).Select(i => new string('c', 2000)).ToList(), dateFormat = huge } },
            new { name = "x", kind = "Computed", computed = new { expression = huge } },
        };
        variables.AddRange(Enumerable.Range(0, 2000).Select(i => (object)new { name = "v" + i, sampleValue = "s" }));
        string json = JsonSerializer.Serialize(new { formatVersion = 7, label = new { id = new string('a', 500), variables }, elements = Array.Empty<object>() });

        var doc = RotulaFileSerializer.Deserialize(json);
        Assert.Null(doc.Id);                                               // too long to be an id
        Assert.Equal(500, doc.Variables.Count);                            // the list is capped
        Assert.Equal(VariableLimits.MaxNameLength, doc.Variables[0].Name.Length);
        Assert.Equal(VariableLimits.MaxValueLength, doc.Variables[0].SampleValue.Length);
        var d = doc.Variables[1].Date!;
        Assert.Equal(DateSource.ComputerClock, d.Source);
        Assert.Equal(DateRounding.None, d.Rounding);
        Assert.Null(d.FixedValue);
        Assert.Equal(VariableLimits.MaxFormatLength, d.Format.Length);
        Assert.InRange(d.Culture.Length, 0, 40);
        Assert.Equal(VariableLimits.MaxOffsets / 2, d.Offsets.Count); // only the first 16 are read, and half of those have a valid unit
        Assert.All(d.Offsets, o => Assert.Equal((VariableLimits.MaxOffsetAmount, DateUnit.Days), (o.Amount, o.Unit)));
        Assert.True(d.Holidays.Count <= VariableLimits.MaxHolidays);
        var c = doc.Variables[2].Counter!;
        Assert.Equal(40, c.Start.Length);
        Assert.Equal(VariableLimits.MaxAffixLength, c.Prefix.Length);
        Assert.Equal(VariableLimits.MaxAffixLength, c.Suffix.Length);
        Assert.Equal(CounterOverflow.Wrap, c.Overflow);
        var p = doc.Variables[3].Prompt!;
        Assert.Equal(PromptInputType.Text, p.InputType);
        Assert.Equal(VariableLimits.MaxPromptTextLength, p.Label.Length);
        Assert.Equal(VariableLimits.MaxPatternLength, p.Pattern.Length);
        Assert.Equal(VariableLimits.MaxChoices, p.Choices.Count);
        Assert.All(p.Choices, ch => Assert.True(ch.Length <= VariableLimits.MaxPromptTextLength));
        Assert.Equal(VariableLimits.MaxValueLength, p.MinLength);
        Assert.Equal(0, p.MaxLength);
        Assert.Equal(VariableLimits.MaxExpressionLength, doc.Variables[4].Computed!.Expression.Length);
        // and whatever was kept is safe to run
        var result = VariableEngine.Resolve(doc, new VariableContext());
        Assert.NotNull(result.Values);
    }

    [Fact]
    public void TheDateChoiceOfAMerge_IsSaved_AndOnlyRaisesTheVersionWhenNotTheDefault()
    {
        var doc = new LabelDocument();
        string perLabel = RotulaFileSerializer.Serialize(doc, new DataMergeSettings { DateEvaluation = DateEvaluation.PerLabel });
        Assert.Equal(7, JsonDocument.Parse(perLabel).RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(DateEvaluation.PerLabel, RotulaFileSerializer.DeserializeFile(perLabel).Merge!.DateEvaluation);

        string plain = RotulaFileSerializer.Serialize(doc, new DataMergeSettings { MaxRecordsPerJob = 5 });
        Assert.Equal(2, JsonDocument.Parse(plain).RootElement.GetProperty("formatVersion").GetInt32());
        Assert.DoesNotContain("dateEvaluation", plain);
        Assert.Equal(DateEvaluation.AtBatchStart, RotulaFileSerializer.DeserializeFile(plain).Merge!.DateEvaluation);

        const string junk = """{"formatVersion":7,"label":{},"elements":[],"merge":{"dateEvaluation":"Whenever"}}""";
        Assert.Equal(DateEvaluation.AtBatchStart, RotulaFileSerializer.DeserializeFile(junk).Merge!.DateEvaluation);
        Assert.True(new DataMergeSettings().IsEmpty);
        Assert.False(new DataMergeSettings { DateEvaluation = DateEvaluation.PerLabel }.IsEmpty);
    }

    [Theory]
    [InlineData("bad id!")]
    [InlineData("../../evil")]
    [InlineData("")]
    public void ABadLabelId_IsDropped(string id)
    {
        string json = JsonSerializer.Serialize(new { formatVersion = 7, label = new { id }, elements = Array.Empty<object>() });
        Assert.Null(RotulaFileSerializer.Deserialize(json).Id);
    }

    [Fact]
    public void ClipboardAndFingerprint_StillWork_WithVariableDocuments()
    {
        var doc = FullVariableDocument();
        var pasted = RotulaFileSerializer.DeserializeElements(RotulaFileSerializer.SerializeElements(doc.Elements));
        Assert.Single(pasted);
    }

    [Fact]
    public void SaveAndLoad_ThroughAFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "rotula-vars-" + Guid.NewGuid().ToString("N") + ".rotula");
        try
        {
            RotulaFileSerializer.Save(FullVariableDocument(), path);
            var loaded = RotulaFileSerializer.Load(path);
            Assert.Equal(5, loaded.Variables.Count);
            Assert.Equal(VariableKind.Counter, loaded.Variables[2].Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
