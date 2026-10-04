using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager.Services;

public static class AppearanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static Appearance Current { get; private set; } = new();
    public static event Action? Changed;
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".mp4", ".webm"];
    public const long MaxMediaBytes = 256 * 1024 * 1024;

    public static Appearance Clone(Appearance value)
    {
        var copy = JsonSerializer.Deserialize<Appearance>(JsonSerializer.Serialize(value))!;
        copy.Radius = 0;
        return copy;
    }

    public static void Validate(Appearance value)
    {
        if (value.Version != 1) throw new InvalidDataException("Unsupported theme version.");
        foreach (var color in new[] { value.Canvas, value.Surface, value.Text, value.Muted, value.Border,
                     value.Accent, value.AccentText, value.Hover, value.Selection, value.Success, value.Danger })
            if (color == null || color.Length != 7 || color[0] != '#' || !uint.TryParse(color[1..], System.Globalization.NumberStyles.HexNumber, null, out _))
                throw new InvalidDataException("Colors must use #RRGGBB.");
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 100 || string.IsNullOrWhiteSpace(value.Font) || value.Font.Length > 100)
            throw new InvalidDataException("A theme name and font are required (up to 100 characters).");
        if (!double.IsFinite(value.Scale) || value.Scale is < .85 or > 1.5 ||
            !double.IsFinite(value.Radius) ||
            !double.IsFinite(value.BackgroundBlur) || value.BackgroundBlur is < 0 or > 30 ||
            new[] { value.SurfaceOpacity, value.BackgroundOpacity, value.OverlayOpacity }.Any(x => !double.IsFinite(x) || x is < 0 or > 1))
            throw new InvalidDataException("Appearance values are outside their allowed ranges.");
        if (value.Fit is not ("Fill" or "Fit" or "Stretch")) throw new InvalidDataException("Invalid background fit.");
        if (value.BackgroundFile == null || (value.BackgroundFile.Length > 0 && !Extensions.Contains(Path.GetExtension(value.BackgroundFile).ToLowerInvariant())))
            throw new InvalidDataException("Unsupported background format.");
    }

    public static void Load()
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, "appearance.json");
            Apply(File.Exists(path) ? JsonSerializer.Deserialize<Appearance>(File.ReadAllText(path)) ?? new() : new());
        }
        catch (Exception ex) { Logger.Error(ex, "Appearance.Load"); Apply(new()); }
    }

    public static void Apply(Appearance value)
    {
        Validate(value);
        Current = Clone(value);
        if (Application.Current is { } app)
        {
            var resources = app.Resources;
            foreach (var (key, color) in new Dictionary<string, string>
                     {
                         ["DarkBg"] = value.Canvas, ["SurfaceBg"] = value.Surface, ["Text"] = value.Text,
                         ["TextSecond"] = value.Muted, ["TextTert"] = value.Muted, ["Border"] = value.Border,
                         ["Accent"] = value.Accent, ["AccentText"] = value.AccentText, ["Hover"] = value.Hover,
                         ["Selection"] = value.Selection, ["Success"] = value.Success, ["Danger"] = value.Danger
                     })
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                if (key == "SurfaceBg") brush.Opacity = value.SurfaceOpacity;
                resources[key] = brush;
            }
            resources["UiFont"] = new FontFamily(value.Font);
            resources["Info"] = resources["TextSecond"];
            resources["Warning"] = resources["TextSecond"];
            resources["Corner"] = new CornerRadius(0);
            resources["UiSize"] = 13d * value.Scale;
            resources["SmallSize"] = 11d * value.Scale;
            resources["HeadingSize"] = 15d * value.Scale;
            resources["ControlHeight"] = (value.Comfortable ? 40d : 34d) * value.Scale;
            resources["WorkspaceMinHeight"] = 450d + Math.Max(0, value.Scale - 1) * 300d;
            resources["DataRowHeight"] = (value.Comfortable ? 44d : 36d) * value.Scale;
            resources["ProfileRowPadding"] = new Thickness(0, (value.Comfortable ? 10 : 6) * value.Scale, 0, (value.Comfortable ? 10 : 6) * value.Scale);
            resources["ControlPadding"] = new Thickness(12, value.Comfortable ? 10 : 7, 12, value.Comfortable ? 10 : 7);
        }
        Changed?.Invoke();
    }

    public static void Save(Appearance value)
    {
        Validate(value);
        Directory.CreateDirectory(AppPaths.Root);
        AtomicFile.WriteAllText(Path.Combine(AppPaths.Root, "appearance.json"), JsonSerializer.Serialize(Clone(value), JsonOptions));
        Apply(value);
    }

    public static string CopyBackground(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(extension)) throw new InvalidDataException("Unsupported background format.");
        if (new FileInfo(path).Length > MaxMediaBytes) throw new InvalidDataException("Backgrounds must be 256 MiB or smaller.");
        Directory.CreateDirectory(AppPaths.Backgrounds);
        var destination = Path.Combine(AppPaths.Backgrounds, Guid.NewGuid().ToString("N") + extension);
        File.Copy(path, destination);
        return destination;
    }

    public static void Export(Appearance value, string path)
    {
        Validate(value);
        var copy = Clone(value);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (copy.BackgroundFile.Length > 0)
        {
            if (!File.Exists(copy.BackgroundFile)) throw new FileNotFoundException("The background file is missing.");
            if (new FileInfo(copy.BackgroundFile).Length > MaxMediaBytes) throw new InvalidDataException("Background too large.");
            var source = copy.BackgroundFile;
            copy.BackgroundFile = "background" + Path.GetExtension(source).ToLowerInvariant();
            zip.CreateEntryFromFile(source, copy.BackgroundFile, CompressionLevel.NoCompression);
        }
        using var writer = new StreamWriter(zip.CreateEntry("theme.json").Open());
        writer.Write(JsonSerializer.Serialize(copy, JsonOptions));
    }

    public static Appearance Import(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var jsonEntry = zip.GetEntry("theme.json") ?? throw new InvalidDataException("theme.json is missing.");
        if (jsonEntry.Length > 65536 || zip.Entries.Count > 2 || zip.Entries.Any(e => e.FullName != "theme.json" && !e.FullName.StartsWith("background.", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid theme archive.");
        using var reader = new StreamReader(jsonEntry.Open());
        var value = JsonSerializer.Deserialize<Appearance>(reader.ReadToEnd()) ?? throw new InvalidDataException("Invalid theme.");
        Validate(value);
        if (value.BackgroundFile.Length > 0)
        {
            if (value.BackgroundFile != "background" + Path.GetExtension(value.BackgroundFile).ToLowerInvariant())
                throw new InvalidDataException("Invalid media path.");
            var media = zip.GetEntry(value.BackgroundFile) ?? throw new InvalidDataException("Background is missing from theme.");
            if (media.Length > MaxMediaBytes) throw new InvalidDataException("Background too large.");
            Directory.CreateDirectory(AppPaths.Backgrounds);
            value.BackgroundFile = Path.Combine(AppPaths.Backgrounds, Guid.NewGuid().ToString("N") + Path.GetExtension(value.BackgroundFile));
            media.ExtractToFile(value.BackgroundFile);
        }
        return Clone(value);
    }
}
