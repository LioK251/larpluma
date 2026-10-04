using System.IO;

namespace GreenLuma_Manager.Services;

public static class AppPaths
{
    public static string Root => Environment.GetEnvironmentVariable("LARPLUMA_DATA_DIR") is { Length: > 0 } path
        ? Path.GetFullPath(path)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larpluma");

    public static string Backgrounds => Path.Combine(Root, "backgrounds");
    public static string Upstream => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GLM_Manager");
}
