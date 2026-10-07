using System.Diagnostics;
using System.IO;
using GreenLuma_Manager;
using GreenLuma_Manager.Services;

internal static class SteamRestartChecks
{
    internal static bool IsFixture => File.Exists(Path.Combine(AppContext.BaseDirectory, "fake-steam.fixture")) &&
        Process.GetCurrentProcess().ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase);

    internal static int RunFixture(string[] args)
    {
        var directory = AppContext.BaseDirectory;
        var stop = Path.Combine(directory, "stop");
        var events = Path.Combine(directory, "events.txt");
        if (args.Contains("-shutdown"))
        {
            File.AppendAllText(events, "shutdown\n");
            if (File.ReadAllText(Path.Combine(directory, "fake-steam.fixture")) != "ignore") File.WriteAllText(stop, "");
            return 0;
        }
        File.Delete(stop);
        File.AppendAllText(events, "start " + string.Join(' ', args) + "\n");
        while (!File.Exists(stop)) Thread.Sleep(25);
        File.AppendAllText(events, "exit\n");
        return 0;
    }

    internal static async Task Run(Action<bool, string> check, string artifacts)
    {
        var directory = Path.Combine(artifacts, "fake-steam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var executable = Path.Combine(directory, "Steam.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Larpluma.Checks.exe"), executable);
        File.WriteAllText(Path.Combine(directory, "fake-steam.fixture"), "graceful");
        var previousRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var previousPreview = App.IsPreview;
        var localHost = Path.Combine(Environment.CurrentDirectory, ".tools", "dotnet");
        if (Directory.Exists(localHost)) Environment.SetEnvironmentVariable("DOTNET_ROOT", localHost);
        var events = Path.Combine(directory, "events.txt");
        try
        {
            App.IsPreview = true;
            try { await SteamRestartService.RestartAsync(directory); throw new Exception("Preview restarted Steam"); }
            catch (InvalidOperationException) { check(!File.Exists(events), "Preview restart launches no processes"); }
            App.IsPreview = false;
            try { await SteamRestartService.RestartAsync(Path.Combine(directory, "missing")); throw new Exception("Missing Steam path accepted"); }
            catch (FileNotFoundException) { check(!File.Exists(events), "Missing executable fails before shutdown"); }

            using (var initial = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true })!)
            {
                await WaitFor(() => File.Exists(events), "Fixture startup");
                await SteamRestartService.RestartAsync(directory, true);
                await WaitFor(() => File.ReadAllText(events).Contains("start -silent"), "Minimized restart");
                check(initial.HasExited, "Steam closes before direct restart");
                var log = File.ReadAllLines(events);
                check(Array.IndexOf(log, "shutdown") < Array.IndexOf(log, "exit") && Array.IndexOf(log, "exit") < Array.IndexOf(log, "start -silent"), "Shutdown, exit, direct launch ordering");
                check(log.All(x => !x.Contains("inhibitbootstrap") && !x.Contains("DLLInjector")), "Restart uses plain Steam arguments");
            }

            File.WriteAllText(Path.Combine(directory, "fake-steam.fixture"), "ignore");
            var restart = SteamRestartService.RestartAsync(directory);
            try { await SteamRestartService.RestartAsync(directory); throw new Exception("Concurrent restart accepted"); }
            catch (InvalidOperationException) { check(true, "Concurrent restart is rejected"); }
            await restart;
            await WaitFor(() => File.ReadAllLines(events).Count(x => x.StartsWith("start ")) == 3, "Forced restart");
            check(File.ReadAllLines(events).Count(x => x == "shutdown") == 2, "Unresponsive client is terminated and restarted once");
            var cream = new GreenLuma_Manager.Models.Config
            {
                UnlockMethod = GreenLuma_Manager.Models.UnlockMethod.CreamInstaller,
                SteamPath = directory, GreenLumaPath = Path.Combine(directory, "missing-greenluma"), StartSteamMinimized = true
            };
            check(await new GreenLuma_Manager.Controllers.GreenLumaLauncher().LaunchAsync(cream), "Selected-method launcher starts plain Steam for CreamInstaller");
            await WaitFor(() => File.ReadAllLines(events).Count(x => x.StartsWith("start ")) == 4, "CreamInstaller Steam launch");
            check(File.ReadAllLines(events).Count(x => x == "shutdown") == 2 && File.ReadAllLines(events).Last(x => x.StartsWith("start ")) == "start -silent",
                "CreamInstaller launch honors minimized preference without shutdown or injection");
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "stop"), "");
            foreach (var process in Process.GetProcessesByName("steam"))
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } path && Path.GetDirectoryName(path)!.Equals(directory, StringComparison.OrdinalIgnoreCase))
                            if (!process.WaitForExit(1000)) { process.Kill(); process.WaitForExit(3000); }
                    }
                    catch (InvalidOperationException) { /* The stop signal may have already ended the fixture. */ }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                    { /* Unrelated protected Steam process; fixture processes received their own stop signal. */ }
                }
            App.IsPreview = previousPreview;
            Environment.SetEnvironmentVariable("DOTNET_ROOT", previousRoot);
        }
    }

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception(message + " timed out");
            await Task.Delay(25);
        }
    }
}
