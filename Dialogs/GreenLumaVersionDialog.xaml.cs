using System.Windows;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager.Dialogs;

public partial class GreenLumaVersionDialog
{
    public const string LegacyVersion = GreenLumaService.LegacyVersion;
    public const string CurrentVersion = GreenLumaService.IniAppListMinVersion;


    private GreenLumaVersionDialog(string detectedVersion)
    {
        InitializeComponent();

        MessageText.Text =
            $"GreenLuma Manager detected GreenLuma version {detectedVersion}.\n\n" +
            "Some GreenLuma builds report an older version number even though they already behave like 1.8.0 " +
            "(for example, the newer AppList.ini format). Please confirm which version's behavior GreenLuma " +
            "Manager should use.\n\nThis choice is asked once and can be changed later in Settings.";

    }

    public string SelectedVersion { get; private set; } = CurrentVersion;

    private void LegacyVersion_Click(object sender, RoutedEventArgs e)
    {
        SelectedVersion = LegacyVersion;
        Finish();
    }

    private void CurrentVersion_Click(object sender, RoutedEventArgs e)
    {
        SelectedVersion = CurrentVersion;
        Finish();
    }

    private void Finish()
    {
        DialogResult = true;
        Close();
    }

    public static string? Show(string detectedVersion)
    {
        var dialog = new GreenLumaVersionDialog(detectedVersion);
        SetOwnerWindow(dialog);
        dialog.ShowDialog();
        return dialog.DialogResult == true ? dialog.SelectedVersion : null;
    }

    private static void SetOwnerWindow(Window dialog)
    {
        var activeWindow = Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w != dialog);

        if (activeWindow != null)
            dialog.Owner = activeWindow;
        else if (Application.Current.MainWindow != null && Application.Current.MainWindow != dialog)
            dialog.Owner = Application.Current.MainWindow;
    }
}