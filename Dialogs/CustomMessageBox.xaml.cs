using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace GreenLuma_Manager.Dialogs;

public partial class CustomMessageBox
{
    private CustomMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;

        SetIcon(icon);
        SetButtons(buttons);

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, _) => { if (Result == MessageBoxResult.None) Result = MessageBoxResult.Cancel; };
    }

    public MessageBoxResult Result { get; private set; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = MessageBoxResult.Cancel;
            DialogResult = false;
            Close();
        }
    }

    private void SetIcon(MessageBoxImage icon)
    {
        var iconData = GetIconData(icon);

        if (!string.IsNullOrEmpty(iconData.PathData))
        {
            IconPath.Data = Geometry.Parse(iconData.PathData);
            IconPath.Fill = iconData.Brush;
        }
        else
        {
            IconPath.Visibility = Visibility.Collapsed;
        }
    }

    private (string PathData, SolidColorBrush Brush) GetIconData(MessageBoxImage icon)
    {
        return icon switch
        {
            MessageBoxImage.Hand => (
                "M12,2C17.53,2 22,6.47 22,12C22,17.53 17.53,22 12,22C6.47,22 2,17.53 2,12C2,6.47 6.47,2 12,2M15.59,7L12,10.59L8.41,7L7,8.41L10.59,12L7,15.59L8.41,17L12,13.41L15.59,17L17,15.59L13.41,12L17,8.41L15.59,7Z",
                (SolidColorBrush)FindResource("Danger")
            ),
            MessageBoxImage.Question => (
                "M10,19H13V22H10V19M12,2C17.35,2.22 19.68,7.62 16.5,11.67C15.67,12.67 14.33,13.33 13.67,14.17C13,15 13,16 13,17H10C10,15.33 10,13.92 10.67,12.92C11.33,11.92 12.67,11.33 13.5,10.67C15.92,8.43 15.32,5.26 12,5A3,3 0 0,0 9,8H6A6,6 0 0,1 12,2Z",
                (SolidColorBrush)FindResource("Info")
            ),
            MessageBoxImage.Exclamation => (
                "M13,14H11V10H13M13,18H11V16H13M1,21H23L12,2L1,21Z",
                (SolidColorBrush)FindResource("Warning")
            ),
            MessageBoxImage.Asterisk => (
                "M11,9H13V7H11M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,17H13V11H11V17Z",
                (SolidColorBrush)FindResource("Info")
            ),
            _ => (
                "M13,9H11V7H13M13,17H11V11H13M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z",
                (SolidColorBrush)FindResource("Info")
            )
        };
    }

    private void SetButtons(MessageBoxButton buttons)
    {
        ButtonPanel.Children.Clear();

        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("OK", MessageBoxResult.OK, true);
                break;

            case MessageBoxButton.OKCancel:
                AddButton("CANCEL", MessageBoxResult.Cancel, false);
                AddButton("OK", MessageBoxResult.OK, true);
                break;

            case MessageBoxButton.YesNoCancel:
                AddButton("CANCEL", MessageBoxResult.Cancel, false);
                AddButton("NO", MessageBoxResult.No, false);
                AddButton("YES", MessageBoxResult.Yes, true);
                break;

            case MessageBoxButton.YesNo:
                AddButton("NO", MessageBoxResult.No, false);
                AddButton("YES", MessageBoxResult.Yes, true);
                break;
        }
    }

    private void AddButton(string text, MessageBoxResult result, bool isPrimary)
    {
        var button = new Button
        {
            Content = text,
            Style = isPrimary
                ? (Style)FindResource("PrimaryBtn")
                : (Style)FindResource("SecondBtn"),
            Margin = ButtonPanel.Children.Count > 0
                ? new Thickness(8, 0, 0, 0)
                : new Thickness(0)
        };

        button.Click += (_, _) =>
        {
            Result = result;
            DialogResult = result != MessageBoxResult.Cancel && result != MessageBoxResult.No;
            Close();
        };

        if (isPrimary) button.IsDefault = true;

        ButtonPanel.Children.Add(button);
    }

    public static MessageBoxResult Show(
        string message,
        string title = "Message",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        var messageBox = new CustomMessageBox(message, title, buttons, icon);

        SetOwnerWindow(messageBox);

        messageBox.ShowDialog();
        return messageBox.Result;
    }

    private static void SetOwnerWindow(CustomMessageBox messageBox)
    {
        var activeWindow = Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive);

        if (activeWindow != null && activeWindow != messageBox)
            messageBox.Owner = activeWindow;
        else if (Application.Current.MainWindow != null && Application.Current.MainWindow != messageBox)
            messageBox.Owner = Application.Current.MainWindow;
    }
}
