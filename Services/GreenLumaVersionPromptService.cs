using GreenLuma_Manager.Dialogs;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Services;

public static class GreenLumaVersionPromptService
{
    public static void EnsureConfirmed(Config? config) => _ = TryEnsureConfirmed(config);

    public static bool TryEnsureConfirmed(Config? config)
    {
        if (config == null) return false;
        if (config.UnlockMethod == UnlockMethod.CreamInstaller) return true;
        if (config.GreenLumaVersionPromptShown) return true;

        var greenLumaPath = config.GreenLumaPath.Trim();
        if (string.IsNullOrWhiteSpace(greenLumaPath))
            return true;

        var detected = GreenLumaService.DetectVersion(greenLumaPath);
        if (detected == null || !string.Equals(detected, GreenLumaService.LegacyVersion, StringComparison.Ordinal))
            return true;

        var chosen = GreenLumaVersionDialog.Show(detected);
        if (chosen == null) return false;

        config.GreenLumaVersionOverride = chosen;
        config.GreenLumaVersionPromptShown = true;
        ConfigService.Save(config);
        return true;
    }
}
