using System.Text;
using System.Windows;
using GreenLuma_Manager.Dialogs;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager;

public partial class MainWindow
{
    private CancellationTokenSource? _unlockerCts;

    private async void UninstallUnlocker_Click(object sender, RoutedEventArgs e) => await RunCreamOperationAsync(true);

    private void CancelUnlocker_Click(object sender, RoutedEventArgs e)
    {
        _unlockerCts?.Cancel();
        BtnCancelUnlocker.IsEnabled = false;
        TxtAppListProgress.Text = "Canceling after the current game…";
    }

    private async Task RunCreamOperationAsync(bool uninstall)
    {
        if (App.IsPreview) { _notificationManager.ShowToast("This operation is disabled in preview mode.", false); return; }
        if (_unlockerCts != null || _config?.UnlockMethod != UnlockMethod.CreamInstaller) return;
        if (_profileController.CurrentProfile == null) { _notificationManager.ShowToast("No profile selected.", false); return; }
        using var cancellation = new CancellationTokenSource();
        _unlockerCts = cancellation;
        CmbProfile.IsEnabled = false;
        BtnCancelUnlocker.Visibility = Visibility.Visible;
        BtnCancelUnlocker.IsEnabled = true;
        UpdateStatus();
        ShowAppListProgress();
        try
        {
            _profileController.SaveCurrentProfile();
            TxtAppListProgress.Text = "Checking selected DLCs and installed Steam games…";
            var plan = await CreamInstallerService.PrepareAsync(_config, _profileController.CurrentProfile, uninstall, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var review = new StringBuilder();
            review.AppendLine(uninstall ? "Remove the recorded Larpluma installation and restore original files for these games:" :
                $"Install {_config.SteamUnlocker} using {(_config.UnlockerProxy ? _config.UnlockerProxyName + ".dll proxy mode" : "standard DLL replacement")}. Close the affected games first.");
            foreach (var game in plan.Games)
            {
                review.AppendLine($"\n{game.Game.Name} ({game.Game.AppId}) — {game.Dlcs.Count} selected DLCs\n{game.Game.Root}");
                if (game.Recover) review.AppendLine("Recover an interrupted operation, then restore originals.");
                else foreach (var directory in game.Files.Keys.Select(System.IO.Path.GetDirectoryName).Distinct())
                    review.AppendLine("  " + (string.IsNullOrEmpty(directory) ? "Game root" : directory));
            }
            if (plan.Issues.Count > 0) review.AppendLine("\nSkipped / needs attention:\n" + string.Join("\n", plan.Issues));
            if (plan.Games.Count == 0)
            {
                CustomMessageBox.Show("No game files will be changed.\n\n" + (plan.Issues.Count == 0 ? "The profile is empty." : string.Join("\n", plan.Issues)), "DLC unlocker");
                return;
            }
            review.AppendLine(uninstall ? "\nContinue with removal?" : "\nOnly profile DLCs will be configured. DLC content files are not downloaded. Continue?");
            if (CustomMessageBox.Show(review.ToString(), uninstall ? "Remove DLC unlocker" : "Install DLC unlocker",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var progress = new Progress<string>(message => TxtAppListProgress.Text = message);
            var result = await CreamInstallerService.ExecuteAsync(plan, progress, cancellation.Token);
            var summary = $"{result.Completed.Count} game(s) {(uninstall ? "restored" : "installed / updated")}.";
            if (result.Canceled) summary += " Canceled before the remaining games.";
            if (result.Completed.Count > 0) summary += "\n\nCompleted:\n" + string.Join("\n", result.Completed);
            if (result.Failed.Count > 0) summary += "\n\nFailed / needs attention:\n" + string.Join("\n", result.Failed);
            CustomMessageBox.Show(summary, "DLC unlocker results", icon: result.Failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { _notificationManager.ShowToast("Unlocker operation canceled."); }
        catch (Exception ex)
        {
            Logger.Error(ex, "MainWindow.CreamInstaller");
            CustomMessageBox.Show(ex.Message, "DLC unlocker", icon: MessageBoxImage.Error);
        }
        finally
        {
            _unlockerCts = null;
            CmbProfile.IsEnabled = true;
            BtnCancelUnlocker.Visibility = Visibility.Collapsed;
            HideAppListProgress();
            UpdateStatus();
        }
    }
}
