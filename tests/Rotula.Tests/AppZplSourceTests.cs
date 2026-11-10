using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Variables;

namespace Rotula.Tests;

/// <summary>
/// The editable ZPL source panel: typed text is re-imported after the quiet period as one undo step, the two directions never feed back
/// into each other, "Revert to design" regenerates, the status line reports what was kept raw, and the hint follows the caret.
/// </summary>
[Collection("App")]
public class AppZplSourceTests : IDisposable
{
    private readonly FakeServices _svc = new();
    private readonly MainViewModel _main;

    public AppZplSourceTests()
    {
        Loc.Instance.SetLanguage("en");
        _main = new MainViewModel(_svc.Build());
        var doc = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(400), Dpi = PrinterDpi.Dpi300, Id = "label-1" };
        doc.Elements.Add(new TextElement { X = 50, Y = 40, Text = "Hello", FontHeight = 40, FontWidth = 30, Name = "Greeting" });
        doc.Elements.Add(new BoxElement { X = 10, Y = 10, WidthDots = 200, HeightDots = 100 });
        _main.Session.Load(doc);
    }

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private ZplSourceViewModel Source => _main.ZplSource;

    private TextElement FirstText => _main.Session.Document.Elements.OfType<TextElement>().First();

    [Fact]
    public void Starts_with_the_generated_text_and_no_edit()
    {
        Assert.Equal(_main.ZplText, Source.Text);
        Assert.Contains("^FDHello^FS", Source.Text);
        Assert.Equal(ZplSourceState.Generated, Source.State);
        Assert.False(Source.IsEdited);
        Assert.False(Source.RevertToDesignCommand.CanExecute(null));
        Assert.Equal("Generated", Source.StateText);
    }

    [Fact]
    public void Typing_waits_for_the_quiet_period_then_reimports_as_one_undo_step()
    {
        int undoBefore = _main.Session.Undo.UndoCount;
        Source.Text = Source.Text.Replace("^FDHello^FS", "^FDHola^FS");
        // Nothing happens until the pause: the label still says Hello and the state says so.
        Assert.Equal(ZplSourceState.Pending, Source.State);
        Assert.Equal("Hello", FirstText.Text);
        Assert.Equal(ZplSourceViewModel.Delay, _svc.Debouncer.LastDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(300), ZplSourceViewModel.Delay);

        Assert.True(_svc.Debouncer.Fire());
        Assert.Equal("Hola", FirstText.Text);
        Assert.Equal(ZplSourceState.Edited, Source.State);
        Assert.True(Source.IsEdited);
        Assert.Equal(undoBefore + 1, _main.Session.Undo.UndoCount);
        Assert.Equal("Undo.ZplSource", _main.Session.Undo.NextUndoKey);
        Assert.Equal("Edited by hand - re-imported", Source.StateText);
        Assert.StartsWith("Re-imported. Every command was understood.", Source.StatusText);
    }

    [Fact]
    public void Rapid_edits_collapse_into_one_reimport_and_one_undo_step()
    {
        int undoBefore = _main.Session.Undo.UndoCount;
        Source.Text = Source.Text.Replace("Hello", "Hel");
        Source.Text = Source.Text.Replace("Hel", "Help");
        Source.Text = Source.Text.Replace("Help", "Helpful");
        Assert.Equal(3, _svc.Debouncer.Restarts);
        _svc.Debouncer.Fire();
        Assert.False(_svc.Debouncer.Fire()); // nothing left pending
        Assert.Equal("Helpful", FirstText.Text);
        Assert.Equal(undoBefore + 1, _main.Session.Undo.UndoCount);
    }

    [Fact]
    public void The_typed_text_is_kept_after_the_reimport_not_replaced_by_the_regenerated_one()
    {
        // Comments are kept as raw elements and regenerated differently (own line), so the typed text and the generated text differ.
        string typed = Source.Text.Replace("^FDHello^FS", "^FDHello^FS^FX note").Replace("\n", "\r\n");
        Source.Text = typed;
        _svc.Debouncer.Fire();
        Assert.Equal(typed, Source.Text);
        Assert.Equal(ZplSourceState.Edited, Source.State);
        Assert.Contains(_main.Session.Document.Elements, e => e is RawZplElement { Zpl: "^FX note^FS" });
    }

    [Fact]
    public void A_change_on_the_canvas_regenerates_the_text_and_clears_the_edited_state()
    {
        Source.Text = Source.Text.Replace("Hello", "Hola");
        _svc.Debouncer.Fire();
        Assert.True(Source.IsEdited);

        _main.Session.Edit("Undo.Move", d => d.Elements[0].X = 99);
        Assert.Equal(ZplSourceState.Generated, Source.State);
        Assert.Equal(_main.ZplText, Source.Text);
        Assert.Contains("^FO99,40", Source.Text);
        Assert.Contains("^FDHola^FS", Source.Text);
    }

    [Fact]
    public void Undo_restores_the_design_and_the_generated_text()
    {
        string before = Source.Text;
        Source.Text = Source.Text.Replace("Hello", "Hola");
        _svc.Debouncer.Fire();
        Assert.Equal("Hola", FirstText.Text);

        _main.UndoCommand.Execute(null);
        Assert.Equal("Hello", FirstText.Text);
        Assert.Equal(before, Source.Text);
        Assert.Equal(ZplSourceState.Generated, Source.State);

        _main.RedoCommand.Execute(null);
        Assert.Equal("Hola", FirstText.Text);
        Assert.Contains("^FDHola^FS", Source.Text);
    }

    [Fact]
    public void A_canvas_change_while_typing_does_not_overwrite_the_text_being_typed()
    {
        string typed = Source.Text.Replace("Hello", "Hola");
        Source.Text = typed;
        _main.Session.Touch();
        _main.Session.Edit("Undo.Move", d => d.Elements[0].X = 77);
        Assert.Equal(typed, Source.Text);
        Assert.Equal(ZplSourceState.Pending, Source.State);
        _svc.Debouncer.Fire();
        // The typed text wins: it was re-imported after the canvas change.
        Assert.Equal(("Hola", 50), (FirstText.Text, FirstText.X));
    }

    [Fact]
    public void Revert_to_design_drops_pending_text_and_shows_the_generated_one()
    {
        string generated = Source.Text;
        Source.Text = "^XA^FO1,1^FDgarbage^FS^XZ";
        Assert.True(Source.RevertToDesignCommand.CanExecute(null));
        Source.RevertToDesignCommand.Execute(null);
        Assert.Null(_svc.Debouncer.Pending);
        Assert.Equal(generated, Source.Text);
        Assert.Equal(ZplSourceState.Generated, Source.State);
        Assert.Equal("Hello", FirstText.Text);
        Assert.False(_main.Session.Undo.CanUndo);
    }

    [Fact]
    public void Revert_after_a_reimport_regenerates_the_canonical_text_of_the_edited_design()
    {
        Source.Text = Source.Text.Replace("^FDHello^FS", "^FDHola^FS   ");
        _svc.Debouncer.Fire();
        Source.RevertToDesignCommand.Execute(null);
        Assert.Equal(_main.ZplText, Source.Text);
        Assert.Contains("^FDHola^FS\n", Source.Text);
        Assert.Equal("Hola", FirstText.Text);
    }

    [Fact]
    public void Unsupported_commands_are_counted_and_named_in_the_status_line()
    {
        Source.Text = Source.Text.Replace("^FDHello^FS", "^FDHello^FS\n^JUS\n^KL1\n^ZZ9\n^MMX\n^ZZ9");
        _svc.Debouncer.Fire();
        // ^JUS, ^KL and ^ZZ are not modelled (kept raw); ^MMX has an unknown print mode (also kept raw). Names are distinct, three are spelled out.
        Assert.StartsWith("Re-imported. 5 commands are not supported and were kept as raw ZPL: ^JU, ^KL, ^ZZ, 1 more", Source.StatusText);
        Assert.Equal(5, Source.LastWarnings.Count(w => w.MessageKey is "Import.Command.Unknown" or "Import.Setting.Unsupported"));
        Assert.Contains("line", Source.StatusDetail, StringComparison.OrdinalIgnoreCase);
        // Nothing was dropped: the raw commands are in the label and come back in the generated text.
        Assert.Equal(5, _main.Session.Document.Elements.OfType<RawZplElement>().Count());
        Assert.Contains("^JUS", _main.ZplText);
    }

    [Fact]
    public void Other_notices_are_counted_separately()
    {
        Source.Text = "^XA^FO10,10^A0N,30,30^FDNo size^FS^XZ";
        _svc.Debouncer.Fire();
        // No ^PW and no ^LL: two notices, no unsupported command.
        Assert.Equal("Re-imported. Every command was understood. · 2 other notices (hover for details)", Source.StatusText);
    }

    [Fact]
    public void Empty_text_changes_nothing_and_says_so()
    {
        int undoBefore = _main.Session.Undo.UndoCount;
        Source.Text = "   ";
        _svc.Debouncer.Fire();
        Assert.Equal(undoBefore, _main.Session.Undo.UndoCount);
        Assert.Equal(2, _main.Session.Document.Elements.Count);
        Assert.StartsWith("Nothing to import", Source.StatusText);
    }

    [Fact]
    public void Several_labels_in_the_text_use_the_first_and_say_so()
    {
        Source.Text = "^XA^PW800^LL400^FO1,1^FDone^FS^XZ^XA^PW800^LL400^FO2,2^FDtwo^FS^XZ";
        _svc.Debouncer.Fire();
        Assert.Equal("one", FirstText.Text);
        Assert.StartsWith("The text holds 2 labels; the first one is used", Source.StatusText);
    }

    [Fact]
    public void Reimport_keeps_what_zpl_cannot_say_resolution_identity_variables_and_names()
    {
        var doc = _main.Session.Document;
        doc.Variables.Add(new LabelVariable("expiry") { Kind = VariableKind.Date, Date = new DateVariableOptions { Format = "gs1" } });
        doc.Elements[1].Locked = true;
        Source.Text = Source.Text.Replace("Hello", "Hola");
        _svc.Debouncer.Fire();

        doc = _main.Session.Document;
        Assert.Equal((PrinterDpi.Dpi300, "label-1"), (doc.Dpi, doc.Id));
        Assert.Equal(VariableKind.Date, Assert.Single(doc.Variables, v => v.Name == "expiry").Kind);
        // The elements kept their kind and place, so their names, locks and ids carried over.
        Assert.Equal("Greeting", FirstText.Name);
        Assert.True(doc.Elements.OfType<BoxElement>().Single().Locked);
        Assert.Equal((800, 400), (doc.WidthDots, doc.HeightDots));
    }

    [Fact]
    public void Reimport_rebinds_a_data_field_whose_value_is_in_the_text()
    {
        _main.Session.Edit("Undo.Property", d =>
        {
            d.Variables.Add(new LabelVariable("Customer", "Acme"));
            ((TextElement)d.Elements[0]).Text = "{{Customer}}";
            d.Elements.Add(new BarcodeElement { X = 20, Y = 200, Symbology = BarcodeSymbology.Code128, Data = "{{Customer}}" });
        });
        Assert.Contains("^FDAcme^FS", Source.Text);
        // Move the text in the ZPL; the field's value is still there, so the binding is put back.
        Source.Text = Source.Text.Replace("^FO50,40", "^FO60,40");
        _svc.Debouncer.Fire();
        Assert.Equal(("{{Customer}}", 60), (FirstText.Text, FirstText.X));
        Assert.Equal("{{Customer}}", _main.Session.Document.Elements.OfType<BarcodeElement>().Single().Data);
        Assert.Contains("2 data field(s) bound again", Source.StatusText);
        // A field whose value was changed in the text becomes fixed text: the user typed a value, not a field.
        Source.Text = Source.Text.Replace("^FDAcme^FS", "^FDBeta^FS");
        _svc.Debouncer.Fire();
        Assert.Equal("Beta", FirstText.Text);
    }

    [Fact]
    public void Reimport_keeps_objects_marked_do_not_print()
    {
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { X = 1, Y = 1, Text = "Design note", NotPrintable = true }));
        Assert.DoesNotContain("Design note", Source.Text);
        Source.Text = Source.Text.Replace("Hello", "Hola");
        _svc.Debouncer.Fire();
        Assert.Contains(_main.Session.Document.Elements, e => e is TextElement { Text: "Design note", NotPrintable: true });
    }

    [Fact]
    public void Numbered_fields_typed_in_the_text_become_data_fields()
    {
        Source.Text = Source.Text.Replace("^FDHello^FS", "^FN1\"Customer name\"^FDHello^FS");
        _svc.Debouncer.Fire();
        Assert.Equal("{{Customer_name}}", FirstText.Text);
        Assert.Equal("Hello", Assert.Single(_main.Session.Document.Variables, v => v.Name == "Customer_name").SampleValue);
        Assert.Contains(_main.Layers, l => l.IsBound);
    }

    [Fact]
    public void The_hint_follows_the_caret()
    {
        // The caret starts at position 0, which is on the ^XA that opens every generated label.
        Assert.StartsWith("^XA  Start of label", Source.HintText);
        // No caret (the editor is not focused): the idle text explains what the line is for.
        Source.CaretIndex = -1;
        Assert.StartsWith("Put the caret on a command", Source.HintText);
        int fo = Source.Text.IndexOf("^FO50,40", StringComparison.Ordinal);
        Source.CaretIndex = fo + 4;
        Assert.StartsWith("^FO x,y,z  Field origin", Source.HintText);
        Assert.Contains("x: left edge", Source.HintText);
        Assert.Contains("x: left edge", Source.HintDetail);

        Source.CaretIndex = Source.Text.IndexOf("^FS", StringComparison.Ordinal) + 1;
        Assert.StartsWith("^FS  ", Source.HintText);

        Source.Text = Source.Text.Replace("^FDHello^FS", "^FDHello^FS^JUS");
        Source.CaretIndex = Source.Text.IndexOf("^JUS", StringComparison.Ordinal) + 2;
        Assert.Contains("kept as raw ZPL", Source.HintText);

        Source.Text = Source.Text.Replace("^JUS", "^YY9");
        Source.CaretIndex = Source.Text.IndexOf("^YY9", StringComparison.Ordinal) + 2;
        Assert.Equal("^YY is not a ZPL command Rotula knows. It will be kept as raw ZPL.", Source.HintText);
    }

    [Fact]
    public void Copy_takes_the_text_as_shown_in_the_panel()
    {
        Source.Text = "^XA^FDtyped^FS^XZ";
        _main.CopyZplCommand.Execute(null);
        Assert.Equal("^XA^FDtyped^FS^XZ", _svc.ClipboardText);
    }

    [Fact]
    public void Texts_follow_the_language()
    {
        Source.Text = Source.Text.Replace("Hello", "Hola");
        _svc.Debouncer.Fire();
        Assert.Equal("Edited by hand - re-imported", Source.StateText);
        // A language change regenerates everything from the design (the design already holds the re-imported text), in the new language.
        _main.SetLanguageCommand.Execute("es");
        Assert.Equal("Generado", Source.StateText);
        Assert.StartsWith("Generado a partir del diseño", Source.StatusText);
        Source.CaretIndex = -1;
        Assert.StartsWith("Coloque el cursor", Source.HintText);
        Assert.Contains("^FDHola^FS", Source.Text);
    }
}
