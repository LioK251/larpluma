using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Services;

public class CacheEntry<T>
{
    public DateTime Expiry { get; set; }
    public T Data { get; set; } = default!;
}

public static class SteamApiCache
{
    internal static readonly ConcurrentDictionary<string, CacheEntry<object>> Cache = new();

}

public class SearchService
{
    private const string SteamStoreApiUrl = "https://api.steampowered.com/IStoreService/GetAppList/v1/";
    private const int BatchSize = 150;
    private const int AppListPageSize = 50000;

    private const string LegacyResourceName = "GreenLuma_Manager.Data.steam_applist_legacy.json";

    private const int StoreApiMaxPerCall = 20;

    private static readonly TimeSpan AppListFetchWait = TimeSpan.FromSeconds(4);
    private static string _steamApiKey = string.Empty;
    private static List<SteamApp>? _appListCache;
    private static readonly SemaphoreSlim AppListLock = new(1, 1);
    private static readonly SemaphoreSlim LegacyAppListLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, GameDetails> DetailsCache = new();
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static volatile bool _isPrefetching;
    private static Task? _appListFetchTask;
    private static readonly TimeSpan StoreApiMinInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan StoreApiRateLimitBackoff = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim StoreApiThrottle = new(1, 1);
    private static DateTime _storeApiAvailableAt = DateTime.MinValue;

    public static void SetApiKey(string? apiKey)
    {
        var next = apiKey ?? string.Empty;
        if (next != _steamApiKey) { _cacheExpiry = DateTime.MinValue; SteamApiCache.Cache.Clear(); }
        _steamApiKey = next;
    }

    public static string? LookupAppName(string appId)
    {
        if (_appListCache == null) return null;
        var app = _appListCache.Find(a => a.AppId == appId);
        return app?.Name;
    }

    private static async Task<List<SteamApp>> GetBestAvailableAppListAsync(CancellationToken ct = default)
    {
        if (_appListCache != null && DateTime.Now < _cacheExpiry)
            return _appListCache;

        if (string.IsNullOrWhiteSpace(_steamApiKey))
            return await GetLegacyAppListAsync(ct).ConfigureAwait(false);

        var fetchTask = _isPrefetching && _appListFetchTask != null
            ? _appListFetchTask
            : StartAppListFetch();

        var legacyTask = GetLegacyAppListAsync(ct);

        var completed = await Task.WhenAny(fetchTask, Task.Delay(AppListFetchWait, ct)).ConfigureAwait(false);
        if (completed == fetchTask && _appListCache != null)
            return _appListCache;

        return await legacyTask.ConfigureAwait(false);
    }

    private static Task StartAppListFetch()
    {
        _isPrefetching = true;
        var task = Task.Run(async () =>
        {
            try
            {
                await GetAppListAsync().ConfigureAwait(false);
            }
            finally
            {
                _isPrefetching = false;
            }
        });
        _appListFetchTask = task;
        return task;
    }

    private static async Task<List<SteamApp>> GetLegacyAppListAsync(CancellationToken ct = default)
    {
        if (_appListCache != null) return _appListCache;

        await LegacyAppListLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_appListCache != null) return _appListCache;
            var legacy = await FetchLegacyAppListAsync(ct).ConfigureAwait(false);
            if (_appListCache == null)
                _appListCache = legacy;
            return _appListCache;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SearchService.GetLegacyAppList");
            return _appListCache ?? [];
        }
        finally
        {
            LegacyAppListLock.Release();
        }
    }

    private static async Task GetAppListAsync(CancellationToken ct = default)
    {
        if (_appListCache != null && DateTime.Now < _cacheExpiry)
            return;

        await AppListLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_appListCache != null && DateTime.Now < _cacheExpiry)
                return;

            var legacyTask = _appListCache != null
                ? Task.FromResult(_appListCache)
                : FetchLegacyAppListAsync(ct);
            var modernTask = string.IsNullOrWhiteSpace(_steamApiKey)
                ? Task.FromResult(new List<SteamApp>())
                : FetchModernAppListAsync(_steamApiKey, ct);

            await Task.WhenAll(legacyTask, modernTask).ConfigureAwait(false);

            _appListCache = MergeAppLists(legacyTask.Result, modernTask.Result);
            _cacheExpiry = DateTime.Now.Add(CacheDuration);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SearchService.GetAppList");
        }
        finally
        {
            _isPrefetching = false;
            AppListLock.Release();
        }
    }

    private static async Task<List<SteamApp>> FetchLegacyAppListAsync(CancellationToken ct)
    {
        var results = new List<SteamApp>();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(LegacyResourceName);
            if (stream == null) return results;

            using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("applist", out var applist)) return results;
            if (!applist.TryGetProperty("apps", out var apps)) return results;

            foreach (var app in apps.EnumerateArray())
            {
                var appId = app.TryGetProperty("appid", out var aidProp) ? aidProp.ToString() : string.Empty;
                var name = app.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? string.Empty : string.Empty;

                if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(name))
                    results.Add(new SteamApp(appId, name, name.ToLowerInvariant()));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SearchService.FetchLegacyAppList");
        }

        return results;
    }

    private static async Task<List<SteamApp>> FetchModernAppListAsync(string apiKey, CancellationToken ct)
    {
        var results = new List<SteamApp>();
        try
        {
            uint lastAppId = 0;
            while (true)
            {
                var url =
                    $"{SteamStoreApiUrl}?key={Uri.EscapeDataString(apiKey)}&include_games=true&include_dlc=true&include_software=true&include_videos=true&include_hardware=true&max_results={AppListPageSize}&last_appid={lastAppId}";

                var response = await HttpClientProvider.Default.GetStringAsync(url, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                if (!root.TryGetProperty("response", out var responseElem)) break;
                if (!responseElem.TryGetProperty("apps", out var apps)) break;

                var hasApps = false;
                foreach (var app in apps.EnumerateArray())
                {
                    hasApps = true;
                    var appId = app.TryGetProperty("appid", out var aidProp) ? aidProp.ToString() : string.Empty;
                    var name = app.TryGetProperty("name", out var nProp)
                        ? nProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(name))
                        results.Add(new SteamApp(appId, name, name.ToLowerInvariant()));
                }

                if (!hasApps) break;

                var haveMore = responseElem.TryGetProperty("have_more_results", out var hmProp) &&
                               hmProp.ValueKind == JsonValueKind.True;
                if (!haveMore) break;

                if (responseElem.TryGetProperty("last_appid", out var laProp) &&
                    laProp.TryGetUInt32(out var newLastAppId))
                    lastAppId = newLastAppId;
                else
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SearchService.FetchModernAppList");
        }

        return results;
    }

    private static List<SteamApp> MergeAppLists(List<SteamApp> legacy, List<SteamApp> modern)
    {
        var merged = new Dictionary<string, SteamApp>(legacy.Count + modern.Count);

        foreach (var app in legacy)
            merged[app.AppId] = app;

        foreach (var app in modern)
            merged[app.AppId] = app;

        try
        {
            return [.. merged.Values.OrderBy(a => uint.TryParse(a.AppId, out var id) ? id : uint.MaxValue)];
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SearchService.MergeAppLists");
            return [.. merged.Values];
        }
    }

    public static Task PrefetchAsync(Config config)
    {
        if (_isPrefetching || (_appListCache != null && DateTime.Now < _cacheExpiry))
            return Task.CompletedTask;

        if (string.IsNullOrWhiteSpace(_steamApiKey) && !config.PrefetchAppList)
            return Task.CompletedTask;

        StartAppListFetch();
        return Task.CompletedTask;
    }

    public static async Task<List<Game>> SearchAsync(string query, int maxResults = 200, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        try
        {
            ct.ThrowIfCancellationRequested();
            var queryLower = query.ToLowerInvariant();
            if (uint.TryParse(query, out _))
            {
                var detailsMap = await FetchGameDetailsBatchAsync([query], ct).ConfigureAwait(false);
                if (detailsMap.TryGetValue(query, out var details) &&
                    !string.IsNullOrEmpty(details.Name) && details.Name != $"App {query}")
                    return [new Game { AppId = query, Name = details.Name, Type = details.Type, ParentAppId = details.ParentAppId }];
            }

            var appList = await GetBestAvailableAppListAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return appList
                .Select(app => (app, score: CalculateScore(app.NameLower, queryLower)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.app.Name.Length)
                .Take(maxResults)
                .Select(x => new Game { AppId = x.app.AppId, Name = x.app.Name, Type = "Game" })
                .ToList();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) throw;
            Logger.Error(ex, "SearchService.SearchAsync");
            return [];
        }
    }

    private static int CalculateScore(string nameLower, string query)
    {
        if (string.IsNullOrEmpty(nameLower))
            return 0;

        var score = 0;

        if (nameLower == query)
            return 10000;

        if (nameLower.StartsWith(query))
            score += 5000;

        var nameWords = nameLower.Split([' ', '-', ':', '_', '™', '®'], StringSplitOptions.RemoveEmptyEntries);
        var queryWords = query.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        if (nameWords.Length > 0 && nameWords[0].StartsWith(query))
            score += 3000;

        var matchingWords = queryWords.Count(queryWord => nameWords.Any(w => w.StartsWith(queryWord)));

        if (queryWords.Length > 1 && matchingWords == queryWords.Length)
            score += 2000;

        if (nameLower.Contains(query))
            score += 1000;

        var lengthPenalty = Math.Min(900, Math.Max(0, (nameLower.Length - query.Length) * 20));
        score -= lengthPenalty;

        if (HasWordBoundaryMatch(nameLower, query))
            score += 500;

        if (ContainsAllCharsInOrder(nameLower, query))
            score += 100;

        return Math.Max(0, score);
    }

    private static bool HasWordBoundaryMatch(string name, string query)
    {
        var words = name.Split([' ', '-', ':', '_'], StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => w.StartsWith(query));
    }

    private static bool ContainsAllCharsInOrder(string text, string chars)
    {
        var charIndex = 0;
        foreach (var c in text)
            if (charIndex < chars.Length && c == chars[charIndex])
                charIndex++;
        return charIndex == chars.Length;
    }

    private static void EvictExpiredDetails()
    {
        if (DetailsCache.Count > 10000)
        {
            var keys = DetailsCache.Keys.Take(5000).ToList();
            foreach (var key in keys)
                DetailsCache.TryRemove(key, out _);
        }
    }

    internal static async Task<Dictionary<string, GameDetails>> FetchGameDetailsBatchAsync(List<string> appIds, CancellationToken ct = default)
    {
        EvictExpiredDetails();
        var results = new Dictionary<string, GameDetails>();
        var uncachedAppIds = new List<string>();

        foreach (var appId in appIds)
        {
            if (DetailsCache.TryGetValue(appId, out var memDetails))
            {
                results[appId] = memDetails;
                continue;
            }

            var key = $"details:{appId}";
            if (SteamApiCache.Cache.TryGetValue(key, out var entry) &&
                DateTime.Now < entry.Expiry &&
                entry.Data is GameDetails cached)
            {
                results[appId] = cached;
                DetailsCache.TryAdd(appId, cached);
            }
            else
            {
                uncachedAppIds.Add(appId);
            }
        }

        if (uncachedAppIds.Count == 0)
            return results;

        var batches = uncachedAppIds.Chunk(BatchSize).ToList();

        foreach (var batch in batches)
            try
            {
                var validAppIds = batch.Where(id => uint.TryParse(id, out _)).ToList();
                if (validAppIds.Count == 0) continue;

                var batchResults =
                    await SteamService.Instance.GetAppInfoBatchAsync(validAppIds.Select(uint.Parse).ToList(), ct)
                        .ConfigureAwait(false);

                foreach (var (appId, details) in batchResults)
                {
                    var appIdStr = appId.ToString();
                    results[appIdStr] = details;

                    var key = $"details:{appIdStr}";
                    SteamApiCache.Cache[key] = new CacheEntry<object>
                    {
                        Expiry = DateTime.Now.Add(TimeSpan.FromMinutes(30)),
                        Data = details
                    };
                    DetailsCache.TryAdd(appIdStr, details);
                }

                var stillMissing = validAppIds.Where(id => !results.ContainsKey(id)).ToList();
                if (stillMissing.Count > 0)
                {
                    var storeResults = await FetchGameDetailsFromStoreApiAsync(stillMissing, ct).ConfigureAwait(false);
                    foreach (var (appId, details) in storeResults)
                    {
                        results[appId] = details;

                        var key = $"details:{appId}";
                        SteamApiCache.Cache[key] = new CacheEntry<object>
                        {
                            Expiry = DateTime.Now.Add(TimeSpan.FromMinutes(30)),
                            Data = details
                        };
                        DetailsCache.TryAdd(appId, details);
                    }
                }

                foreach (var appIdStr in validAppIds.Where(id => !results.ContainsKey(id)))
                {
                    var fallbackDetails = new GameDetails(appIdStr, "Game", $"App {appIdStr}");
                    results[appIdStr] = fallbackDetails;
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                Logger.Error(ex, "SearchService.FetchBatch");
                foreach (var appIdStr in batch)
                    if (!results.ContainsKey(appIdStr))
                        results[appIdStr] = new GameDetails(appIdStr, "Game", $"App {appIdStr}");
            }

        return results;
    }

    private static async Task<Dictionary<string, GameDetails>> FetchGameDetailsFromStoreApiAsync(
        IReadOnlyList<string> appIds, CancellationToken ct = default)
    {
        var results = new Dictionary<string, GameDetails>();

        if (appIds.Count > StoreApiMaxPerCall)
            Logger.Debug(
                $"SearchService.FetchGameDetailsFromStoreApi: {appIds.Count} unresolved apps, only trying the first {StoreApiMaxPerCall} to avoid rate limiting");

        foreach (var appId in appIds.Take(StoreApiMaxPerCall))
        {
            var details = await FetchSingleAppDetailsAsync(appId, ct).ConfigureAwait(false);
            if (details != null)
                results[appId] = details;
        }

        return results;
    }

    public static async Task<GameDetails?> FetchSingleAppDetailsAsync(string appId, CancellationToken ct = default)
    {
        await StoreApiThrottle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _storeApiAvailableAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            using var response = await HttpClientProvider.Default.GetAsync(url, cts.Token).ConfigureAwait(false);

            _storeApiAvailableAt = DateTime.UtcNow.Add(StoreApiMinInterval);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Logger.Warn("SearchService.FetchSingleAppDetails: rate limited, backing off");
                _storeApiAvailableAt = DateTime.UtcNow.Add(StoreApiRateLimitBackoff);
                return null;
            }

            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty(appId, out var appElement))
                return null;
            if (!appElement.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                return null;
            if (!appElement.TryGetProperty("data", out var data))
                return null;

            var name = data.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var type = data.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            return string.IsNullOrWhiteSpace(name)
                ? null
                : new GameDetails(appId, SteamService.MapSteamTypeToDisplayType(type ?? "game"), name,
                    ParentAppId: data.TryGetProperty("fullgame", out var parent) && parent.TryGetProperty("appid", out var parentId) ? parentId.ToString() : null,
                    HeaderImage: data.TryGetProperty("header_image", out var header) ? header.GetString() : null,
                    ListOfDlc: data.TryGetProperty("dlc", out var dlc) ? dlc.EnumerateArray().Select(x => x.ToString()).ToList() : []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.Error(ex, $"SearchService.FetchSingleAppDetails: appId={appId}");
            return null;
        }
        finally
        {
            StoreApiThrottle.Release();
        }
    }

    public static async Task PopulateGameDetailsAsync(Game game, CancellationToken ct = default)
    {
        var detailsMap = await FetchGameDetailsBatchAsync([game.AppId], ct).ConfigureAwait(false);
        if (detailsMap.TryGetValue(game.AppId, out var details))
        {
            if (details.Name != $"App {game.AppId}")
            {
                game.Name = details.Name;
                game.Type = details.Type;
            }

            if (uint.TryParse(details.ParentAppId, out var parentId) && parentId > 0 && details.ParentAppId != game.AppId)
            {
                var parent = (await FetchGameDetailsBatchAsync([details.ParentAppId], ct).ConfigureAwait(false)).GetValueOrDefault(details.ParentAppId);
                if (parent != null && parent.Name != $"App {parentId}") game.ParentName = parent.Name;
                game.ParentAppId = details.ParentAppId;
            }

            var iconPath = await IconCacheService.CacheIconForGameAsync(details).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(iconPath))
                game.IconUrl = iconPath;
        }
    }

    public static async Task FetchIconUrlAsync(Game game, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(game.IconUrl) && !game.IconUrl.StartsWith("http"))
            return;

        await PopulateGameDetailsAsync(game, ct).ConfigureAwait(false);
    }

    public static async Task FetchIconUrlsAsync(List<Game> games, Action? onTypesLoaded = null,
        CancellationToken ct = default)
    {
        var appIds = games.Select(g => g.AppId).Distinct().ToList();
        var detailsMap = await FetchGameDetailsBatchAsync(appIds, ct).ConfigureAwait(false);

        foreach (var game in games)
            if (detailsMap.TryGetValue(game.AppId, out var details))
            {
                if (!string.IsNullOrEmpty(details.Name) && details.Name != $"App {game.AppId}")
                    game.Name = details.Name;
                game.Type = details.Type;
                if (!string.IsNullOrWhiteSpace(details.ParentAppId) && details.ParentAppId != game.AppId)
                    game.ParentAppId = details.ParentAppId;
            }

        onTypesLoaded?.Invoke();

        var semaphore = new SemaphoreSlim(8);
        var tasks = games.Select(async game =>
        {
            if (detailsMap.TryGetValue(game.AppId, out var details))
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var iconPath = await IconCacheService.CacheIconForGameAsync(details).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(iconPath))
                        game.IconUrl = iconPath;
                }
                finally
                {
                    semaphore.Release();
                }
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, string>> FetchStoreBrowseItemsAsync(
        List<string> appIds, CancellationToken ct = default)
    {
        var results = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(_steamApiKey)) return results;

        try
        {
            var ids = appIds
                .Where(id => uint.TryParse(id, out _))
                .Select(id => new { appid = uint.Parse(id) })
                .ToArray();

            var inputJson = JsonSerializer.Serialize(new
            {
                ids = ids.Select(x => new { x.appid }).ToArray(),
                context = new { language = "english", country_code = "US" },
                data_request = new { include_basic_info = true }
            });

            var url =
                $"https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?key={Uri.EscapeDataString(_steamApiKey)}&input_json={Uri.EscapeDataString(inputJson)}";
            var response = await HttpClientProvider.Default.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);

            if (!doc.RootElement.TryGetProperty("response", out var resp)) return results;
            if (!resp.TryGetProperty("store_items", out var items)) return results;

            foreach (var item in items.EnumerateArray())
            {
                var appId = item.TryGetProperty("appid", out var aidProp) ? aidProp.GetUInt32().ToString() : null;
                var name = item.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;

                if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(name))
                    results[appId] = name;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SearchService.FetchStoreBrowseItems");
        }

        return results;
    }

    public static async Task<Dictionary<string, string>> ResolveAppNamesAsync(List<string> appIds)
    {
        var results = new Dictionary<string, string>();
        var unresolved = new List<string>();

        foreach (var id in appIds)
        {
            var name = LookupAppName(id);
            if (name != null)
                results[id] = name;
            else
                unresolved.Add(id);
        }

        if (unresolved.Count > 0)
        {
            var browseResults = await FetchStoreBrowseItemsAsync(unresolved).ConfigureAwait(false);
            foreach (var (id, name) in browseResults)
                results[id] = name;
        }

        return results;
    }

    private record SteamApp(string AppId, string Name, string NameLower);
}
