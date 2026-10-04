using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace GreenLuma_Manager.Controls;

public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(UIElement), typeof(TitleBar));
    public UIElement? Actions { get => (UIElement?)GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public string? Caption { get; set; }
    public bool ShowMinimize { get; set; }
    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window == null) return;
        if (Caption == null) TxtTitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = _window });
        else TxtTitle.Text = Caption;
        _window.StateChanged += OnStateChanged;
        UpdateButtons();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window != null) _window.StateChanged -= OnStateChanged;
        _window = null;
    }

    private void OnStateChanged(object? sender, EventArgs e) => UpdateButtons();
    private void UpdateButtons()
    {
        BtnMinimize.Visibility = ShowMinimize && _window?.ResizeMode != ResizeMode.NoResize ? Visibility.Visible : Visibility.Collapsed;
        BtnMaximize.Visibility = _window?.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip ? Visibility.Visible : Visibility.Collapsed;
        var maximized = _window?.WindowState == WindowState.Maximized;
        MaximizeIcon.Data = Geometry.Parse(maximized ? "M4 1 H13 V10 M1 4 H10 V13 H1 Z" : "M2 2 H12 V12 H2 Z");
        BtnMaximize.ToolTip = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(BtnMaximize, maximized ? "Restore" : "Maximize");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) { if (_window != null) SystemCommands.MinimizeWindow(_window); }
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_window == null) return;
        if (_window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(_window);
        else SystemCommands.MaximizeWindow(_window);
    }
    private void Close_Click(object sender, RoutedEventArgs e) { if (_window != null) SystemCommands.CloseWindow(_window); }
}
