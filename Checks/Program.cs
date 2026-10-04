using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GreenLuma_Manager;
using GreenLuma_Manager.Controls;
using GreenLuma_Manager.Dialogs;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;
using GreenLuma_Manager.Controllers;
using GreenLuma_Manager.Plugins;

internal static class Program
{
    private static int _assertions;
    private static string _artifacts = "";
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _assertions++;
    }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { _assertions++; return; }
        throw new Exception(message);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        var output = args.FirstOrDefault(x => x.StartsWith("--output="))?[9..] ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "checks");
        _artifacts = Path.GetFullPath(output);
        Directory.CreateDirectory(_artifacts);
        Environment.SetEnvironmentVariable("LARPLUMA_DATA_DIR", Path.Combine(_artifacts, "data-" + Guid.NewGuid().ToString("N")));
        App.IsPreview = true;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GreenLuma-Manager;component/Themes/Controls.xaml") });
        var bindingErrors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(bindingErrors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var exit = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (args.Contains("--native-window"))
                {
                    await TestNativeWindow();
                    Check(bindingErrors.ToString().Length == 0, "Native window WPF bindings have no errors");
                    File.WriteAllText(Path.Combine(_artifacts, "native-pass.txt"), $"PASS: {_assertions} native window interaction checks.");
                    exit = 0;
                    return;
                }
                TestInputs();
                TestThemes();
                TestImport();
                await TestCancellation();
                await TestAppLists();
                TestPlugins();
                await TestUi();
                PrivateDesktop.Run(Path.Combine(_artifacts, "native"));
                Check(File.Exists(Path.Combine(_artifacts, "native", "native-pass.txt")), "Real window controls passed on a private desktop");
                if (args.Contains("--media")) await TestMedia();
                if (args.Contains("--live")) await TestLive();
                Check(bindingErrors.ToString().Length == 0, "WPF binding errors: " + bindingErrors);
                Console.WriteLine($"PASS: {_assertions} checks; screenshots: {_artifacts}");
                exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(_artifacts, "failure.txt"), ex.ToString()); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return exit;
    }

    private static void TestInputs()
    {
        var inputs = new[] { "3764200", " 3764200 ", "https://store.steampowered.com/app/3764200/Resident_Evil_Requiem/", "https://store.steampowered.com/app/3764200/?l=english#details" };
        foreach (var input in inputs) Check(SteamInput.Parse(input).AppId == 3764200, "App input normalization");
        Check(SteamInput.Parse("Resident Evil").Name == "Resident Evil", "Name input");
        Check(SteamInput.Parse("10").AppId == 10, "Short App IDs");
        foreach (var bad in new[] { "", "0", "-5", "4294967296", "https://evil.example/app/3764200/", "https://store.steampowered.com.evil.example/app/3764200/", "https://store.steampowered.com/sub/3764200/", "https://user@store.steampowered.com/app/10/" }) Reject(() => SteamInput.Parse(bad), "Accepted invalid input: " + bad);
        var dlcs = GameDlcService.MergeIds("10", ["10", "20", "020", "0", "bad"], Enumerable.Range(21, 100).Select(x => x.ToString()));
        Check(dlcs.Count == 101 && dlcs[0] == "20" && !dlcs.Contains("10"), "DLC IDs must be deduplicated without a 20-entry limit");
        var row = new DlcSelection { Game = MainWindow.PreviewGame().BaseGame!, AlreadyAdded = true };
        row.Selected = true;
        Check(!row.Selected && !row.CanSelect, "Already-added rows cannot be selected");
    }

    private static void TestThemes()
    {
        AppearanceService.Apply(new());
        var custom = Appearance.Light(); custom.Name = "My theme"; custom.Scale = 1.25; custom.Radius = 8;
        AppearanceService.Save(custom);
        AppearanceService.Load();
        Check(AppearanceService.Current.Name == "My theme" && AppearanceService.Current.Scale == 1.25, "Appearance persistence");
        Check(AppearanceService.Current.Radius == 0 && ((CornerRadius)Application.Current.Resources["Corner"]) == new CornerRadius(0), "Old radius values cannot round the interface");
        Check(JsonSerializer.Deserialize<Appearance>(File.ReadAllText(Path.Combine(AppPaths.Root, "appearance.json")))!.Radius == 0, "Saved legacy radius is normalized to zero");
        Check(((SolidColorBrush)Application.Current.Resources["DarkBg"]).Color == Colors.White, "Light resources applied");
        var png = Path.Combine(_artifacts, "background.png");
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(Brushes.Gray, null, new Rect(0, 0, 32, 32)); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(png)) encoder.Save(stream);
        custom.BackgroundFile = AppearanceService.CopyBackground(png);
        var archive = Path.Combine(AppPaths.Root, "custom.larptheme"); AppearanceService.Export(custom, archive);
        var imported = AppearanceService.Import(archive);
        Check(imported.Name == "My theme" && File.Exists(imported.BackgroundFile), "Theme import and embedded background");
        Check(imported.Radius == 0, "Exported and imported themes preserve square corners");
        var legacy = Path.Combine(AppPaths.Root, "rounded-legacy.larptheme");
        using (var zip = ZipFile.Open(legacy, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("theme.json").Open());
            writer.Write(JsonSerializer.Serialize(new Appearance { Radius = 16, Name = "Old rounded theme" }));
        }
        Check(AppearanceService.Import(legacy).Radius == 0, "An existing rounded theme archive is accepted with square corners");
        File.WriteAllText(Path.Combine(AppPaths.Root, "appearance.json"), JsonSerializer.Serialize(new Appearance { Radius = 8, Canvas = "#FFFFFF" }));
        AppearanceService.Load();
        Check(AppearanceService.Current.Radius == 0 && AppearanceService.Current.Canvas == "#FFFFFF", "Existing saved themes lose only their corner radius");
        Check(File.ReadAllBytes(imported.BackgroundFile).SequenceEqual(File.ReadAllBytes(png)), "Background bytes preserved");
        Reject(() => AppearanceService.Apply(new Appearance { Canvas = "red" }), "Reject invalid colors");
        Reject(() => AppearanceService.Apply(new Appearance { Scale = double.NaN }), "Reject NaN scale");
        var malformed = Path.Combine(AppPaths.Root, "invalid.larptheme");
        using (var zip = ZipFile.Open(malformed, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("theme.json").Open()); writer.Write(JsonSerializer.Serialize(new Appearance { BackgroundFile = "../escape.png" }));
        }
        Reject(() => AppearanceService.Import(malformed), "Reject traversal in imported theme");
        AppearanceService.Save(new());
    }

    private static void TestImport()
    {
        var source = Path.Combine(_artifacts, "upstream-fixture"); Directory.CreateDirectory(Path.Combine(source, "profiles"));
        var config = JsonSerializer.Serialize(new Config { SteamPath = "C:/fixture/Steam", ReplaceSteamAutostart = true, AutoUpdate = true });
        File.WriteAllText(Path.Combine(source, "config.json"), config);
        var profile = new Profile { Name = "Imported", Games = [new Game { AppId = "10", Name = "Counter-Strike", Type = "Game", IconUrl = "old/cache.png" }] };
        File.WriteAllText(Path.Combine(source, "profiles", "Imported.json"), JsonSerializer.Serialize(profile));
        Check(MigrationService.ImportFrom(source) == 1, "Import profile count");
        Check(!ConfigService.Load().ReplaceSteamAutostart && !ConfigService.Load().AutoUpdate, "Import cannot enable startup or binary replacement");
        Check(ProfileService.Load("Imported")?.Games[0].IconUrl == "", "Imported icons rehydrate");
        Check(File.ReadAllText(Path.Combine(source, "config.json")) == config, "Original settings unchanged");
        Check(MigrationService.ImportFrom(source) == 0, "Import does not overwrite existing profiles");
        ConfigService.Save(new Config { FirstRun = false, DisableUpdateCheck = true });
        ProfileService.Save(new Profile { Name = "default" });
    }

    private static async Task TestCancellation()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await SearchService.FetchSingleAppDetailsAsync("10", canceled.Token); throw new Exception("Store cancellation failed"); }
        catch (OperationCanceledException) { _assertions++; }
        SearchService.SetApiKey("");
        var names = await SearchService.SearchAsync("Portal", 20);
        Check(names.Any(x => x.Name == "Portal: First Slice"), "Name search works from the bundled catalog without an API key");
        try { await SearchService.SearchAsync("Portal", 20, canceled.Token); throw new Exception("Name cancellation failed"); }
        catch (OperationCanceledException) { _assertions++; }
        Check((await SearchService.SearchAsync("Portal", 20)).Count == names.Count, "Canceled search does not poison a repeated query");
        Check(!await UpdateService.PerformAutoUpdateAsync("https://github.com/3vil3vo/GreenLuma-Manager/file.exe"), "Upstream binary installation disabled");
    }

    private static async Task TestAppLists()
    {
        var folder = Path.Combine(_artifacts, "applist-" + Guid.NewGuid().ToString("N"));
        var profile = new Profile { Name = "Fixture", Games = [new Game { AppId = "10", Name = "First", Type = "Game", Depots = ["11"] }, new Game { AppId = "10", Name = "Duplicate", Type = "Game" }, new Game { AppId = "20", Name = "Last", Type = "DLC" }] };
        var count = await GreenLumaService.GenerateAppListAsync(profile, new Config { GreenLumaPath = folder });
        var appList = Path.Combine(folder, "AppList");
        Check(count == 3 && File.ReadAllText(Path.Combine(appList, "0.txt")) == "10" && File.ReadAllText(Path.Combine(appList, "1.txt")) == "11" && File.ReadAllText(Path.Combine(appList, "2.txt")) == "20", "Legacy AppList preserves order and deduplicates apps and depots");
        var ini = (Task<bool>)typeof(GreenLumaService).GetMethod("GenerateIniAppListAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [appList, new List<string> { "10", "11", "20" }])!;
        Check(await ini && GreenLumaService.ReadAppIdsFromIni(Path.Combine(appList, "AppList.ini")).SequenceEqual(["10", "11", "20"]) && Directory.GetFiles(appList).Length == 1, "INI AppList round trip replaces legacy files");
        Check(typeof(IPlugin).Assembly.GetName().Name == "GreenLuma-Manager" && typeof(IPlugin).FullName == "GreenLuma_Manager.Plugins.IPlugin", "Upstream plugin contract identity preserved");
    }

    private static void TestPlugins()
    {
        PluginService.Initialize();
        Check(PluginService.ImportPlugin(typeof(CheckPlugin).Assembly.Location) == "", "Existing IPlugin assembly imports");
        PluginService.Initialize();
        Check(PluginService.GetEnabledPlugins().Any(x => x.Name == "Compatibility fixture"), "Plugin loads against the retained contract");
        PluginService.OnApplicationStartup();
        Check(File.Exists(Path.Combine(AppPaths.Root, "plugin-started.txt")), "Plugin startup callback runs");
        PluginService.OnApplicationShutdown();
        Check(File.Exists(Path.Combine(AppPaths.Root, "plugin-stopped.txt")), "Plugin shutdown callback runs");
        PluginService.RemovePlugin(PluginService.GetAllPlugins().Single(x => x.Name == "Compatibility fixture"));
        Check(PluginService.GetAllPlugins().Count == 0, "Plugin removes cleanly");
    }

    private static async Task TestUi()
    {
        var window = new MainWindow(); Application.Current.MainWindow = window;
        Layout(window); await Task.Delay(50);
        WindowFrameChecks.Validate(window, Check);
        Console.WriteLine("Window bounds checked on " + WindowFrameChecks.ValidateMonitors(window, Check) + " monitors.");
        Check(((TextBox)window.FindName("TxtSearchInput")).ActualHeight == 34, "Search input uses lazysync's 34-pixel height");
        Check(((Border)window.FindName("PluginToolbar")).Visibility == Visibility.Collapsed, "No extra toolbar without enabled plugins");
        PluginService.ImportPlugin(typeof(CheckPlugin).Assembly.Location); PluginService.Initialize(); window.UpdatePluginButtons(); Layout(window);
        var shortcuts = (StackPanel)window.FindName("PnlPluginButtons");
        Check(((Border)window.FindName("PluginToolbar")).IsVisible && shortcuts.Children.Count == 1, "Enabled plugins retain their shortcuts below the title bar");
        ((Button)shortcuts.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(File.Exists(Path.Combine(AppPaths.Root, "plugin-ui.txt")), "Plugin shortcut opens the existing plugin interface");
        PluginService.OnApplicationShutdown(); PluginService.RemovePlugin(PluginService.GetAllPlugins().Single()); window.UpdatePluginButtons(); Layout(window);
        Capture(window, "main-dark.png");
        var task = (Task)Invoke(window, "OpenDetailsAsync", (uint)3764200, false)!; await task;
        Layout(window);
        await Task.Delay(150);
        var details = (DataGrid)window.FindName("DgDetails");
        Check(details.Items.Count == 4, "Base game and every fixture DLC displayed");
        Check(details.Items.Cast<DlcSelection>().All(x => !x.Selected), "DLC selection starts empty");
        WindowFrameChecks.Validate(window, Check);
        foreach (var checkbox in WindowFrameChecks.Descendants<CheckBox>(details))
        {
            DependencyObject parent = checkbox;
            while (parent is not DataGridCell) parent = VisualTreeHelper.GetParent(parent);
            var cell = (DataGridCell)parent;
            Check(checkbox.ActualWidth <= cell.ActualWidth - cell.Padding.Left - cell.Padding.Right, "DLC checkboxes fit without clipping square borders");
        }
        Capture(window, "dlc-dark.png");
        Invoke(window, "SelectAllDetails_Click", window, new RoutedEventArgs());
        Invoke(window, "AddSelectedDetails_Click", window, new RoutedEventArgs());
        var profile = (ComboBox)window.FindName("CmbProfile");
        var current = ProfileService.Load((string)profile.SelectedItem)!;
        Check(current.Games.Count == 4, "Add selected saves game and DLCs");
        Check(details.Items.Cast<DlcSelection>().All(x => x.AlreadyAdded && !x.CanSelect), "Duplicate selection disabled");
        await (Task)Invoke(window, "OpenDetailsAsync", (uint)3764200, false)!;
        Check(details.Items.Cast<DlcSelection>().All(x => x.AlreadyAdded), "Already-added state retained when reopening");
        Invoke(window, "BackToResults_Click", window, new RoutedEventArgs());
        var undo = (Button)window.FindName("BtnAddAll");
        Invoke(window, "AddAllGames_Click", undo, new RoutedEventArgs());
        Check(ProfileService.Load((string)profile.SelectedItem)!.Games.Count == 0, "Bulk undo removes only additions");
        var controller = (GameListController)typeof(MainWindow).GetField("_gameListController", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var first = new Game { AppId = "10", Name = "Alpha", Type = "Game" };
        var second = new Game { AppId = "20", Name = "Beta", Type = "DLC" };
        controller.LoadGames([first, second]); controller.MoveDown(first);
        Check(controller.Games.Select(x => x.AppId).SequenceEqual(["20", "10"]), "Profile move changes ordering");
        controller.LoadGames(controller.Games.ToList());
        Check(controller.Games.Select(x => x.AppId).SequenceEqual(["20", "10"]), "Reload preserves custom ordering");
        controller.StartRename(first); controller.CommitRename(first, "Renamed");
        controller.StartRename(first); first.Name = "Temporary"; controller.CancelRename(first);
        Check(first.Name == "Renamed" && !first.IsEditing, "Rename commit and cancel preserve the name");
        controller.LoadGames([]);
        AppearanceService.Apply(Appearance.Light()); Layout(window); await Task.Delay(150); Capture(window, "main-light.png");
        foreach (var scale in new[] { .85, 1.5 }) { var theme = new Appearance { Scale = scale, Comfortable = true }; AppearanceService.Apply(theme); window.Width = 960; window.Height = 620; Layout(window); await Task.Delay(100); Check(window.ActualWidth == 960, "Minimum window layout"); Capture(window, "scale-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png"); }
        AppearanceService.Apply(new()); window.Width = 1120; window.Height = 760;
        var settings = new SettingsDialog(new Config { FirstRun = false });
        foreach (var page in new[] { "General", "System", "Advanced", "Appearance" })
        {
            ((RadioButton)settings.FindName("Nav" + page)).IsChecked = true;
            Layout(settings); await Task.Delay(100); Capture(settings, "settings-" + page.ToLowerInvariant() + ".png");
            WindowFrameChecks.Validate(settings, Check);
        }
        var editor = (AppearanceEditor)settings.FindName("ViewAppearance");
        AppearanceService.Apply(Appearance.Light()); editor.Revert();
        Check(AppearanceService.Current.Canvas == "#141414", "Discard restores saved theme");
        var plugins = new PluginsDialog(); Layout(plugins); Capture(plugins, "plugins.png");
        WindowFrameChecks.Validate(plugins, Check);
        var create = new CreateProfileDialog(); Layout(create); Capture(create, "create-profile.png");
        WindowFrameChecks.Validate(create, Check);
        var message = (Window)Activator.CreateInstance(typeof(CustomMessageBox), BindingFlags.Instance | BindingFlags.NonPublic, null, ["Example error message", "Connection failed", MessageBoxButton.OKCancel, MessageBoxImage.Error], null)!;
        Layout(message); Capture(message, "message.png");
        WindowFrameChecks.Validate(message, Check);
        var version = (Window)Activator.CreateInstance(typeof(GreenLumaVersionDialog), BindingFlags.Instance | BindingFlags.NonPublic, null, ["1.7.9"], null)!;
        Layout(version); Capture(version, "version.png"); WindowFrameChecks.Validate(version, Check);
        var versionBar = WindowFrameChecks.Descendants<TitleBar>(version).Single();
        ((Button)versionBar.FindName("BtnClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
        Check(!version.IsVisible && version.DialogResult != true, "Caption close cancels the version dialog without choosing a version");
        var messageBar = WindowFrameChecks.Descendants<TitleBar>(message).Single();
        ((Button)messageBar.FindName("BtnClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
        Check(!message.IsVisible && ((CustomMessageBox)message).Result == MessageBoxResult.Cancel, "Caption close returns Cancel from messages");
        AppearanceService.Apply(Appearance.Light());
        var settingsBar = WindowFrameChecks.Descendants<TitleBar>(settings).Single();
        ((Button)settingsBar.FindName("BtnClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
        Check(!settings.IsVisible && AppearanceService.Current.Canvas == "#141414", "Caption close discards unsaved appearance edits");
        plugins.Close(); create.Close(); window.Close();
    }

    private static async Task TestLive()
    {
        App.IsPreview = false;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await GameDlcService.GetAsync(3764200, limit.Token, true);
        Check(result.BaseGame?.Name == "Resident Evil Requiem", "Live example game resolves");
        var store = await SearchService.FetchSingleAppDetailsAsync("3764200", limit.Token);
        Check(store?.ListOfDlc?.All(id => result.Dlcs.Any(x => x.AppId == id)) == true, "Every live Store DLC appears");
        var fromDlc = await GameDlcService.GetAsync(3990820, limit.Token, true);
        Check(fromDlc.BaseGame?.AppId == "3764200" && fromDlc.Dlcs.Count == result.Dlcs.Count, "DLC App ID follows its parent game and sibling DLCs");
        Console.WriteLine($"LIVE: {result.BaseGame!.Name}, {result.Dlcs.Count} DLCs, partial={result.Partial}");
        File.WriteAllText(Path.Combine(_artifacts, "live-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        SteamService.Instance.Dispose(); App.IsPreview = true;
    }

    private static async Task TestNativeWindow()
    {
        AppearanceService.Apply(new());
        var window = new MainWindow(); Application.Current.MainWindow = window; Layout(window);
        await Task.Delay(100);
        var bar = WindowFrameChecks.Descendants<TitleBar>(window).Single();
        ((Button)bar.FindName("BtnMinimize")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150);
        Check(window.WindowState == WindowState.Minimized, "Minimize button minimizes the real HWND");
        SystemCommands.RestoreWindow(window); await Task.Delay(150);
        Check(window.WindowState == WindowState.Normal, "Minimized window restores");
        ((Button)bar.FindName("BtnMaximize")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150);
        Check(window.WindowState == WindowState.Maximized && (string)((Button)bar.FindName("BtnMaximize")).ToolTip == "Restore", "Maximize button maximizes and switches to Restore");
        WindowFrameChecks.ValidateMaximizedWindow(window, Check); Capture(window, "maximized.png");
        ((Button)bar.FindName("BtnMaximize")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150);
        Check(window.WindowState == WindowState.Normal, "Restore button restores the real HWND");
        window.Width = 1180; window.Height = 800; Layout(window);
        Check(window.ActualWidth == 1180 && window.ActualHeight == 800, "Window size changes update layout");
        WindowFrameChecks.DoubleClickCaption(window); await Task.Delay(150);
        Check(window.WindowState == WindowState.Maximized, "Native caption double-click maximizes");
        WindowFrameChecks.DoubleClickCaption(window); await Task.Delay(150);
        Check(window.WindowState == WindowState.Normal, "Native caption double-click restores");
        var versionFixture = Path.Combine(_artifacts, "version-fixture"); Directory.CreateDirectory(versionFixture);
        File.Copy(typeof(CheckPlugin).Assembly.Location, Path.Combine(versionFixture, "GreenLuma_2026_x64.dll"), overwrite: true);
        Check(GreenLumaService.DetectVersion(versionFixture) == GreenLumaService.LegacyVersion, "Version fixture is detected without a real GreenLuma installation");
        var config = new Config { GreenLumaPath = versionFixture, GreenLumaVersionOverride = "1.8.0" };
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = Application.Current.Windows.OfType<GreenLumaVersionDialog>().Single();
            var title = WindowFrameChecks.Descendants<TitleBar>(dialog).Single();
            ((Button)title.FindName("BtnClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, DispatcherPriority.ApplicationIdle);
        Check(!GreenLumaVersionPromptService.TryEnsureConfirmed(config) && !config.GreenLumaVersionPromptShown && config.GreenLumaVersionOverride == "1.8.0", "Closing required version selection cancels the dependent operation and preserves configuration");
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = Application.Current.Windows.OfType<GreenLumaVersionDialog>().Single();
            Invoke(dialog, "CurrentVersion_Click", dialog, new RoutedEventArgs());
        }, DispatcherPriority.ApplicationIdle);
        Check(GreenLumaVersionPromptService.TryEnsureConfirmed(config) && config.GreenLumaVersionPromptShown && config.GreenLumaVersionOverride == "1.8.0", "Version selection still confirms and saves normally");
        ((Button)bar.FindName("BtnClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
        Check(!window.IsVisible, "Main caption close closes the real HWND");
    }

    private static async Task TestMedia()
    {
        var layer = new BackgroundLayer();
        var window = new Window { Width = 640, Height = 400, Content = layer, Style = (Style)Application.Current.FindResource(typeof(Window)) };
        AppearanceService.Apply(new());
        Layout(window);
        async Task<string?> ApplyMedia(Appearance theme)
        {
            var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStatus(string? error) => ready.TrySetResult(error);
            layer.StatusChanged += OnStatus;
            try { AppearanceService.Apply(theme); return await ready.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
            finally { layer.StatusChanged -= OnStatus; }
        }
        foreach (var extension in new[] { "gif", "mp4", "webm" })
        {
            var file = Path.Combine(Environment.CurrentDirectory, "artifacts", "media", "background." + extension);
            var theme = new Appearance { BackgroundFile = AppearanceService.CopyBackground(file), BackgroundOpacity = .5 };
            var mediaError = await ApplyMedia(theme);
            var browser = (Microsoft.Web.WebView2.Wpf.WebView2CompositionControl?)typeof(BackgroundLayer).GetField("_browser", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(layer);
            if (mediaError != null && browser?.CoreWebView2 != null) Console.WriteLine(await browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({html:document.documentElement.outerHTML,complete:document.getElementById('media')?.complete,width:document.getElementById('media')?.naturalWidth,webview:!!window.chrome?.webview})"));
            Check(mediaError == null, extension + " background loads: " + layer.Error);
            Check(browser?.CoreWebView2 != null, extension + " uses composed WebView2");
            var loaded = await browser!.CoreWebView2.ExecuteScriptAsync("media.tagName==='IMG'?media.naturalWidth:media.videoWidth");
            Check(loaded == "320", extension + " media is decoded");
            theme.Paused = true;
            Check(await ApplyMedia(theme) == null && browser.Visibility == Visibility.Hidden, extension + " pauses to a still image");
            theme.Paused = false;
            Check(await ApplyMedia(theme) == null && browser.Visibility == Visibility.Visible, extension + " resumes");
            window.WindowState = WindowState.Minimized; await Task.Delay(250);
            Check(browser.Visibility == Visibility.Hidden, extension + " suspends when minimized");
            window.WindowState = WindowState.Normal; await Task.Delay(250);
            Check(browser.Visibility == Visibility.Visible, extension + " resumes when restored");
            theme.ReducedMotion = true;
            Check(await ApplyMedia(theme) == null, extension + " reduced motion");
            Capture(window, "background-" + extension + ".png");
        }
        var missing = new Appearance { BackgroundFile = Path.Combine(_artifacts, "missing.png") };
        Check(await ApplyMedia(missing) != null, "Missing background reports an inline error");
        AppearanceService.Apply(new()); window.Close();
    }

    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Layout(Window window)
    {
        if (!window.IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -12000;
            window.Top = -12000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
        }
        var width = double.IsNaN(window.Width) ? 600 : window.Width;
        var height = double.IsNaN(window.Height) ? 400 : window.Height;
        window.Measure(new Size(width, height)); window.Arrange(new Rect(0, 0, width, height)); window.UpdateLayout();
    }
    private static void Capture(Window window, string name)
    {
        var root = (FrameworkElement)window.Content;
        var width = Math.Max(1, (int)root.ActualWidth); var height = Math.Max(1, (int)root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var canvas = new DrawingVisual(); using (var drawing = canvas.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(canvas); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_artifacts, name)); encoder.Save(file);
    }
}

public sealed class CheckPlugin : IPlugin
{
    public string Name => "Compatibility fixture";
    public string Version => "1.0";
    public string Author => "Checks";
    public string Description => "Exercises the upstream plugin ABI.";
    public Geometry Icon => Geometry.Empty;
    public void Initialize() { }
    public void OnApplicationStartup() => File.WriteAllText(Path.Combine(AppPaths.Root, "plugin-started.txt"), "ok");
    public void OnApplicationShutdown() => File.WriteAllText(Path.Combine(AppPaths.Root, "plugin-stopped.txt"), "ok");
    public void ShowUi(Window owner) => File.WriteAllText(Path.Combine(AppPaths.Root, "plugin-ui.txt"), "ok");
}
