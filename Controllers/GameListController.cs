using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Controllers;

public class GameListController
{
    private readonly NotificationManager _notificationManager;
    private readonly UIElement _pnlEmptyGames;

    private string? _searchFilter;
    private string? _typeFilter;

    public GameListController(ItemsControl lstGames, UIElement pnlEmptyGames, NotificationManager notificationManager)
    {
        _pnlEmptyGames = pnlEmptyGames;
        _notificationManager = notificationManager;

        lstGames.ItemsSource = Groups;
    }

    public ObservableCollection<Game> Games { get; } = [];
    public ObservableCollection<GameGroup> Groups { get; } = [];

    public string? EditingOriginalName { get; set; }

    public bool IsFilterActive => !string.IsNullOrEmpty(_searchFilter);
    public bool IsTypeFilterActive => !string.IsNullOrEmpty(_typeFilter) && _typeFilter != "All";

    public void SetSearchFilter(string? filter)
    {
        _searchFilter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        ApplyFilters();
    }

    public void SetTypeFilter(string? type)
    {
        _typeFilter = type;
        ApplyFilters();
    }

    public void ApplyFilters()
    {
        if (Games.Any(x => x.IsEditing)) return;
        var expanded = Groups.ToDictionary(x => x.AppId, x => x.IsExpanded);
        var searchLower = _searchFilter?.ToLowerInvariant();
        var typeFilter = IsTypeFilterActive ? _typeFilter : null;
        bool Matches(Game game)
        {
            if (searchLower != null)
            {
                var normalizedName = NormalizeForSearch(game.Name.ToLowerInvariant());
                if (!normalizedName.Contains(searchLower)) return false;
            }

            if (typeFilter != null)
            {
                if (string.Equals(typeFilter, "Other", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(game.Type, "Game", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(game.Type, "DLC", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else if (!string.Equals(game.Type, typeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        Groups.Clear();
        foreach (var entries in Games.GroupBy(GroupKey))
        {
            var parent = Games.FirstOrDefault(x => x.AppId == entries.Key);
            var children = entries.Where(x => x != parent && Matches(x)).ToList();
            var visibleParent = parent != null && Matches(parent) ? parent : null;
            if (visibleParent == null && children.Count == 0) continue;
            Groups.Add(new GameGroup
            {
                AppId = entries.Key,
                Name = parent?.Name ?? entries.Select(x => x.ParentName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? $"Game {entries.Key}",
                Game = visibleParent,
                Children = children,
                IsExpanded = IsFilterActive || expanded.GetValueOrDefault(entries.Key)
            });
        }

        _notificationManager.UpdateGameCount(Games.Count(Matches), IsFilterActive || IsTypeFilterActive);
        UpdateGameListState();
    }

    private string GroupKey(Game game)
    {
        if (!uint.TryParse(game.ParentAppId, out var parent) || parent == 0 || game.ParentAppId == game.AppId)
            return game.AppId;
        // ponytail: linear parent lookup; index App IDs if large profiles become slow.
        var parentGame = Games.FirstOrDefault(x => x.AppId == game.ParentAppId);
        // Do not nest malformed parent chains or cycles.
        return !string.IsNullOrEmpty(parentGame?.ParentAppId) && parentGame.ParentAppId != parentGame.AppId
            ? game.AppId : game.ParentAppId!;
    }

    private void MetadataChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Game.ParentAppId) or nameof(Game.Type) ||
            e.PropertyName == nameof(Game.Name) && sender is Game { IsEditing: false }) ApplyFilters();
    }

    private static string NormalizeForSearch(string input)
    {
        return input
            .Replace("'", "")
            .Replace("ʻ", "")
            .Replace("ʼ", "")
            .Replace("’", "");
    }

    public void ClearGames()
    {
        foreach (var game in Games) game.PropertyChanged -= MetadataChanged;
        Games.Clear();
        ApplyFilters();
    }

    public void AddGame(Game game)
    {
        var sorted = Games.Zip(Games.Skip(1), (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) <= 0).All(x => x);
        var index = sorted ? BinarySearchInsertIndex(game.Name) : Games.Count;
        Games.Insert(index, game);
        game.PropertyChanged += MetadataChanged;
        ApplyFilters();
    }

    public void RemoveGame(Game game)
    {
        game.PropertyChanged -= MetadataChanged;
        Games.Remove(game);
        ApplyFilters();
    }

    public void LoadGames(IEnumerable<Game> games)
    {
        var loaded = games.ToList();
        foreach (var game in Games) game.PropertyChanged -= MetadataChanged;
        Games.Clear();
        Groups.Clear();
        _searchFilter = null;
        _typeFilter = null;
        foreach (var game in loaded)
        {
            Games.Add(game);
            game.PropertyChanged += MetadataChanged;
        }
        ApplyFilters();
    }

    public void UpdateGameListState()
    {
        _pnlEmptyGames.Visibility = Games.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void StartRename(Game game)
    {
        if (game.IsEditing) return;
        EditingOriginalName = game.Name;
        game.IsEditing = true;
    }

    public void CommitRename(Game game, string? newName)
    {
        game.IsEditing = false;
        var trimmed = newName?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed == EditingOriginalName)
        {
            if (EditingOriginalName != null) game.Name = EditingOriginalName;
            EditingOriginalName = null;
            ApplyFilters();
            return;
        }

        game.Name = trimmed;
        foreach (var child in Games.Where(x => x.ParentAppId == game.AppId)) child.ParentName = trimmed;
        EditingOriginalName = null;
        ApplyFilters();
    }

    public void CancelRename(Game game)
    {
        game.IsEditing = false;
        if (EditingOriginalName != null)
        {
            game.Name = EditingOriginalName;
            EditingOriginalName = null;
        }
        ApplyFilters();
    }

    public List<string> GetSelectedAppIds()
    {
        return Games.Select(g => g.AppId).ToList();
    }

    public void MoveUp(Game game)
    {
        MoveSibling(game, -1);
    }

    public void MoveDown(Game game)
    {
        MoveSibling(game, 1);
    }

    private void MoveSibling(Game game, int direction)
    {
        if (!Games.Contains(game)) return;
        var key = GroupKey(game);
        if (key != game.AppId)
        {
            var siblings = Games.Where(x => GroupKey(x) == key && x.AppId != key).ToList();
            var target = siblings.IndexOf(game) + direction;
            if (target >= 0 && target < siblings.Count) Games.Move(Games.IndexOf(game), Games.IndexOf(siblings[target]));
        }
        else
        {
            var keys = Games.Select(GroupKey).Distinct().ToList();
            var index = keys.IndexOf(key);
            var target = index + direction;
            if (target < 0 || target >= keys.Count) return;
            (keys[index], keys[target]) = (keys[target], keys[index]);
            var ordered = Games.OrderBy(x => keys.IndexOf(GroupKey(x))).ToList();
            for (var i = 0; i < ordered.Count; i++) Games.Move(Games.IndexOf(ordered[i]), i);
        }
        ApplyFilters();
    }

    private int BinarySearchInsertIndex(string name)
    {
        var lo = 0;
        var hi = Games.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (string.Compare(Games[mid].Name, name, StringComparison.OrdinalIgnoreCase) <= 0)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }
}
