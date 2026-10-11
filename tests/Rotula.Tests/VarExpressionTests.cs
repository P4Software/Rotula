using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Expressions;

namespace Rotula.Tests;

/// <summary>The computed-variable expression language: what it accepts, what it refuses, and its hard limits.</summary>
public class VarExpressionTests
{
    private static readonly Dictionary<string, string> Vals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sku"] = "abc-123", ["qty"] = "7", ["empty"] = "", ["name"] = "  Ana  ", ["when"] = "2026-03-07", ["amount"] = "1234.5", ["#Counter"] = "0042",
    };

    private static string Eval(string text)
    {
        var tree = ExpressionParser.Parse(text, out var perr);
        Assert.True(perr is null, text + " => " + perr);
        string r = ExpressionEvaluator.Evaluate(tree!, n => Vals.GetValueOrDefault(n), out var eerr);
        Assert.True(eerr is null, text + " => " + eerr);
        return r;
    }

    private static ExpressionError ParseError(string text)
    {
        Assert.Null(ExpressionParser.Parse(text, out var err));
        return err!;
    }

    private static ExpressionError EvalError(string text)
    {
        var tree = ExpressionParser.Parse(text, out var perr);
        Assert.True(perr is null, perr?.ToString());
        ExpressionEvaluator.Evaluate(tree!, n => Vals.GetValueOrDefault(n), out var err);
        Assert.NotNull(err);
        return err!;
    }

    [Theory]
    [InlineData("'hello'", "hello")]
    [InlineData("\"it's\"", "it's")]
    [InlineData("'it''s'", "it's")]
    [InlineData(@"'a\'b'", "a'b")]
    [InlineData(@"'tab\there'", "tab\there")]
    [InlineData("42", "42")]
    [InlineData("-3.5", "-3.5")]
    [InlineData("sku", "abc-123")]
    [InlineData("#Counter", "0042")]
    [InlineData("SKU", "abc-123")]
    [InlineData("sku & '/' & qty", "abc-123/7")]
    [InlineData("('a' & 'b') & 'c'", "abc")]
    [InlineData("concat('a', 'b', 'c')", "abc")]
    [InlineData("join('-', 'a', '', 'b', empty, 'c')", "a-b-c")]
    [InlineData("upper(sku)", "ABC-123")]
    [InlineData("LOWER('AbC')", "abc")]
    [InlineData("trim(name)", "Ana")]
    [InlineData("length(sku)", "7")]
    [InlineData("left(sku, 3)", "abc")]
    [InlineData("left(sku, 99)", "abc-123")]
    [InlineData("right(sku, 3)", "123")]
    [InlineData("substring(sku, 4)", "123")]
    [InlineData("substring(sku, 4, 2)", "12")]
    [InlineData("substring(sku, 50)", "")]
    [InlineData("padleft(qty, 4, '0')", "0007")]
    [InlineData("padleft(qty, 4)", "   7")]
    [InlineData("padright(qty, 3, '*')", "7**")]
    [InlineData("padleft('long text', 3)", "long text")]
    [InlineData("replace(sku, '-', '')", "abc123")]
    [InlineData("ifempty(empty, 'n/a')", "n/a")]
    [InlineData("ifempty(sku, 'n/a')", "abc-123")]
    [InlineData("coalesce(empty, '', 'x', 'y')", "x")]
    [InlineData("number(amount, '0000.00')", "1234.50")]
    [InlineData("number(qty, '000')", "007")]
    [InlineData("number(amount, 'N1', 'en-US')", "1,234.5")]
    [InlineData("date(when, 'yyMMdd')", "260307")]
    [InlineData("date(when, 'julian')", "066")]
    [InlineData("date('20260307', 'iso')", "2026-03-07")]
    [InlineData("upper(left(sku, 3)) & '-' & padleft(qty, 4, '0')", "ABC-0007")]
    [InlineData("  upper (  sku  )  ", "ABC-123")]
    public void Accepts(string expression, string expected) => Assert.Equal(expected, Eval(expression));

    [Fact]
    public void ThePlainTemplateExample_WorksAsAnExpression()
    {
        // 'lot = L{{julian}}-{{counter}}' written as an expression over two other variables.
        Assert.Equal("L066-0042", Eval("'L' & '066' & '-' & #Counter"));
    }

    [Theory]
    [InlineData("", VariableMessageKeys.ExpressionEmpty, 0)]
    [InlineData("   ", VariableMessageKeys.ExpressionEmpty, 0)]
    [InlineData("'abc", VariableMessageKeys.ExpressionUnterminatedString, 1)]
    [InlineData("upper(", VariableMessageKeys.ExpressionUnexpectedEnd, 7)]
    [InlineData("upper(sku", VariableMessageKeys.ExpressionUnexpectedEnd, 10)]
    [InlineData("upper(sku))", VariableMessageKeys.ExpressionUnexpectedToken, 11)]
    [InlineData("sku sku", VariableMessageKeys.ExpressionUnexpectedToken, 5)]
    [InlineData("sku &", VariableMessageKeys.ExpressionUnexpectedEnd, 6)]
    [InlineData("& sku", VariableMessageKeys.ExpressionUnexpectedToken, 1)]
    [InlineData("sku + 1", VariableMessageKeys.ExpressionUnexpectedChar, 5)]
    [InlineData("sku;drop", VariableMessageKeys.ExpressionUnexpectedChar, 4)]
    [InlineData("nosuch(sku)", VariableMessageKeys.ExpressionUnknownFunction, 1)]
    [InlineData("upper()", VariableMessageKeys.ExpressionArgumentCount, 1)]
    [InlineData("upper(sku, sku)", VariableMessageKeys.ExpressionArgumentCount, 1)]
    [InlineData("12abc", VariableMessageKeys.ExpressionBadNumber, 1)]
    [InlineData("1.", VariableMessageKeys.ExpressionBadNumber, 1)]
    [InlineData(@"'bad\q'", VariableMessageKeys.ExpressionUnexpectedChar, 5)]
    [InlineData("#(x)", VariableMessageKeys.ExpressionUnexpectedChar, 1)]
    [InlineData("#Counter()", VariableMessageKeys.ExpressionUnexpectedToken, 9)]
    [InlineData("sku.Length", VariableMessageKeys.ExpressionUnexpectedChar, 4)]
    [InlineData("sku[0]", VariableMessageKeys.ExpressionUnexpectedChar, 4)]
    [InlineData("`ls`", VariableMessageKeys.ExpressionUnexpectedChar, 1)]
    [InlineData("$(whoami)", VariableMessageKeys.ExpressionUnexpectedChar, 1)]
    [InlineData("{{sku}}", VariableMessageKeys.ExpressionUnexpectedChar, 1)]
    public void Rejects_WithAKeyAndAPosition(string expression, string key, int position)
    {
        var e = ParseError(expression);
        Assert.Equal(key, e.Key);
        Assert.Equal(position, e.Position);
    }

    [Fact]
    public void NothingDangerousIsReachable()
    {
        // Every name that looks like code is just an unknown function or variable; there is no way to call anything but the fixed list.
        foreach (var attempt in new[] { "System.IO.File.ReadAllText('x')", "Process(sku)", "eval(sku)", "exec('calc')", "new(sku)", "typeof(sku)" })
        {
            var tree = ExpressionParser.Parse(attempt, out var err);
            Assert.True(tree is null && err is not null, attempt);
        }
        Assert.Equal(16, ExpressionFunctions.All.Count);
        Assert.All(ExpressionFunctions.All, f => Assert.Matches("^[a-z]+$", f.Name));
    }

    [Fact]
    public void RuntimeErrors_CarryAKeyAndAPosition()
    {
        Assert.Equal(VariableMessageKeys.ExpressionUnknownVariable, EvalError("upper(nosuch)").Key);
        Assert.Equal(7, EvalError("upper(nosuch)").Position);
        Assert.Equal(VariableMessageKeys.ExpressionBadArgument, EvalError("left(sku, -1)").Key);
        Assert.Equal(VariableMessageKeys.ExpressionBadArgument, EvalError("left(sku, 'x')").Key);
        Assert.Equal(VariableMessageKeys.ExpressionBadArgument, EvalError("replace(sku, '', 'x')").Key);
        Assert.Equal(VariableMessageKeys.ExpressionBadArgument, EvalError("padleft(sku, 5, 'ab')").Key);
        Assert.Equal(VariableMessageKeys.ExpressionNotNumber, EvalError("number(sku, 'N2')").Key);
        Assert.Equal(VariableMessageKeys.ExpressionNotDate, EvalError("date(sku, 'iso')").Key);
        Assert.Equal(VariableMessageKeys.ExpressionBadFormat, EvalError("date(when, 'yyyy''')").Key);
        Assert.Equal(VariableMessageKeys.ExpressionBadFormat, EvalError("number(amount, '')").Key);
    }

    [Fact]
    public void Limits_Length_Depth_Nodes()
    {
        var tooLong = ParseError(new string(' ', ExpressionParser.MaxLength) + "sku");
        Assert.Equal(VariableMessageKeys.ExpressionTooLong, tooLong.Key);

        string deep = new string('(', ExpressionParser.MaxDepth + 2) + "sku" + new string(')', ExpressionParser.MaxDepth + 2);
        Assert.Equal(VariableMessageKeys.ExpressionTooDeep, ParseError(deep).Key);
        string nestedCalls = string.Concat(Enumerable.Repeat("upper(", 40)) + "sku" + new string(')', 40);
        Assert.Equal(VariableMessageKeys.ExpressionTooDeep, ParseError(nestedCalls).Key);

        string ok = new string('(', ExpressionParser.MaxDepth - 1) + "sku" + new string(')', ExpressionParser.MaxDepth - 1);
        Assert.Equal("abc-123", Eval(ok));

        string manyNodes = string.Join("&", Enumerable.Repeat("'a'", ExpressionParser.MaxNodes + 5));
        Assert.True(manyNodes.Length < ExpressionParser.MaxLength);
        Assert.Equal(VariableMessageKeys.ExpressionTooComplex, ParseError(manyNodes).Key);
    }

    [Fact]
    public void Limits_Output_Size()
    {
        string big = new('x', 9000);
        var vals = new Dictionary<string, string> { ["big"] = big, ["one"] = "a" };
        string Run(string e)
        {
            var tree = ExpressionParser.Parse(e, out var perr)!;
            Assert.Null(perr);
            return ExpressionEvaluator.Evaluate(tree, n => vals.GetValueOrDefault(n), out var err) + "|" + (err?.Key ?? "");
        }
        Assert.EndsWith("|" + VariableMessageKeys.ExpressionResultTooLong, Run("big & big"));
        Assert.EndsWith("|" + VariableMessageKeys.ExpressionResultTooLong, Run("padleft(one, 20000, '*')"));
        Assert.EndsWith("|" + VariableMessageKeys.ExpressionResultTooLong, Run("replace(big, 'x', 'yy')"));      // would be 18,000
        Assert.EndsWith("|" + VariableMessageKeys.ExpressionResultTooLong, Run("join(big, 'a', 'b', 'c')"));
        Assert.EndsWith("|", Run("replace(big, 'xx', 'y')")); // shrinking is fine
        Assert.EndsWith("|", Run("padleft(one, 10000, '*')"));
    }

    [Fact]
    public void Fuzz_NeverThrows_AndAlwaysEndsInAResultOrAKeyedError()
    {
        var rng = new Random(12345);
        const string alphabet = "abc_#()'\",& \t\\.-0123456789upperleftsubstringjoinpadleftreplace";
        var tokens = new[] { "upper(", "left(", "join(", "padleft(", "date(", "number(", "sku", "qty", "'x'", ")", ",", "&", "(", "0", "-1", "1.5", "#Counter", "\"", "'", "\\" };
        for (int i = 0; i < 20000; i++)
        {
            string text = i % 2 == 0
                ? new string(Enumerable.Range(0, rng.Next(0, 40)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray())
                : string.Concat(Enumerable.Range(0, rng.Next(0, 15)).Select(_ => tokens[rng.Next(tokens.Length)]));
            var tree = ExpressionParser.Parse(text, out var perr);
            Assert.True((tree is null) == (perr is not null), text);
            if (perr is not null)
            {
                Assert.StartsWith("Variable.Expr.", perr.Key);
                Assert.InRange(perr.Position, 0, text.Length + 1);
                continue;
            }
            string result = ExpressionEvaluator.Evaluate(tree!, n => Vals.GetValueOrDefault(n), out var eerr);
            Assert.True(result.Length <= VariableLimits.MaxValueLength);
            if (eerr is not null) Assert.StartsWith("Variable.Expr.", eerr.Key);
        }
    }

    [Fact]
    public void FuzzedHugeInput_IsRefusedFast()
    {
        var rng = new Random(7);
        for (int i = 0; i < 50; i++)
        {
            string text = new string(Enumerable.Range(0, 200_000).Select(_ => (char)rng.Next(32, 127)).ToArray());
            Assert.Equal(VariableMessageKeys.ExpressionTooLong, ParseError(text).Key);
        }
    }

    // ---- inside the engine -------------------------------------------------------------------

    private static VariableResolution Resolve(LabelDocument doc, Dictionary<string, string>? record = null) =>
        VariableEngine.Resolve(doc, new VariableContext { Time = FakeTime.AtLocal(2026, 3, 7), Record = record });

    [Fact]
    public void Computed_ReadsRecordSamplesAndOtherComputed_InAnyOrder()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Computed("full", "label & '/' & code"));      // reads a computed variable declared after it
        doc.Variables.Add(Vars.Computed("code", "upper(left(sku, 3))"));
        doc.Variables.Add(new LabelVariable("label", "LBL"));
        var r = Resolve(doc, new() { ["sku"] = "abcdef" });
        Assert.Equal("LBL/ABC", r.Values["full"]);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Computed_Cycles_AreReportedOnce_NotAStackOverflow()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Computed("a", "b & 'x'"));
        doc.Variables.Add(Vars.Computed("b", "c"));
        doc.Variables.Add(Vars.Computed("c", "a"));
        doc.Variables.Add(Vars.Computed("self", "self"));
        var r = Resolve(doc);
        Assert.Equal(2, r.Issues.Count(i => i.MessageKey == VariableMessageKeys.ExpressionCycle));
        Assert.True(r.HasErrors);
        // design-time check finds them too
        Assert.Equal(2, VariableEngine.ValidateDefinitions(doc).Count(i => i.MessageKey == VariableMessageKeys.ExpressionCycle));
    }

    [Fact]
    public void Computed_ErrorsBecomeIssues_AndTheSampleValueStandsIn()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(new LabelVariable("bad", "fallback") { Kind = VariableKind.Computed, Computed = new ComputedVariableOptions { Expression = "upper(" } });
        doc.Variables.Add(new LabelVariable("missing", "fb2") { Kind = VariableKind.Computed, Computed = new ComputedVariableOptions { Expression = "'a' & nosuch & 'b'" } });
        var r = Resolve(doc);
        Assert.Equal("fallback", r.Values["bad"]);
        // a name nothing supplies counts as empty (like a missing {{placeholder}}) and is a warning, not a failure
        Assert.Equal("ab", r.Values["missing"]);
        var unknown = r.Issues.Single(i => i.MessageKey == VariableMessageKeys.ExpressionUnknownVariable);
        Assert.Equal(ValidationSeverity.Warning, unknown.Severity);
        Assert.Equal(new[] { "missing", "7", "nosuch" }, unknown.Args);
        var issue = r.Issues.First(i => i.MessageKey == VariableMessageKeys.ExpressionUnexpectedEnd);
        Assert.Equal(new[] { "bad", "7" }, issue.Args);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    [Fact]
    public void Computed_CanUseDatesCountersAndPrompts()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Date("julian", "julian"));
        doc.Variables.Add(Vars.Counter("serial", c => c.Padding = 4));
        doc.Variables.Add(Vars.Prompt("shift", p => p.Default = "a"));
        doc.Variables.Add(Vars.Computed("lot", "'L' & julian & '-' & serial & upper(shift)"));
        var r = Resolve(doc);
        Assert.Equal("L066-0001A", r.Values["lot"]);
    }
}
