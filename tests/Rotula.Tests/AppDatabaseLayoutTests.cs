using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Rotula.App.Views;

namespace Rotula.Tests;

/// <summary>
/// Layout regression tests for the database panel. They run real WPF controls on one long-lived STA thread that owns a
/// single <see cref="Application"/> (WPF allows only one per process), so later layout tests can reuse <see cref="WpfTestHost"/>.
/// </summary>
public sealed class AppDatabaseLayoutTests
{
    /// <summary>A table row as the list template reads it (name and kind).</summary>
    private sealed record FakeTable(string QualifiedName, string Kind);

    /// <summary>
    /// The few members of the real view-model the table tab binds to. Two-way bindings need settable properties, otherwise WPF
    /// refuses the binding, so this is a small class rather than an anonymous object.
    /// </summary>
    private sealed class FakeDatabaseContext
    {
        public ObservableCollection<FakeTable> Tables { get; init; } = new();
        public bool ShowNoTables { get; set; }
        public bool HasSelectedTable { get; set; }
        public int ModeIndex { get; set; }
        public object? SelectedTable { get; set; }
        public string TableFilter { get; set; } = "";
        public string QueryText { get; set; } = "";
    }

    /// <summary>
    /// A database with thousands of tables must scroll inside the table list. The panel sits inside the Data window's
    /// ScrollViewer, which measures its content with unlimited height; an unbounded list would grow to show every table at once
    /// (no scrollbar of its own, and very slow), which is the bug reported with a SQL Server that had many tables.
    /// </summary>
    [Fact]
    public void TableList_WithThousandsOfTables_ScrollsInsideItsOwnBoundedArea()
    {
        var result = WpfTestHost.Run(() =>
        {
            var tables = new ObservableCollection<FakeTable>(Enumerable.Range(1, 5000).Select(i => new FakeTable($"dbo.Table_{i:0000}", i % 7 == 0 ? "View" : "Table")));
            var view = new DatabaseSourceView { DataContext = new FakeDatabaseContext { Tables = tables } };

            // Exactly how the Data window hosts it: inside a ScrollViewer, which offers unlimited height to its content.
            var host = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = view };
            var size = new Size(1000, 760);
            host.Measure(size);
            host.Arrange(new Rect(size));
            host.UpdateLayout();

            var list = FindDescendants<ListBox>(view).First(l => l.ItemsSource is not null);
            var listScroll = FindDescendants<ScrollViewer>(list).First();
            return (ListHeight: list.ActualHeight, ListScrollable: listScroll.ScrollableHeight, Realised: CountRealisedItems(list));
        });

        Assert.True(result.ListHeight > 0, "The table list was never laid out.");
        Assert.True(result.ListHeight <= 520, $"The table list grew to {result.ListHeight:0} px instead of staying bounded.");
        Assert.True(result.ListScrollable > 0, "The table list has no scrollable range, so the user cannot reach the other tables.");
        Assert.True(result.Realised < 500, $"{result.Realised} rows were realised: the list is not virtualised.");
    }

    private static int CountRealisedItems(ListBox list) => FindDescendants<ListBoxItem>(list).Count();

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }
}

/// <summary>
/// One STA thread with one WPF <see cref="Application"/> and the app's theme loaded, shared by every layout test. WPF controls must be
/// created and measured on that thread; <see cref="Run{T}"/> hands a function to it and returns the result or rethrows its exception.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> HostDispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Runs <paramref name="work"/> on the shared STA thread and returns its result.</summary>
    public static T Run<T>(Func<T> work) => HostDispatcher.Value.Invoke(work);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // The same dictionaries App.xaml merges, so the view's static resources (cards, buttons, icons) resolve as in the real app.
            foreach (var name in new[] { "Icons.xaml", "Theme.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Rotula.App;component/Resources/{name}", UriKind.Absolute) });
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "WPF test host" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
