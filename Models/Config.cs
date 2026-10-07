using System.Runtime.Serialization;

namespace GreenLuma_Manager.Models;

public enum UnlockMethod { GreenLuma, CreamInstaller }
public enum SteamUnlocker { SmokeAPI, CreamAPI }

[DataContract]
public class Config
{
    [DataMember] public UnlockMethod UnlockMethod { get; set; }
    [DataMember] public SteamUnlocker SteamUnlocker { get; set; }
    [DataMember] public bool UnlockerProxy { get; set; }
    [DataMember] public string UnlockerProxyName { get; set; } = "winmm";
    [DataMember] public bool CreamExtraProtection { get; set; }

    [DataMember] public string SteamPath { get; set; } = string.Empty;

    [DataMember] public string GreenLumaPath { get; set; } = string.Empty;

    [DataMember] public bool NoHook { get; set; } = true;

    [DataMember] public bool DisableUpdateCheck { get; set; }

    [DataMember] public bool AutoUpdate { get; set; }

    [DataMember] public string LastProfile { get; set; } = "default";

    [DataMember] public bool CheckUpdate { get; set; } = true;

    [DataMember] public bool ReplaceSteamAutostart { get; set; }

    [DataMember] public bool PrefetchAppList { get; set; }

    [DataMember] public bool StartSteamMinimized { get; set; }

    [DataMember] public bool DisableGreenLumaVersionNotice { get; set; }

    [DataMember] public bool GreenLumaVersionPromptShown { get; set; }

    [DataMember] public string GreenLumaVersionOverride { get; set; } = string.Empty;

    [DataMember] public bool CheckGreenLumaUpdates { get; set; }

    [DataMember] public bool GreenLumaUpdateCheckAutoDetectDone { get; set; }

    [DataMember] public int GreenLumaUpdateCheckFailedAttempts { get; set; }

    [DataMember] public bool FirstRun { get; set; } = true;

    [DataMember] public string SteamApiKey { get; set; } = string.Empty;

    [DataMember] public double WindowWidth { get; set; }

    [DataMember] public double WindowHeight { get; set; }
}
