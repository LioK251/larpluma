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
    private string? _detailBaseId;
    private bool _detailPackage;
    private GameContentSummary? _detailSummary;
    private Task<bool>? _contentTask;
    private bool _additionalLoaded;
    private bool _selectAllPending;

    private void BindDetails()
    {
        DgDetails.ItemsSource = _detailPackage ? null : _detailRows.Where(x => x.Game.AppId == _detailBaseId).ToList();
        var children = _detailRows.Where(x => _detailPackage || x.Game.AppId != _detailBaseId).ToList();
        DgAdditional.ItemsSource = AdditionalContent.IsExpanded ? children : null;
        var count = _detailSummary?.ContentIds.Count ?? 0;
        AdditionalContent.Header = $"{(_detailPackage ? "Package contents" : "Additional content")} ({count})";
        AdditionalContent.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

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
        _selectAllPending = false;
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
        _detailBaseId = null;
        _detailPackage = false;
        _detailSummary = null;
        _contentTask = null;
        _additionalLoaded = false;
        AdditionalContent.IsExpanded = false;
        AdditionalStatus.Text = "Expand to load additional content.";
        AdditionalStatus.Visibility = Visibility.Visible;
        BtnRetryContent.Visibility = Visibility.Collapsed;
        BindDetails();
        DetailPane.Visibility = Visibility.Visible;
        SearchResultsPane.Visibility = Visibility.Collapsed;
        DetailTitle.Text = "Loading game…";
        DetailStatus.Text = $"Resolving App ID {id} and its additional content.";
        BtnAddSelected.IsEnabled = false;
        BtnUnknown.Visibility = Visibility.Collapsed;
        try
        {
            var preview = App.IsPreview ? PreviewGame() : null;
            var result = preview != null
                ? new GameContentSummary(preview.BaseGame, preview.Dlcs.Select(x => x.AppId).ToList(), preview.Partial, preview.Status, preview.IsPackage)
                : await GameDlcService.GetSummaryAsync(id, token, refresh);
            token.ThrowIfCancellationRequested();
            DetailTitle.Text = result.BaseGame?.Name ?? $"App {id}";
            DetailStatus.Text = result.Status;
            if (result.BaseGame == null) BtnUnknown.Visibility = Visibility.Visible;
            _detailBaseId = result.BaseGame?.AppId;
            _detailPackage = result.IsPackage;
            _detailSummary = result;
            var games = result.IsPackage || result.BaseGame == null ? [] : new[] { result.BaseGame };
            _detailRows = games.Select(game => new DlcSelection { Game = game, AlreadyAdded = _gameListController.Games.Any(g => g.AppId == game.AppId) }).ToList();
            BindDetails();
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

    private async Task<bool> EnsureAdditionalContentAsync()
    {
        if (_additionalLoaded) return true;
        if (_detailSummary == null || _detailCts == null) return false;
        var task = _contentTask ??= LoadAdditionalContentAsync(_detailSummary, _detailCts.Token);
        var loaded = await task;
        if (!loaded && _contentTask == task) _contentTask = null;
        return loaded;
    }

    private async Task<bool> LoadAdditionalContentAsync(GameContentSummary summary, CancellationToken token)
    {
        AdditionalStatus.Text = "Loading additional content…";
        AdditionalStatus.Visibility = Visibility.Visible;
        BtnRetryContent.Visibility = Visibility.Collapsed;
        try
        {
            // Yield even for a cached result so expansion can paint its loading state.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
            var result = App.IsPreview ? PreviewGame() : await GameDlcService.GetContentAsync(summary, token);
            token.ThrowIfCancellationRequested();
            var addedIds = _gameListController.Games.Select(x => x.AppId).ToHashSet();
            _detailRows.AddRange(result.Dlcs.Select(game => new DlcSelection { Game = game, AlreadyAdded = addedIds.Contains(game.AppId) }));
            _additionalLoaded = true;
            AdditionalStatus.Text = result.Status;
            AdditionalStatus.Visibility = result.Partial ? Visibility.Visible : Visibility.Collapsed;
            BindDetails();
            BtnAddSelected.IsEnabled = _detailRows.Any(x => x.CanSelect);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return false;
            AdditionalStatus.Text = "Could not load additional content. Retry when your connection is available.";
            BtnRetryContent.Visibility = Visibility.Visible;
            Logger.Error(ex, "MainWindow.LoadAdditionalContent");
            return false;
        }
    }

    private async void AdditionalContent_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != AdditionalContent) return;
        await EnsureAdditionalContentAsync();
        BindDetails();
    }

    private void AdditionalContent_Collapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource == AdditionalContent) DgAdditional.ItemsSource = null;
    }

    private async void RetryContent_Click(object sender, RoutedEventArgs e) => await EnsureAdditionalContentAsync();

    private void Content_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // Nested lists own wheel scrolling; the outer workspace must not consume it.
        var pending = new Queue<DependencyObject>();
        pending.Enqueue((DependencyObject)sender);
        while (pending.TryDequeue(out var node))
        {
            if (node is ScrollViewer scroll)
            {
                if (scroll.ScrollableHeight <= 0) return;
                if (SystemParameters.WheelScrollLines < 0)
                { if (e.Delta < 0) scroll.PageDown(); else scroll.PageUp(); }
                else
                    for (var line = 0; line < Math.Max(1, SystemParameters.WheelScrollLines); line++)
                    { if (e.Delta < 0) scroll.LineDown(); else scroll.LineUp(); }
                e.Handled = true;
                return;
            }
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                pending.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
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
    private async void SelectAllDetails_Click(object sender, RoutedEventArgs e)
    {
        var token = _detailCts?.Token ?? new CancellationToken(true);
        _selectAllPending = true;
        if (AdditionalContent.Visibility == Visibility.Visible)
        {
            AdditionalContent.IsExpanded = true;
            if (!await EnsureAdditionalContentAsync()) return;
        }
        if (token.IsCancellationRequested || !_selectAllPending) return;
        foreach (var row in _detailRows) row.Selected = row.CanSelect;
        _selectAllPending = false;
    }
    private void ClearDetails_Click(object sender, RoutedEventArgs e) { _selectAllPending = false; foreach (var row in _detailRows) row.Selected = false; }

    private void AddSelectedDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_profileController.CurrentProfile == null) { _notificationManager.ShowToast("Select a profile first.", false); return; }
        var selected = _detailRows.Where(x => x.Selected && x.CanSelect).Select(x => x.Game).ToList();
        if (selected.Count == 0) { _notificationManager.ShowToast("Select the game or additional content to add.", false); return; }
        var added = new List<Game>();
        var existingIds = _gameListController.Games.Select(x => x.AppId).ToHashSet();
        _gameListController.BatchUpdate(() =>
        {
            foreach (var game in selected)
            {
                if (!existingIds.Add(game.AppId)) continue;
                var copy = new Game { AppId = game.AppId, Name = game.Name, Type = game.Type, IconUrl = game.IconUrl, ParentAppId = game.ParentAppId, ParentName = game.ParentName };
                _gameListController.AddGame(copy);
                added.Add(copy);
            }
        });
        _profileController.SaveCurrentProfile();
        _lastAddAllGames = added;
        BtnAddAll.Content = "Undo";
        PnlResultsHeader.Visibility = Visibility.Visible;
        BtnAddAll.Visibility = Visibility.Visible;
        UpdateResultCount();
        _detailRows = _detailRows.Select(row => new DlcSelection { Game = row.Game, AlreadyAdded = existingIds.Contains(row.Game.AppId) }).ToList();
        BindDetails();
        BtnAddSelected.IsEnabled = _detailRows.Any(x => x.CanSelect);
        _notificationManager.ShowToast($"Added {added.Count} entries to {_profileController.CurrentProfile.Name}.");
    }

    private void AddUnknown_Click(object sender, RoutedEventArgs e)
    {
        if (_detailId is not { } id) return;
        if (Dialogs.CustomMessageBox.Show($"Add unrecognized App ID {id} to your profile?", "Add unknown app", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        OnSearchResultSelected(new Game { AppId = id.ToString(), Name = $"Unknown App {id}", Type = "Unknown" });
    }

    public static GameWithDlc PreviewGame()
    {
        var result = new GameWithDlc(
        new Game { AppId = "3764200", Name = "Resident Evil Requiem", Type = "Game" },
        [new Game { AppId = "3990820", Name = "Resident Evil Requiem - Deluxe Kit", Type = "DLC" },
         new Game { AppId = "3990800", Name = "Resident Evil Requiem - Bonus content", Type = "DLC" },
         new Game { AppId = "4460240", Name = "Resident Evil Requiem - Additional content", Type = "DLC" }],
        false, "Preview fixture · 3 DLCs. No Steam installation is modified.");
        GameDlcService.Associate(result.BaseGame!, result.Dlcs);
        return result;
    }
}
