using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using GreenLuma_Manager.Controls;

internal static class WindowFrameChecks
{
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var node in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return node;
    }

    internal static void Validate(Window window, Action<bool, string> check)
    {
        var chrome = WindowChrome.GetWindowChrome(window);
        check(window.WindowStyle == WindowStyle.None && chrome is { CaptionHeight: 44 } && chrome.CornerRadius == new CornerRadius(0) && chrome.GlassFrameThickness == new Thickness(0), window.Title + ": custom square chrome");
        var bar = Descendants<TitleBar>(window).Single();
        check(bar.ActualHeight == 44, window.Title + ": single 44-pixel title bar");
        check(Descendants<Border>(window).All(x => x.CornerRadius == new CornerRadius(0)), window.Title + ": all rendered controls have square corners");
        var close = (Button)bar.FindName("BtnClose");
        var maximize = (Button)bar.FindName("BtnMaximize");
        check(close.IsVisible && maximize.IsVisible == (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip), window.Title + ": applicable window controls");
        var hwnd = new WindowInteropHelper(window).Handle;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            check(DwmGetWindowAttribute(hwnd, 33, out var preference, sizeof(int)) == 0 && preference == 1, window.Title + ": Windows 11 corner rounding is disabled");
        nint Hit(FrameworkElement target, Point point)
        {
            var screen = target.PointToScreen(point);
            var packed = ((int)screen.X & 0xffff) | (((int)screen.Y & 0xffff) << 16);
            return SendMessage(hwnd, 0x84, 0, (nint)packed);
        }
        check(Hit(bar, new Point(100, 22)) == 2, window.Title + ": blank caption delegates drag, double-click, and system menu to Windows");
        check(Hit(close, new Point(close.ActualWidth / 2, close.ActualHeight / 2)) == 1, window.Title + ": caption buttons receive client clicks");
        if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            check(Hit(window, new Point(1, window.ActualHeight / 2)) == 10, window.Title + ": edge resize hit test");
    }

    // All monitor geometry is checked while the window is hidden, then restored offscreen.
    internal static int ValidateMonitors(Window window, Action<bool, string> check)
    {
        var monitors = new List<MonitorInfo>();
        bool Collect(nint monitor, nint dc, ref NativeRect rect, nint data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) monitors.Add(info);
            return true;
        }
        EnumDisplayMonitors(0, 0, Collect, 0);
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var original);
        window.Hide();
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<MinMaxInfo>());
        try
        {
            foreach (var monitor in monitors)
            {
                SetWindowPos(hwnd, 0, monitor.Work.Left + 10, monitor.Work.Top + 10, 0, 0, 0x15); // NOACTIVATE | NOZORDER | NOSIZE
                Marshal.StructureToPtr(new MinMaxInfo(), memory, false);
                SendMessage(hwnd, 0x24, 0, memory);
                var bounds = Marshal.PtrToStructure<MinMaxInfo>(memory);
                check(bounds.MaxPosition.X == monitor.Work.Left - monitor.Monitor.Left && bounds.MaxPosition.Y == monitor.Work.Top - monitor.Monitor.Top && bounds.MaxSize.X == monitor.Work.Right - monitor.Work.Left && bounds.MaxSize.Y == monitor.Work.Bottom - monitor.Work.Top, "Maximized bounds respect each monitor's taskbar and origin");
                var dpi = VisualTreeHelper.GetDpi(window);
                check(bounds.MinTrackSize.X >= window.MinWidth * dpi.DpiScaleX && bounds.MinTrackSize.Y >= window.MinHeight * dpi.DpiScaleY, "Minimum resize bounds respect monitor DPI");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
            SetWindowPos(hwnd, 0, original.Left, original.Top, original.Right - original.Left, original.Bottom - original.Top, 0x14);
            window.Show();
        }
        return monitors.Count;
    }

    internal static void ValidateMaximizedWindow(Window window, Action<bool, string> check)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info); GetWindowRect(hwnd, out var actual);
        check(actual.Left == info.Work.Left && actual.Top == info.Work.Top && actual.Right == info.Work.Right && actual.Bottom == info.Work.Bottom, "Actual maximized window fits its monitor's work area");
    }

    internal static void DoubleClickCaption(Window window)
    {
        var point = window.PointToScreen(new Point(100, 22));
        var packed = ((int)point.X & 0xffff) | (((int)point.Y & 0xffff) << 16);
        SendMessage(new WindowInteropHelper(window).Handle, 0xa3, 2, (nint)packed);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
}
