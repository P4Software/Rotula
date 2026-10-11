using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Barcodes;
using Rotula.Core.Barcodes.QrContent;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests;

/// <summary>
/// The QR content builder, the GS1 builder and the option rows of the barcode property panel: each kind builds the expected text, fields can be
/// bound to a data column, Apply stores the definition and the text in one undo step, reopening restores the form, editing the text by hand drops
/// the definition, the capacity meter speaks, and Apply is disabled until the content is valid.
/// </summary>
[Collection("App")]
public class AppBuilderTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly MainViewModel _main;

    public AppBuilderTests()
    {
        _fake.Settings.Current.Language = "en";
        _main = new MainViewModel(_fake.Build());
    }

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private static readonly QrBuilderSettings Settings = new(QrErrorCorrection.Quartile, 2, 4, 203);

    private BarcodeElement AddBarcode(BarcodeSymbology symbology, string data = "https://example.com")
    {
        var b = new BarcodeElement { Symbology = symbology, Data = data, X = 20, Y = 20 };
        _main.Commands.Add(b);
        return b;
    }

    private BarcodeElement Live(string id) => (BarcodeElement)_main.Session.Document.Elements.First(e => e.Id == id);

    private static QrFieldEditor Editor(QrContentBuilderViewModel vm, string name) => vm.Editors.First(e => e.Name == name);

    private static void Fill(QrContentBuilderViewModel vm, params (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values) Editor(vm, name).Row.Text = value;
    }

    private void DeclareVariable(string name, string sample)
    {
        _main.Session.Edit("test", d => d.Variables.Add(new LabelVariable(name, sample)));
        _main.Fields.Refresh();
    }

    // ------------------------------------------------------------------------------ every kind builds its text

    [Theory]
    [InlineData(QrContentKind.Url, "https://example.com/menu")]
    [InlineData(QrContentKind.Text, "Hello")]
    [InlineData(QrContentKind.Wifi, "WIFI:T:WPA;S:CafeGuest;P:welcome2026;")]
    [InlineData(QrContentKind.VCard, "BEGIN:VCARD")]
    [InlineData(QrContentKind.MeCard, "MECARD:")]
    [InlineData(QrContentKind.GS1DigitalLink, "https://id.gs1.org/01/09506000134352")]
    [InlineData(QrContentKind.Email, "mailto:ana@example.com")]
    [InlineData(QrContentKind.Phone, "tel:+50761234567")]
    [InlineData(QrContentKind.Location, "geo:8.983333,-79.516667")]
    [InlineData(QrContentKind.CalendarEvent, "BEGIN:VEVENT")]
    public void EachKind_BuildsTheExpectedText_FromItsExampleValues(QrContentKind kind, string expectedStart)
    {
        var vm = new QrContentBuilderViewModel(null, null, Settings);
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == kind));
        Assert.Equal(kind, vm.Kind);
        foreach (var f in QrContentSchema.For(kind).Fields.Where(f => f.Hint != QrInputHint.Flag)) Editor(vm, f.Name).Row.Text = f.Sample;
        Assert.StartsWith(expectedStart, vm.GeneratedText);
        Assert.Equal(QrContentBuilder.BuildTemplate(vm.CurrentSpec()), vm.GeneratedText);
        Assert.True(vm.CanApply, string.Join("; ", vm.Messages.Select(m => m.Text)));
        Assert.NotNull(vm.PreviewMatrix);
    }

    [Fact]
    public void Sms_BuildsFromNumberAndMessage()
    {
        var vm = new QrContentBuilderViewModel(null, null, Settings);
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Sms));
        Fill(vm, ("number", "+50761234567"), ("message", "Hello"));
        Assert.Contains("+50761234567", vm.GeneratedText);
        Assert.Contains("Hello", vm.GeneratedText);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void TheForm_UsesTheRightControlForEachKindOfField()
    {
        var vm = new QrContentBuilderViewModel(null, null, Settings);
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Wifi));
        Assert.True(Editor(vm, "security").IsChoice);
        Assert.Equal(4, Editor(vm, "security").Choices.Count);
        Assert.True(Editor(vm, "hidden").IsFlag);
        Assert.True(Editor(vm, "password").IsPassword);
        // The password is shown, not masked, and the dialog says why it matters.
        Assert.Contains("plain text", Editor(vm, "password").Note);
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.CalendarEvent));
        Assert.True(Editor(vm, "start").IsDateTime);
        Assert.True(Editor(vm, "description").IsMultiline);
        Assert.Contains("+50761234567", QrContentSchema.For(QrContentKind.Phone).Fields[0].Sample);
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Phone));
        Assert.Contains("country code", Editor(vm, "number").Hint);
    }

    [Fact]
    public void ChoicesFlagsAndDates_WriteIntoTheDefinition()
    {
        var vm = new QrContentBuilderViewModel(null, null, Settings);
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Wifi));
        Fill(vm, ("ssid", "Cafe"), ("password", "secret123"));
        var security = Editor(vm, "security");
        security.SelectedChoice = security.Choices.First(c => (string)c.Value! == "Wpa3Sae");
        Editor(vm, "hidden").Flag = true;
        Assert.Equal("Wpa3Sae", vm.CurrentSpec().Get("security"));
        Assert.Equal("true", vm.CurrentSpec().Get("hidden"));
        Assert.Contains("H:true", vm.GeneratedText);

        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.CalendarEvent));
        var start = Editor(vm, "start");
        start.Row.Text = "2026-03-14T09:00:00-05:00";
        Assert.Equal(new DateTime(2026, 3, 14), start.Date);
        Assert.Equal("09:00", start.TimeText);
        // Picking another day keeps the time and the zone the value came with.
        start.Date = new DateTime(2026, 3, 20);
        Assert.Equal("2026-03-20T09:00:00-05:00", start.Row.Text);
        start.TimeText = "10:30";
        Assert.Equal("2026-03-20T10:30:00-05:00", start.Row.Text);
        start.TimeText = "ten";
        Assert.True(start.TimeInvalid);
    }

    // ------------------------------------------------------------------------------ binding a field

    [Fact]
    public void InsertField_PutsThePlaceholderInTheText_AndBindingReplacesAChoice()
    {
        DeclareVariable("FullName", "Ana Perez");
        var field = _main.Fields.Find("FullName")!;
        var vm = new QrContentBuilderViewModel(_main.Fields, null, Settings, null, _main.Session.Document.Variables);
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.VCard));
        var first = Editor(vm, "firstName");
        first.Row.Text = "Mr. ";
        first.Row.InsertField(field, 4, 0);
        Assert.Equal("Mr. {{FullName}}", first.Row.Text);
        Assert.Contains("FN:Mr. {{FullName}}", vm.GeneratedText.Replace("\r\n", "\n").Replace("N:;Mr. {{FullName}}", "FN:Mr. {{FullName}}"));
        Assert.Contains("{{FullName}}", vm.GeneratedText);

        // A choice cannot hold mixed text: binding makes the placeholder the whole value and the control gives way to the text.
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Wifi));
        var security = Editor(vm, "security");
        security.Row.BindTo(field);
        Assert.True(security.IsBound);
        Assert.False(security.IsFixed);
        Assert.True(security.ShowUseFixed);
        security.UseFixedValueCommand.Execute(null);
        Assert.False(security.IsBound);
        Assert.Equal("Wpa", security.SelectedChoice!.Value);
    }

    [Fact]
    public void ABoundField_ShowsTheSampleInThePreview_AndStillValidates()
    {
        DeclareVariable("Web", "https://example.com/a");
        var vm = new QrContentBuilderViewModel(_main.Fields, null, Settings, null, _main.Session.Document.Variables);
        Editor(vm, "url").Row.BindTo(_main.Fields.Find("Web")!);
        Assert.Equal("{{Web}}", vm.GeneratedText);
        Assert.Equal("https://example.com/a", vm.PreviewText);
        Assert.True(vm.CanApply);
        Assert.False(string.IsNullOrEmpty(vm.PreviewNote));
    }

    // ------------------------------------------------------------------------------ apply, undo, reopen, hand edit

    [Fact]
    public void Apply_StoresTheDefinitionAndTheText_InOneUndoStep()
    {
        var qr = AddBarcode(BarcodeSymbology.QrCode);
        _fake.ToolDialogs.OnQrBuilder = vm =>
        {
            vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Wifi));
            Fill(vm, ("ssid", "CafeGuest"), ("password", "welcome2026"));
            vm.ApplyCommand.Execute(null);
        };
        int undoBefore = _main.Session.Undo.UndoCount;
        Assert.True(_main.Properties.EditQrContent(qr.Id));

        var now = Live(qr.Id);
        Assert.StartsWith("WIFI:T:WPA;S:CafeGuest;P:welcome2026;", now.Data);
        Assert.True(QrContentSpec.TryParse(now.QrContentSpec, out var spec));
        Assert.Equal(QrContentKind.Wifi, spec.Kind);
        Assert.Equal(undoBefore + 1, _main.Session.Undo.UndoCount);

        _main.Session.Undo.Undo();
        Assert.Equal("https://example.com", Live(qr.Id).Data);
        Assert.Equal(string.Empty, Live(qr.Id).QrContentSpec);
        _main.Session.Undo.Redo();
        Assert.Equal(now.Data, Live(qr.Id).Data);
        Assert.Equal(now.QrContentSpec, Live(qr.Id).QrContentSpec);
    }

    [Fact]
    public void Cancel_ChangesNothing()
    {
        var qr = AddBarcode(BarcodeSymbology.QrCode);
        int undoBefore = _main.Session.Undo.UndoCount;
        _fake.ToolDialogs.OnQrBuilder = vm => vm.CancelCommand.Execute(null);
        Assert.False(_main.Properties.EditQrContent(qr.Id));
        Assert.Equal(undoBefore, _main.Session.Undo.UndoCount);
        Assert.Equal("https://example.com", Live(qr.Id).Data);
    }

    [Fact]
    public void ReopeningTheBuilder_RestoresTheFields()
    {
        var qr = AddBarcode(BarcodeSymbology.QrCode);
        _fake.ToolDialogs.OnQrBuilder = vm =>
        {
            vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Email));
            Fill(vm, ("to", "ana@example.com"), ("subject", "Order {{Sku}}"));
            vm.ApplyCommand.Execute(null);
        };
        DeclareVariable("Sku", "A-100");
        Assert.True(_main.Properties.EditQrContent(qr.Id));

        QrContentBuilderViewModel? reopened = null;
        _fake.ToolDialogs.OnQrBuilder = vm => { reopened = vm; vm.CancelCommand.Execute(null); };
        _main.Properties.EditQrContent(qr.Id);
        Assert.NotNull(reopened);
        Assert.True(reopened!.Restored);
        Assert.Equal(QrContentKind.Email, reopened.Kind);
        Assert.Equal("ana@example.com", Editor(reopened, "to").Row.Text);
        Assert.Equal("Order {{Sku}}", Editor(reopened, "subject").Row.Text);
        Assert.Contains(reopened.Kinds, k => k.IsSelected && k.Kind == QrContentKind.Email);
    }

    [Fact]
    public void TheBuilderStartsFromTheCurrentText_WhenThereIsNoDefinition()
    {
        var qr = AddBarcode(BarcodeSymbology.QrCode, "https://example.com/x");
        QrContentBuilderViewModel? seen = null;
        _fake.ToolDialogs.OnQrBuilder = vm => { seen = vm; vm.CancelCommand.Execute(null); };
        _main.Properties.EditQrContent(qr.Id);
        Assert.Equal(QrContentKind.Url, seen!.Kind);
        Assert.Equal("https://example.com/x", Editor(seen, "url").Row.Text);

        var plain = AddBarcode(BarcodeSymbology.QrCode, "just some words");
        _main.Properties.EditQrContent(plain.Id);
        Assert.Equal(QrContentKind.Text, seen.Kind);
        Assert.Equal("just some words", Editor(seen, "text").Row.Text);
    }

    [Fact]
    public void EditingTheTextByHand_DropsTheDefinition_AndExplainsIt()
    {
        var qr = AddBarcode(BarcodeSymbology.QrCode);
        _fake.ToolDialogs.OnQrBuilder = vm =>
        {
            Fill(vm, ("url", "https://example.com/menu"));
            vm.ApplyCommand.Execute(null);
        };
        Assert.True(_main.Properties.EditQrContent(qr.Id));
        Assert.NotEqual(string.Empty, Live(qr.Id).QrContentSpec);
        var status = _main.Properties.Rows.OfType<NoteRow>().Single(r => r.LabelKey == "Prop.QrContentSpec");
        Assert.Contains("Web address", status.Text);

        var data = _main.Properties.Rows.OfType<TextRow>().Single(r => r.LabelKey == "Prop.Data");
        data.Text = "https://example.com/other";
        Assert.Equal("https://example.com/other", Live(qr.Id).Data);
        Assert.Equal(string.Empty, Live(qr.Id).QrContentSpec);
        status = _main.Properties.Rows.OfType<NoteRow>().Single(r => r.LabelKey == "Prop.QrContentSpec");
        Assert.Contains("by hand", status.Text);
    }

    [Fact]
    public void ChangingTheSymbology_DropsTheDefinition()
    {
        var qr = AddBarcode(BarcodeSymbology.QrCode);
        _fake.ToolDialogs.OnQrBuilder = vm => { Fill(vm, ("url", "https://example.com/menu")); vm.ApplyCommand.Execute(null); };
        _main.Properties.EditQrContent(qr.Id);
        var picker = _main.Properties.Rows.OfType<ChoiceRow>().Single(r => r.LabelKey == "Prop.Symbology");
        picker.Selected = picker.Items.First(i => Equals(i.Value, BarcodeSymbology.DataMatrix));
        Assert.Equal(string.Empty, Live(qr.Id).QrContentSpec);
    }

    [Fact]
    public void ANewQrCode_CanStartInTheBuilder_AsOneUndoStep()
    {
        _fake.ToolDialogs.OnQrBuilder = vm =>
        {
            vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Phone));
            Fill(vm, ("number", "+50761234567"));
            vm.ApplyCommand.Execute(null);
        };
        int before = _main.Session.Undo.UndoCount;
        _main.AddElementCommand.Execute(ToolboxKind.QrCode);
        var qr = _main.Session.Document.Elements.OfType<BarcodeElement>().Single();
        Assert.Equal("tel:+50761234567", qr.Data);
        Assert.NotEqual(string.Empty, qr.QrContentSpec);
        Assert.Equal(before + 1, _main.Session.Undo.UndoCount);
        _main.Session.Undo.Undo();
        Assert.Empty(_main.Session.Document.Elements);
    }

    [Fact]
    public void ANewQrCode_KeepsTheExample_WhenTheBuilderIsCancelled()
    {
        _fake.ToolDialogs.OnQrBuilder = vm => vm.CancelCommand.Execute(null);
        _main.AddElementCommand.Execute(ToolboxKind.QrCode);
        var qr = _main.Session.Document.Elements.OfType<BarcodeElement>().Single();
        Assert.Equal("https://example.com", qr.Data);
        Assert.Equal(string.Empty, qr.QrContentSpec);
    }

    [Fact]
    public void ContentBuilderButton_IsOnTheDataRow_OnlyForQrCodes()
    {
        AddBarcode(BarcodeSymbology.QrCode);
        var data = _main.Properties.Rows.OfType<TextRow>().Single(r => r.LabelKey == "Prop.Data");
        Assert.True(data.HasAction);
        Assert.Equal("Content builder...", data.ActionCaption);
        AddBarcode(BarcodeSymbology.Code128, "123");
        data = _main.Properties.Rows.OfType<TextRow>().Single(r => r.LabelKey == "Prop.Data");
        Assert.Equal("GS1 builder...", data.ActionCaption);
        AddBarcode(BarcodeSymbology.Ean13, "5901234123457");
        data = _main.Properties.Rows.OfType<TextRow>().Single(r => r.LabelKey == "Prop.Data");
        Assert.False(data.HasAction);
    }

    // ------------------------------------------------------------------------------ validation and capacity

    [Fact]
    public void Apply_IsDisabledUntilTheContentIsValid()
    {
        var vm = new QrContentBuilderViewModel(null, null, Settings);
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Wifi));
        Assert.False(vm.CanApply);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        // A fresh form waits quietly: no wall of errors before the user typed anything.
        Assert.Empty(vm.Messages);
        Assert.NotEqual(string.Empty, vm.StatusHint);

        Fill(vm, ("ssid", "Cafe"));
        Assert.False(vm.CanApply); // a secured network needs its password
        Fill(vm, ("password", "welcome2026"));
        Assert.True(vm.CanApply);
        Fill(vm, ("ssid", ""));
        Assert.False(vm.CanApply);
        Assert.Contains(vm.Messages, m => m.IsError);
        Assert.True(Editor(vm, "ssid").HasError);

        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Url));
        Fill(vm, ("url", "not a web address"));
        Assert.False(vm.CanApply);
        Assert.True(Editor(vm, "url").HasError);
        vm.ApplyCommand.Execute(null);
        Assert.False(vm.Applied);
        Fill(vm, ("url", "https://example.com"));
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void FieldErrors_AreExplainedInPlainLanguage_InBothLanguages()
    {
        var vm = new QrContentBuilderViewModel(null, null, Settings);
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Email));
        Fill(vm, ("to", "nobody"));
        Assert.Contains("email address is not valid", Editor(vm, "to").Error);
        Loc.Instance.SetLanguage("es");
        var es = new QrContentBuilderViewModel(null, null, Settings);
        es.SelectKindCommand.Execute(es.Kinds.First(k => k.Kind == QrContentKind.Email));
        Fill(es, ("to", "nobody"));
        Assert.Contains("correo", Editor(es, "to").Error);
        Assert.Equal("Correo", es.Kinds.First(k => k.Kind == QrContentKind.Email).Label.Replace("electrónico", "").Trim());
    }

    [Fact]
    public void TheCapacityMeter_ReportsHowFullTheCodeIs_AndWhatToDo()
    {
        var vm = new QrContentBuilderViewModel(null, null, Settings);
        vm.SelectKindCommand.Execute(vm.Kinds.First(k => k.Kind == QrContentKind.Text));
        Fill(vm, ("text", "Hello"));
        Assert.StartsWith("Fits: ", vm.CapacityText);
        Assert.Contains("level Q", vm.CapacityText);
        Assert.Contains("version", vm.CapacityText);
        Assert.False(vm.CapacityIsWarning);
        Assert.True(vm.CanApply);

        // Too long for the level: Apply stays disabled and the hint names a level that would fit.
        Fill(vm, ("text", new string('x', 1700)));
        Assert.Equal(CapacityState.Overflow, vm.Capacity.State);
        Assert.Contains("Lower the level or shorten the text", vm.CapacityText);
        Assert.Contains("Level", vm.CapacityHint);
        Assert.True(vm.CapacityIsError);
        Assert.False(vm.CanApply);
        vm.SelectedLevel = vm.LevelChoices.First(l => Equals(l.Value, QrErrorCorrection.Low));
        Assert.Equal(CapacityState.Fits, vm.Capacity.State);
        Assert.True(vm.CanApply);
        Assert.Equal(QrErrorCorrection.Low, vm.Level);

        // Nearly full is allowed but warned about.
        Fill(vm, ("text", new string('x', 2700)));
        Assert.Equal(CapacityState.Dense, vm.Capacity.State);
        Assert.True(vm.CapacityIsWarning);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void TheLevelChosenInTheDialog_GoesToTheElementWithTheContent()
    {
        var qr = AddBarcode(BarcodeSymbology.QrCode);
        _fake.ToolDialogs.OnQrBuilder = vm =>
        {
            Fill(vm, ("url", "https://example.com/menu"));
            vm.SelectedLevel = vm.LevelChoices.First(l => Equals(l.Value, QrErrorCorrection.High));
            vm.ApplyCommand.Execute(null);
        };
        _main.Properties.EditQrContent(qr.Id);
        Assert.Equal(QrErrorCorrection.High, Live(qr.Id).QrErrorCorrection);
        _main.Session.Undo.Undo();
        Assert.Equal(QrErrorCorrection.Quartile, Live(qr.Id).QrErrorCorrection);
    }

    [Fact]
    public void ThePrintedSize_IsShown_AndATooSmallModuleIsWarnedAbout()
    {
        var vm = new QrContentBuilderViewModel(null, null, new QrBuilderSettings(QrErrorCorrection.Quartile, 2, 1, 203));
        Fill(vm, ("url", "https://example.com"));
        Assert.True(vm.SizeIsWarning);
        Assert.Contains("Too small to scan", vm.SizeText);
        var ok = new QrContentBuilderViewModel(null, null, new QrBuilderSettings(QrErrorCorrection.Quartile, 2, 4, 203));
        Fill(ok, ("url", "https://example.com"));
        Assert.False(ok.SizeIsWarning);
        Assert.Contains("mm", ok.SizeText);
        Assert.Contains("in)", ok.SizeText);
    }

    [Fact]
    public void ThePreviewImage_IsDrawnFromTheMatrix()
    {
        var vm = new QrContentBuilderViewModel(null, null, Settings);
        Fill(vm, ("url", "https://example.com"));
        var image = Rotula.App.Views.MatrixImageConverter.Render(vm.PreviewMatrix!);
        Assert.True(image.PixelWidth >= vm.PreviewMatrix!.Width + 8);
        Assert.Equal(image.PixelWidth, image.PixelHeight);
    }

    // ------------------------------------------------------------------------------ option rows

    private List<string> Keys() => _main.Properties.Rows.SkipWhile(r => r.LabelKey != "Prop.Data").Skip(1).Select(r => r.LabelKey).ToList();

    [Fact]
    public void TheQrRows_AreTheCatalogueOptions_WithTheNewControls()
    {
        AddBarcode(BarcodeSymbology.QrCode);
        var info = BarcodeCatalog.Get(BarcodeSymbology.QrCode);
        Assert.Equal(info.Options.Count, Keys().Count);
        foreach (var key in new[] { "Prop.QrModel", "Prop.ModuleSize", "Prop.QrErrorCorrection", "Prop.QrMask", "Prop.QrInputMode", "Prop.DigitalLink", "Prop.QrContentSpec" })
            Assert.Contains(key, Keys());

        var level = _main.Properties.Rows.OfType<ChoiceRow>().Single(r => r.LabelKey == "Prop.QrErrorCorrection");
        Assert.Equal(new[] { "Low (7%)", "Medium (15%)", "Quartile (25%)", "High (30%)" }, level.Items.Select(i => i.Display));
        Assert.Equal(QrErrorCorrection.Quartile, level.Selected!.Value);
        level.Selected = level.Items.First(i => Equals(i.Value, QrErrorCorrection.High));
        Assert.Equal(QrErrorCorrection.High, ((BarcodeElement)_main.Session.Document.Elements[0]).QrErrorCorrection);

        var model = _main.Properties.Rows.OfType<ChoiceRow>().Single(r => r.LabelKey == "Prop.QrModel");
        Assert.Equal(2, model.Selected!.Value);
        model.Selected = model.Items.First(i => Equals(i.Value, 1));
        Assert.Equal(1, ((BarcodeElement)_main.Session.Document.Elements[0]).QrModel);

        var mask = _main.Properties.Rows.OfType<ChoiceRow>().Single(r => r.LabelKey == "Prop.QrMask");
        Assert.Equal(9, mask.Items.Count);
        Assert.Equal(-1, mask.Selected!.Value);
        mask.Selected = mask.Items.First(i => Equals(i.Value, 5));
        Assert.Equal(5, ((BarcodeElement)_main.Session.Document.Elements[0]).QrMask);

        var input = _main.Properties.Rows.OfType<ChoiceRow>().Single(r => r.LabelKey == "Prop.QrInputMode");
        Assert.Equal(5, input.Items.Count);
        Assert.Equal(QrInputMode.Automatic, input.Selected!.Value);
        input.Selected = input.Items.First(i => Equals(i.Value, QrInputMode.ManualByte));
        Assert.Equal(QrInputMode.ManualByte, ((BarcodeElement)_main.Session.Document.Elements[0]).QrInputMode);

        var link = _main.Properties.Rows.OfType<BoolRow>().Single(r => r.LabelKey == "Prop.DigitalLink");
        link.Value = true;
        Assert.True(((BarcodeElement)_main.Session.Document.Elements[0]).DigitalLink);
    }

    [Fact]
    public void TheDataMatrixRows_HaveTheGs1AndDigitalLinkToggles()
    {
        AddBarcode(BarcodeSymbology.DataMatrix, "123");
        Assert.Equal(BarcodeCatalog.Get(BarcodeSymbology.DataMatrix).Options.Count, Keys().Count);
        var gs1 = _main.Properties.Rows.OfType<BoolRow>().Single(r => r.LabelKey == "Prop.DataMatrixGs1");
        gs1.Value = true;
        Assert.True(((BarcodeElement)_main.Session.Document.Elements[0]).DataMatrixGs1);
        Assert.Contains("Prop.DigitalLink", Keys());
    }

    [Fact]
    public void TheModuleSizeRow_ShowsThePrintedSize_AndWarnsWhenTooSmall()
    {
        var qr = AddBarcode(BarcodeSymbology.QrCode);
        var size = _main.Properties.Rows.OfType<IntRow>().Single(r => r.LabelKey == "Prop.ModuleSize");
        Assert.True(size.HasDetail);
        Assert.Contains("Printed size", size.Detail);
        Assert.Contains("203 dpi", size.Detail);
        Assert.False(size.DetailIsWarning);
        size.Text = "1";
        Assert.Equal(1, Live(qr.Id).ModuleSize);
        Assert.True(size.DetailIsWarning);
        Assert.Contains("Too small to scan", size.Detail);
        Assert.Contains("at least 2 dots", size.Detail);
        size.Text = "6";
        Assert.False(size.DetailIsWarning);
    }

    [Fact]
    public void TheErrorCorrectionRow_CarriesTheCapacityMeter_AndFollowsTheData()
    {
        var qr = AddBarcode(BarcodeSymbology.QrCode, "https://example.com");
        var level = _main.Properties.Rows.OfType<ChoiceRow>().Single(r => r.LabelKey == "Prop.QrErrorCorrection");
        Assert.StartsWith("Fits: ", level.Detail);
        Assert.Contains("of capacity at level Q, version", level.Detail);
        Assert.False(level.DetailIsWarning);

        var data = _main.Properties.Rows.OfType<TextRow>().Single(r => r.LabelKey == "Prop.Data");
        data.Text = new string('x', 1800);
        Assert.True(level.DetailIsWarning);
        Assert.Contains("Does not fit at level Q", level.Detail);
        Assert.Contains("Lower the level or shorten the text", level.Detail);
        data.Text = "https://example.com";
        Assert.StartsWith("Fits: ", level.Detail);
        Assert.Equal(qr.Id, Live(qr.Id).Id);
    }

    [Fact]
    public async Task TheCapacityMeter_MeasuresTheLongestLoadedRecord()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string csv = Path.Combine(dir, "urls.csv");
            File.WriteAllText(csv, "Link\nhttps://a.io/1\nhttps://example.com/" + new string('p', 300) + "\nhttps://b.io/2\n");
            Assert.True(await _main.Data.LoadAsync(csv));
            var qr = AddBarcode(BarcodeSymbology.QrCode, "{{Link}}");
            var level = _main.Properties.Rows.OfType<ChoiceRow>().Single(r => r.LabelKey == "Prop.QrErrorCorrection");
            Assert.Contains("longest of 3 loaded records", level.Detail);
            var payload = QrSizing.Analyze("https://example.com/" + new string('p', 300), QrErrorCorrection.Quartile);
            Assert.Contains(((int)Math.Round(payload.UsedPercent)).ToString() + "%", level.Detail);
            Assert.Equal("{{Link}}", Live(qr.Id).Data);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { /* best effort */ } }
    }

    [Fact]
    public void TheQrRows_AreTranslated()
    {
        Loc.Instance.SetLanguage("es");
        AddBarcode(BarcodeSymbology.QrCode);
        var level = _main.Properties.Rows.OfType<ChoiceRow>().Single(r => r.LabelKey == "Prop.QrErrorCorrection");
        Assert.Contains("Cabe", level.Detail);
        Assert.Equal(new[] { Loc.Get("Qr.Level.L"), Loc.Get("Qr.Level.M"), Loc.Get("Qr.Level.Q"), Loc.Get("Qr.Level.H") }, level.Items.Select(i => i.Display));
        Assert.Contains("Baja", level.Items[0].Display);
        var data = _main.Properties.Rows.OfType<TextRow>().Single(r => r.LabelKey == "Prop.Data");
        Assert.Equal("Asistente de contenido...", data.ActionCaption);
    }

    // ------------------------------------------------------------------------------ the GS1 builder

    private static Gs1Segment Seg(Gs1BuilderViewModel vm, string ai) => vm.Segments.First(s => s.Ai == ai);

    [Fact]
    public void TheGs1Presets_AddTheirIdentifiers()
    {
        var vm = new Gs1BuilderViewModel(null, BarcodeSymbology.Code128, null);
        Assert.Equal(new[] { "Shipping (SSCC)", "Product (GTIN, batch, expiry)", "Serialisation (GTIN, serial)" }, vm.Presets.Select(p => p.Name));
        vm.ApplyPresetCommand.Execute(vm.Presets[0]);
        Assert.Equal(new[] { "00" }, vm.Segments.Select(s => s.Ai));
        vm.ApplyPresetCommand.Execute(vm.Presets[1]);
        Assert.Equal(new[] { "01", "10", "17" }, vm.Segments.Select(s => s.Ai));
        vm.ApplyPresetCommand.Execute(vm.Presets[2]);
        Assert.Equal(new[] { "01", "21" }, vm.Segments.Select(s => s.Ai));
    }

    [Fact]
    public void TheIdentifierPicker_SearchesTheDictionary()
    {
        var vm = new Gs1BuilderViewModel(null, BarcodeSymbology.Code128, null);
        Assert.Contains(vm.Results, r => r.Ai == "01");
        vm.Search = "10";
        Assert.Equal("10", vm.Results[0].Ai);
        Assert.False(string.IsNullOrEmpty(vm.Results[0].Format));
        vm.Search = "expiration";
        Assert.Contains(vm.Results, r => r.Ai == "17");
        vm.Search = "zzzzzzzz";
        Assert.Empty(vm.Results);
        vm.Search = "01";
        vm.AddAiCommand.Execute(vm.Results.First(r => r.Ai == "01"));
        Assert.Single(vm.Segments);
    }

    [Fact]
    public void AValidProductMessage_BuildsTheBracketedFormAndShowsTheFnc1Markers()
    {
        var vm = new Gs1BuilderViewModel(null, BarcodeSymbology.Code128, null);
        Assert.False(vm.CanApply);
        vm.ApplyPresetCommand.Execute(vm.Presets[1]);
        Assert.False(vm.CanApply);
        Seg(vm, "01").Value.Row.Text = "09506000134352";
        Seg(vm, "10").Value.Row.Text = "ABC123";
        Seg(vm, "17").Value.Row.Text = "251231";
        Assert.True(vm.CanApply, string.Join("; ", vm.Messages.Select(m => m.Text)));
        Assert.Equal("(01)09506000134352(10)ABC123(17)251231", vm.Template);
        Assert.Equal("(01)09506000134352(10)ABC123(17)251231", vm.HumanReadable);
        // The batch is of variable length, so an FNC1 separates it from the expiry date that follows.
        Assert.Equal("[FNC1]0109506000134352" + "10ABC123[FNC1]17251231", vm.Fnc1Text);
        Assert.Equal("33 of 48 characters", vm.CounterText);
        Assert.NotNull(vm.PreviewMatrix);
    }

    [Fact]
    public void AWrongValue_IsExplainedPerIdentifier_AndBlocksApply()
    {
        var vm = new Gs1BuilderViewModel(null, BarcodeSymbology.Code128, null);
        vm.ApplyPresetCommand.Execute(vm.Presets[1]);
        Seg(vm, "01").Value.Row.Text = "09506000134353"; // wrong check digit
        Seg(vm, "10").Value.Row.Text = "ABC123";
        Seg(vm, "17").Value.Row.Text = "251331"; // month 13
        Assert.False(vm.CanApply);
        Assert.True(Seg(vm, "01").Value.HasError);
        Assert.True(Seg(vm, "17").Value.HasError);
        Assert.Equal(2, vm.Messages.Count(m => m.IsError));
        Assert.Null(vm.PreviewMatrix);
        Seg(vm, "01").Value.Row.Text = "09506000134352";
        Seg(vm, "17").Value.Row.Text = "251231";
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Gs128_IsLimitedTo48Characters_ButDataMatrixIsNot()
    {
        void Fill48(Gs1BuilderViewModel vm)
        {
            vm.ApplyPresetCommand.Execute(vm.Presets[1]);
            Seg(vm, "01").Value.Row.Text = "09506000134352";
            Seg(vm, "10").Value.Row.Text = new string('A', 20);
            Seg(vm, "17").Value.Row.Text = "251231";
            vm.AddAiCore("21")!.Value.Row.Text = new string('B', 20);
        }
        var gs128 = new Gs1BuilderViewModel(null, BarcodeSymbology.Code128, null);
        Fill48(gs128);
        Assert.True(gs128.DataCharacters > Rotula.Core.Gs1.Gs1Validator.MaxGs128DataCharacters);
        Assert.False(gs128.CanApply);
        Assert.True(gs128.CounterIsWarning);
        Assert.Contains(gs128.Messages, m => m.IsError && m.Text.Contains("48"));
        Assert.Contains("of 48", gs128.CounterText);

        gs128.RemoveSegment(Seg(gs128, "21"));
        Assert.True(gs128.CanApply, string.Join("; ", gs128.Messages.Select(m => m.Text)));
        Assert.True(gs128.DataCharacters <= 48);

        var dm = new Gs1BuilderViewModel(null, BarcodeSymbology.DataMatrix, null);
        Fill48(dm);
        Assert.True(dm.DataCharacters > 48);
        Assert.True(dm.CanApply, string.Join("; ", dm.Messages.Select(m => m.Text)));
        Assert.DoesNotContain("of 48", dm.CounterText);
    }

    [Fact]
    public void TheGs1Builder_BindsFields_AndChecksThemWithTheirSample()
    {
        DeclareVariable("Lot", "L-77");
        var vm = new Gs1BuilderViewModel(_main.Fields, BarcodeSymbology.Code128, null, _main.Session.Document.Variables);
        vm.ApplyPresetCommand.Execute(vm.Presets[1]);
        Seg(vm, "01").Value.Row.Text = "09506000134352";
        Seg(vm, "17").Value.Row.Text = "251231";
        var lot = Seg(vm, "10");
        lot.Value.Row.BindTo(_main.Fields.Find("Lot")!);
        Assert.Equal("{{Lot}}", lot.Text);
        Assert.Equal("(01)09506000134352(10){{Lot}}(17)251231", vm.Template);
        Assert.Equal("(01)09506000134352(10)L-77(17)251231", vm.HumanReadable);
        Assert.True(vm.CanApply);

        // A field nobody can give a value to yet is not checked now, and the length is only an estimate.
        var open = new Gs1BuilderViewModel(_main.Fields, BarcodeSymbology.Code128, null, _main.Session.Document.Variables);
        open.ApplyPresetCommand.Execute(open.Presets[2]);
        Seg(open, "01").Value.Row.Text = "09506000134352";
        Seg(open, "21").Value.Row.Text = "{{Unknown}}";
        Assert.True(Seg(open, "21").Value.HasWarning);
        Assert.True(open.CanApply);
        Assert.Contains("About", open.CounterText);
    }

    [Fact]
    public void ApplyingTheGs1Builder_SwitchesTheBarcodeToGs1_InOneUndoStep()
    {
        var code = AddBarcode(BarcodeSymbology.Code128, "123");
        _fake.ToolDialogs.OnGs1Builder = vm =>
        {
            vm.ApplyPresetCommand.Execute(vm.Presets[0]);
            Seg(vm, "00").Value.Row.Text = "340123450000000017";
            vm.ApplyCommand.Execute(null);
        };
        int before = _main.Session.Undo.UndoCount;
        Assert.True(_main.Properties.EditGs1(code.Id));
        var now = Live(code.Id);
        Assert.Equal("(00)340123450000000017", now.Data);
        Assert.Equal(Code128Mode.Ean, now.Code128Mode);
        Assert.Equal(before + 1, _main.Session.Undo.UndoCount);
        _main.Session.Undo.Undo();
        Assert.Equal("123", Live(code.Id).Data);
        Assert.NotEqual(Code128Mode.Ean, Live(code.Id).Code128Mode);

        var dm = AddBarcode(BarcodeSymbology.DataMatrix, "x");
        dm.DigitalLink = true;
        _fake.ToolDialogs.OnGs1Builder = vm =>
        {
            vm.ApplyPresetCommand.Execute(vm.Presets[2]);
            Seg(vm, "01").Value.Row.Text = "09506000134352";
            Seg(vm, "21").Value.Row.Text = "SER001";
            vm.ApplyCommand.Execute(null);
        };
        Assert.True(_main.Properties.EditGs1(dm.Id));
        var liveDm = Live(dm.Id);
        Assert.Equal("(01)09506000134352(21)SER001", liveDm.Data);
        Assert.True(liveDm.DataMatrixGs1);
        Assert.False(liveDm.DigitalLink);
    }

    [Fact]
    public void ReopeningTheGs1Builder_RestoresTheIdentifiersFromTheData()
    {
        var code = AddBarcode(BarcodeSymbology.Code128, "(01)09506000134352(10)ABC123");
        Gs1BuilderViewModel? seen = null;
        _fake.ToolDialogs.OnGs1Builder = vm => { seen = vm; vm.CancelCommand.Execute(null); };
        _main.Properties.EditGs1(code.Id);
        Assert.True(seen!.Restored);
        Assert.Equal(new[] { "01", "10" }, seen.Segments.Select(s => s.Ai));
        Assert.Equal("ABC123", Seg(seen, "10").Value.Row.Text);
        Assert.True(seen.CanApply);
    }

    [Fact]
    public void Gs1Texts_AreTranslated()
    {
        Loc.Instance.SetLanguage("es");
        var vm = new Gs1BuilderViewModel(null, BarcodeSymbology.Code128, null);
        Assert.Contains("Envío", vm.Presets[0].Name);
        vm.ApplyPresetCommand.Execute(vm.Presets[0]);
        Assert.Contains("de 48 caracteres", vm.CounterText);
        Assert.Contains("Rellene", vm.StatusHint);
    }
}
