using System.Windows;
using System.Windows.Controls;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager;

public partial class MainWindow
{
    private CancellationTokenSource? _detailCts;
    private uint? _detailId;
    private List<DlcSelection> _detailRows = [];

    private async Task RunSearchAsync()
    {
        try
        {
            var input = SteamInput.Parse(TxtSearchInput.Text);
            CancelDetails();
            _searchController.CancelSearch();
            _lastAddAllGames = null;
            BtnAddAll.Content = "Add all";
            if (input.AppId is { } id) await OpenDetailsAsync(id);
            else await _searchController.ExecuteSearchAsync(input.Name);
        }
        catch (Exception ex) { _notificationManager.ShowToast(ex.Message, false); }
    }

    private void CancelDetails()
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;
        if (DetailPane != null) DetailPane.Visibility = Visibility.Collapsed;
        if (SearchResultsPane != null) SearchResultsPane.Visibility = Visibility.Visible;
    }

    private async Task OpenDetailsAsync(uint id, bool refresh = false)
    {
        CancelDetails();
        _searchController.CancelSearch();
        _searchController.HideLoading();
        _detailId = id;
        _detailCts = new CancellationTokenSource();
        var token = _detailCts.Token;
        _detailRows = [];
        DgDetails.ItemsSource = _detailRows;
        DetailPane.Visibility = Visibility.Visible;
        SearchResultsPane.Visibility = Visibility.Collapsed;
        DetailTitle.Text = "Loading game…";
        DetailStatus.Text = $"Resolving App ID {id} and its DLCs.";
        BtnAddSelected.IsEnabled = false;
        BtnUnknown.Visibility = Visibility.Collapsed;
        try
        {
            var result = App.IsPreview ? PreviewGame() : await GameDlcService.GetAsync(id, token, refresh);
            token.ThrowIfCancellationRequested();
            DetailTitle.Text = result.BaseGame?.Name ?? $"App {id}";
            DetailStatus.Text = result.Status;
            if (result.BaseGame == null) BtnUnknown.Visibility = Visibility.Visible;
            var games = result.IsPackage ? result.Dlcs : result.BaseGame == null ? result.Dlcs : new[] { result.BaseGame }.Concat(result.Dlcs);
            _detailRows = games.Select(game => new DlcSelection { Game = game, AlreadyAdded = _gameListController.Games.Any(g => g.AppId == game.AppId) }).ToList();
            DgDetails.ItemsSource = _detailRows;
            BtnAddSelected.IsEnabled = _detailRows.Any(x => x.CanSelect);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            DetailTitle.Text = $"App {id}";
            DetailStatus.Text = "Could not load Steam data. Retry when your connection is available. " + ex.Message;
            BtnUnknown.Visibility = Visibility.Visible;
            Logger.Error(ex, "MainWindow.OpenDetails");
        }
    }

    private void SearchSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_searchController == null || DetailPane.Visibility == Visibility.Visible) return;
        if (DgResults.SelectedItem is Game game && uint.TryParse(game.AppId, out var id)) _ = OpenDetailsAsync(id);
    }

    private void BackToResults_Click(object sender, RoutedEventArgs e)
    {
        CancelDetails();
        DgResults.SelectedItem = null;
    }
    private async void RetryDetails_Click(object sender, RoutedEventArgs e) { if (_detailId is { } id) await OpenDetailsAsync(id, true); }
    private void SelectAllDetails_Click(object sender, RoutedEventArgs e) { foreach (var row in _detailRows) row.Selected = row.CanSelect; }
    private void ClearDetails_Click(object sender, RoutedEventArgs e) { foreach (var row in _detailRows) row.Selected = false; }

    private void AddSelectedDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_profileController.CurrentProfile == null) { _notificationManager.ShowToast("Select a profile first.", false); return; }
        var selected = _detailRows.Where(x => x.Selected && x.CanSelect).Select(x => x.Game).ToList();
        if (selected.Count == 0) { _notificationManager.ShowToast("Select the game or DLCs to add.", false); return; }
        var added = new List<Game>();
        foreach (var game in selected)
        {
            if (_gameListController.Games.Any(g => g.AppId == game.AppId)) continue;
            var copy = new Game { AppId = game.AppId, Name = game.Name, Type = game.Type, IconUrl = game.IconUrl };
            _gameListController.AddGame(copy);
            added.Add(copy);
        }
        _profileController.SaveCurrentProfile();
        _lastAddAllGames = added;
        BtnAddAll.Content = "Undo";
        PnlResultsHeader.Visibility = Visibility.Visible;
        BtnAddAll.Visibility = Visibility.Visible;
        UpdateResultCount();
        _detailRows = _detailRows.Select(row => new DlcSelection { Game = row.Game, AlreadyAdded = _gameListController.Games.Any(g => g.AppId == row.Game.AppId) }).ToList();
        DgDetails.ItemsSource = _detailRows;
        BtnAddSelected.IsEnabled = _detailRows.Any(x => x.CanSelect);
        _notificationManager.ShowToast($"Added {added.Count} entries to {_profileController.CurrentProfile.Name}.");
    }

    private void AddUnknown_Click(object sender, RoutedEventArgs e)
    {
        if (_detailId is not { } id) return;
        if (Dialogs.CustomMessageBox.Show($"Add unrecognized App ID {id} to your profile?", "Add unknown app", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        OnSearchResultSelected(new Game { AppId = id.ToString(), Name = $"Unknown App {id}", Type = "Unknown" });
    }

    public static GameWithDlc PreviewGame() => new(
        new Game { AppId = "3764200", Name = "Resident Evil Requiem", Type = "Game" },
        [new Game { AppId = "3990820", Name = "Resident Evil Requiem - Deluxe Kit", Type = "DLC" },
         new Game { AppId = "3990800", Name = "Resident Evil Requiem - Bonus content", Type = "DLC" },
         new Game { AppId = "4460240", Name = "Resident Evil Requiem - Additional content", Type = "DLC" }],
        false, "Preview fixture · 3 DLCs. No Steam installation is modified.");
}
