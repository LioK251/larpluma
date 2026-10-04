using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

// Exercise real minimize/maximize commands without showing test windows on the user's desktop.
internal static class PrivateDesktop
{
    internal static void Run(string output)
    {
        var name = "LarplumaChecks-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(name, null, 0, 0, 0x10000000, 0);
        if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ProcessInfo process = default;
        try
        {
            var localHost = Path.Combine(Environment.CurrentDirectory, ".tools", "dotnet", "dotnet.exe");
            var host = File.Exists(localHost) ? localHost : "dotnet.exe";
            var command = new System.Text.StringBuilder($"\"{host}\" \"{Assembly.GetExecutingAssembly().Location}\" --native-window \"--output={output}\"");
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = name, Flags = 1, ShowWindow = 0 };
            if (!CreateProcess(null, command, 0, 0, false, 0x08000000, 0, Environment.CurrentDirectory, ref startup, out process))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(process.Thread); process.Thread = 0;
            if (WaitForSingleObject(process.Process, 30000) != 0)
            {
                TerminateProcess(process.Process, 1);
                throw new Exception("Private-desktop window checks timed out.");
            }
            GetExitCodeProcess(process.Process, out var exit);
            if (exit != 0) throw new Exception("Native window checks failed: " + File.ReadAllText(Path.Combine(output, "failure.txt")));
        }
        finally
        {
            if (process.Thread != 0) CloseHandle(process.Thread);
            if (process.Process != 0) CloseHandle(process.Process);
            CloseDesktop(desktop);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XCount, YCount, Fill, Flags;
        public short ShowWindow, ReservedLength;
        public nint ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDesktop(string name, string? device, nint mode, uint flags, uint access, nint security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string? application, System.Text.StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(nint process, out uint exit);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint exit);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
