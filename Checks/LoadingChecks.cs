using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GreenLuma_Manager;
using GreenLuma_Manager.Controllers;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

internal static class LoadingChecks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);

    internal static async Task Run(MainWindow window, GameListController controller, Action<bool, string> check)
    {
        const uint id = 9999001;
        var parent = new Game { AppId = id.ToString(), Name = "Large fixture", Type = "Game", IconUrl = "fixture" };
        var children = Enumerable.Range(700000, 500).Select(x => new Game
        {
            AppId = x.ToString(), Name = $"Content {x}", Type = x % 2 == 0 ? "DLC" : "Soundtrack",
            ParentAppId = parent.AppId, ParentName = parent.Name, IconUrl = "fixture"
        }).ToList();
        var summary = new GameContentSummary(parent, children.Select(x => x.AppId).ToList(), false, "Fixture");
        var summaries = (ConcurrentDictionary<uint, (DateTime, GameContentSummary)>)typeof(GameDlcService).GetField("Summaries", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var contentCache = (ConcurrentDictionary<uint, (DateTime, GameWithDlc)>)typeof(GameDlcService).GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        summaries[id] = (DateTime.UtcNow.AddMinutes(5), summary);
        contentCache[id] = (DateTime.UtcNow.AddMinutes(5), new(parent, children, false, "Fixture"));
        try
        {
            // Use the production service path with seeded caches; no Steam/game files are touched.
            App.IsPreview = false;
            await (Task)Invoke(window, "OpenDetailsAsync", id, false)!;
            var baseGrid = (DataGrid)window.FindName("DgDetails");
            var contentGrid = (DataGrid)window.FindName("DgAdditional");
            var expander = (Expander)window.FindName("AdditionalContent");
            List<DlcSelection> Rows() => (List<DlcSelection>)typeof(MainWindow).GetField("_detailRows", Private)!.GetValue(window)!;
            check(baseGrid.Items.Count == 1 && contentGrid.Items.Count == 0 && Rows().Count == 1,
                "500 content entries are not resolved or bound before expansion");
            expander.IsExpanded = true;
            check(await (Task<bool>)Invoke(window, "EnsureAdditionalContentAsync")!, "First expansion resolves content");
            window.UpdateLayout(); await Task.Delay(150); window.UpdateLayout();
            var realized = WindowFrameChecks.Descendants<DataGridRow>(contentGrid).Count();
            check(contentGrid.Items.Count == 500 && realized > 0 && realized < 30, "Large lookup virtualizes rows to its viewport");
            CheckWheel(contentGrid, window, check, "Lookup content scrolls inside its dropdown");
            Rows()[1].Selected = true;
            var retained = Rows()[1];
            expander.IsExpanded = false;
            check(contentGrid.Items.Count == 0 && retained.Selected, "Collapsing clears containers without clearing selection");
            expander.IsExpanded = true;
            await (Task<bool>)Invoke(window, "EnsureAdditionalContentAsync")!;
            check(ReferenceEquals(Rows()[1], retained) && retained.Selected, "Reopening reuses resolved selection objects");

            await (Task)Invoke(window, "OpenDetailsAsync", id, false)!;
            Invoke(window, "SelectAllDetails_Click", window, new RoutedEventArgs());
            Invoke(window, "ClearDetails_Click", window, new RoutedEventArgs());
            await (Task<bool>)Invoke(window, "EnsureAdditionalContentAsync")!;
            await Task.Delay(25);
            check(Rows().All(x => !x.Selected), "Clear cancels a pending Select all while content loads");
            Invoke(window, "SelectAllDetails_Click", window, new RoutedEventArgs());
            await Task.Delay(25);
            check(Rows().Count == 501 && Rows().All(x => x.Selected), "Select all awaits and selects all 500 content entries");

            await (Task)Invoke(window, "OpenDetailsAsync", id, false)!;
            expander.IsExpanded = true;
            var stale = (Task<bool>)Invoke(window, "EnsureAdditionalContentAsync")!;
            Invoke(window, "CancelDetails");
            check(!await stale && Rows().Count == 1, "Canceled expansion cannot append stale content");

            // A failed content load is retryable without losing the base row.
            await (Task)Invoke(window, "OpenDetailsAsync", id, false)!;
            var active = (GameContentSummary)typeof(MainWindow).GetField("_detailSummary", Private)!.GetValue(window)!;
            active.ContentIds[0] = "invalid-fixture-id";
            expander.IsExpanded = true;
            check(!await (Task<bool>)Invoke(window, "EnsureAdditionalContentAsync")!, "Content failure remains contained in dropdown");
            check(baseGrid.Items.Count == 1 && ((Button)window.FindName("BtnRetryContent")).Visibility == Visibility.Visible, "Content retry keeps the base game available");
            active.ContentIds[0] = children[0].AppId;
            check(await (Task<bool>)Invoke(window, "EnsureAdditionalContentAsync")!, "Content retry succeeds without duplicating rows");
            check(Rows().Count == 501, "Retry appends content once");

            const uint packageId = id + 1;
            var package = new Game { AppId = packageId.ToString(), Name = "Fixture package", Type = "Package" };
            summaries[packageId] = (DateTime.UtcNow.AddMinutes(5), new(package, [parent.AppId, children[0].AppId], false, "Fixture", true));
            contentCache[packageId] = (DateTime.UtcNow.AddMinutes(5), new(package, [parent, children[0]], false, "Fixture", true));
            try
            {
                await (Task)Invoke(window, "OpenDetailsAsync", packageId, false)!;
                check(baseGrid.Items.Count == 0 && contentGrid.Items.Count == 0 && expander.Header.ToString()!.StartsWith("Package contents"), "Package apps remain deferred and its heading is not selectable");
                Invoke(window, "SelectAllDetails_Click", window, new RoutedEventArgs());
                await (Task<bool>)Invoke(window, "EnsureAdditionalContentAsync")!;
                await Task.Delay(25);
                check(Rows().Count == 2 && Rows().All(x => x.Selected) && Rows().All(x => x.Game.AppId != package.AppId), "Package Select all selects its apps without adding the package ID");
            }
            finally { summaries.TryRemove(packageId, out _); contentCache.TryRemove(packageId, out _); }
        }
        finally { App.IsPreview = true; summaries.TryRemove(id, out _); contentCache.TryRemove(id, out _); }

        int resets = 0;
        NotifyCollectionChangedEventHandler changed = (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
        controller.Groups.CollectionChanged += changed;
        try
        {
            controller.LoadGames([parent, .. children]);
            resets = 0;
            var timer = Stopwatch.StartNew();
            controller.BatchUpdate(() =>
            {
                foreach (var child in children) { child.Type = "Software"; controller.ApplyFilters(); }
            });
            timer.Stop();
            var metadataResets = resets;
            check(resets == 1 && controller.Groups.Single().Children.Count == 500, "500 metadata changes cause one complete grouping refresh");
            resets = 0;
            try { controller.BatchUpdate(() => { children[0].Type = "DLC"; throw new InvalidOperationException("Fixture"); }); }
            catch (InvalidOperationException) { }
            children[0].Type = "Software";
            check(resets == 2, "An interrupted batch restores refreshes for later changes");
            window.UpdateLayout(); await Task.Delay(150); window.UpdateLayout();
            var list = (ListBox)window.FindName("LstGames");
            var dropdown = WindowFrameChecks.Descendants<Expander>(list).Single();
            var childList = (ListBox)dropdown.Content;
            check(childList.Items.Count == 0, "Collapsed saved content has no bound child controls");
            dropdown.IsExpanded = true;
            window.UpdateLayout(); await Task.Delay(150); window.UpdateLayout();
            var childContainers = WindowFrameChecks.Descendants<ListBoxItem>(childList).Count();
            check(childList.Items.Count == 500 && childContainers > 0 && childContainers < 30, "Expanded saved content uses a virtualized viewport");
            CheckWheel(childList, window, check, "Saved content scrolls inside its dropdown");
            check(controller.GetSelectedAppIds().Count == 501, "Virtualization does not remove saved AppList entries");
            controller.LoadGames(Enumerable.Range(800000, 1000).Select(x => new Game { AppId = x.ToString(), Name = $"Game {x}", Type = "Game" }));
            window.UpdateLayout(); await Task.Delay(150); window.UpdateLayout();
            check(WindowFrameChecks.Descendants<ListBoxItem>(list).Count() < 40, "Large profile virtualizes its parent groups");
            var selector = (ComboBox)window.FindName("CmbUnlockMethod");
            selector.SelectedIndex = 1;
            check(ConfigService.Load().UnlockMethod == UnlockMethod.CreamInstaller &&
                (string)((Button)window.FindName("BtnGenerateApplist")).Content == "Install / update DLC unlocker",
                "Quick method selector saves CreamInstaller and updates actions immediately");
            selector.SelectedIndex = 0;
            check(ConfigService.Load().UnlockMethod == UnlockMethod.GreenLuma &&
                (string)((Button)window.FindName("BtnLaunchGreenluma")).Content == "Launch GreenLuma",
                "Quick method selector restores GreenLuma actions");
            Console.WriteLine($"Loading checks: 500 metadata updates -> {metadataResets} refreshes; batch work {timer.ElapsedMilliseconds}ms.");
        }
        finally { controller.Groups.CollectionChanged -= changed; }
    }

    private static void CheckWheel(Control control, MainWindow window, Action<bool, string> check, string message)
    {
        var scroll = WindowFrameChecks.Descendants<ScrollViewer>(control).First();
        var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
        { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        control.RaiseEvent(wheel);
        window.UpdateLayout();
        check(wheel.Handled && scroll.VerticalOffset > 0, message);
        scroll.ScrollToTop(); window.UpdateLayout();
    }
}
