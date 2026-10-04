using System.Text.RegularExpressions;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Services;

public partial class UpdateService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/3vil3vo/GreenLuma-Manager/releases/latest";
    private static readonly string[] RcSeparator = ["-rc"];

    private static string CurrentVersion => MainWindow.UpstreamVersion;

    [GeneratedRegex("\"tag_name\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex TagNameRegex();

    [GeneratedRegex("\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex BrowserDownloadUrlRegex();

    [GeneratedRegex("\"body\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex BodyRegex();

    public static async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            var response = await HttpClientProvider.GitHub.GetStringAsync(GitHubApiUrl).ConfigureAwait(false);
            return ParseUpdateInfo(response);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "UpdateService.CheckForUpdates");
            return null;
        }
    }

    private static UpdateInfo? ParseUpdateInfo(string jsonResponse)
    {
        var tagMatch = TagNameRegex().Match(jsonResponse);
        var downloadMatch = BrowserDownloadUrlRegex().Match(jsonResponse);
        var bodyMatch = BodyRegex().Match(jsonResponse);

        if (!tagMatch.Success)
            return null;

        var latestTag = tagMatch.Groups[1].Value;
        var downloadUrl = downloadMatch.Success ? downloadMatch.Groups[1].Value : string.Empty;
        var releaseNotes = bodyMatch.Success
            ? bodyMatch.Groups[1].Value.Replace("\\n", "\n").Replace("\\r", "\r")
            : string.Empty;

        var currentNormalized = NormalizeVersion(CurrentVersion);
        var latestNormalized = NormalizeVersion(latestTag);

        return new UpdateInfo
        {
            CurrentVersion = CurrentVersion,
            LatestVersion = FormatDisplayVersion(latestTag),
            LatestVersionTag = latestTag,
            UpdateAvailable =
                string.Compare(latestNormalized, currentNormalized, StringComparison.OrdinalIgnoreCase) > 0,
            DownloadUrl = downloadUrl,
            ReleaseNotes = releaseNotes
        };
    }

    private static string NormalizeVersion(string version)
    {
        var cleaned = version.ToLowerInvariant().Trim().Replace("v", "");

        if (string.IsNullOrEmpty(cleaned))
            return "00000.00000.00000.0000.0000";

        if (cleaned.StartsWith("rc") && !cleaned.Contains(".0.0"))
        {
            var rcPart = cleaned[2..];
            var rcParts = rcPart.Split('.');

            var rcMajor = ParseVersionPart(rcParts, 0);
            var rcMinor = rcParts.Length > 1 ? ParseVersionPart(rcParts, 1) : 0;

            return $"00001.00000.00000.{rcMajor:D4}.{rcMinor:D4}";
        }

        var parts = cleaned.Split('-', 2);
        var baseVersion = parts[0];

        var versionParts = baseVersion.Split('.');
        var major = ParseVersionPart(versionParts, 0);
        var minor = ParseVersionPart(versionParts, 1);
        var patch = ParseVersionPart(versionParts, 2);

        if (parts.Length == 1) return $"{major:D5}.{minor:D5}.{patch:D5}.9999.9999";

        var prerelease = parts[1];

        if (prerelease.StartsWith("rc"))
        {
            var rcPart = prerelease[2..];
            var rcParts = rcPart.Split('.');

            var rcMajor = ParseVersionPart(rcParts, 0);
            var rcMinor = rcParts.Length > 1 ? ParseVersionPart(rcParts, 1) : 0;

            return $"{major:D5}.{minor:D5}.{patch:D5}.{rcMajor:D4}.{rcMinor:D4}";
        }

        return $"{major:D5}.{minor:D5}.{patch:D5}.0000.0000";
    }

    private static int ParseVersionPart(string[] parts, int index)
    {
        if (index >= parts.Length)
            return 0;

        if (int.TryParse(parts[index], out var result))
            return result;

        return 0;
    }

    private static string FormatDisplayVersion(string version)
    {
        var cleaned = version.ToLowerInvariant().Replace("v", "");

        if (cleaned.Contains("-rc"))
        {
            var parts = cleaned.Split(RcSeparator, StringSplitOptions.None);
            if (parts.Length > 1) return "RC" + parts[1].ToUpperInvariant();
        }

        if (cleaned.StartsWith("rc")) return cleaned.ToUpperInvariant();

        return cleaned;
    }

    public static Task<bool> PerformAutoUpdateAsync(string downloadUrl) => Task.FromResult(false);
}
