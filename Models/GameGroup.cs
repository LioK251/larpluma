namespace GreenLuma_Manager.Models;

// Display only: profiles and AppLists continue to use the flat Game collection.
public sealed class GameGroup
{
    public required string AppId { get; init; }
    public required string Name { get; init; }
    public Game? Game { get; init; }
    public required List<Game> Children { get; init; }
    public bool HasGame => Game != null;
    public bool HasChildren => Children.Count > 0;
    public string ContentLabel => $"Additional content ({Children.Count})";
    public bool IsExpanded { get; set; }
}
