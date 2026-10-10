using Rotula.App.Editing;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests;

[Collection("App")]
public class AppUndoTests
{
    private static (EditorSession Session, EditorCommands Cmd, Func<DateTime> Clock, Action<TimeSpan> Advance) Create(params LabelElement[] elements)
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var doc = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(1200) };
        doc.Elements.AddRange(elements);
        var session = new EditorSession(doc, () => now);
        return (session, new EditorCommands(session), () => now, t => now += t);
    }

    [Fact]
    public void Add_ThenUndo_RemovesElement_RedoRestoresIt()
    {
        var (s, c, _, _) = Create();
        c.Add(AppTestSupport.Box(5, 5));
        Assert.Single(s.Document.Elements);
        s.PerformUndo();
        Assert.Empty(s.Document.Elements);
        s.PerformRedo();
        Assert.Single(s.Document.Elements);
        Assert.Equal(5, s.Document.Elements[0].X);
    }

    [Fact]
    public void UndoIsUnlimited_ThousandStepsBackAndForth()
    {
        var (s, c, _, _) = Create();
        for (int i = 0; i < 1000; i++) c.Add(AppTestSupport.Box(i, i));
        Assert.Equal(1000, s.Undo.UndoCount);
        for (int i = 0; i < 1000; i++) s.PerformUndo();
        Assert.Empty(s.Document.Elements);
        Assert.False(s.Undo.CanUndo);
        for (int i = 0; i < 1000; i++) s.PerformRedo();
        Assert.Equal(1000, s.Document.Elements.Count);
        Assert.Equal(999, s.Document.Elements[^1].X);
    }

    [Fact]
    public void NewEdit_AfterUndo_ClearsRedo()
    {
        var (s, c, _, _) = Create();
        c.Add(AppTestSupport.Box(1, 1));
        s.PerformUndo();
        Assert.True(s.Undo.CanRedo);
        c.Add(AppTestSupport.Box(2, 2));
        Assert.False(s.Undo.CanRedo);
    }

    [Fact]
    public void Undo_RestoresPropertyValues_AndSelection()
    {
        var box = AppTestSupport.Box(10, 10);
        var (s, c, _, _) = Create(box);
        s.Select(box);
        s.Edit("Undo.Property", d => ((BoxElement)d.Elements[0]).WidthDots = 777);
        c.DeleteSelected();
        Assert.Empty(s.SelectedIds);
        s.PerformUndo();
        Assert.Equal(777, ((BoxElement)s.Document.Elements[0]).WidthDots);
        Assert.Equal(new[] { box.Id }, s.SelectedIds);
        s.PerformUndo();
        Assert.Equal(100, ((BoxElement)s.Document.Elements[0]).WidthDots);
    }

    [Fact]
    public void UndoRedoUndo_KeepsSnapshotsPristine()
    {
        var (s, c, _, _) = Create(AppTestSupport.Box(0, 0));
        s.SetSelection(new[] { s.Document.Elements[0].Id });
        c.Nudge(10, 0);
        for (int i = 0; i < 3; i++)
        {
            s.PerformUndo();
            Assert.Equal(0, s.Document.Elements[0].X);
            s.PerformRedo();
            Assert.Equal(10, s.Document.Elements[0].X);
        }
    }

    [Fact]
    public void Undo_RestoresLabelSettings()
    {
        var (s, _, _, _) = Create();
        s.Edit("Undo.LabelSettings", d => { d.Width = Measure.Millimeters(50); d.Dpi = PrinterDpi.Dpi600; d.Variables.Add(new LabelVariable("a", "b")); });
        s.PerformUndo();
        Assert.Equal(Measure.Dots(800), s.Document.Width);
        Assert.Equal(PrinterDpi.Dpi203, s.Document.Dpi);
        Assert.Empty(s.Document.Variables);
    }

    [Fact]
    public void RapidNudges_MergeIntoOneStep_ButSlowOnesDoNot()
    {
        var box = AppTestSupport.Box(0, 0);
        var (s, c, _, advance) = Create(box);
        s.Select(box);
        for (int i = 0; i < 5; i++)
        {
            c.Nudge(1, 0);
            advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.Equal(1, s.Undo.UndoCount);
        Assert.Equal(5, s.Document.Elements[0].X);

        advance(TimeSpan.FromSeconds(5));
        c.Nudge(1, 0);
        Assert.Equal(2, s.Undo.UndoCount);

        s.PerformUndo();
        Assert.Equal(5, s.Document.Elements[0].X);
        s.PerformUndo();
        Assert.Equal(0, s.Document.Elements[0].X);
    }

    [Fact]
    public void Undo_NeverMergesIntoTheStepJustUndone()
    {
        var box = AppTestSupport.Box(0, 0);
        var (s, c, _, _) = Create(box);
        s.Select(box);
        c.Nudge(1, 0);
        s.PerformUndo();
        s.PerformRedo();
        c.Nudge(1, 0);
        Assert.Equal(2, s.Undo.UndoCount);
    }

    [Fact]
    public void DirtyFlag_FollowsHistory_AndSaveMarker()
    {
        var (s, c, _, _) = Create();
        Assert.False(s.IsDirty);
        c.Add(AppTestSupport.Box(0, 0));
        Assert.True(s.IsDirty);
        s.MarkSaved();
        Assert.False(s.IsDirty);
        c.Add(AppTestSupport.Box(1, 1));
        Assert.True(s.IsDirty);
        s.PerformUndo();
        Assert.False(s.IsDirty); // back at the saved state
        s.PerformUndo();
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void Load_ClearsHistoryAndSelection_AndCanStartDirty()
    {
        var (s, c, _, _) = Create();
        c.Add(AppTestSupport.Box(0, 0));
        s.Load(new LabelDocument());
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.SelectedIds);
        Assert.False(s.IsDirty);
        s.Load(new LabelDocument(), markDirty: true);
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void Gesture_EndsAsOneUndoStep_AndCancelRestores()
    {
        var box = AppTestSupport.Box(0, 0);
        var (s, _, _, _) = Create(box);
        s.Select(box);

        var token = s.BeginGesture();
        for (int i = 1; i <= 20; i++) { s.Document.Elements[0].X = i; s.Touch(); }
        s.EndGesture(token, "Undo.Move");
        Assert.Equal(1, s.Undo.UndoCount);
        Assert.Equal(20, s.Document.Elements[0].X);
        s.PerformUndo();
        Assert.Equal(0, s.Document.Elements[0].X);

        var token2 = s.BeginGesture();
        s.Document.Elements[0].X = 99; s.Touch();
        s.CancelGesture(token2);
        Assert.Equal(0, s.Document.Elements[0].X);
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void Revision_ChangesOnEveryChange_IncludingUndo()
    {
        var (s, c, _, _) = Create();
        long r0 = s.Revision;
        c.Add(AppTestSupport.Box(0, 0));
        long r1 = s.Revision;
        s.PerformUndo();
        long r2 = s.Revision;
        Assert.True(r1 > r0 && r2 > r1);
    }

    [Fact]
    public void DescriptionKeys_AreExposedForTheUndoMenu()
    {
        var (s, c, _, _) = Create();
        Assert.Null(s.Undo.NextUndoKey);
        c.Add(AppTestSupport.Box(0, 0));
        Assert.Equal("Undo.Add", s.Undo.NextUndoKey);
        s.PerformUndo();
        Assert.Equal("Undo.Add", s.Undo.NextRedoKey);
    }
}
