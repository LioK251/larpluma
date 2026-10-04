using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using GreenLuma_Manager.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GreenLuma_Manager.Controls;

public sealed class BackgroundLayer : Grid
{
    private readonly Image _image = new() { IsHitTestVisible = false };
    private readonly Border _overlay = new() { IsHitTestVisible = false };
    private WebView2CompositionControl? _browser;
    private Window? _window;
    private string _path = "";
    private int _generation;
    private bool _initializing;
    public string? Error { get; private set; }
    public event Action<string?>? StatusChanged;

    public BackgroundLayer()
    {
        ClipToBounds = true;
        Children.Add(_image);
        Children.Add(_overlay);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppearanceService.Changed += Refresh;
        _window = Window.GetWindow(this);
        if (_window != null) _window.StateChanged += OnStateChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppearanceService.Changed -= Refresh;
        if (_window != null) _window.StateChanged -= OnStateChanged;
        _generation++;
        _browser?.Dispose();
        _browser = null;
        _path = "";
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private async void Refresh()
    {
        var generation = ++_generation;
        var theme = AppearanceService.Current;
        _overlay.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Canvas));
        _overlay.Opacity = theme.OverlayOpacity;
        _image.Opacity = theme.BackgroundOpacity;
        _image.Effect = theme.BackgroundBlur > 0 ? new BlurEffect { Radius = theme.BackgroundBlur } : null;
        _image.Stretch = theme.Fit switch { "Fit" => Stretch.Uniform, "Stretch" => Stretch.Fill, _ => Stretch.UniformToFill };
        Error = null;
        try
        {
            var path = theme.BackgroundFile;
            var changed = path != _path;
            if (changed)
            {
                _browser?.Dispose();
                if (_browser != null) Children.Remove(_browser);
                _browser = null;
                _initializing = false;
                _image.Source = null;
                _path = path;
            }
            if (path.Length == 0) { _overlay.Opacity = 0; StatusChanged?.Invoke(null); return; }
            if (!File.Exists(path)) throw new FileNotFoundException("Background file is missing. Choose another background.");
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var animated = extension is ".gif" or ".mp4" or ".webm";
            var paused = theme.Paused || theme.ReducedMotion || !SystemParameters.ClientAreaAnimation || _window?.WindowState == WindowState.Minimized;
            if (!animated || (extension == ".gif" && theme.ReducedMotion))
            {
                if (_browser != null)
                {
                    _browser.Visibility = Visibility.Hidden;
                    if (_browser.CoreWebView2 != null) await _browser.CoreWebView2.TrySuspendAsync();
                    if (generation != _generation) return;
                }
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
                _image.Source = bitmap;
                _image.Visibility = Visibility.Visible;
            }
            else
            {
                if (_initializing) return;
                if (_browser == null)
                {
                    _initializing = true;
                    _browser = new WebView2CompositionControl { IsHitTestVisible = false, DefaultBackgroundColor = System.Drawing.Color.Transparent };
                    Children.Insert(1, _browser);
                    var browser = _browser;
                    await browser.EnsureCoreWebView2Async(await WebView2Helper.GetEnvironmentAsync());
                    if (browser != _browser) return;
                    var core = browser.CoreWebView2;
                    core.Settings.AreDefaultContextMenusEnabled = false;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.IsStatusBarEnabled = false;
                    core.NewWindowRequested += (_, args) => args.Handled = true;
                    core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                    core.NavigationStarting += (_, args) =>
                    {
                        Logger.Info("Background navigation: " + args.Uri.Split(',')[0]);
                        if (args.Uri != "about:blank" && !args.Uri.StartsWith("data:text/html", StringComparison.Ordinal)) args.Cancel = true;
                    };
                    core.SetVirtualHostNameToFolderMapping("larpluma-background.local", Path.GetDirectoryName(path)!, CoreWebView2HostResourceAccessKind.DenyCors);
                    var url = "https://larpluma-background.local/" + Uri.EscapeDataString(Path.GetFileName(path));
                    var media = extension == ".gif" ? $"<img id='media' src='{url}'>" : $"<video id='media' src='{url}' muted loop playsinline></video>";
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.WebMessageReceived += (_, args) =>
                    {
                        if (args.TryGetWebMessageAsString() == "ready") ready.TrySetResult();
                        else ready.TrySetException(new InvalidDataException("This background could not be decoded. Try PNG, GIF, H.264 MP4, or WebM."));
                    };
                    core.NavigateToString("<!doctype html><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; img-src https://larpluma-background.local; media-src https://larpluma-background.local; style-src 'unsafe-inline'; script-src 'unsafe-inline'\"><style>html,body{margin:0;width:100%;height:100%;overflow:hidden}img,video{width:100%;height:100%;object-fit:cover}</style>" + media + "<script>const m=document.getElementById('media');m.addEventListener('error',()=>chrome.webview.postMessage('error'));m.addEventListener(m.tagName==='VIDEO'?'loadeddata':'load',()=>chrome.webview.postMessage('ready'));</script>");
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    _initializing = false;
                    if (generation != _generation) { Refresh(); return; }
                }
                _browser.Visibility = Visibility.Visible;
                var fit = theme.Fit switch { "Fit" => "contain", "Stretch" => "fill", _ => "cover" };
                _browser.CoreWebView2.Resume();
                await _browser.CoreWebView2.ExecuteScriptAsync($"media.style.objectFit={JsonSerializer.Serialize(fit)};media.style.opacity={theme.BackgroundOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture)};media.style.filter='blur({theme.BackgroundBlur.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)';if(media.tagName==='VIDEO'){{media.pause();{(paused ? "" : "media.play().catch(()=>{});")}}}");
                if (generation != _generation) return;
                if (paused)
                {
                    using var capture = new MemoryStream();
                    await _browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
                    if (generation != _generation) return;
                    capture.Position = 0;
                    var snapshot = new BitmapImage(); snapshot.BeginInit(); snapshot.CacheOption = BitmapCacheOption.OnLoad; snapshot.StreamSource = capture; snapshot.EndInit(); snapshot.Freeze();
                    _image.Source = snapshot;
                    _image.Opacity = 1;
                    _image.Effect = null;
                    _image.Visibility = Visibility.Visible;
                    _browser.Visibility = Visibility.Hidden;
                    await _browser.CoreWebView2.TrySuspendAsync();
                }
                else
                {
                    _browser.CoreWebView2.Resume();
                    _image.Visibility = Visibility.Hidden;
                }
            }
        }
        catch (Exception ex)
        {
            _initializing = false;
            if (generation != _generation) return;
            Error = ex.Message;
            if (_browser != null)
            {
                _browser.Dispose(); Children.Remove(_browser); _browser = null;
            }
            _image.Source = null;
            Logger.Error(ex, "Background.Refresh");
        }
        if (generation == _generation) StatusChanged?.Invoke(Error);
    }
}
