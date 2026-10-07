using System.IO;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager.Controllers;

public class GreenLumaLauncher
{
    public bool ValidatePaths(Config? config)
    {
        if (config?.UnlockMethod == UnlockMethod.CreamInstaller)
            return CreamInstallerService.HasValidSteamPath(config.SteamPath);
        return config != null
               && !string.IsNullOrWhiteSpace(config.GreenLumaPath)
               && Directory.Exists(config.GreenLumaPath);
    }

    public bool IsAppListGenerated(Config config)
    {
        return GreenLumaService.IsAppListGenerated(config);
    }

    public async Task<bool> LaunchAsync(Config config)
    {
        if (config.UnlockMethod == UnlockMethod.CreamInstaller)
        {
            SteamRestartService.Launch(config.SteamPath, config.StartSteamMinimized);
            return true;
        }
        return await GreenLumaService.LaunchGreenLumaAsync(config);
    }
}
