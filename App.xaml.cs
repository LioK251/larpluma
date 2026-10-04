using System.IO;
using System.Windows;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager;

public partial class App
{
    public static bool IsPreview { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsPreview = e.Args.Contains("--preview");
        if (IsPreview)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LARPLUMA_DATA_DIR")))
                Environment.SetEnvironmentVariable("LARPLUMA_DATA_DIR", Path.Combine(AppContext.BaseDirectory, "preview-data"));
            AppearanceService.Load();
            if (e.Args.Contains("--smoke"))
                Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(1000);
                        if (MainWindow is not MainWindow { IsLoaded: true } window || window.Title != "Larpluma")
                            throw new InvalidOperationException("The packaged main window did not load.");
                        Directory.CreateDirectory(AppPaths.Root);
                        File.WriteAllText(Path.Combine(AppPaths.Root, "smoke-pass.txt"), "Larpluma single-file startup passed.");
                        Shutdown(0);
                    }
                    catch (Exception ex) { Logger.Error(ex, "PackageSmoke"); Shutdown(1); }
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        try
        {
            AppearanceService.Load();
            MigrationService.OfferImport();
            PluginService.Initialize();
            PluginService.OnApplicationStartup();

            var config = ConfigService.Load();

            if (e.Args.Length > 0)
                foreach (var arg in e.Args)
                    if (string.Equals(arg, "--launch-greenluma", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (GreenLumaVersionPromptService.TryEnsureConfirmed(config))
                                GreenLumaService.LaunchGreenLumaAsync(config).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "App.LaunchGreenluma");
                        }

                        Shutdown();
                        return;
                    }

            var profiles = ProfileService.LoadAll();
            var valid = new HashSet<string>(profiles
                .SelectMany(p => p.Games)
                .Where(g => !string.IsNullOrWhiteSpace(g.AppId))
                .Select(g => g.AppId));
            IconCacheService.DeleteUnusedIcons(valid);
            _ = WarmupIconsAsync(profiles);
            SearchService.SetApiKey(config.SteamApiKey);
            _ = SearchService.PrefetchAsync(config);
            _ = Task.Run(() => { _ = SteamService.Instance; });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "App.OnStartup");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (!IsPreview) SteamService.Instance.Dispose();
            PluginService.OnApplicationShutdown();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "App.OnExit");
        }

        base.OnExit(e);
    }

    private static async Task WarmupIconsAsync(List<Profile> profiles)
    {
        try
        {
            foreach (var profile in profiles)
            {
                var changed = false;

                await Parallel.ForEachAsync(
                    profile.Games.Where(g => !string.IsNullOrWhiteSpace(g.AppId)),
                    new ParallelOptions { MaxDegreeOfParallelism = 6 },
                    async (game, _) =>
                    {
                        try
                        {
                            var cached = IconCacheService.GetCachedIconPath(game.AppId);
                            if (string.IsNullOrEmpty(cached))
                            {
                                string? path = null;
                                if (!string.IsNullOrWhiteSpace(game.IconUrl))
                                    path = await IconCacheService.DownloadAndCacheIconAsync(game.AppId, game.IconUrl);

                                if (string.IsNullOrEmpty(path))
                                {
                                    await SearchService.FetchIconUrlAsync(game);
                                    if (!string.IsNullOrWhiteSpace(game.IconUrl))
                                        path = await IconCacheService.DownloadAndCacheIconAsync(game.AppId,
                                            game.IconUrl);
                                }

                                if (!string.IsNullOrEmpty(path))
                                {
                                    game.IconUrl = path;
                                    changed = true;
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(game.IconUrl) && !File.Exists(cached))
                            {
                                var path = await IconCacheService.DownloadAndCacheIconAsync(game.AppId, game.IconUrl);
                                if (!string.IsNullOrEmpty(path))
                                {
                                    game.IconUrl = path;
                                    changed = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "App.WarmupIcon");
                        }
                    });

                if (changed)
                    try
                    {
                        ProfileService.Save(profile);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "App.WarmupSave");
                    }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "App.WarmupIcons");
        }
    }
}
