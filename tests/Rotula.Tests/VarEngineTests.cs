using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Zpl;

namespace Rotula.Tests;

/// <summary>Resolution order, ZPL generation with variables (including the printer clock), and batch merge with every kind.</summary>
public class VarEngineTests
{
    private static LabelDocument Doc(params LabelElement[] elements) => CoreTestSupport.NewDoc(elements);

    private static TextElement Text(string text, int y = 20) => new() { Id = "t" + y, Text = text, X = 20, Y = y };

    private static VariableContext Ctx(FakeTime? time = null) => new() { Time = time ?? FakeTime.AtLocal(2026, 3, 7, 14, 5, 9) };

    // ------------------------------------------------------------------------------------ order and precedence

    [Fact]
    public void ResolutionOrder_RecordThenSamplesThenPromptThenCounterThenDateThenComputed()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(new LabelVariable("plain", "SAMPLE"));
        doc.Variables.Add(new LabelVariable("fromdata", "SAMPLE"));
        doc.Variables.Add(Vars.Prompt("ask", p => p.Default = "DEFAULT"));
        doc.Variables.Add(Vars.Counter("serial", c => c.Padding = 2));
        doc.Variables.Add(Vars.Date("day", "iso"));
        doc.Variables.Add(Vars.Computed("all", "plain & '|' & fromdata & '|' & ask & '|' & serial & '|' & day & '|' & fromrecord"));
        var record = new Dictionary<string, string> { ["fromdata"] = "DATA", ["fromrecord"] = "REC" };

        var r = VariableEngine.Resolve(doc, new VariableContext { Time = FakeTime.AtLocal(2026, 3, 7), Record = record });
        Assert.Equal("SAMPLE|DATA|DEFAULT|01|2026-03-07|REC", r.Values["all"]);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void CounterDateAndComputed_ReplaceADataColumnOfTheSameName_WithAWarning_ButPlainDoesNot()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Counter("serial"));
        doc.Variables.Add(Vars.Date("day", "iso"));
        doc.Variables.Add(Vars.Computed("c", "'x'"));
        doc.Variables.Add(new LabelVariable("plain", "S"));
        var record = new Dictionary<string, string> { ["serial"] = "DATA1", ["day"] = "DATA2", ["c"] = "DATA3", ["plain"] = "DATA4" };
        var r = VariableEngine.Resolve(doc, new VariableContext { Time = FakeTime.AtLocal(2026, 3, 7), Record = record });
        Assert.Equal(("1", "2026-03-07", "x", "DATA4"), (r.Values["serial"], r.Values["day"], r.Values["c"], r.Values["plain"]));
        var warned = r.Issues.Where(i => i.MessageKey == VariableMessageKeys.RecordOverridden).Select(i => i.Args[0]).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "c", "day", "serial" }, warned);
    }

    [Fact]
    public void TheFirstDeclarationOfAName_Wins()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Computed("x", "'first'"));
        doc.Variables.Add(new LabelVariable("x", "second"));
        Assert.Equal("first", VariableEngine.Resolve(doc, Ctx()).Values["x"]);
        Assert.Contains(VariableEngine.ValidateDefinitions(doc), i => i.MessageKey == VariableMessageKeys.NameDuplicate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1abc")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public void BadNames_AreFlaggedByTheDesignTimeCheck(string name)
    {
        var doc = new LabelDocument();
        doc.Variables.Add(new LabelVariable(name, "x"));
        Assert.Equal(VariableMessageKeys.NameInvalid, Assert.Single(VariableEngine.ValidateDefinitions(doc)).MessageKey);
    }

    // ------------------------------------------------------------------------------------ the generator

    [Fact]
    public void WithoutAContext_NewKindsShowTheirSampleValue_AndNothingReadsTheClock()
    {
        var doc = Doc(Text("{{day}} {{serial}} {{who}} {{calc}}"));
        doc.Variables.Add(Vars.Date("day", new DateVariableOptions { Format = "iso" }, sample: "S-DAY"));
        doc.Variables.Add(new LabelVariable("serial", "S-SER") { Kind = VariableKind.Counter, Counter = new CounterVariableOptions() });
        doc.Variables.Add(Vars.Prompt("who", null, sample: "S-WHO"));
        doc.Variables.Add(new LabelVariable("calc", "S-CALC") { Kind = VariableKind.Computed, Computed = new ComputedVariableOptions { Expression = "'x'" } });
        string zpl = CoreTestSupport.Zpl(doc);
        Assert.Contains("^FDS-DAY S-SER S-WHO S-CALC^FS", zpl);
        Assert.Equal(zpl, CoreTestSupport.Zpl(doc)); // byte identical on every run
        // with a record, the record wins over the sample
        Assert.Contains("^FDfrom data S-SER", CoreTestSupport.Zpl(doc, new Dictionary<string, string> { ["day"] = "from data" }));
    }

    [Fact]
    public void ExpiryAndLot_ExamplesFromTheUserGuide()
    {
        var doc = Doc(Text("EXP {{expiry}}"), Text("LOT {{lot}}", 80));
        doc.Variables.Add(Vars.Date("expiry", "gs1", new DateOffset(30, DateUnit.Days)));
        doc.Variables.Add(Vars.Date("julian", "julian"));
        doc.Variables.Add(Vars.Counter("counter", c => c.Padding = 4));
        doc.Variables.Add(Vars.Computed("lot", "'L' & julian & '-' & counter"));
        var result = ZplGenerator.GenerateWithVariables(doc, Ctx());
        Assert.False(result.HasErrors);
        Assert.Contains("^FDEXP 260406^FS", result.Zpl);
        Assert.Contains("^FDLOT L066-0001^FS", result.Zpl);
    }

    [Fact]
    public void TheSameContext_GivesByteIdenticalZpl_AndAnotherInstantChangesIt()
    {
        var doc = Doc(Text("{{t}}"));
        doc.Variables.Add(Vars.Date("t", "isoDateTime"));
        string a = ZplGenerator.GenerateWithVariables(doc, Ctx(FakeTime.AtLocal(2026, 3, 7, 10))).Zpl;
        string b = ZplGenerator.GenerateWithVariables(doc, Ctx(FakeTime.AtLocal(2026, 3, 7, 10))).Zpl;
        string c = ZplGenerator.GenerateWithVariables(doc, Ctx(FakeTime.AtLocal(2026, 3, 7, 11))).Zpl;
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        // a fixed Now beats the provider's clock
        var ctx = Ctx(FakeTime.AtLocal(2030, 1, 1));
        ctx.Now = FakeTime.AtLocal(2026, 3, 7, 10).GetUtcNow();
        Assert.Equal(a, ZplGenerator.GenerateWithVariables(doc, ctx).Zpl);
    }

    [Fact]
    public void QuantityOverride_ReplacesTheLabelQuantity()
    {
        var doc = Doc(Text("x"));
        doc.Print.Quantity = 50;
        Assert.Contains("^PQ1,0,1,N", ZplGenerator.GenerateWithVariables(doc, Ctx(), 1).Zpl);
        Assert.Contains("^PQ50,0,1,N", ZplGenerator.GenerateWithVariables(doc, Ctx()).Zpl);
    }

    [Fact]
    public void AUsedPromptWithoutAnAnswer_ShowsUpAsAnErrorInTheResult_WhenPrinting()
    {
        var doc = Doc(Text("{{who}}"));
        doc.Variables.Add(Vars.Prompt("who", p => p.Required = true));
        var ctx = Ctx();
        ctx.RequirePromptAnswers = true;
        var result = ZplGenerator.GenerateWithVariables(doc, ctx);
        Assert.True(result.HasErrors);
        Assert.Equal(VariableMessageKeys.PromptMissingAnswer, result.Issues[0].MessageKey);
    }

    // ------------------------------------------------------------------------------------ printer clock

    private static LabelDocument PrinterClockLabel()
    {
        var doc = Doc(Text("EXP {{expiry}}"), Text("Printed {{now}}", 70), Text("Both {{now}} {{expiry}}", 120), Text("Plain {{plain}}", 170));
        var expiry = new DateVariableOptions { Source = DateSource.PrinterClock, Format = "gs1" };
        expiry.Offsets.Add(new DateOffset(30, DateUnit.Days));
        doc.Variables.Add(Vars.Date("expiry", expiry));
        doc.Variables.Add(Vars.Date("now", new DateVariableOptions { Source = DateSource.PrinterClock, Format = "yyyy-MM-dd HH:mm" }));
        doc.Variables.Add(new LabelVariable("plain", "value"));
        return doc;
    }

    [Fact]
    public void PrinterClock_WritesClockCodes_AndTheOffsetCommand_Golden()
    {
        var result = ZplGenerator.GenerateWithVariables(PrinterClockLabel(), Ctx());
        Assert.False(result.HasErrors);
        Assert.Empty(result.Issues);
        CoreTestSupport.AssertGolden("variables/printer_clock", result.Zpl);
        // the key facts, spelled out
        Assert.Contains("\n^SO2,0,30,0,0,0,0\n", result.Zpl);
        Assert.Contains("^FC%,{^FDEXP {y{m{d^FS", result.Zpl);
        Assert.Contains("^FC%^FDPrinted %Y-%m-%d %H:%M^FS", result.Zpl);
        Assert.Contains("^FC%,{^FDBoth %Y-%m-%d %H:%M {y{m{d^FS", result.Zpl);
        Assert.Contains("^FDPlain value^FS", result.Zpl);
        Assert.DoesNotContain("^FC^", result.Zpl);
    }

    [Fact]
    public void PrinterClockLabel_StillShowsComputerValuesInThePreviewData()
    {
        var r = VariableEngine.Resolve(PrinterClockLabel(), Ctx());
        Assert.Equal("260406", r.Values["expiry"]);
        Assert.Equal("2026-03-07 14:05", r.Values["now"]);
        Assert.Equal(2, r.PrinterBindings.Count);
        Assert.Equal(new[] { 2 }, r.ClockOffsets.Keys);
    }

    [Fact]
    public void PrinterClock_PicksAnIndicatorThatIsNotInTheText()
    {
        var doc = Doc(Text("100% sure {{now}}"), Text("{ and * and % and ! {{now}}", 70));
        doc.Variables.Add(Vars.Date("now", new DateVariableOptions { Source = DateSource.PrinterClock, Format = "HH:mm" }));
        string zpl = ZplGenerator.GenerateWithVariables(doc, Ctx()).Zpl;
        Assert.Contains("^FC{^FD100% sure {H:{M^FS", zpl);                       // '%' is in the text, so '{' is the indicator
        Assert.Contains("^FC@^FD{ and * and % and ! @H:@M^FS", zpl);              // '%', '{', '*' and '!' are taken: '@' is next
    }

    [Fact]
    public void PrinterClock_UsesTheIndicatorsTheUserSetOnTheField()
    {
        var doc = Doc(new TextElement { Text = "{{now}} {{later}}", Clock = new FieldClockOptions { Primary = '#', Secondary = '$' } });
        doc.Variables.Add(Vars.Date("now", new DateVariableOptions { Source = DateSource.PrinterClock, Format = "HH:mm" }));
        var later = new DateVariableOptions { Source = DateSource.PrinterClock, Format = "HH:mm" };
        later.Offsets.Add(new DateOffset(2, DateUnit.Hours));
        doc.Variables.Add(Vars.Date("later", later));
        var result = ZplGenerator.GenerateWithVariables(doc, Ctx());
        Assert.Contains("^FC#,$^FD#H:#M $H:$M^FS", result.Zpl);
        Assert.Contains("^SO2,0,0,0,2,0,0", result.Zpl);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void PrinterClock_FieldWithItsOwnClockButNoIndicatorForTheOffset_FallsBackWithAWarning()
    {
        var doc = Doc(new TextElement { Id = "e", Text = "{{later}}", Clock = new FieldClockOptions { Primary = '#' } });
        var later = new DateVariableOptions { Source = DateSource.PrinterClock, Format = "HH:mm" };
        later.Offsets.Add(new DateOffset(2, DateUnit.Hours));
        doc.Variables.Add(Vars.Date("later", later));
        var result = ZplGenerator.GenerateWithVariables(doc, Ctx());
        Assert.Contains("^FD16:05^FS", result.Zpl); // 14:05 + 2 h from the computer clock
        var w = Assert.Single(result.Issues);
        Assert.Equal((ValidationSeverity.Warning, VariableMessageKeys.PrinterNoIndicator, "e"), (w.Severity, w.MessageKey, w.ElementId));
    }

    private static DateVariableOptions Printer(Action<DateVariableOptions> configure)
    {
        var o = new DateVariableOptions { Source = DateSource.PrinterClock, Format = "iso" };
        configure(o);
        return o;
    }

    public static IEnumerable<object[]> Fallbacks() => new[]
    {
        new object[] { "business days", VariableMessageKeys.PrinterBusinessDays },
        new object[] { "rounding", VariableMessageKeys.PrinterRounding },
        new object[] { "utc", VariableMessageKeys.PrinterUtc },
        new object[] { "iso week format", VariableMessageKeys.PrinterFormat },
        new object[] { "fraction format", VariableMessageKeys.PrinterFormat },
        new object[] { "culture names", VariableMessageKeys.PrinterCulture },
        new object[] { "huge offset", VariableMessageKeys.DateOffsetRange },
    };

    [Theory, MemberData(nameof(Fallbacks))]
    public void PrinterClock_ThingsThePrinterCannotDo_FallBackToTheComputerClock_WithAWarning(string what, string key)
    {
        var o = what switch
        {
            "business days" => Printer(x => x.Offsets.Add(new DateOffset(2, DateUnit.BusinessDays))),
            "rounding" => Printer(x => x.Rounding = DateRounding.EndOfMonth),
            "utc" => Printer(x => x.Utc = true),
            "iso week format" => Printer(x => x.Format = "isoWeek"),
            "fraction format" => Printer(x => x.Format = "HH:mm:ss.fff"),
            "culture names" => Printer(x => { x.Format = "MMM"; x.Culture = "es-ES"; }),
            _ => Printer(x => x.Offsets.Add(new DateOffset(33000, DateUnit.Minutes))),
        };
        var doc = Doc(Text("{{d}}"));
        doc.Variables.Add(Vars.Date("d", o));
        var result = ZplGenerator.GenerateWithVariables(doc, Ctx());
        Assert.DoesNotContain("^FC", result.Zpl);
        Assert.DoesNotContain("^SO", result.Zpl);
        Assert.Contains(result.Issues, i => i.MessageKey == key && i.Severity == ValidationSeverity.Warning && i.Args[0] == "d");
        Assert.Contains("^FD", result.Zpl);
    }

    [Fact]
    public void PrinterClock_FixedSourceAndAThirdOffset_FallBack()
    {
        var doc = Doc(Text("{{a}} {{b}} {{c}}"));
        foreach (var (name, hours) in new[] { ("a", 1), ("b", 2), ("c", 3) })
        {
            var o = Printer(x => x.Format = "HH");
            o.Offsets.Add(new DateOffset(hours, DateUnit.Hours));
            doc.Variables.Add(Vars.Date(name, o));
        }
        var result = ZplGenerator.GenerateWithVariables(doc, Ctx());
        Assert.Contains("^SO2,0,0,0,1,0,0", result.Zpl);
        Assert.Contains("^SO3,0,0,0,2,0,0", result.Zpl);
        Assert.Contains(result.Issues, i => i.MessageKey == VariableMessageKeys.PrinterSlotsFull && i.Args[0] == "c");
        Assert.Contains("^FC%,{,*^FD{H *H 17^FS", result.Zpl); // a on clock 2, b on clock 3, c from the computer (14 + 3)

        var fixedDoc = Doc(Text("{{f}}"));
        fixedDoc.Variables.Add(Vars.Date("f", new DateVariableOptions { Source = DateSource.Fixed, FixedValue = new DateTime(2030, 5, 6), Format = "iso" }));
        Assert.Contains("^FD2030-05-06^FS", ZplGenerator.GenerateWithVariables(fixedDoc, Ctx()).Zpl);
    }

    [Fact]
    public void PrinterClock_InsideABarcode_UsesTheComputerClock_AndSaysSo()
    {
        var doc = Doc(new BarcodeElement { Id = "bc", Symbology = BarcodeSymbology.Code39, Data = "{{d}}", X = 10, Y = 10 }, Text("{{d}}", 200));
        doc.Variables.Add(Vars.Date("d", Printer(x => x.Format = "compact")));
        var result = ZplGenerator.GenerateWithVariables(doc, Ctx());
        Assert.False(result.HasErrors);
        Assert.Contains("^FD20260307^FS", result.Zpl);   // the barcode carries the computer date
        Assert.Contains(result.Issues, i => i.MessageKey == VariableMessageKeys.PrinterBarcode);
        Assert.Contains("^FC%^FD%Y%m%d^FS", result.Zpl); // the text field still uses the printer clock
    }

    [Fact]
    public void PrinterClockCodes_ComeFromTheDocumentedTable()
    {
        // Every code the mapper can produce is one the printer documents (^FC): a b c letters from the ZPL II guide.
        var documented = new HashSet<string> { "%a", "%A", "%b", "%B", "%d", "%H", "%I", "%j", "%m", "%M", "%p", "%S", "%U", "%w", "%W", "%y", "%Y" };
        var produced = new HashSet<string>();
        foreach (var preset in DatePresets.All.Where(p => p.PrinterTokens is not null))
            for (int i = 0; i < preset.PrinterTokens!.Length; i++)
                if (preset.PrinterTokens[i] == '%') produced.Add(preset.PrinterTokens.Substring(i, 2));
        foreach (var pattern in new[] { "dd", "ddd", "dddd", "MM", "MMM", "MMMM", "yy", "yyyy", "HH", "hh", "mm", "ss", "tt" })
        {
            Assert.True(DateFormats.TryMapToPrinter(pattern, out string tokens, out _, out _), pattern);
            produced.Add(tokens);
        }
        Assert.True(produced.IsSubsetOf(documented), string.Join(",", produced.Except(documented)));
        // and the unpadded or fractional forms are not mapped
        foreach (var bad in new[] { "d", "M", "yyy", "H", "h", "m", "s", "f", "fff", "zzz", "K", "g", "tt tt z" })
            Assert.False(DateFormats.TryMapToPrinter(bad, out _, out _, out string problem) && problem.Length == 0, bad);
        Assert.True(DateFormats.TryMapToPrinter("dd'/'MM \\y yyyy", out string literal, out _, out _));
        Assert.Equal("%d/%m y %Y", literal);
    }

    // ------------------------------------------------------------------------------------ batch merge

    private static MergeTable People() => new(new[] { "name", "qty" }, new[]
    {
        new[] { "Ana", "5" },
        new[] { "Luis", "12" },
        new[] { "Eva", "7" },
    });

    private static LabelDocument BatchLabel()
    {
        var doc = Doc(Text("{{name}} {{serial}} {{day}} {{shift}} {{code}}"));
        doc.Variables.Add(Vars.Counter("serial", c => { c.Padding = 3; c.Prefix = "N"; }));
        doc.Variables.Add(Vars.Date("day", "iso"));
        doc.Variables.Add(Vars.Prompt("shift", p => p.Uppercase = true));
        doc.Variables.Add(Vars.Computed("code", "upper(left(name, 2)) & padleft(qty, 3, '0')"));
        return doc;
    }

    [Fact]
    public void Batch_ResolvesEveryKindPerRecord()
    {
        var doc = BatchLabel();
        var options = new MergeOptions { Time = FakeTime.AtLocal(2026, 3, 7, 10) };
        options.PromptAnswers["shift"] = "b";
        var set = new MergeDataSet(doc, People(), options);
        var report = new MergeReport();
        var labels = MergeBatch.Labels(doc, set, report).ToList();
        Assert.Equal(3, labels.Count);
        Assert.Contains("^FDAna N001 2026-03-07 B AN005^FS", labels[0].Zpl);
        Assert.Contains("^FDLuis N002 2026-03-07 B LU012^FS", labels[1].Zpl);
        Assert.Contains("^FDEva N003 2026-03-07 B EV007^FS", labels[2].Zpl);
        Assert.Empty(report.Issues);
        // nothing is reported as an unmapped placeholder: the label defines serial, day, shift and code itself
        Assert.DoesNotContain(set.Issues, i => i.MessageKey == "Merge.Placeholder.Unmapped");
    }

    [Fact]
    public void Batch_DateIsReadOncePerBatchByDefault_OrPerLabelOnRequest()
    {
        var doc = Doc(Text("{{t}}"));
        doc.Variables.Add(Vars.Date("t", "HH:mm"));
        string Run(DateEvaluation mode)
        {
            var time = FakeTime.AtLocal(2026, 3, 7, 23, 58);
            var options = new MergeOptions { Time = time, DateEvaluation = mode };
            var set = new MergeDataSet(doc, People(), options);
            var times = new List<string>();
            foreach (var label in MergeBatch.Labels(doc, set))
            {
                times.Add(label.Zpl.Split("^FD")[1].Split("^FS")[0]);
                time.Set(time.GetUtcNow().AddMinutes(1)); // the run crosses midnight
            }
            return string.Join(",", times);
        }
        Assert.Equal("23:58,23:58,23:58", Run(DateEvaluation.AtBatchStart));
        Assert.Equal("23:58,23:59,00:00", Run(DateEvaluation.PerLabel));
    }

    [Fact]
    public void Batch_PersistentCounters_ReserveBeforeAndConfirmAfter()
    {
        var doc = BatchLabel();
        doc.EnsureId();
        doc.Variables[0].Counter!.Remember = true;
        var store = new MemoryCounterStore();
        string Run(int take)
        {
            var options = new MergeOptions { Time = FakeTime.AtLocal(2026, 3, 7), CounterStore = store };
            options.PromptAnswers["shift"] = "a";
            var set = new MergeDataSet(doc, People(), options);
            var labels = MergeBatch.Labels(doc, set).Take(take).ToList(); // Take stops early, like a print that fails midway
            return string.Join(" ", labels.Select(l => l.Zpl.Split("^FD")[1].Split(' ')[1]));
        }
        Assert.Equal("N001 N002", Run(2));
        var rec = store.Get(CounterKey.For(doc.Id!, "serial"))!;
        Assert.Equal(2, rec.Next);               // only the two labels that were handed out count as used; the third number was given back
        Assert.Empty(rec.Pending);
        Assert.Equal("N003 N004 N005", Run(3));  // a new run continues, never reusing N001/N002
        Assert.Equal(5, store.Get(CounterKey.For(doc.Id!, "serial"))!.Next);
    }

    [Fact]
    public void Batch_TheCallerCanOwnTheJob_SoOnlyWhatWasSentIsConfirmed()
    {
        var doc = BatchLabel();
        doc.EnsureId();
        doc.Variables[0].Counter!.Remember = true;
        var store = new MemoryCounterStore();
        var options = new MergeOptions { Time = FakeTime.AtLocal(2026, 3, 7), CounterStore = store };
        options.PromptAnswers["shift"] = "a";
        var set = new MergeDataSet(doc, People(), options);
        var job = VariableJob.Begin(doc, set.CreateBatchContext(), set.RecordCount);
        options.Job = job;
        string key = CounterKey.For(doc.Id!, "serial");

        var labels = MergeBatch.Labels(doc, set).ToList();              // all three are generated ...
        Assert.Equal(3, labels.Count);
        Assert.Contains("N001", labels[0].Zpl);
        var rec = store.Get(key)!;
        Assert.Equal((3L, 1), (rec.Next, rec.Pending.Count));            // ... but the batch did not confirm anything: the run is still open

        // sending failed after the second label: only two numbers were used, the third goes back
        job.Complete(labels[1].RecordNumber);
        rec = store.Get(key)!;
        Assert.Equal((2L, 1L, 0), (rec.Next, rec.LastUsedIndex, rec.Pending.Count));
        Assert.Equal("N002", rec.LastValue);

        // nothing sent at all: Cancel gives everything back
        options.Job = null;
        var second = VariableJob.Begin(doc, set.CreateBatchContext(), set.RecordCount);
        options.Job = second;
        MergeBatch.Labels(doc, set).ToList();
        second.Cancel();
        Assert.Equal(2, store.Get(key)!.Next);
    }

    [Fact]
    public void Batch_WithoutAStore_StopsBeforePrintingAnythingWhenACounterRemembers()
    {
        var doc = BatchLabel();
        doc.EnsureId();
        doc.Variables[0].Counter!.Remember = true;
        var options = new MergeOptions { Time = FakeTime.AtLocal(2026, 3, 7) }; // no CounterStore: the numbers would start again at N001
        options.PromptAnswers["shift"] = "a";
        var report = new MergeReport();
        var labels = MergeBatch.Labels(doc, new MergeDataSet(doc, People(), options), report).ToList();
        Assert.Empty(labels);
        Assert.True(report.Stopped);
        Assert.Contains(report.Issues, i => i.RecordNumber == 0 && i.Issue.MessageKey == VariableMessageKeys.CounterNoStore && i.Issue.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Batch_ASkippedRecordLeavesAGap_AndStillCountsAsUsed()
    {
        var doc = Doc(Text("{{serial}}"), new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "{{ean}}", X = 10, Y = 100 });
        doc.EnsureId();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 2; }));
        var table = new MergeTable(new[] { "ean" }, new[] { new[] { "4006381333931" }, new[] { "123" }, new[] { "4006381333931" } });
        var store = new MemoryCounterStore();
        var set = new MergeDataSet(doc, table, new MergeOptions { CounterStore = store });
        var report = new MergeReport();
        var labels = MergeBatch.Labels(doc, set, report).ToList();
        Assert.Equal(new[] { 2 }, report.SkippedRecords);
        Assert.Contains("^FD01^FS", labels[0].Zpl);
        Assert.Contains("^FD03^FS", labels[1].Zpl);
        Assert.Equal(3, store.Get(CounterKey.For(doc.Id!, "serial"))!.Next);
    }

    [Fact]
    public void Batch_ABlockedJob_StopsBeforePrintingAnything()
    {
        var doc = BatchLabel();                     // no id: a remembering counter cannot work
        doc.Variables[0].Counter!.Remember = true;
        var options = new MergeOptions { CounterStore = new MemoryCounterStore() };
        var report = new MergeReport();
        var labels = MergeBatch.Labels(doc, new MergeDataSet(doc, People(), options), report).ToList();
        Assert.Empty(labels);
        Assert.True(report.Stopped);
        Assert.Contains(report.Issues, i => i.RecordNumber == 0 && i.Issue.MessageKey == VariableMessageKeys.CounterNeedsLabelId);
    }

    [Fact]
    public void Batch_AQuestionNobodyAnswered_StopsTheBatchOnce_NotOncePerRecord()
    {
        var doc = BatchLabel(); // asks "shift"
        var options = new MergeOptions { Time = FakeTime.AtLocal(2026, 3, 7) };
        var report = new MergeReport();
        var labels = MergeBatch.Labels(doc, new MergeDataSet(doc, People(), options), report).ToList();
        Assert.Empty(labels);
        Assert.True(report.Stopped);
        var issue = Assert.Single(report.Issues);
        Assert.Equal((0, VariableMessageKeys.PromptMissingAnswer), (issue.RecordNumber, issue.Issue.MessageKey));
        // a data column with that name answers it for the records that have a value
        var table = new MergeTable(new[] { "name", "qty", "shift" }, new[] { new[] { "Ana", "5", "N" }, new[] { "Eva", "7", "" } });
        var report2 = new MergeReport();
        var set = new MergeDataSet(doc, table, options);
        var done = MergeBatch.Labels(doc, set, report2).ToList();
        Assert.Single(done);                       // the record with an empty cell has no answer: skipped, with the key
        Assert.Contains(report2.Issues, i => i.RecordNumber == 2 && i.Issue.MessageKey == VariableMessageKeys.PromptMissingAnswer);
    }

    [Fact]
    public void Batch_EachVariableWarningIsReportedOnce_NotOncePerRecord()
    {
        var doc = Doc(Text("{{d}}"));
        doc.Variables.Add(Vars.Date("d", Printer(x => x.Rounding = DateRounding.EndOfDay)));
        var report = new MergeReport();
        MergeBatch.Labels(doc, new MergeDataSet(doc, People(), new MergeOptions { Time = FakeTime.AtLocal(2026, 3, 7) }), report).ToList();
        Assert.Single(report.Issues, i => i.Issue.MessageKey == VariableMessageKeys.PrinterRounding);
    }

    [Fact]
    public void Batch_APlainLabel_IsUnchanged()
    {
        var doc = Doc(Text("{{name}}"));
        doc.Variables.Add(new LabelVariable("name", "S"));
        var set = new MergeDataSet(doc, People());
        Assert.Equal(set.GetRecordData(0), set.GetRecordData(0));
        Assert.Equal(new[] { "name", "qty" }.OrderBy(x => x), set.GetRecordData(0).Keys.OrderBy(x => x));
    }

    [Fact]
    public void Batch_GetRecordData_ShowsVariableValuesForThePreview()
    {
        var doc = BatchLabel();
        doc.EnsureId();
        doc.Variables[0].Counter!.Remember = true;
        var store = new MemoryCounterStore();
        var options = new MergeOptions { Time = FakeTime.AtLocal(2026, 3, 7), CounterStore = store };
        var set = new MergeDataSet(doc, People(), options);
        var data = set.GetRecordData(1);
        // Record 2 (index 1) shows the second position even though nothing was printed yet.
        Assert.Equal(("N002", "2026-03-07", "LU012"), (data["serial"], data["day"], data["code"]));
        Assert.Contains("N002", ZplGenerator.Generate(doc, data).Zpl);
        Assert.Null(store.Get(CounterKey.For(doc.Id!, "serial"))); // previewing reserves nothing
    }

    // ------------------------------------------------------------------------------------ preview data and conversion

    [Fact]
    public void BuildPreviewData_FeedsTheExistingGeneratorUnchanged()
    {
        var doc = Doc(Text("{{expiry}}"));
        doc.Variables.Add(Vars.Date("expiry", "gs1", new DateOffset(1, DateUnit.Days)));
        var data = VariableEngine.BuildPreviewData(doc, Ctx());
        Assert.Contains("^FD260308^FS", ZplGenerator.Generate(doc, data).Zpl);
    }

    [Fact]
    public void ResolutionConversion_KeepsDefinitionsAndTheLabelId()
    {
        var doc = new LabelDocument { Dpi = PrinterDpi.Dpi203 };
        doc.EnsureId();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Prefix = "Z"; }));
        doc.Variables.Add(Vars.Date("d", "gs1", new DateOffset(3, DateUnit.Weeks)));
        var converted = LabelDpiConverter.Convert(doc, PrinterDpi.Dpi300).Document;
        Assert.Equal(doc.Id, converted.Id);
        Assert.Equal("Z", converted.Variables[0].Counter!.Prefix);
        Assert.True(converted.Variables[0].Counter!.Remember);
        Assert.Equal(DateUnit.Weeks, converted.Variables[1].Date!.Offsets[0].Unit);
        Assert.NotSame(doc.Variables[0].Counter, converted.Variables[0].Counter);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var v = Vars.Prompt("p", o => { o.Choices.Add("a"); o.InputType = PromptInputType.Choice; });
        var copy = v.Clone();
        copy.Prompt!.Choices.Add("b");
        copy.Prompt.Label = "changed";
        Assert.Single(v.Prompt!.Choices);
        Assert.Equal("", v.Prompt.Label);
        var d = Vars.Date("d", "iso", new DateOffset(1, DateUnit.Days));
        d.Date!.Holidays.Add(new DateOnly(2026, 1, 1));
        var dc = d.Clone();
        dc.Date!.Offsets.Clear();
        dc.Date.Holidays.Clear();
        Assert.Single(d.Date.Offsets);
        Assert.Single(d.Date.Holidays);
    }
}
