using System.Globalization;

namespace GreenLuma_Manager.Services;

public record SteamInput(string Name, uint? AppId)
{
    public static SteamInput Parse(string input)
    {
        input = input.Trim();
        if (input.Length == 0) throw new ArgumentException("Enter a game name, Steam link, or App ID.");
        if (input.Length > 2048) throw new ArgumentException("Search input is too long.");
        if (input.All(char.IsAsciiDigit))
            return new("", ParseId(input));
        if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase) || input.Contains("steampowered.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http") ||
                !string.Equals(uri.Host, "store.steampowered.com", StringComparison.OrdinalIgnoreCase) ||
                uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
                throw new ArgumentException("Use a Steam Store link such as https://store.steampowered.com/app/3764200/.");
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "app") throw new ArgumentException("Use a Steam /app/ link or enter a numeric ID.");
            return new("", ParseId(parts[1]));
        }
        if (input.StartsWith('-') && input[1..].All(char.IsAsciiDigit)) throw new ArgumentException("App IDs must be positive numbers.");
        if (input.Length is < 2 or > 200) throw new ArgumentException("Game names must be 2–200 characters.");
        return new(input, null);
    }

    private static uint ParseId(string text) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
        ? id : throw new ArgumentException("App ID must be a positive 32-bit number.");
}
