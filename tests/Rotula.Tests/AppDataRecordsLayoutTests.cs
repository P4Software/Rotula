using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.App.Views;
using Rotula.Core.Elements;

namespace Rotula.Tests;

/// <summary>
/// The records list of the Data window with thousands of rows, hosted in real WPF controls on the shared STA thread
/// (<see cref="WpfTestHost"/>). It guards against the "170,002 px" lesson: a list measured with unlimited height builds every row and never
/// scrolls. The records grid must stay inside a bounded height, build only the rows on screen, and scroll.
/// </summary>
[Collection("App")]
public class AppDataRecordsLayoutTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();

    public AppDataRecordsLayoutTests() => _fake.Settings.Current.Language = "en";

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    private async Task<DataMergeViewModel> LoadedAsync(int rows)
    {
        var main = new MainViewModel(_fake.Build());
        main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "{{n}}" }));
        var sb = new StringBuilder("n,name,city,note\n");
        for (int i = 1; i <= rows; i++) sb.Append(i).Append(",Name").Append(i).Append(",City").Append(i % 9).Append(",some longer note text ").Append(i).Append('\n');
        string path = Path.Combine(_dir, "rows.csv");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Assert.True(await main.Data.LoadAsync(path));
        return main.Data;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static bool IsInside<T>(DependencyObject o) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(o); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is T) return true;
        return false;
    }

    /// <summary>Hosts the view the way the Data window does: in a tab page, under the options row, inside a window-sized frame.</summary>
    private static (Grid Frame, DataRecordsView View) HostInTab(DataMergeViewModel vm, Size size)
    {
        var view = new DataRecordsView { DataContext = vm };
        var page = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        options.Children.Add(new TextBlock { Text = "options row" });
        DockPanel.SetDock(options, Dock.Top);
        page.Children.Add(options);
        page.Children.Add(view);
        var tabs = new TabControl { Margin = new Thickness(24, 8, 24, 12) };
        tabs.Items.Add(new TabItem { Header = "Data", Content = page });
        var frame = new Grid { Width = size.Width, Height = size.Height };
        frame.Children.Add(tabs);
        frame.Measure(size);
        frame.Arrange(new Rect(size));
        frame.UpdateLayout();
        return (frame, view);
    }

    [Fact]
    public async Task TheRecordsGrid_WithManyRows_IsVirtualised_Bounded_AndScrolls()
    {
        var vm = await LoadedAsync(6000);
        var result = WpfTestHost.Run(() =>
        {
            var (frame, view) = HostInTab(vm, new Size(1000, 640));
            var grid = view.Grid;
            var scroll = Descendants<ScrollViewer>(grid).First();
            int realisedBefore = Descendants<DataGridRow>(grid).Count();
            int firstBefore = Descendants<DataGridRow>(grid).Select(r => ((RecordRow)r.Item).Number).Min();

            scroll.ScrollToVerticalOffset(3000);
            frame.UpdateLayout();
            int firstAfter = Descendants<DataGridRow>(grid).Select(r => ((RecordRow)r.Item).Number).Min();
            var measured = (ViewHeight: view.ActualHeight, GridHeight: grid.ActualHeight, Scrollable: scroll.ScrollableHeight, Realised: realisedBefore,
                Rows: grid.Items.Count, firstBefore, firstAfter, Columns: grid.Columns.Count);
            view.DataContext = null;
            return measured;
        });

        Assert.Equal(6000, result.Rows);
        Assert.True(result.GridHeight > 100, "The grid was never laid out.");
        Assert.True(result.ViewHeight <= 640, $"The records view grew to {result.ViewHeight:0} px instead of staying inside its tab.");
        Assert.True(result.Scrollable > 0, "The grid has no scrollable range, so the user cannot reach the other records.");
        Assert.InRange(result.Realised, 1, 120);
        Assert.Equal(1, result.firstBefore);
        Assert.True(result.firstAfter > 50, $"Scrolling did not move the rows (first row is still {result.firstAfter}).");
        Assert.Equal(2 + 4, result.Columns);   // tick box, row number, and the four data columns
    }

    [Fact]
    public async Task TheRecordsGrid_AfterFilteringTo40Rows_ShowsJustThoseRows()
    {
        var vm = await LoadedAsync(5000);
        vm.FilterText = "City3";
        await vm.RefreshViewAsync();
        int expected = vm.VisibleRows.Count;
        Assert.InRange(expected, 100, 5000);
        vm.FilterText = "Name4999";
        await vm.RefreshViewAsync();
        var rows = WpfTestHost.Run(() =>
        {
            var (_, view) = HostInTab(vm, new Size(1000, 640));
            var numbers = Descendants<DataGridRow>(view.Grid).Select(r => ((RecordRow)r.Item).Number).ToArray();
            view.DataContext = null;
            return numbers;
        });
        Assert.Equal(new[] { 4999 }, rows);
    }

    /// <summary>
    /// In the Data window the records list must sit in a tab page and never inside a ScrollViewer: a ScrollViewer measures its content with unlimited
    /// height, which builds every row (the 170,002 px bug). The window itself cannot be laid out in a test (an unshown window has no template, and
    /// hosting it would bind a long-lived printer list to the WPF thread), so the XAML is checked and the tab-page layout is tested above.
    /// </summary>
    [Fact]
    public void TheDataWindowXaml_HostsTheRecordsListInATabPage_NotInAScrollViewer()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Rotula.sln"))) dir = Path.GetDirectoryName(dir)!;
        Assert.NotNull(dir);
        var doc = System.Xml.Linq.XDocument.Load(Path.Combine(dir!, "src", "Rotula.App", "Views", "DataWindow.xaml"));
        var view = doc.Descendants().Single(e => e.Name.LocalName == "DataRecordsView");
        var ancestors = view.Ancestors().Select(a => a.Name.LocalName).ToList();
        Assert.Contains("TabItem", ancestors);
        Assert.DoesNotContain("ScrollViewer", ancestors);
    }

    [Fact]
    public async Task EveryControlOfTheRecordsList_HasAnAccessibleName()
    {
        var vm = await LoadedAsync(50);
        var findings = WpfTestHost.Run(() =>
        {
            var (_, view) = HostInTab(vm, new Size(1000, 640));
            var unnamed = new List<string>();
            static bool Named(DependencyObject o, string? content) =>
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(o)) || !string.IsNullOrWhiteSpace(content);
            // Parts inside the grid and the drop-down belong to WPF's own templates (the grid has no corner button, the combo's inner parts are
            // named by the combo); the controls this view declares are checked.
            bool Own(DependencyObject o) => !IsInside<DataGrid>(o) && !IsInside<ComboBox>(o);
            foreach (var b in Descendants<Button>(view).Where(b => b.GetType() == typeof(Button) && Own(b)))
                if (!Named(b, b.Content as string)) unnamed.Add("Button:" + b.Content);
            foreach (var t in Descendants<TextBox>(view).Where(Own)) if (!Named(t, null)) unnamed.Add("TextBox:" + t.Text);
            foreach (var c in Descendants<ComboBox>(view)) if (!Named(c, null)) unnamed.Add("ComboBox");
            foreach (var c in Descendants<CheckBox>(view)) if (!Named(c, c.Content as string)) unnamed.Add("CheckBox");
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(view.Grid))) unnamed.Add("DataGrid");
            var rowBoxes = Descendants<CheckBox>(view.Grid).Where(c => c.DataContext is RecordRow).Select(c => AutomationProperties.GetName(c)).ToList();
            var headerBox = Descendants<CheckBox>(view.Grid).FirstOrDefault(c => c.DataContext is not RecordRow);
            var found = (Unnamed: unnamed, RowBoxes: rowBoxes, HeaderName: headerBox is null ? null : AutomationProperties.GetName(headerBox));
            view.DataContext = null;
            return found;
        });

        Assert.Empty(findings.Unnamed);
        Assert.NotEmpty(findings.RowBoxes);
        Assert.All(findings.RowBoxes, n => Assert.Matches(@"^Select record \d+$", n));
        Assert.Equal("Select or unselect all shown records", findings.HeaderName);
    }

    [Fact]
    public async Task TheRecordsList_FollowsTheLanguage_ForNamesAndHeaders()
    {
        var vm = await LoadedAsync(20);
        Loc.Instance.SetLanguage("es");
        var names = WpfTestHost.Run(() =>
        {
            var (_, view) = HostInTab(vm, new Size(1000, 640));
            var list = Descendants<CheckBox>(view.Grid).Where(c => c.DataContext is RecordRow).Select(c => AutomationProperties.GetName(c)).ToList();
            view.DataContext = null;
            return list;
        });
        Assert.All(names, n => Assert.StartsWith("Seleccionar el registro", n));
    }
}
