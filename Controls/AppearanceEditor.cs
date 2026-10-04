using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;
using Microsoft.Win32;

namespace GreenLuma_Manager.Controls;

public sealed class AppearanceEditor : UserControl
{
    private Appearance _saved = AppearanceService.Clone(AppearanceService.Current);
    private Appearance _draft = AppearanceService.Clone(AppearanceService.Current);
    private readonly StackPanel _fields = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
    private readonly TextBlock _mediaStatus = new() { TextWrapping = TextWrapping.Wrap };
    private bool _building;
    private bool _valid = true;

    public AppearanceEditor()
    {
        var content = new StackPanel();
        content.Children.Add(Heading("Appearance"));
        content.Children.Add(Note("Customize the manager. Changes preview immediately; save them or discard to restore your theme."));
        var presets = new WrapPanel { Margin = new Thickness(0, 8, 0, 12) };
        presets.Children.Add(Action("Dark", () => Reset(new())));
        presets.Children.Add(Action("Light", () => Reset(Appearance.Light())));
        presets.Children.Add(Action("Import theme", Import));
        presets.Children.Add(Action("Export theme", Export));
        content.Children.Add(presets);
        var preview = new Grid { Height = 150, ClipToBounds = true, Margin = new Thickness(0, 0, 0, 16) };
        preview.SetResourceReference(BackgroundProperty, "DarkBg");
        var backdrop = new BackgroundLayer { IsHitTestVisible = false };
        backdrop.StatusChanged += status => _mediaStatus.Text = status ?? "Background playback is muted. Media stays local to your app data.";
        preview.Children.Add(backdrop);
        var sample = new StackPanel { Margin = new Thickness(20) };
        sample.Children.Add(Heading("Your workspace"));
        sample.Children.Add(Note("A quiet place for your Steam profiles."));
        var sampleButton = Action("Sample action", () => { }); sampleButton.HorizontalAlignment = HorizontalAlignment.Left;
        sample.Children.Add(sampleButton);
        preview.Children.Add(sample);
        content.Children.Add(preview);
        content.Children.Add(_fields);
        content.Children.Add(_mediaStatus);
        content.Children.Add(_error);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(Action("Save appearance", () => { if (Commit()) _error.Text = "Appearance saved."; }));
        actions.Children.Add(Action("Discard appearance", Revert));
        actions.Children.Add(Action("Reset to dark", () => Reset(new())));
        content.Children.Add(actions);
        Content = new ScrollViewer { Content = content, Padding = new Thickness(4, 0, 12, 24) };
        Build();
    }

    private static TextBlock Heading(string text)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(StyleProperty, "HeaderTxt"); return block;
    }
    private static TextBlock Note(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(StyleProperty, "DescriptionTxt"); return block;
    }
    private static Button Action(string text, System.Action callback)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += (_, _) => callback(); return button;
    }
    private void Reset(Appearance theme) { _draft = theme; Build(); Preview(); }

    private void Build()
    {
        _building = true;
        _fields.Children.Clear();
        TextField("Theme name", _draft.Name, x => _draft.Name = x);
        var colors = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        colors.ColumnDefinitions.Add(new()); colors.ColumnDefinitions.Add(new());
        var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        var right = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(right, 1); colors.Children.Add(left); colors.Children.Add(right);
        var properties = new[] { ("Canvas", "Canvas"), ("Surface", "Surface"), ("Text", "Text"), ("Secondary text", "Muted"), ("Border", "Border"), ("Accent", "Accent"), ("Accent text", "AccentText"), ("Hover", "Hover"), ("Selection", "Selection"), ("Success", "Success"), ("Error", "Danger") };
        for (var i = 0; i < properties.Length; i++)
        {
            var (label, name) = properties[i]; var property = typeof(Appearance).GetProperty(name)!;
            TextField(label, (string)property.GetValue(_draft)!, x => property.SetValue(_draft, x.Trim()), i % 2 == 0 ? left : right);
        }
        _fields.Children.Add(colors);
        Dropdown("Font", Fonts.SystemFontFamilies.Select(x => x.Source).Distinct().OrderBy(x => x).ToArray(), _draft.Font, x => _draft.Font = x);
        SliderField("Interface scale", .85, 1.5, _draft.Scale, x => _draft.Scale = x);
        Checkbox("Comfortable spacing", _draft.Comfortable, x => _draft.Comfortable = x);
        SliderField("Surface opacity", 0, 1, _draft.SurfaceOpacity, x => _draft.SurfaceOpacity = x);
        _fields.Children.Add(Heading("Background"));
        _fields.Children.Add(Note("PNG, JPEG, BMP, GIF, MP4 or WebM · up to 256 MiB. Animated backgrounds require Microsoft Edge WebView2."));
        _fields.Children.Add(Note(_draft.BackgroundFile.Length == 0 ? "Solid background" : Path.GetFileName(_draft.BackgroundFile)));
        var backgroundActions = new WrapPanel();
        backgroundActions.Children.Add(Action("Choose file", ChooseBackground));
        backgroundActions.Children.Add(Action("Remove", () => { _draft.BackgroundFile = ""; Build(); Preview(); }));
        _fields.Children.Add(backgroundActions);
        Dropdown("Background fit", ["Fill", "Fit", "Stretch"], _draft.Fit, x => _draft.Fit = x);
        SliderField("Background opacity", 0, 1, _draft.BackgroundOpacity, x => _draft.BackgroundOpacity = x);
        SliderField("Background blur", 0, 30, _draft.BackgroundBlur, x => _draft.BackgroundBlur = x);
        SliderField("Overlay opacity", 0, 1, _draft.OverlayOpacity, x => _draft.OverlayOpacity = x);
        Checkbox("Pause background", _draft.Paused, x => _draft.Paused = x);
        Checkbox("Reduced motion", _draft.ReducedMotion, x => _draft.ReducedMotion = x);
        _building = false;
        _valid = true;
    }

    private void TextField(string label, string value, Action<string> set, StackPanel? target = null)
    {
        var panel = target ?? _fields;
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        var input = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 12) };
        input.TextChanged += (_, _) => { if (!_building) { set(input.Text); Preview(); } };
        panel.Children.Add(input);
    }
    private void Dropdown(string label, string[] choices, string value, Action<string> set)
    {
        _fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        var input = new ComboBox { ItemsSource = choices, SelectedItem = value, Margin = new Thickness(0, 0, 0, 14) };
        input.SelectionChanged += (_, _) => { if (!_building && input.SelectedItem is string selected) { set(selected); Preview(); } };
        _fields.Children.Add(input);
    }
    private void SliderField(string label, double min, double max, double value, Action<double> set)
    {
        var caption = new TextBlock { Text = $"{label} · {value:0.##}" };
        _fields.Children.Add(caption);
        var input = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new Thickness(0, 8, 0, 16), TickFrequency = max <= 1.5 ? .01 : 1, IsSnapToTickEnabled = true };
        input.ValueChanged += (_, _) => { caption.Text = $"{label} · {input.Value:0.##}"; if (!_building) { set(input.Value); Preview(); } };
        _fields.Children.Add(input);
    }
    private void Checkbox(string label, bool value, Action<bool> set)
    {
        var input = new CheckBox { Content = label, IsChecked = value };
        input.Click += (_, _) => { set(input.IsChecked == true); Preview(); }; _fields.Children.Add(input);
    }
    private void Preview()
    {
        try { AppearanceService.Apply(_draft); _valid = true; _error.Text = ""; }
        catch (Exception ex) { _valid = false; _error.Text = ex.Message; }
    }
    public bool Commit()
    {
        if (!_valid) return false;
        try { AppearanceService.Save(_draft); _saved = AppearanceService.Clone(_draft); return true; }
        catch (Exception ex) { _error.Text = ex.Message; return false; }
    }
    public void Revert() { _draft = AppearanceService.Clone(_saved); Build(); Preview(); }
    private void ChooseBackground()
    {
        var dialog = new OpenFileDialog { Title = "Choose background", Filter = "Backgrounds|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.webm" };
        if (dialog.ShowDialog() != true) return;
        try { _draft.BackgroundFile = AppearanceService.CopyBackground(dialog.FileName); Build(); Preview(); }
        catch (Exception ex) { _error.Text = ex.Message; }
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Larpluma themes|*.larptheme" };
        if (dialog.ShowDialog() != true) return;
        try { Reset(AppearanceService.Import(dialog.FileName)); }
        catch (Exception ex) { _error.Text = ex.Message; }
    }
    private void Export()
    {
        if (!_valid) return;
        var dialog = new SaveFileDialog { Filter = "Larpluma themes|*.larptheme", FileName = "theme.larptheme" };
        if (dialog.ShowDialog() != true) return;
        var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { AppearanceService.Export(_draft, temporary); File.Move(temporary, dialog.FileName, true); _error.Text = "Theme exported."; }
        catch (Exception ex) { _error.Text = ex.Message; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
