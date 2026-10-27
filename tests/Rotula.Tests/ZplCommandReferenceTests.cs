using Rotula.Core.Zpl.Catalog;

namespace Rotula.Tests;

/// <summary>The parameter reference behind the hint line of the ZPL source panel: coverage of the supported commands and the caret lookup.</summary>
public class ZplCommandReferenceTests
{
    [Fact]
    public void Every_supported_command_has_a_signature()
    {
        var missing = ZplCommandCatalog.All.Where(c => c.Status == ZplCommandStatus.Supported && ZplCommandReference.FindSignature(c.Id) is null).Select(c => c.Id).ToList();
        Assert.True(missing.Count == 0, "Supported commands without a parameter signature: " + string.Join(" ", missing));
    }

    [Fact]
    public void Every_signature_names_a_catalogued_command_and_is_well_formed()
    {
        foreach (var s in ZplCommandReference.All)
        {
            Assert.True(ZplCommandCatalog.Find(s.Id) is not null, s.Id + " is not in commands.json");
            Assert.Equal(1, ZplCommandReference.All.Count(x => x.Id == s.Id));
            foreach (var p in s.Parameters)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Name), s.Id);
                Assert.False(string.IsNullOrWhiteSpace(p.Meaning), s.Id + " " + p.Name);
                Assert.DoesNotContain('\n', p.Meaning);
            }
        }
    }

    [Fact]
    public void Describe_merges_name_summary_and_parameters()
    {
        var hint = ZplCommandReference.Describe("^fo")!;
        Assert.Equal("^FO", hint.Id);
        Assert.Equal(ZplCommandCatalog.Find("^FO")!.Name, hint.Name);
        Assert.Equal(ZplCommandCatalog.Find("^FO")!.Summary, hint.Summary);
        Assert.Equal("x,y,z", hint.ParameterList);
        Assert.StartsWith("^FO x,y,z  ", hint.Headline);
        Assert.Contains("x: ", hint.ParameterText);
        Assert.Equal(ZplCommandStatus.Supported, hint.Status);

        // A command without parameters shows only its id and name.
        Assert.Equal("^FS  " + ZplCommandCatalog.Find("^FS")!.Name, ZplCommandReference.Describe("^FS")!.Headline);
        // A command the JSON catalogue lists but this table does not still gets a hint, without parameters.
        var kiosk = ZplCommandCatalog.All.First(c => ZplCommandReference.FindSignature(c.Id) is null);
        Assert.Empty(ZplCommandReference.Describe(kiosk.Id)!.Parameters);
        Assert.Null(ZplCommandReference.Describe("^YY"));
        Assert.Null(ZplCommandReference.Describe(""));
    }

    [Fact]
    public void A_command_with_both_prefixes_is_found_under_either()
    {
        Assert.NotNull(ZplCommandReference.FindSignature("~SD"));
        // ^CC and ~CC are one command listed once (under ^CC); ~PR is a different command from ^PR and must not borrow its signature.
        Assert.Equal(ZplCommandCatalog.Find("^CC")!.Name, ZplCommandReference.Describe("~CC")!.Name);
        Assert.Equal("x", Assert.Single(ZplCommandReference.Describe("~CC")!.Parameters).Name);
        Assert.Null(ZplCommandReference.FindSignature("~PR"));
    }

    [Theory]
    [InlineData("^XA^FO10,20^FDHello^FS^XZ", 0, "^XA")]   // caret at the very start, on the prefix
    [InlineData("^XA^FO10,20^FDHello^FS^XZ", 3, "^FO")]   // on the ^ of ^FO
    [InlineData("^XA^FO10,20^FDHello^FS^XZ", 8, "^FO")]   // inside the parameters
    [InlineData("^XA^FO10,20^FDHello^FS^XZ", 15, "^FD")]  // inside the data
    [InlineData("^XA^FO10,20^FDHello^FS^XZ", 23, "^XZ")]  // at the end of the text
    [InlineData("^XA^A0N,30,30^FDx^FS^XZ", 6, "^A")]      // ^A is one letter; the font id is a parameter
    [InlineData("^XA~DGR:X.GRF,8,1,FF^XZ", 7, "~DG")]
    public void At_caret_finds_the_command_the_caret_belongs_to(string text, int caret, string expected)
    {
        Assert.Equal(expected, ZplCommandReference.AtCaret(text, caret)!.Id);
    }

    [Fact]
    public void At_caret_is_null_outside_any_command()
    {
        Assert.Null(ZplCommandReference.AtCaret("hello ^FO1,1", 2));   // plain text before the first command
        Assert.Null(ZplCommandReference.AtCaret("", 0));
        Assert.Null(ZplCommandReference.AtCaret("^FO1,1", 99));
        Assert.Null(ZplCommandReference.AtCaret("^FO1,1", -1));
        Assert.Null(ZplCommandReference.AtCaret("^YY9", 2));          // a command neither catalogue knows
        Assert.Equal("^YY", ZplCommandReference.CommandTokenAtCaret("^YY9", 2));
    }
}
