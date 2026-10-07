using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GreenLuma_Manager;
using GreenLuma_Manager.Controllers;
using GreenLuma_Manager.Dialogs;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

internal static class CreamInstallerChecks
{
    internal static async Task Run(Action<bool, string> check, string artifacts)
    {
        var fixture = Path.Combine(artifacts, "unlockers-" + Guid.NewGuid().ToString("N"));
        var steam = Path.Combine(fixture, "Steam");
        var external = Path.Combine(fixture, "Extra Library");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(external, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "Steam.exe"), "fixture, never executed");
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\" { \"0\" { \"path\" \"" + steam.Replace("\\", "\\\\") + "\" } \"1\" { \"path\" \"" + external.Replace("\\", "\\\\") + "\" } }");
        var root = CreateGame(steam, "10", "Fixture game");
        var second = CreateGame(external, "20", "External game");
        var api32 = Path.Combine(root, "steam_api.dll");
        var api64 = Path.Combine(root, "bin", "steam_api64.dll");
        MakePe(api32, false); MakePe(api64, true);
        MakePe(Path.Combine(root, "bin", "game.exe"), true);
        MakePe(Path.Combine(second, "steam_api64.dll"), true);
        var original32 = File.ReadAllBytes(api32); var original64 = File.ReadAllBytes(api64);
        var config = new Config { UnlockMethod = UnlockMethod.CreamInstaller, SteamPath = steam };
        var entries = new List<Game>
        {
            new() { AppId = "11", Name = "DLC \"one\"\n[steam]", Type = "DLC", ParentAppId = "10" },
            new() { AppId = "12", Name = "Soundtrack", Type = "Soundtrack", ParentAppId = "10" },
            new() { AppId = "21", Name = "External DLC", Type = "DLC", ParentAppId = "20" }
        };
        var discovered = CreamInstallerService.DiscoverGames(steam);
        check(discovered.Count == 2 && discovered["20"].Root == second, "Unlocker discovers primary and additional Steam libraries");
        var plan = CreamInstallerService.PrepareResolved(config, entries, false);
        check(plan.Games.Count == 2 && plan.Games[0].Dlcs.Keys.SequenceEqual(["11"]) && plan.Issues.Count == 1,
            "DLC-only profiles find their installed parent; soundtrack stays excluded");
        using var smoke = JsonDocument.Parse(CreamInstallerService.BuildConfig(config, "10", plan.Games[0].Dlcs));
        check(smoke.RootElement.GetProperty("$version").GetInt32() == 4 && smoke.RootElement.GetProperty("default_app_status").GetString() == "original" &&
            smoke.RootElement.GetProperty("override_dlc_status").GetProperty("11").GetString() == "unlocked" &&
            !smoke.RootElement.GetProperty("auto_inject_inventory").GetBoolean(), "SmokeAPI config targets selected DLCs and preserves other entitlement/inventory behavior");
        check(smoke.RootElement.GetProperty("extra_dlcs").GetProperty("10").GetProperty("dlcs").GetProperty("11").GetString() == entries[0].Name,
            "SmokeAPI safely encodes selected DLC names for injection");
        var many = Enumerable.Range(100, 100).ToDictionary(i => i.ToString(), i => "DLC " + i);
        using var large = JsonDocument.Parse(CreamInstallerService.BuildConfig(config, "10", many));
        check(large.RootElement.GetProperty("extra_dlcs").GetProperty("10").GetProperty("dlcs").EnumerateObject().Count() == 100, "Unlocker config includes DLC selections beyond 64 entries");
        foreach (var game in plan.Games) CreamInstallerService.ApplyGame(plan, game);
        check(!File.ReadAllBytes(api32).SequenceEqual(original32) && !File.ReadAllBytes(api64).SequenceEqual(original64), "Standard installation deploys matching x86/x64 DLLs");
        check(File.ReadAllBytes(Path.Combine(root, "steam_api_o.dll")).SequenceEqual(original32) &&
            File.ReadAllBytes(Path.Combine(root, "bin", "steam_api64_o.dll")).SequenceEqual(original64), "Original DLLs remain available to both runtime wrappers");
        var installedHash = Hash(api64);
        var noDlcs = CreamInstallerService.PrepareResolved(config, [new() { AppId = "10", Name = "Fixture game", Type = "Game" }], false);
        CreamInstallerService.ApplyGame(noDlcs, noDlcs.Games.Single());
        using (var cleared = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "SmokeAPI.config.json"))))
            check(!cleared.RootElement.GetProperty("override_dlc_status").EnumerateObject().Any(), "Updating an existing managed game with no selected DLCs clears its configured DLC overrides");
        var repeat = CreamInstallerService.PrepareResolved(config, entries, false);
        foreach (var game in repeat.Games) CreamInstallerService.ApplyGame(repeat, game);
        check(Hash(api64) == installedHash && File.ReadAllBytes(Path.Combine(root, "bin", "steam_api64_o.dll")).SequenceEqual(original64), "Repeated install never backs up an unlocker as the original");

        config.SteamUnlocker = SteamUnlocker.CreamAPI; config.CreamExtraProtection = true;
        var cream = CreamInstallerService.PrepareResolved(config, entries.Take(1), false);
        CreamInstallerService.ApplyGame(cream, cream.Games.Single());
        var ini = File.ReadAllText(Path.Combine(root, "cream_api.ini"));
        check(ini.Contains("unlockall = false") && ini.Contains("extraprotection = true") && ini.Contains("[steam_misc]") &&
            ini.Split("[steam]").Length == 2 && !File.Exists(Path.Combine(root, "SmokeAPI.config.json")), "Engine update emits scoped CreamAPI config and removes the prior owned config");
        check(File.ReadAllBytes(Path.Combine(root, "steam_api_o.dll")).SequenceEqual(original32), "Engine change preserves the first original backup");

        foreach (var engine in new[] { SteamUnlocker.SmokeAPI, SteamUnlocker.CreamAPI })
        foreach (var proxy in new[] { "winmm", "winhttp", "version" })
        {
            config.SteamUnlocker = engine; config.UnlockerProxy = true; config.UnlockerProxyName = proxy;
            var proxyPlan = CreamInstallerService.PrepareResolved(config, entries.Take(1), false);
            CreamInstallerService.ApplyGame(proxyPlan, proxyPlan.Games.Single());
            var proxyPath = Path.Combine(root, "bin", proxy + ".dll");
            check(File.Exists(proxyPath) && CreamInstallerService.Is64Bit(proxyPath) && File.ReadAllBytes(api64).SequenceEqual(original64) &&
                File.ReadAllBytes(api32).SequenceEqual(original32), $"{engine} {proxy} proxy keeps original API DLLs unchanged");
            check(new[] { "winmm", "winhttp", "version" }.Where(p => p != proxy).All(p => !File.Exists(Path.Combine(root, "bin", p + ".dll"))), "Changing proxy removes only prior owned proxies");
        }
        var remove = CreamInstallerService.PrepareResolved(config, entries, true);
        foreach (var game in remove.Games) CreamInstallerService.ApplyGame(remove, game);
        check(File.ReadAllBytes(api32).SequenceEqual(original32) && File.ReadAllBytes(api64).SequenceEqual(original64) &&
            !Directory.Exists(Path.Combine(root, ".larpluma-unlocker")) && !Directory.Exists(Path.Combine(second, ".larpluma-unlocker")),
            "Uninstall restores originals across libraries independent of current engine/proxy settings");

        config.SteamUnlocker = SteamUnlocker.SmokeAPI; config.UnlockerProxy = false;
        var fail = CreamInstallerService.PrepareResolved(config, entries.Take(1), false);
        using (var locked = new FileStream(api64, FileMode.Open, FileAccess.Read, FileShare.Read))
            ExpectFailure(() => CreamInstallerService.ApplyGame(fail, fail.Games.Single()), check, "Locked target causes rollback");
        check(File.ReadAllBytes(api32).SequenceEqual(original32) && File.ReadAllBytes(api64).SequenceEqual(original64) &&
            !File.Exists(Path.Combine(root, "SmokeAPI.config.json")) && !File.Exists(Path.Combine(root, ".larpluma-unlocker", "pending.json")),
            "Failed game operation restores already-written DLLs and configuration");
        var clean = CreamInstallerService.PrepareResolved(config, entries.Take(1), true);
        CreamInstallerService.ApplyGame(clean, clean.Games.Single());

        var stale = CreamInstallerService.PrepareResolved(config, entries.Take(1), false);
        File.AppendAllText(api32, "game update");
        ExpectFailure(() => CreamInstallerService.ApplyGame(stale, stale.Games.Single()), check, "Files changed after review are rejected before replacement");
        check(File.ReadAllText(api32).EndsWith("game update"), "Review-time conflict leaves the updated game DLL intact");
        File.WriteAllBytes(api32, original32);
        var updatePlan = CreamInstallerService.PrepareResolved(config, entries.Take(1), false);
        CreamInstallerService.ApplyGame(updatePlan, updatePlan.Games.Single());
        var changed = File.ReadAllBytes(api32).Concat(new byte[] { 1 }).ToArray(); File.WriteAllBytes(api32, changed);
        var blocked = CreamInstallerService.PrepareResolved(config, entries.Take(1), true);
        check(blocked.Games.Count == 0 && blocked.Issues.Single().Contains("changed") && File.ReadAllBytes(api32).SequenceEqual(changed), "Uninstall preserves DLLs changed by a game update and retains backups");
        File.WriteAllBytes(api32, updatePlan.Games.Single().Files["steam_api.dll"]!);
        SimulateInterruptedOperation(root, check);
        var recovery = CreamInstallerService.PrepareResolved(config, entries.Take(1), true);
        check(recovery.Games.Single().Recover, "Interrupted operations are offered for explicit recovery");
        CreamInstallerService.ApplyGame(recovery, recovery.Games.Single());
        check(File.ReadAllBytes(api32).SequenceEqual(original32) && !Directory.Exists(Path.Combine(root, ".larpluma-unlocker")), "Interrupted write recovers then uninstalls with original bytes restored");

        config.UnlockerProxy = true;
        config.UnlockerProxyName = "winmm";
        File.WriteAllText(Path.Combine(root, "bin", "winmm.dll"), "foreign proxy");
        var conflict = CreamInstallerService.PrepareResolved(config, entries.Take(1), false);
        check(conflict.Games.Count == 0 && File.ReadAllText(Path.Combine(root, "bin", "winmm.dll")) == "foreign proxy", "Foreign proxy is never overwritten");
        File.Delete(Path.Combine(root, "bin", "winmm.dll"));
        Directory.CreateDirectory(Path.Combine(root, "EasyAntiCheat"));
        check(CreamInstallerService.PrepareResolved(config, entries.Take(1), false).Games.Count == 0, "Upstream protected-game exclusions block installation");
        Directory.Delete(Path.Combine(root, "EasyAntiCheat"));
        MakePe(Path.Combine(root, "bin", "helper32.exe"), false);
        MakePe(Path.Combine(root, "bin", "game32.exe"), false);
        check(CreamInstallerService.PrepareResolved(config, entries.Take(1), false).Issues.Single().Contains("Mixed"), "Mixed proxy architectures fail before writes");
        File.Delete(Path.Combine(root, "bin", "game32.exe"));
        config.UnlockerProxy = false;
        check(CreamInstallerService.PrepareResolved(config, [], false).Games.Count == 0, "Empty profiles make no changes");
        check(CreamInstallerService.PrepareResolved(config, [new() { AppId = "99", Name = "Missing", Type = "Game" }, new() { AppId = "98", Name = "Missing DLC", Type = "DLC", ParentAppId = "99" }], false).Issues.Single().Contains("not installed"), "Uninstalled games are reported before applying");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        ExpectFailure(() => CreamInstallerService.PrepareResolved(config, entries, false, canceled.Token), check, "Preparation honors cancellation");
        App.IsPreview = true;
        try { await CreamInstallerService.ExecuteAsync(stale, null, CancellationToken.None); throw new Exception("Preview allowed mutation"); }
        catch (InvalidOperationException) { check(File.ReadAllBytes(api32).SequenceEqual(original32), "Preview prevents unlocker mutations"); }

        var defaults = JsonSerializer.Deserialize<Config>("{\"SteamPath\":\"legacy\"}")!;
        check(defaults.UnlockMethod == UnlockMethod.GreenLuma && defaults.SteamUnlocker == SteamUnlocker.SmokeAPI && !defaults.UnlockerProxy, "Legacy configs default to GreenLuma and SmokeAPI standard mode");
        check(new GreenLumaLauncher().ValidatePaths(config) && GreenLumaVersionPromptService.TryEnsureConfirmed(config), "CreamInstaller needs no GreenLuma installation or version prompt");
        check(await GreenLumaService.GenerateAppListAsync(new Profile { Games = entries }, config) == -1 && !await GreenLumaService.LaunchGreenLumaAsync(config), "GreenLuma service guards prevent accidental deployment in CreamInstaller mode");
        TestReceiptEscape(config, entries.Take(1), root, check);
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_30.acf"), "\"AppState\" { \"appid\" \"30\" \"installdir\" \"..\\\\..\\\\outside\" }");
        ExpectFailure(() => CreamInstallerService.DiscoverGames(steam), check, "Manifest paths cannot escape their Steam library");
        File.Delete(Path.Combine(steam, "steamapps", "appmanifest_30.acf"));
        var batch = CreamInstallerService.PrepareResolved(config, entries, false);
        try
        {
            App.IsPreview = false;
            using var stopAfterStart = new CancellationTokenSource();
            var result = await CreamInstallerService.ExecuteAsync(batch, new CancelOnProgress(stopAfterStart), stopAfterStart.Token);
            check(result.Canceled && result.Completed.Count == 1 && result.Failed.Count == 0 &&
                !Directory.Exists(Path.Combine(second, ".larpluma-unlocker")), "Cancellation finishes the current game and skips remaining games");
            var cleanup = CreamInstallerService.PrepareResolved(config, entries.Take(1), true);
            CreamInstallerService.ApplyGame(cleanup, cleanup.Games.Single());
            batch = CreamInstallerService.PrepareResolved(config, entries, false);
            using (var locked = new FileStream(api64, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                result = await CreamInstallerService.ExecuteAsync(batch, null, CancellationToken.None);
                check(result.Failed.Count == 1 && result.Completed.Count == 1 && File.ReadAllBytes(api32).SequenceEqual(original32),
                    "Batch reports individual failures and continues with independent games after rollback");
            }
            cleanup = CreamInstallerService.PrepareResolved(config, entries, true);
            foreach (var game in cleanup.Games) CreamInstallerService.ApplyGame(cleanup, game);
        }
        finally { App.IsPreview = true; }
        await RunUi(config, check, artifacts);
    }

    private static void SimulateInterruptedOperation(string root, Action<bool, string> check)
    {
        var state = Path.Combine(root, ".larpluma-unlocker");
        var before = JsonSerializer.Deserialize<CreamInstallerService.Receipt>(File.ReadAllText(Path.Combine(state, "receipt.json")))!;
        Directory.CreateDirectory(Path.Combine(state, "transaction"));
        var path = Path.Combine(root, "steam_api.dll");
        File.Copy(path, Path.Combine(state, "transaction", "0"), true);
        var after = Encoding.UTF8.GetBytes("interrupted configuration");
        var changes = new[] { new CreamInstallerService.FileRecord { Path = "steam_api.dll", Slot = 0, OriginalHash = Hash(path), InstalledHash = Convert.ToHexString(SHA256.HashData(after)) } };
        File.WriteAllText(Path.Combine(state, "pending.json"), JsonSerializer.Serialize(new { Before = before, Changes = changes }));
        File.WriteAllBytes(path, after);
        check(File.Exists(Path.Combine(state, "pending.json")), "Durable transaction fixture survives a simulated interruption");
    }

    private static void TestReceiptEscape(Config config, IEnumerable<Game> entries, string root, Action<bool, string> check)
    {
        var plan = CreamInstallerService.PrepareResolved(config, entries, false); CreamInstallerService.ApplyGame(plan, plan.Games.Single());
        var receiptPath = Path.Combine(root, ".larpluma-unlocker", "receipt.json");
        var good = File.ReadAllText(receiptPath);
        var receipt = JsonSerializer.Deserialize<CreamInstallerService.Receipt>(good)!;
        receipt.Files[0].Path = "../steam_api.dll";
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt));
        check(CreamInstallerService.PrepareResolved(config, entries, true).Games.Count == 0, "Restoration records cannot escape the game directory");
        File.WriteAllText(receiptPath, good);
        var remove = CreamInstallerService.PrepareResolved(config, entries, true); CreamInstallerService.ApplyGame(remove, remove.Games.Single());
    }

    private static async Task RunUi(Config config, Action<bool, string> check, string artifacts)
    {
        var saved = ConfigService.Load();
        try
        {
            var settings = new SettingsDialog(config) { Left = -12000, Top = -12000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            settings.Loaded += (_, _) => settings.Dispatcher.InvokeAsync(() =>
            {
                check(((StackPanel)settings.FindName("CreamOptions")).IsVisible && !((FrameworkElement)settings.FindName("GreenLumaPathPanel")).IsVisible, "Settings shows engine options and hides GreenLuma paths");
                settings.GetType().GetMethod("Ok_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(settings, [settings, new RoutedEventArgs()]);
            });
            check(settings.ShowDialog() == true && ConfigService.Load().UnlockMethod == UnlockMethod.CreamInstaller, "CreamInstaller settings save with no GreenLuma directory");
            var discard = new SettingsDialog(config) { Left = -12000, Top = -12000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            discard.Loaded += (_, _) => discard.Dispatcher.InvokeAsync(() =>
            {
                ((RadioButton)discard.FindName("RbMethodGreenLuma")).IsChecked = true;
                discard.GetType().GetMethod("Cancel_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(discard, [discard, new RoutedEventArgs()]);
            });
            check(discard.ShowDialog() == false && config.UnlockMethod == UnlockMethod.CreamInstaller, "Discard does not mutate the saved method");
            var window = new MainWindow(); window.ShowActivated = false; window.ShowInTaskbar = false; window.Left = -12000; window.Top = -12000;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Show(); await Task.Delay(50);
            check((string)((Button)window.FindName("BtnGenerateApplist")).Content == "Install / update DLC unlocker" &&
                (string)((Button)window.FindName("BtnLaunchGreenluma")).Content == "Launch Steam" &&
                !((Button)window.FindName("BtnUninstallUnlocker")).IsEnabled, "CreamInstaller actions and preview guards appear in the main window");
            var stealth = ConfigService.Load().NoHook; window.ToggleStealthCommand.Execute(null);
            check(ConfigService.Load().NoHook == stealth, "Hidden stealth keyboard shortcut cannot change settings in CreamInstaller mode");
            window.GenerateApplistCommand.Execute(null); window.LaunchGreenlumaCommand.Execute(null);
            check(!Directory.Exists(Path.Combine(config.SteamPath, "AppList")), "Preview keyboard shortcuts perform no deployment");
            window.UpdateLayout(); Capture(window, Path.Combine(artifacts, "unlocker-main-dark.png"));
            window.Close();
            foreach (var light in new[] { false, true })
            {
                AppearanceService.Apply(light ? Appearance.Light() : new());
                var visual = new SettingsDialog(config)
                {
                    Left = -12000, Top = -12000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false
                };
                visual.Show(); await Task.Delay(50); visual.UpdateLayout();
                WindowFrameChecks.Validate(visual, check);
                check(((RadioButton)visual.FindName("RbMethodCream")).Focusable &&
                    System.Windows.Automation.AutomationProperties.GetName((ComboBox)visual.FindName("CmbSteamUnlocker")) == "Steam unlocker engine",
                    "Method selector and engine controls expose native keyboard/accessibility semantics");
                Capture(visual, Path.Combine(artifacts, light ? "unlocker-settings-light.png" : "unlocker-settings-dark.png"));
                ((CheckBox)visual.FindName("ChkUnlockerProxy")).IsChecked = true;
                ((ComboBox)visual.FindName("CmbSteamUnlocker")).SelectedIndex = 1;
                visual.UpdateLayout();
                check(((StackPanel)visual.FindName("ProxyOptions")).IsVisible && ((CheckBox)visual.FindName("ChkCreamProtection")).IsVisible,
                    "Proxy and extra-protection controls appear with their relevant options");
                Capture(visual, Path.Combine(artifacts, light ? "unlocker-proxy-light.png" : "unlocker-proxy-dark.png"));
                ((RadioButton)visual.FindName("RbMethodGreenLuma")).IsChecked = true;
                check(!((StackPanel)visual.FindName("CreamOptions")).IsVisible && ((FrameworkElement)visual.FindName("GreenLumaPathPanel")).IsVisible,
                    "Switching back restores the GreenLuma settings layout without changing saved values");
                visual.Close();
            }
        }
        finally { ConfigService.Save(saved); }
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<string>
    {
        public void Report(string value) => cancellation.Cancel();
    }

    private static void Capture(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var canvas = new DrawingVisual();
        using (var drawing = canvas.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(canvas); bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }

    private static string CreateGame(string library, string id, string name)
    {
        var root = Path.Combine(library, "steamapps", "common", name); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_" + id + ".acf"), $"\"AppState\" {{ \"appid\" \"{id}\" \"name\" \"{name}\" \"installdir\" \"{name}\" }}");
        return root;
    }

    private static void MakePe(string path, bool x64)
    {
        // Header-only PE fixture. Never loaded or executed.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[512]; data[0] = (byte)'M'; data[1] = (byte)'Z';
        BitConverter.GetBytes(128).CopyTo(data, 60); data[128] = (byte)'P'; data[129] = (byte)'E';
        BitConverter.GetBytes((ushort)(x64 ? 0x8664 : 0x14c)).CopyTo(data, 132);
        BitConverter.GetBytes((ushort)(x64 ? 240 : 224)).CopyTo(data, 148);
        BitConverter.GetBytes((ushort)0x2002).CopyTo(data, 150);
        BitConverter.GetBytes((ushort)(x64 ? 0x20b : 0x10b)).CopyTo(data, 152);
        BitConverter.GetBytes(512).CopyTo(data, 212);
        File.WriteAllBytes(path, data);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void ExpectFailure(Action action, Action<bool, string> check, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OperationCanceledException)
        { check(true, message); return; }
        throw new Exception(message + " did not fail");
    }
}
