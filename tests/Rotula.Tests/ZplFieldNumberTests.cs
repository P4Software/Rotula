using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using static Rotula.Tests.CoreTestSupport;
using static Rotula.Tests.ZplParserTestSupport;

namespace Rotula.Tests;

/// <summary>
/// Numbered fields (<c>^FN</c>) as the printer's variables: on import they become <c>{{placeholders}}</c> with declared sample values, and a label
/// saved as a stored format (<c>^DF</c>) writes its placeholders as numbered fields that a recall (<c>^XF</c>) fills in.
/// </summary>
public class ZplFieldNumberTests
{
    private static string[] Keys(ZplParseResult r) => r.Warnings.Select(w => w.MessageKey).ToArray();

    // ---- import: ^FN becomes a placeholder -------------------------------------------------------------

    [Fact]
    public void Fixture_with_numbered_fields_imports_as_placeholders_with_sample_values()
    {
        var r = ZplParser.ParseFile(ImportFixture("field_numbers"));
        var doc = r.Document;
        Assert.Empty(Keys(r));

        var name = Assert.IsType<TextElement>(doc.Elements[0]);
        Assert.Equal(("{{Customer_name}}", 1, "Customer name"), (name.Text, name.FieldNumber, name.FieldPrompt));
        var order = Assert.IsType<TextElement>(doc.Elements[1]);
        Assert.Equal(("{{Field2}}", 2), (order.Text, order.FieldNumber));
        Assert.Equal("Fixed heading", Assert.IsType<TextElement>(doc.Elements[2]).Text);
        var serial = Assert.IsType<BarcodeElement>(doc.Elements[3]);
        Assert.Equal(("{{Serial}}", 3), (serial.Data, serial.FieldNumber));
        // The second field with number 1 shares the variable: on the printer both print the same data.
        Assert.Equal("{{Customer_name}}", Assert.IsType<TextElement>(doc.Elements[4]).Text);

        Assert.Equal(new[] { ("Customer_name", "Acme Ltd"), ("Field2", "Order 0042"), ("Serial", "12345678") },
            doc.Variables.Select(v => (v.Name, v.SampleValue)).ToArray());
    }

    [Fact]
    public void Imported_numbered_fields_print_the_same_and_merge_from_data()
    {
        var doc = ZplParser.ParseFile(ImportFixture("field_numbers")).Document;
        string sample = ZplGenerator.Generate(doc).Zpl;
        Assert.Contains("^FN1\"Customer name\"^FDAcme Ltd^FS", sample);
        Assert.Contains("^FN3\"Serial\"^FD12345678^FS", sample);
        string merged = ZplGenerator.Generate(doc, new Dictionary<string, string> { ["Customer_name"] = "Beta Co", ["Serial"] = "99" }).Zpl;
        Assert.Contains("^FN1\"Customer name\"^FDBeta Co^FS", merged);
        Assert.Contains("^FN3\"Serial\"^FD99^FS", merged);
        Assert.Contains("^FN2^FDOrder 0042^FS", merged);
    }

    [Theory]
    [InlineData("Customer name", "Customer_name")]
    [InlineData("Lot  number ", "Lot_number")]
    [InlineData("123", "Field_123")]
    [InlineData(" 7 up", "Field_7_up")]
    public void Prompt_text_becomes_a_valid_placeholder_name(string prompt, string expected)
    {
        var r = ZplParser.Parse("^XA^FO10,10^A0N,30,30^FN5\"" + prompt + "\"^FDx^FS^XZ");
        Assert.Equal("{{" + expected + "}}", Assert.IsType<TextElement>(Assert.Single(r.Document.Elements)).Text);
    }

    [Fact]
    public void A_prompt_of_only_punctuation_falls_back_to_the_number()
    {
        var r = ZplParser.Parse("^XA^FO10,10^A0N,30,30^FN5\" \"^FDx^FS^XZ");
        Assert.Equal("{{Field5}}", Assert.IsType<TextElement>(Assert.Single(r.Document.Elements)).Text);
    }

    [Fact]
    public void Two_numbers_with_the_same_prompt_get_different_names()
    {
        var r = ZplParser.Parse("^XA^FO10,10^A0N,30,30^FN1\"Name\"^FDa^FS^FO10,50^A0N,30,30^FN2\"Name\"^FDb^FS^XZ");
        Assert.Equal(new[] { "{{Name}}", "{{Name_2}}" }, r.Document.Elements.Cast<TextElement>().Select(t => t.Text).ToArray());
        Assert.Equal(new[] { ("Name", "a"), ("Name_2", "b") }, r.Document.Variables.Select(v => (v.Name, v.SampleValue)).ToArray());
    }

    [Fact]
    public void A_later_field_with_the_same_number_supplies_the_sample_when_the_first_had_none()
    {
        var r = ZplParser.Parse("^XA^FO10,10^A0N,30,30^FN4^FS^FO10,50^A0N,30,30^FN4^FDLater^FS^XZ");
        Assert.Equal("Later", Assert.Single(r.Document.Variables).SampleValue);
    }

    [Fact]
    public void A_printer_serial_number_keeps_its_start_value_instead_of_becoming_a_placeholder()
    {
        var r = ZplParser.Parse("^XA^FO10,10^A0N,30,30^FN2^SN001,1,Y^FS^XZ");
        var t = Assert.IsType<TextElement>(Assert.Single(r.Document.Elements));
        Assert.Equal(("001", 2), (t.Text, t.FieldNumber));
        Assert.NotNull(t.Serial);
        Assert.Empty(r.Document.Variables);
    }

    [Fact]
    public void A_recall_file_imports_its_numbered_fields_as_placeholders()
    {
        var r = ZplParser.Parse("^XA^XFR:SHIP.ZPL^FS^FN1^FDAcme^FS^FN2^FD42^FS^XZ");
        Assert.IsType<RecallPlaceholderElement>(r.Document.Elements[0]);
        Assert.Equal(new[] { "{{Field1}}", "{{Field2}}" }, r.Document.Elements.OfType<TextElement>().Select(t => t.Text).ToArray());
        Assert.Equal(new[] { "Acme", "42" }, r.Document.Variables.Select(v => v.SampleValue).ToArray());
    }

    // ---- generation: stored formats and recalls -------------------------------------------------------

    private static LabelDocument FormatDoc()
    {
        var doc = NewDoc(
            new TextElement { X = 30, Y = 30, FontHeight = 40, FontWidth = 40, Text = "{{Customer}}" },
            new TextElement { X = 30, Y = 90, FontHeight = 30, FontWidth = 30, Text = "Order {{Order}}" },
            new TextElement { X = 30, Y = 140, FontHeight = 30, FontWidth = 30, Text = "Fixed heading" },
            new BarcodeElement { X = 30, Y = 200, Symbology = BarcodeSymbology.Code128, Data = "{{Serial}}", Height = 80, FieldNumber = 7, FieldPrompt = "Serial" },
            new TextElement { X = 400, Y = 30, FontHeight = 30, FontWidth = 30, Text = "{{Customer}}" });
        doc.Variables.Add(new LabelVariable("Customer", "Acme Ltd"));
        doc.Variables.Add(new LabelVariable("Order", "0042"));
        doc.Variables.Add(new LabelVariable("Serial", "12345678"));
        return doc;
    }

    [Fact]
    public void Stored_format_writes_placeholders_as_numbered_fields_without_data()
    {
        var result = ZplGenerator.GenerateStoredFormat(FormatDoc(), "ship");
        Assert.False(result.HasErrors);
        // Own folder: the top-level goldens must be fixpoints of parse-then-generate, which a stored format (^DF kept raw) is not.
        AssertGolden("formats/format_stored", result.Zpl);
        // Element numbers are kept (7), other names get the lowest free numbers in label order, the same name shares one number.
        Assert.Equal(new[] { (1, "Customer"), (7, "Serial"), (1, "Customer") }, result.FormatFields.Select(f => (f.Number, f.Name)).ToArray());
        // The mixed field cannot be numbered: it is stored with its sample value and reported.
        Assert.Contains(result.Issues, i => i.MessageKey == "Format.Field.Mixed" && i.Severity == Rotula.Core.Validation.ValidationSeverity.Warning);
        Assert.DoesNotContain("^PQ", result.Zpl);
    }

    [Fact]
    public void Recall_fills_the_same_numbers_with_data_or_sample_values()
    {
        var sample = ZplGenerator.GenerateFormatRecall(FormatDoc(), "ship");
        AssertGolden("formats/format_recall", sample.Zpl);
        var merged = ZplGenerator.GenerateFormatRecall(FormatDoc(), "R:SHIP.ZPL", new Dictionary<string, string> { ["Customer"] = "Niño S.A.", ["Serial"] = "77" }, quantity: 3);
        Assert.Contains("^XFR:SHIP.ZPL^FS", merged.Zpl);
        Assert.Contains("^FH_^FN1^FDNi_C3_B1o S.A.^FS", merged.Zpl);
        Assert.Contains("^FN7^FD77^FS", merged.Zpl);
        Assert.Contains("^PQ3,0,1,N", merged.Zpl);
        // Number 1 is shared by two elements: it is written once.
        Assert.Equal(1, merged.Zpl.Split("^FN1^").Length - 1);
    }

    [Fact]
    public void Stored_format_round_trips_through_the_importer_as_placeholders()
    {
        var format = ZplGenerator.GenerateStoredFormat(FormatDoc(), "ship").Zpl;
        var r = ZplParser.Parse(format);
        // The store command itself is not modelled, so it is kept verbatim and reported; the fields come back as placeholders.
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { Zpl: "^DFR:SHIP.ZPL" });
        Assert.Contains("Import.StoredFormat.Unsupported", Keys(r));
        var texts = r.Document.Elements.OfType<TextElement>().Select(t => t.Text).ToArray();
        Assert.Equal(new[] { "{{Customer}}", "Order 0042", "Fixed heading", "{{Customer}}" }, texts);
        Assert.Equal("{{Serial}}", Assert.Single(r.Document.Elements.OfType<BarcodeElement>()).Data);
        Assert.Equal(new[] { "Customer", "Serial" }, r.Document.Variables.Select(v => v.Name).ToArray());
        // Saving the re-read label as a format again writes the same numbered fields.
        var again = ZplGenerator.GenerateStoredFormat(r.Document, "ship");
        Assert.Equal(new[] { (1, "Customer"), (7, "Serial"), (1, "Customer") }, again.FormatFields.Select(f => (f.Number, f.Name)).ToArray());
    }

    [Fact]
    public void A_bad_format_name_is_an_error_and_names_are_normalised()
    {
        Assert.Equal("R:SHIP.ZPL", ZplGenerator.NormalizeFormatPath("ship"));
        Assert.Equal("E:LABEL.ZPL", ZplGenerator.NormalizeFormatPath("e:label.zpl"));
        Assert.True(ZplGenerator.IsValidFormatPath("R:SHIP.ZPL"));
        Assert.False(ZplGenerator.IsValidFormatPath("R:SHIP.GRF"));
        var result = ZplGenerator.GenerateStoredFormat(FormatDoc(), "bad name");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.MessageKey == "Format.Path.Invalid");
    }

    [Fact]
    public void A_label_without_placeholders_stores_as_fixed_fields_only()
    {
        var result = ZplGenerator.GenerateStoredFormat(NewDoc(new TextElement { X = 10, Y = 10, Text = "Hello" }), "plain");
        Assert.Empty(result.FormatFields);
        Assert.DoesNotContain("^FN", result.Zpl);
        Assert.Contains("^DFR:PLAIN.ZPL^FS", result.Zpl);
        Assert.Contains("^FDHello^FS", result.Zpl);
    }
}
