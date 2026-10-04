using System.IO;
using System.Text.Json;
using System.Windows;
using GreenLuma_Manager.Dialogs;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager.Services;

public static class MigrationService
{
    public static void OfferImport()
    {
        var marker = Path.Combine(AppPaths.Root, "upstream-import.json");
        if (File.Exists(marker) || !Directory.Exists(AppPaths.Upstream)) return;
        var accept = CustomMessageBox.Show(
            "Import your GreenLuma Manager settings and profiles into Larpluma? Originals remain untouched. Plugins can be imported separately. Windows startup replacement stays off.",
            "Import existing data", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        try
        {
            if (accept) ImportFrom(AppPaths.Upstream);
            Directory.CreateDirectory(AppPaths.Root);
            AtomicFile.WriteAllText(marker, JsonSerializer.Serialize(new { Imported = accept }));
        }
        catch (Exception ex) { Logger.Error(ex, "Migration.Import"); CustomMessageBox.Show("Import failed. Your original data is unchanged. " + ex.Message, "Import failed", icon: MessageBoxImage.Error); }
    }

    public static int ImportFrom(string source)
    {
        var profiles = new List<Profile>();
        var folder = Path.Combine(source, "profiles");
        if (Directory.Exists(folder))
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                var profile = ProfileService.Import(file) ?? throw new InvalidDataException("Invalid profile: " + Path.GetFileName(file));
                if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 50 || profile.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.Name.Contains(".."))
                    throw new InvalidDataException("Invalid profile name.");
                foreach (var game in profile.Games) game.IconUrl = "";
                profiles.Add(profile);
            }
        var configFile = Path.Combine(source, "config.json");
        var config = File.Exists(configFile) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(configFile)) ?? throw new InvalidDataException("Invalid upstream configuration.") : new Config();
        config.ReplaceSteamAutostart = false;
        config.AutoUpdate = false;
        config.FirstRun = false;
        Directory.CreateDirectory(Path.Combine(AppPaths.Root, "profiles"));
        var count = 0;
        foreach (var profile in profiles)
        {
            var target = Path.Combine(AppPaths.Root, "profiles", profile.Name + ".json");
            if (File.Exists(target)) continue;
            AtomicFile.WriteAllText(target, JsonSerializer.Serialize(profile));
            count++;
        }
        AtomicFile.WriteAllText(Path.Combine(AppPaths.Root, "config.json"), JsonSerializer.Serialize(config));
        return count;
    }
}
