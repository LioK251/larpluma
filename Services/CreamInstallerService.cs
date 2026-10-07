using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Utilities;
using SteamKit2;

namespace GreenLuma_Manager.Services;

// Steam-only adaptation of CreamInstaller v5.2.0.0. See Assets/Unlockers/NOTICE.md.
internal static class CreamInstallerService
{
    private const string StateDirectory = ".larpluma-unlocker";
    private static readonly string[] Proxies = ["winmm", "winhttp", "version"];
    private static readonly HashSet<string> ManagedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam_api.dll", "steam_api64.dll", "steam_api_o.dll", "steam_api64_o.dll",
        "cream_api.ini", "SmokeAPI.config.json", "winmm.dll", "winhttp.dll", "version.dll"
    };
    // ponytail: one operation at a time; use per-game locks if parallel installation is needed.
    private static readonly SemaphoreSlim OperationLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Dictionary<(SteamUnlocker, bool), (string Hash, byte[] Data)> Resources = LoadResources();

    internal sealed record InstalledGame(string AppId, string Name, string Root);
    internal sealed record GamePlan(InstalledGame Game, Dictionary<string, string> Dlcs,
        Dictionary<string, byte[]?> Files, Dictionary<string, string?> Expected, bool Recover);
    internal sealed record Plan(bool Uninstall, SteamUnlocker Engine, List<GamePlan> Games, List<string> Issues);
    internal sealed record Result(List<string> Completed, List<string> Failed, bool Canceled);
    internal sealed class Receipt
    {
        public string AppId { get; set; } = "";
        public SteamUnlocker Engine { get; set; }
        public List<FileRecord> Files { get; set; } = [];
    }
    internal sealed class FileRecord
    {
        public string Path { get; set; } = "";
        public int Slot { get; set; }
        public string? OriginalHash { get; set; }
        public string? InstalledHash { get; set; }
    }
    private sealed class Journal
    {
        public Receipt Before { get; set; } = new();
        public List<FileRecord> Changes { get; set; } = [];
    }

    internal static bool HasValidSteamPath(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "Steam.exe"));

    internal static async Task<Plan> PrepareAsync(Config config, Profile profile, bool uninstall, CancellationToken ct)
    {
        ValidateOptions(config);
        var games = profile.Games.Select(g => new Game
        {
            AppId = g.AppId, Name = g.Name, Type = g.Type, ParentAppId = g.ParentAppId, ParentName = g.ParentName
        }).ToList();
        var unresolved = games.Where(g => !IsType(g, "Game") && !IsType(g, "Soundtrack") &&
            (!IsType(g, "DLC") || !ValidId(g.ParentAppId))).Select(g => g.AppId).Where(ValidId).Distinct().ToList();
        if (unresolved.Count > 0)
        {
            var details = await SearchService.FetchGameDetailsBatchAsync(unresolved, ct).ConfigureAwait(false);
            foreach (var game in games)
                if (details.TryGetValue(game.AppId, out var detail))
                {
                    game.Type = detail.Type; game.ParentAppId = detail.ParentAppId;
                    if (!string.IsNullOrWhiteSpace(detail.Name)) game.Name = detail.Name;
                }
        }
        return await Task.Run(() => PrepareResolved(config, games, uninstall, ct), ct).ConfigureAwait(false);
    }

    internal static Plan PrepareResolved(Config config, IEnumerable<Game> entries, bool uninstall, CancellationToken ct = default)
    {
        ValidateOptions(config);
        var installed = DiscoverGames(config.SteamPath);
        var issues = new List<string>();
        var grouped = new Dictionary<string, Dictionary<string, string>>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!ValidId(entry.AppId)) { issues.Add($"{entry.Name}: invalid App ID."); continue; }
            if (IsType(entry, "DLC") && ValidId(entry.ParentAppId) && entry.ParentAppId != entry.AppId)
            {
                if (!grouped.TryGetValue(entry.ParentAppId!, out var dlcs)) grouped[entry.ParentAppId!] = dlcs = [];
                dlcs[entry.AppId] = entry.Name;
            }
            else if (IsType(entry, "Game")) grouped.TryAdd(entry.AppId, []);
            else issues.Add($"{entry.Name}: skipped {entry.Type}; only games and resolved DLCs are supported.");
        }
        var plan = new Plan(uninstall, config.SteamUnlocker, [], issues);
        foreach (var (appId, dlcs) in grouped)
        {
            ct.ThrowIfCancellationRequested();
            if (!installed.TryGetValue(appId, out var game)) { issues.Add($"App {appId}: not installed in a configured Steam library."); continue; }
            if (!uninstall && dlcs.Count == 0 && !File.Exists(Path.Combine(game.Root, StateDirectory, "receipt.json")))
            { issues.Add($"{game.Name}: no DLC selected in this profile."); continue; }
            try { plan.Games.Add(PrepareGame(config, game, dlcs, uninstall)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or BadImageFormatException or JsonException)
            { issues.Add($"{game.Name}: {ex.Message}"); }
        }
        return plan;
    }

    internal static Dictionary<string, InstalledGame> DiscoverGames(string steamPath)
    {
        if (!HasValidSteamPath(steamPath)) throw new ArgumentException("Set a valid Steam directory in Settings → General.");
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(Path.Combine(steamPath, "steamapps")) };
        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            var vdf = ReadVdf(vdfPath);
            foreach (var child in vdf.Children.Where(c => uint.TryParse(c.Name, out _)))
            {
                var path = child["path"].Value ?? child.Value;
                if (!string.IsNullOrWhiteSpace(path)) libraries.Add(Path.GetFullPath(Path.Combine(path, "steamapps")));
            }
        }
        var games = new Dictionary<string, InstalledGame>();
        foreach (var library in libraries)
        {
            if (!Directory.Exists(library)) continue;
            EnsureNoLinks(library);
            foreach (var manifest in Directory.EnumerateFiles(library, "appmanifest_*.acf"))
            {
                EnsureNoLinks(manifest);
                var app = ReadVdf(manifest);
                var id = app["appid"].Value;
                var folder = app["installdir"].Value;
                if (!ValidId(id) || string.IsNullOrWhiteSpace(folder)) continue;
                var common = Path.GetFullPath(Path.Combine(library, "common"));
                var root = ContainedPath(common, folder);
                if (Directory.Exists(root)) games.TryAdd(id!, new(id!, app["name"].Value ?? $"App {id}", root));
            }
        }
        return games;
    }

    private static KeyValue ReadVdf(string path)
    {
        var value = new KeyValue();
        if (!value.ReadFileAsText(path)) throw new InvalidDataException($"Cannot read Steam manifest: {path}");
        return value;
    }

    private static GamePlan PrepareGame(Config config, InstalledGame game, Dictionary<string, string> dlcs, bool uninstall)
    {
        EnsureNoLinks(game.Root);
        var state = Path.Combine(game.Root, StateDirectory);
        EnsureNoLinks(state);
        if (File.Exists(Path.Combine(state, "pending.json")))
        {
            if (!uninstall) throw new IOException("An interrupted operation needs recovery. Use Uninstall DLC unlocker first.");
            var journal = LoadJournal(game);
            ValidateRecovery(game, journal);
            return new(game, dlcs, [], [], true);
        }
        var receipt = ReadReceipt(game);
        if (uninstall && receipt == null) throw new IOException("No Larpluma-managed installation to remove.");
        if (receipt != null) VerifyReceipt(game, receipt);
        var files = receipt?.Files.ToDictionary(f => f.Path, f => Original(game, f), StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        if (!uninstall)
        {
            var allFiles = Walk(game.Root).ToList();
            if (game.AppId == "218620" || allFiles.Any(p => p.Split(Path.DirectorySeparatorChar).Any(s =>
                    s.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) || s.Equals("BattlEye", StringComparison.OrdinalIgnoreCase))))
                throw new IOException("Blocked by CreamInstaller's protected-game exclusions.");
            foreach (var path in allFiles.Where(p => File.Exists(p)))
            {
                var name = Path.GetFileName(path);
                var relative = Path.GetRelativePath(game.Root, path);
                if (receipt?.Files.Any(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)) == true) continue;
                if (name.Equals("steam_api_o.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("steam_api64_o.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("cream_api.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("SmokeAPI.config.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SmokeAPI.json", StringComparison.OrdinalIgnoreCase) ||
                    (name.Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase)) &&
                    Resources.Values.Any(r => r.Hash == HashFile(path)))
                    throw new IOException($"Existing foreign unlocker files found: {relative}. Remove them with their original installer first.");
            }
            var configBytes = BuildConfig(config, game.AppId, dlcs);
            if (!config.UnlockerProxy)
            {
                var apis = allFiles.Where(p => File.Exists(p) && (Path.GetFileName(p).Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(p).Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase))).ToList();
                if (apis.Count == 0) throw new IOException("No Steam API DLLs found for standard installation.");
                foreach (var api in apis)
                {
                    var x64 = Path.GetFileName(api).Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase);
                    if (Is64Bit(api) != x64) throw new IOException($"Steam API architecture mismatch: {api}");
                    var relative = Path.GetRelativePath(game.Root, api);
                    var original = files.GetValueOrDefault(relative) ?? File.ReadAllBytes(api);
                    files[relative] = Resources[(config.SteamUnlocker, x64)].Data;
                    files[Path.GetRelativePath(game.Root, Path.Combine(Path.GetDirectoryName(api)!, x64 ? "steam_api64_o.dll" : "steam_api_o.dll"))] = original;
                    files[Path.GetRelativePath(game.Root, Path.Combine(Path.GetDirectoryName(api)!, ConfigName(config.SteamUnlocker)))] = configBytes;
                }
            }
            else
            {
                var directories = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var exe in allFiles.Where(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IncorrectExecutable(game.Root, p)))
                {
                    var x64 = Is64Bit(exe);
                    var directory = Path.GetDirectoryName(exe)!;
                    if (directories.TryGetValue(directory, out var previous) && previous != x64)
                        throw new IOException($"Mixed executable architectures in {directory}; use standard installation.");
                    directories[directory] = x64;
                }
                if (directories.Count == 0) throw new IOException("No supported game executables found for proxy installation.");
                foreach (var (directory, x64) in directories)
                {
                    var proxy = Path.GetRelativePath(game.Root, Path.Combine(directory, config.UnlockerProxyName + ".dll"));
                    if (File.Exists(Target(game.Root, proxy)) && receipt?.Files.Any(f => f.Path.Equals(proxy, StringComparison.OrdinalIgnoreCase)) != true)
                        throw new IOException($"A foreign proxy DLL already exists: {proxy}");
                    files[proxy] = Resources[(config.SteamUnlocker, x64)].Data;
                    files[Path.GetRelativePath(game.Root, Path.Combine(directory, ConfigName(config.SteamUnlocker)))] = configBytes;
                }
            }
        }
        foreach (var path in files.Keys) _ = Target(game.Root, path);
        var expected = files.Keys.ToDictionary(p => p, p => HashFile(Target(game.Root, p)), StringComparer.OrdinalIgnoreCase);
        // Include the receipt in review-time validation, so another instance cannot change the installation unnoticed.
        expected[StateDirectory + "/receipt.json"] = HashFile(Path.Combine(state, "receipt.json"));
        return new(game, dlcs, files, expected, false);
    }

    private static IEnumerable<string> Walk(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (Path.GetFileName(path).Equals(StateDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            yield return path;
            if (Directory.Exists(path)) foreach (var child in Walk(path)) yield return child;
        }
    }

    private static bool IncorrectExecutable(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).ToUpperInvariant();
        return new[] { "SETUP", "REDIST", "SUPPORT", "HELPER", "CEFPROCESS", "ZFGAMEBROWSER", "MONO", "PLUGINS", "MODDING", "BATTLEYE", "ANTICHEAT" }.Any(relative.Contains)
            || relative.Contains("CRASH") && (relative.Contains("PAD") || relative.Contains("REPORT"))
            || relative.Contains("MOD") && relative.Contains("MANAGER");
    }

    internal static bool Is64Bit(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        return reader.PEHeaders.CoffHeader.Machine switch
        {
            Machine.I386 => false, Machine.Amd64 => true,
            _ => throw new InvalidDataException($"Unsupported executable architecture: {path}")
        };
    }

    internal static byte[] BuildConfig(Config config, string appId, Dictionary<string, string> dlcs)
    {
        ValidateOptions(config);
        if (!ValidId(appId) || dlcs.Keys.Any(id => !ValidId(id))) throw new InvalidDataException("Invalid Steam App ID.");
        if (config.SteamUnlocker == SteamUnlocker.SmokeAPI)
            return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["$version"] = 4, ["logging"] = false, ["log_steam_http"] = false, ["default_app_status"] = "original",
                ["override_app_status"] = new Dictionary<string, string>(),
                ["override_dlc_status"] = dlcs.Keys.ToDictionary(id => id, _ => "unlocked"),
                ["auto_inject_inventory"] = false, ["extra_inventory_items"] = Array.Empty<string>(),
                ["extra_dlcs"] = new Dictionary<string, object> { [appId] = new { dlcs } }
            }, JsonOptions);
        var text = new StringBuilder("[steam]\n");
        text.AppendLine($"appid = {appId}");
        text.AppendLine("unlockall = false\norgapi = steam_api_o.dll\norgapi64 = steam_api64_o.dll");
        text.AppendLine($"extraprotection = {config.CreamExtraProtection.ToString().ToLowerInvariant()}");
        text.AppendLine("forceoffline = false\n\n[steam_misc]\ndisableuserinterface = false\n\n[dlc]");
        foreach (var (id, name) in dlcs.OrderBy(p => uint.Parse(p.Key, CultureInfo.InvariantCulture)))
            text.AppendLine($"{id} = {new string(name.Select(c => char.IsControl(c) || c is '[' or ']' or ';' ? ' ' : c).ToArray())}");
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    internal static async Task<Result> ExecuteAsync(Plan plan, IProgress<string>? progress, CancellationToken ct)
    {
        if (App.IsPreview) throw new InvalidOperationException("This operation is disabled in preview mode.");
        if (!await OperationLock.WaitAsync(0, ct)) throw new InvalidOperationException("Another unlocker operation is running.");
        try
        {
            var result = new Result([], [], false);
            foreach (var game in plan.Games)
            {
                if (ct.IsCancellationRequested) return result with { Canceled = true };
                progress?.Report($"{(plan.Uninstall ? "Removing" : "Installing")} {game.Game.Name}…");
                try { await Task.Run(() => ApplyGame(plan, game)).ConfigureAwait(false); result.Completed.Add(game.Game.Name); }
                catch (Exception ex) { Logger.Error(ex, "CreamInstaller.Apply"); result.Failed.Add($"{game.Game.Name}: {ex.Message}"); }
            }
            return result;
        }
        finally { OperationLock.Release(); }
    }

    // Called directly by the isolated filesystem checks; production uses ExecuteAsync's preview/concurrency guards.
    internal static void ApplyGame(Plan plan, GamePlan game)
    {
        var root = game.Game.Root;
        EnsureNoLinks(root);
        var state = Path.Combine(root, StateDirectory);
        EnsureNoLinks(state);
        EnsureNoLinks(Path.Combine(state, "operation.lock"));
        Directory.CreateDirectory(state);
        using (var operation = new FileStream(Path.Combine(state, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (game.Recover)
            {
                Recover(game.Game);
                var receipt = ReadReceipt(game.Game)!;
                VerifyReceipt(game.Game, receipt);
                var recoveryFiles = receipt.Files.ToDictionary(f => f.Path, f => Original(game.Game, f), StringComparer.OrdinalIgnoreCase);
                game = game with { Files = recoveryFiles, Expected = recoveryFiles.Keys.ToDictionary(p => p, p => HashFile(Target(root, p))), Recover = false };
            }
            foreach (var (relative, expected) in game.Expected)
            {
                var target = relative == StateDirectory + "/receipt.json" ? Path.Combine(state, "receipt.json") : Target(root, relative);
                EnsureNoLinks(target);
                if (HashFile(target) != expected) throw new IOException($"Files changed after review: {relative}. Prepare the operation again.");
            }
            if (File.Exists(Path.Combine(state, "pending.json"))) throw new IOException("An interrupted operation needs recovery first.");
            var before = ReadReceipt(game.Game) ?? new Receipt { AppId = game.Game.AppId, Engine = plan.Engine };
            VerifyReceipt(game.Game, before);
            Directory.CreateDirectory(Path.Combine(state, "originals"));
            foreach (var path in game.Files.Keys.Where(p => before.Files.All(f => !f.Path.Equals(p, StringComparison.OrdinalIgnoreCase))))
            {
                var slot = before.Files.Count == 0 ? 0 : before.Files.Max(f => f.Slot) + 1;
                var target = Target(root, path);
                var hash = HashFile(target);
                if (hash != null) File.Copy(target, Backup(state, "originals", slot), true);
                before.Files.Add(new() { Path = path, Slot = slot, OriginalHash = hash, InstalledHash = hash });
            }
            WriteJson(Path.Combine(state, "receipt.json"), before);
            Directory.CreateDirectory(Path.Combine(state, "transaction"));
            var changes = new List<FileRecord>();
            foreach (var (path, data) in game.Files)
            {
                var target = Target(root, path);
                var hash = HashFile(target);
                var slot = changes.Count;
                if (hash != null) File.Copy(target, Backup(state, "transaction", slot), true);
                changes.Add(new() { Path = path, Slot = slot, OriginalHash = hash, InstalledHash = data == null ? null : Hash(data) });
            }
            WriteJson(Path.Combine(state, "pending.json"), new Journal { Before = before, Changes = changes });
            try
            {
                foreach (var (path, data) in game.Files) WriteTarget(root, path, data);
                var after = JsonSerializer.Deserialize<Receipt>(JsonSerializer.Serialize(before))!;
                after.Engine = plan.Engine;
                foreach (var file in after.Files) file.InstalledHash = HashFile(Target(root, file.Path));
                WriteJson(Path.Combine(state, "receipt.json"), after);
                File.Delete(Path.Combine(state, "pending.json"));
            }
            catch (Exception ex)
            {
                try { Recover(game.Game); }
                catch (Exception recovery) { throw new IOException($"{ex.Message} Recovery failed: {recovery.Message}. Backups remain in {state}; use Uninstall to recover.", ex); }
                throw new IOException($"{ex.Message} Changes to this game were rolled back.", ex);
            }
            DeleteStateSubdirectory(state, "transaction");
            if (plan.Uninstall)
            {
                DeleteStateSubdirectory(state, "originals");
                File.Delete(Path.Combine(state, "receipt.json"));
            }
        }
        if (plan.Uninstall)
        {
            File.Delete(Path.Combine(state, "operation.lock"));
            if (!Directory.EnumerateFileSystemEntries(state).Any()) Directory.Delete(state);
        }
    }

    private static void Recover(InstalledGame game)
    {
        var state = Path.Combine(game.Root, StateDirectory);
        var journal = LoadJournal(game);
        ValidateRecovery(game, journal);
        foreach (var change in journal.Changes)
        {
            if (HashFile(Target(game.Root, change.Path)) == change.OriginalHash) continue;
            WriteTarget(game.Root, change.Path, change.OriginalHash == null ? null : File.ReadAllBytes(Backup(state, "transaction", change.Slot)));
        }
        WriteJson(Path.Combine(state, "receipt.json"), journal.Before);
        EnsureNoLinks(Path.Combine(state, "payload.tmp"));
        File.Delete(Path.Combine(state, "payload.tmp"));
        File.Delete(Path.Combine(state, "pending.json"));
        DeleteStateSubdirectory(state, "transaction");
    }

    private static Journal LoadJournal(InstalledGame game)
    {
        var state = Path.Combine(game.Root, StateDirectory);
        var journal = ReadJson<Journal>(Path.Combine(state, "pending.json"));
        ValidateRecords(game, journal.Before);
        ValidateRecords(game, new Receipt { AppId = game.AppId, Files = journal.Changes });
        return journal;
    }

    private static void ValidateRecovery(InstalledGame game, Journal journal)
    {
        var state = Path.Combine(game.Root, StateDirectory);
        foreach (var change in journal.Changes)
        {
            var current = HashFile(Target(game.Root, change.Path));
            if (current != change.OriginalHash && current != change.InstalledHash)
                throw new IOException($"Changed file prevents safe recovery: {change.Path}. Backups have been retained.");
            if (change.OriginalHash != null && HashFile(Backup(state, "transaction", change.Slot)) != change.OriginalHash)
                throw new IOException($"Recovery backup is missing or changed: {change.Path}");
        }
        foreach (var file in journal.Before.Files) _ = Original(game, file);
    }

    private static Receipt? ReadReceipt(InstalledGame game)
    {
        var path = Path.Combine(game.Root, StateDirectory, "receipt.json");
        if (!File.Exists(path))
        {
            var state = Path.GetDirectoryName(path)!;
            if (Directory.Exists(state) && Directory.EnumerateFileSystemEntries(state).Any(p => Path.GetFileName(p) != "operation.lock"))
                throw new IOException("Unrecognized Larpluma recovery data; preserve it and inspect it before installing.");
            return null;
        }
        var receipt = ReadJson<Receipt>(path);
        ValidateRecords(game, receipt);
        return receipt;
    }

    private static void ValidateRecords(InstalledGame game, Receipt receipt)
    {
        if (receipt == null || receipt.AppId != game.AppId || receipt.Files == null || receipt.Files.Count > 4096 || !Enum.IsDefined(receipt.Engine) ||
            receipt.Files.Any(f => f == null || string.IsNullOrWhiteSpace(f.Path)) ||
            receipt.Files.Select(f => Target(game.Root, f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != receipt.Files.Count ||
            receipt.Files.Select(f => f.Slot).Distinct().Count() != receipt.Files.Count)
            throw new InvalidDataException("Invalid unlocker restoration record.");
        foreach (var file in receipt.Files)
        {
            _ = Target(game.Root, file.Path);
            if (file.Slot < 0 || file.Slot > 8192 || !ValidHash(file.OriginalHash) || !ValidHash(file.InstalledHash))
                throw new InvalidDataException("Invalid unlocker backup record.");
        }
    }

    private static void VerifyReceipt(InstalledGame game, Receipt receipt)
    {
        foreach (var file in receipt.Files)
        {
            if (HashFile(Target(game.Root, file.Path)) != file.InstalledHash)
                throw new IOException($"Managed file changed (possibly a game update): {file.Path}. No files were overwritten; preserve the backups.");
            _ = Original(game, file);
        }
    }

    private static byte[]? Original(InstalledGame game, FileRecord file)
    {
        if (file.OriginalHash == null) return null;
        var path = Backup(Path.Combine(game.Root, StateDirectory), "originals", file.Slot);
        if (HashFile(path) != file.OriginalHash) throw new IOException($"Original backup missing or changed: {file.Path}");
        return File.ReadAllBytes(path);
    }

    private static string Backup(string state, string directory, int slot)
    {
        var path = Path.Combine(state, directory, slot.ToString(CultureInfo.InvariantCulture));
        EnsureNoLinks(path);
        return path;
    }

    private static void WriteTarget(string root, string relative, byte[]? data)
    {
        var path = Target(root, relative);
        if (data == null) { File.Delete(path); return; }
        var temp = Path.Combine(root, StateDirectory, "payload.tmp");
        EnsureNoLinks(temp);
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, true);
    }

    private static void DeleteStateSubdirectory(string state, string directory)
    {
        var path = Path.Combine(state, directory);
        EnsureNoLinks(path);
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFileSystemEntries(path))
        {
            EnsureNoLinks(file);
            if (Directory.Exists(file)) throw new IOException("Unexpected directory in unlocker backups.");
            File.Delete(file);
        }
        Directory.Delete(path);
    }

    private static T ReadJson<T>(string path)
    {
        EnsureNoLinks(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Restoration record is too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid restoration record.");
    }

    private static void WriteJson<T>(string path, T data)
    {
        EnsureNoLinks(path); EnsureNoLinks(path + ".tmp");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    }

    private static string Target(string root, string relative)
    {
        if (!ManagedNames.Contains(Path.GetFileName(relative))) throw new InvalidDataException("Unrecognized unlocker file in restoration record.");
        return ContainedPath(root, relative);
    }

    private static string ContainedPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split(['/', '\\']).Any(p => p is ".." or "." || p.Equals(StateDirectory, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Invalid relative game path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path escapes the game directory.");
        EnsureNoLinks(path);
        return path;
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Linked paths are not supported: {current}");
    }

    private static bool ValidId(string? id) => id != null && uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 && value.ToString(CultureInfo.InvariantCulture) == id;
    private static bool IsType(Game game, string type) => game.Type.Equals(type, StringComparison.OrdinalIgnoreCase);
    private static bool ValidHash(string? hash) => hash == null || hash.Length == 64 && hash.All(Uri.IsHexDigit);
    private static string? HashFile(string path) { using var stream = File.Exists(path) ? File.OpenRead(path) : null; return stream == null ? null : Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string ConfigName(SteamUnlocker engine) => engine == SteamUnlocker.SmokeAPI ? "SmokeAPI.config.json" : "cream_api.ini";

    private static void ValidateOptions(Config config)
    {
        if (!Enum.IsDefined(config.UnlockMethod) || !Enum.IsDefined(config.SteamUnlocker) || !Proxies.Contains(config.UnlockerProxyName))
            throw new ArgumentException("Invalid unlocker settings.");
    }

    private static Dictionary<(SteamUnlocker, bool), (string, byte[])> LoadResources()
    {
        string[] hashes = ["145AADFFFDE5140991995D76DCA8F2423E9D7F9DBB66BC21565CEE59746F027B", "3891CD70B8E06CAD474B9EA5B6D7C3D19B9948C41DF2495E3CF02D2A256498EF", "23E91E44DE1ADBD0544A578AB80E46C968F9394E5B419240034A55A59B131675", "D00A9B72E696CF5C88A5B4BBC4D7D308F9BAC51A8C4712D6B26CFD863938A25F"];
        var result = new Dictionary<(SteamUnlocker, bool), (string, byte[])>();
        foreach (var engine in new[] { SteamUnlocker.SmokeAPI, SteamUnlocker.CreamAPI })
        foreach (var x64 in new[] { false, true })
        {
            var name = $"GreenLuma_Manager.Assets.Unlockers.{engine}.{(x64 ? "steam_api64.dll" : "steam_api.dll")}";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidDataException($"Missing embedded unlocker: {name}");
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            var data = memory.ToArray(); var hash = Hash(data);
            if (hash != hashes[(int)engine * 2 + (x64 ? 1 : 0)]) throw new InvalidDataException("Embedded unlocker checksum mismatch.");
            result[(engine, x64)] = (hash, data);
        }
        return result;
    }
}
