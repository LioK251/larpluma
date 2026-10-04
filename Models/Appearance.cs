namespace GreenLuma_Manager.Models;

public class Appearance
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Dark";
    public string Canvas { get; set; } = "#141414";
    public string Surface { get; set; } = "#1D1D1D";
    public string Text { get; set; } = "#EBEBEB";
    public string Muted { get; set; } = "#A3A3A3";
    public string Border { get; set; } = "#343434";
    public string Accent { get; set; } = "#EEEEEE";
    public string AccentText { get; set; } = "#141414";
    public string Hover { get; set; } = "#292929";
    public string Selection { get; set; } = "#383838";
    public string Success { get; set; } = "#E4E4E4";
    public string Danger { get; set; } = "#EBEBEB";
    public string Font { get; set; } = "Segoe UI";
    public double Scale { get; set; } = 1;
    public bool Comfortable { get; set; }
    // Retained for importing version-1 themes. The interface always uses square corners.
    public double Radius { get; set; }
    public double SurfaceOpacity { get; set; } = 1;
    public string BackgroundFile { get; set; } = "";
    public string Fit { get; set; } = "Fill";
    public double BackgroundOpacity { get; set; } = .35;
    public double BackgroundBlur { get; set; }
    public double OverlayOpacity { get; set; } = .3;
    public bool ReducedMotion { get; set; }
    public bool Paused { get; set; }

    public static Appearance Light() => new()
    {
        Name = "Light", Canvas = "#FFFFFF", Surface = "#F7F7F7", Text = "#171717",
        Muted = "#656565", Border = "#DDDDDD", Accent = "#171717", AccentText = "#FFFFFF",
        Hover = "#EAEAEA", Selection = "#E0E0E0", Success = "#242424", Danger = "#242424"
    };
}
