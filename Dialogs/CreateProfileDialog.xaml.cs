using System.IO;
using System.Windows;
using System.Windows.Input;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Dialogs;

public partial class CreateProfileDialog
{
    public CreateProfileDialog()
    {
        InitializeComponent();
        Result = null;
        TxtProfileName.Focus();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public Profile? Result { get; private set; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel_Click(sender, null);
    }

    private void ProfileName_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IsValidProfileNameCharacter(e.Text);
    }

    private static bool IsValidProfileNameCharacter(string text)
    {
        return text.All(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_');
    }

    private void ProfileName_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Return:
                Ok_Click(sender, null);
                break;
            case Key.Escape:
                Cancel_Click(sender, null);
                break;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs? e)
    {
        var profileName = TxtProfileName.Text.Trim();

        if (!ValidateProfileName(profileName))
            return;

        Result = new Profile { Name = profileName };
        DialogResult = true;
        Close();
    }

    private static bool ValidateProfileName(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            CustomMessageBox.Show(
                "Profile name cannot be empty.",
                "Validation",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        if (profileName.Length > 50)
        {
            CustomMessageBox.Show(
                "Profile name is too long (max 50 characters).",
                "Validation",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        if (ContainsInvalidCharacters(profileName))
        {
            CustomMessageBox.Show(
                "Profile name contains invalid characters.",
                "Validation",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        return true;
    }

    private static bool ContainsInvalidCharacters(string profileName)
    {
        return profileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
               profileName.Contains('/') ||
               profileName.Contains('\\');
    }

    private void Cancel_Click(object sender, RoutedEventArgs? e)
    {
        DialogResult = false;
        Close();
    }
}