using System.ComponentModel;

namespace GreenLuma_Manager.Models;

public record GameWithDlc(Game? BaseGame, List<Game> Dlcs, bool Partial, string Status, bool IsPackage = false);

public record GameContentSummary(Game? BaseGame, List<string> ContentIds, bool Partial, string Status, bool IsPackage = false);

public sealed class DlcSelection : INotifyPropertyChanged
{
    public required Game Game { get; init; }
    public bool AlreadyAdded { get; set; }
    public bool CanSelect => !AlreadyAdded;
    public string Label => AlreadyAdded ? "Already added" : Game.Type;
    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { _selected = value && CanSelect; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
