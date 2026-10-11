using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Rotula.App.ViewModels;
using Rotula.App.Views;
using Rotula.Core.Model;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>
/// Regression for the stack overflow seen when Data merge or the Print dialog was opened more than once: bound radio buttons of
/// separate window instances shared one group (same GroupName) and uncheck each other, and the mirrored "false" they write back
/// flipped the other bound property, endlessly. A depth watchdog turns the overflow into a test failure instead of a dead test host.
/// </summary>
[Collection("App")]
public class RadioGroupRecursionTests
{
    private const int MaxEvents = 150;

    /// <summary>
    /// Breaks a runaway radio loop before the stack overflows. The loop lives inside WPF (a radio's group unchecks its sibling, the
    /// sibling's binding writes back and refreshes, and so on), so it shows up as a flood of Checked / Unchecked events on radio
    /// buttons. A class-wide handler counts them for every radio in the process and, once the budget is spent, removes the bindings of
    /// the radio being toggled so the chain ends; the test then fails on <see cref="Tripped"/> instead of the host dying.
    /// </summary>
    private sealed class LoopGuard : IDisposable
    {
        private static readonly object Gate = new();
        private static LoopGuard? _active;
        private int _events;

        public LoopGuard() { lock (Gate) _active = this; }

        static LoopGuard()
        {
            // Class handlers cannot be removed, so register once and route to whichever guard is active.
            RoutedEventHandler h = (s, _) => _active?.Count((RadioButton)s);
            EventManager.RegisterClassHandler(typeof(RadioButton), ToggleButton.CheckedEvent, h, true);
            EventManager.RegisterClassHandler(typeof(RadioButton), ToggleButton.UncheckedEvent, h, true);
        }

        /// <summary>True once the budget was exceeded.</summary>
        public bool Tripped { get; private set; }

        /// <summary>Toggles seen since the last <see cref="Reset"/>.</summary>
        public int Events => _events;

        /// <summary>Starts a new budget (one user action).</summary>
        public void Reset() => _events = 0;

        private void Count(RadioButton radio)
        {
            if (++_events <= MaxEvents) return;
            Tripped = true;
            BindingOperations.ClearBinding(radio, ToggleButton.IsCheckedProperty);
        }

        public void Dispose() { lock (Gate) if (ReferenceEquals(_active, this)) _active = null; }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the WPF host thread. The view-models subscribe to the process-wide language events and never
    /// unsubscribe, so the ones built here would keep reacting (from another thread) when later tests switch the language and touch
    /// collections owned by the host thread; the subscriber lists are put back afterwards.
    /// </summary>
    private static T RunIsolated<T>(Func<T> work)
    {
        var snapshot = LanguageEventSnapshot.Take();
        try { return WpfTestHost.Run(work); }
        finally { snapshot.Restore(); }
    }

    private static PrinterSettingsViewModel NewPrinterVm(FakeServices fake)
    {
        var doc = new LabelDocument();
        return new PrinterSettingsViewModel(fake.Printer, new Rotula.App.Services.PrintSettings(), () => { }, () => doc,
            (_, _) => Task.FromResult(new Rotula.App.Services.PrinterDetection(null, null, null)), _ => { });
    }

    private static Window HostWindow(UIElement content) => new() { Content = content, Width = 400, Height = 300, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None };

    private static IEnumerable<RadioButton> Radios(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton r) yield return r;
            foreach (var nested in Radios(child)) yield return nested;
        }
    }

    /// <summary>Radios in the logical tree, available straight after construction (before any layout pass).</summary>
    private static IEnumerable<RadioButton> LogicalRadios(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is RadioButton r) yield return r;
            foreach (var nested in LogicalRadios(child)) yield return nested;
        }
    }

    /// <summary>
    /// Drops every binding in a window. A closed window stays reachable through the view-model events it subscribed to and through
    /// the static language source, so left alone it would keep reacting to later tests that switch the language from another thread.
    /// </summary>
    private static void Detach(Window window)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(window);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            BindingOperations.ClearAllBindings(node);
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) pending.Push(VisualTreeHelper.GetChild(node, i));
            foreach (var logical in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) pending.Push(logical);
        }
        window.DataContext = null;
    }

    private static void Select(RadioButton radio)
        => ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();

    [Fact]
    public void ReopeningDataMerge_And_Print_WindowsOnTheSamePrinterSettings_DoesNotRunAway()
    {
        var fake = new FakeServices();
        // MainViewModel applies the saved language to the process-wide Loc; keep English like the other App tests expect.
        fake.Settings.Current.Language = "en";
        var (tripped, events, network) = RunIsolated(() =>
        {
            var main = new MainViewModel(fake.Build());
            using var guard = new LoopGuard();
            var opened = new List<Window>();
            try
            {
                // The real app: one printer view-model for the toolbar, the Print dialog and the Data merge window, and windows that are
                // closed but kept alive by their view-model subscriptions until the garbage collector runs.
                for (int round = 0; round < 3; round++)
                {
                    var data = new DataWindow(main.Data) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
                    opened.Add(data);
                    data.Show(); data.UpdateLayout();
                    foreach (var radio in Radios(data).Where(r => r.Content is string).Reverse().Take(2)) Select(radio);
                    data.Close();
                    guard.Reset();
                    main.Printer.UseNetwork = !main.Printer.UseNetwork;
                }
                return (guard.Tripped, guard.Events, main.Printer.UseNetwork);
            }
            finally { foreach (var w in opened) { Detach(w); if (w.IsLoaded) w.Close(); } }
        });
        Assert.False(tripped, "Radio buttons toggled each other endlessly");
        Assert.InRange(events, 0, 150);
        _ = network;
    }

    [Fact]
    public void ReopenedViews_DoNotShareARadioGroup()
    {
        var (count, shared) = RunIsolated(() =>
        {
            var vm = NewPrinterVm(new FakeServices());
            var wins = new[] { new PrinterSettingsView { DataContext = vm }, new PrinterSettingsView { DataContext = vm } }.Select(HostWindow).ToList();
            try
            {
                foreach (var w in wins) { w.Show(); w.UpdateLayout(); }
                var all = wins.SelectMany(w => Radios(w)).ToList();
                // Radios that carry a group name are one group per root only while the roots differ; across views it is fragile, so none may be named.
                return (all.Count, all.Count(r => !string.IsNullOrEmpty(r.GroupName)));
            }
            finally { foreach (var w in wins) { Detach(w); w.Close(); } }
        });
        Assert.Equal(4, count);
        Assert.Equal(0, shared);
    }

    [Fact]
    public void CounterRadios_OfTwoCounters_AreIndependent()
    {
        var result = RunIsolated(() =>
        {
            var one = new CounterEditor(new CounterSettings { Name = "A" }, () => { });
            var two = new CounterEditor(new CounterSettings { Name = "B" }, () => { });
            // The two-way helper must ignore the unchecked radio's "false" write-back.
            one.IsNumeric = false;
            bool afterFalse = one.IsAlphanumeric;
            one.IsAlphanumeric = false; // back to numeric
            one.WrapOnOverflow = false;
            bool afterWrapFalse = one.StopOnOverflow;
            return (afterFalse, afterWrapFalse, two.IsAlphanumeric);
        });
        // Writing "false" to the helper of the checked option must not switch to the other option.
        Assert.False(result.afterFalse);
        Assert.False(result.afterWrapFalse);
        Assert.False(result.Item3);
    }

    /// <summary>Remembers the subscribers of <see cref="Rotula.App.Localization.Loc"/>'s events so a test can undo what it added.</summary>
    private sealed class LanguageEventSnapshot
    {
        private static readonly System.Reflection.FieldInfo[] Fields = new[] { "LanguageChanged", "PropertyChanged" }
            .Select(n => typeof(Rotula.App.Localization.Loc).GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!)
            .ToArray();

        private readonly Delegate?[] _saved = Fields.Select(f => (Delegate?)f.GetValue(Rotula.App.Localization.Loc.Instance)).ToArray();

        public static LanguageEventSnapshot Take() => new();

        public void Restore()
        {
            for (int i = 0; i < Fields.Length; i++) Fields[i].SetValue(Rotula.App.Localization.Loc.Instance, _saved[i]);
        }
    }
}
