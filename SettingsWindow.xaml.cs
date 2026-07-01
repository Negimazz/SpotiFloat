// File: SettingsWindow.xaml.cs
// What it does: Saves the Spotify Client ID entered by the user.
// Why it exists: Keeps Spotify setup inside the app instead of requiring PowerShell.
// RELATED FILES: SettingsWindow.xaml, Services/AppSettingsService.cs, MainWindow.xaml.cs

using System.Windows;
using SpotiFloat.Services;

namespace SpotiFloat;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService settingsService;

    public SettingsWindow(AppSettingsService settingsService)
    {
        InitializeComponent();
        this.settingsService = settingsService;
        ClientIdTextBox.Text = settingsService.ClientId;
        ClientIdTextBox.SelectAll();
        ClientIdTextBox.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        settingsService.SaveClientId(ClientIdTextBox.Text);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
