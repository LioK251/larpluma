using System.Collections.Concurrent;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Services;

public static class GameDlcService
{
    private static readonly ConcurrentDictionary<uint, (DateTime Expiry, GameWithDlc Result)> Cache = new();

    public static async Task<GameWithDlc> GetAsync(uint id, CancellationToken ct = default, bool refresh = false)
    {
        ct.ThrowIfCancellationRequested();
        if (!refresh && Cache.TryGetValue(id, out var cached) && cached.Expiry > DateTime.UtcNow)
            return Copy(cached.Result);
        var result = await FetchAsync(id, ct, true);
        if (!result.Partial && result.BaseGame != null)
        {
            if (Cache.Count > 256) foreach (var key in Cache.Keys.Take(128)) Cache.TryRemove(key, out _);
            Cache[id] = (DateTime.UtcNow.AddMinutes(30), Copy(result));
        }
        return result;
    }

    private static async Task<GameWithDlc> FetchAsync(uint id, CancellationToken ct, bool followParent)
    {
        var storeTask = SearchService.FetchSingleAppDetailsAsync(id.ToString(), ct);
        var steamTask = SteamService.Instance.GetAppInfoBatchAsync([id], ct);
        await Task.WhenAll(storeTask, steamTask);
        ct.ThrowIfCancellationRequested();
        var store = await storeTask;
        var steam = (await steamTask).GetValueOrDefault(id);
        if (steam != null && (string.IsNullOrWhiteSpace(steam.Name) || steam.Name == $"App {id}")) steam = null;
        var details = steam ?? store;
        if (details == null || details.Name == $"App {id}")
        {
            var package = await SteamService.Instance.GetPackageAppIdsAsync(id).WaitAsync(TimeSpan.FromSeconds(12), ct);
            ct.ThrowIfCancellationRequested();
            if (package.Count > 0)
            {
                var packageDetails = await ResolveAsync(package.Select(x => x.ToString()).ToList(), ct);
                return new(new Game { AppId = id.ToString(), Name = $"Package {id}", Type = "Package" }, packageDetails,
                    packageDetails.Any(g => g.Name.StartsWith("App ")), $"{packageDetails.Count} apps in package {id}. Select entries to add.", true);
            }
            return new(null, [], true, "Could not resolve this App ID. Check your connection and retry, or explicitly add it as an unknown app.");
        }
        var parent = details.ParentAppId ?? store?.ParentAppId;
        if (followParent && details.Type == "DLC" && uint.TryParse(parent, out var parentId) && parentId > 0 && parentId != id)
            return await FetchAsync(parentId, ct, false);
        var ids = MergeIds(id.ToString(), steam?.ListOfDlc, store?.ListOfDlc);
        var dlcs = await ResolveAsync(ids, ct);
        var partial = store == null || steam == null || dlcs.Any(x => x.Name == $"App {x.AppId}");
        var status = partial
            ? $"{dlcs.Count} DLCs discovered · some Steam data could not be retrieved. Retry to refresh; unresolved entries remain visible by ID."
            : dlcs.Count == 0 ? "No DLCs listed by Steam." : $"{dlcs.Count} DLCs · select the game and DLCs you want to add.";
        return new(ToGame(details), dlcs, partial, status);
    }

    public static List<string> MergeIds(string baseId, params IEnumerable<string>?[] lists) => lists
        .Where(x => x != null).SelectMany(x => x!).Select(x => x.Trim())
        .Where(x => uint.TryParse(x, out var number) && number > 0).Select(x => uint.Parse(x).ToString())
        .Where(x => x != baseId).Distinct().OrderBy(uint.Parse).ToList();

    private static async Task<List<Game>> ResolveAsync(List<string> ids, CancellationToken ct)
    {
        var resolved = new Dictionary<uint, GameDetails>();
        foreach (var batch in ids.Chunk(150))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var pair in await SteamService.Instance.GetAppInfoBatchAsync(batch.Select(uint.Parse).ToList(), ct)) resolved[pair.Key] = pair.Value;
        }
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (resolved.TryGetValue(uint.Parse(id), out var known) && known.Name != $"App {id}") continue;
            var store = await SearchService.FetchSingleAppDetailsAsync(id, ct);
            if (store != null) resolved[uint.Parse(id)] = store;
        }
        return ids.Select(id => resolved.TryGetValue(uint.Parse(id), out var value) ? ToGame(value) :
            new Game { AppId = id, Name = $"App {id}", Type = "DLC" }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Game ToGame(GameDetails value) => new() { AppId = value.AppId, Name = value.Name, Type = value.Type };
    private static Game CloneGame(Game value) => new() { AppId = value.AppId, Name = value.Name, Type = value.Type, IconUrl = value.IconUrl };
    private static GameWithDlc Copy(GameWithDlc value) => value with { BaseGame = value.BaseGame == null ? null : CloneGame(value.BaseGame), Dlcs = value.Dlcs.Select(CloneGame).ToList() };
}
