using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GreenLuma_Manager.Services;

public static class SteamRestartService
{
    private static readonly SemaphoreSlim RestartLock = new(1, 1);

    public static async Task RestartAsync(string steamDirectory, bool minimized = false)
    {
        if (App.IsPreview) throw new InvalidOperationException("This operation is disabled in preview mode.");
        if (string.IsNullOrWhiteSpace(steamDirectory)) throw new ArgumentException("Set the Steam directory in Settings → General.", nameof(steamDirectory));
        var directory = Path.GetFullPath(steamDirectory);
        var executable = Path.Combine(directory, "Steam.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Steam executable not found at the configured path.", executable);
        if (!await RestartLock.WaitAsync(0)) throw new InvalidOperationException("Steam is already restarting.");
        try
        {
            using (var shutdown = Process.Start(new ProcessStartInfo(executable, "-shutdown")
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true
            }) ?? throw new IOException("Could not request Steam shutdown."))
            {
                try { await shutdown.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { shutdown.Kill(); await shutdown.WaitForExitAsync(); }
            }

            if (!await WaitForClientsAsync(directory, TimeSpan.FromSeconds(10)))
            {
                foreach (var client in FindClients(directory))
                    using (client)
                        if (!client.HasExited) client.Kill(entireProcessTree: true);
                if (!await WaitForClientsAsync(directory, TimeSpan.FromSeconds(3)))
                    throw new IOException("Steam did not exit. It has not been reopened.");
            }

            using var launched = Process.Start(new ProcessStartInfo(executable, minimized ? "-silent" : "")
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true
            }) ?? throw new IOException("Could not start Steam.");
        }
        finally { RestartLock.Release(); }
    }

    private static List<Process> FindClients(string directory)
    {
        var clients = new List<Process>();
        try
        {
            foreach (var name in new[] { "steam", "steamwebhelper" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = ExecutablePath(process);
                    if (path != null && Path.GetFullPath(path).StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        clients.Add(process);
                    else process.Dispose();
                }
                catch (InvalidOperationException) { process.Dispose(); }
                catch { process.Dispose(); throw; }
            }
            return clients;
        }
        catch { foreach (var client in clients) client.Dispose(); throw; }
    }

    private static string? ExecutablePath(Process process)
    {
        using var handle = OpenProcess(0x1000, false, process.Id);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 87 || process.HasExited) return null;
            throw new Win32Exception(error);
        }
        var path = new StringBuilder(32768);
        var size = path.Capacity;
        if (QueryFullProcessImageName(handle, 0, path, ref size)) return path.ToString();
        var queryError = Marshal.GetLastWin32Error();
        if (process.HasExited) return null;
        throw new Win32Exception(queryError);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

    private static async Task<bool> WaitForClientsAsync(string directory, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var clients = FindClients(directory);
            var running = clients.Count > 0;
            foreach (var client in clients) client.Dispose();
            if (!running) return true;
            await Task.Delay(100);
        } while (timer.Elapsed < timeout);
        return false;
    }
}
