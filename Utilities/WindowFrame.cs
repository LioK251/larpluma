using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace GreenLuma_Manager.Utilities;

public static class WindowFrame
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WindowFrame), new PropertyMetadata(false, OnEnabled));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);

    private static void OnEnabled(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Window window || e.NewValue is not true) return;
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 44, CornerRadius = new CornerRadius(0), GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip ? new Thickness(6) : new Thickness(0),
            UseAeroCaptionButtons = false
        });
        window.SourceInitialized += OnSourceInitialized;
        window.PreviewKeyDown += OnKeyDown;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        var window = (Window)sender!;
        WindowChrome.GetWindowChrome(window).ResizeBorderThickness = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip ? new Thickness(6) : new Thickness(0);
        var handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowProc);
        window.Closed += (_, _) => source?.RemoveHook(WindowProc);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var square = 1; // DWMWCP_DONOTROUND
            _ = DwmSetWindowAttribute(handle, 33, ref square, sizeof(int));
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            var window = (Window)sender;
            SystemCommands.ShowSystemMenu(window, window.PointToScreen(new Point(18, 44)));
            e.Handled = true;
        }
    }

    private static nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024) return 0; // WM_GETMINMAXINFO uses physical pixels for this monitor.
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return 0;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        limits.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        if (HwndSource.FromHwnd(hwnd)?.RootVisual is Window window)
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            limits.MinTrackSize.X = Math.Max(limits.MinTrackSize.X, (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX));
            limits.MinTrackSize.Y = Math.Max(limits.MinTrackSize.Y, (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY));
            if (double.IsFinite(window.MaxWidth)) limits.MaxSize.X = Math.Min(limits.MaxSize.X, (int)(window.MaxWidth * dpi.DpiScaleX));
            if (double.IsFinite(window.MaxHeight)) limits.MaxSize.Y = Math.Min(limits.MaxSize.Y, (int)(window.MaxHeight * dpi.DpiScaleY));
        }
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
