// File: MainWindow.xaml.cs
// What it does: Connects the overlay UI to Spotify playback data.
// Why it exists: Keeps window behavior, tray actions, refresh timing, and display updates together.
// RELATED FILES: MainWindow.xaml, SettingsWindow.xaml, Services/SpotifyAuthService.cs, Services/SpotifyPlaybackService.cs

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpotiFloat.Models;
using SpotiFloat.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SpotiFloat;

public partial class MainWindow : Window
{
    private readonly AppSettingsService settingsService = new();
    private readonly SpotifyAuthService authService;
    private readonly SpotifyPlaybackService playbackService;
    private readonly DispatcherTimer refreshTimer = new();
    private readonly Forms.NotifyIcon trayIcon = new();
    private bool isExitRequested;
    private SpotifyNowPlaying? currentTrack;

    public MainWindow()
    {
        InitializeComponent();

        authService = new SpotifyAuthService(settingsService);
        playbackService = new SpotifyPlaybackService(authService);
        refreshTimer.Interval = TimeSpan.FromSeconds(2);
        refreshTimer.Tick += async (_, _) => await RefreshPlaybackAsync();

        ConfigureTrayIcon();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!authService.IsConfigured)
        {
            SetMessage("Spotify setup needed", "Right-click tray icon");
            return;
        }

        await authService.LoadSavedTokenAsync();
        refreshTimer.Start();
        await RefreshPlaybackAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!isExitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        trayIcon.Visible = false;
        trayIcon.Dispose();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void ConfigureTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Set Spotify Client ID", null, (_, _) => OpenSettings());
        menu.Items.Add("Connect / Reconnect Spotify", null, async (_, _) => await ConnectSpotifyAsync());
        menu.Items.Add("Show / Hide Overlay", null, (_, _) => ToggleOverlay());
        menu.Items.Add("Refresh Now", null, async (_, _) => await RefreshPlaybackAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        trayIcon.Icon = Drawing.SystemIcons.Application;
        trayIcon.Text = "SpotiFloat";
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => ToggleOverlay();
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(settingsService)
        {
            Owner = IsVisible ? this : null
        };

        if (window.ShowDialog() == true)
        {
            _ = ResetConnectionAfterSettingsChangeAsync();
        }
    }

    private async Task ResetConnectionAfterSettingsChangeAsync()
    {
        await authService.ClearSavedTokenAsync();
        SetMessage("Spotify Client ID saved", "Reconnect from tray menu");
    }

    private async Task ConnectSpotifyAsync()
    {
        if (!authService.IsConfigured)
        {
            OpenSettings();
            return;
        }

        SetMessage("Connecting Spotify", "Approve access in your browser");

        try
        {
            await authService.ClearSavedTokenAsync();
            await authService.SignInAsync();
            refreshTimer.Start();
            await RefreshPlaybackAsync();
        }
        catch (Exception ex)
        {
            SetMessage("Spotify connection failed", ex.Message);
        }
    }

    private void ToggleOverlay()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        Activate();
    }

    private void ExitApplication()
    {
        isExitRequested = true;
        Close();
    }

    private async Task RefreshPlaybackAsync()
    {
        if (!authService.HasToken)
        {
            SetMessage("SpotiFloat", "Right-click tray icon to connect");
            return;
        }

        try
        {
            currentTrack = await playbackService.GetNowPlayingAsync();

            if (currentTrack is null)
            {
                SetMessage("Spotify is not playing", "Play music in Spotify");
                return;
            }

            TitleText.Text = currentTrack.Title;
            ArtistText.Text = currentTrack.Artist;
            SetAlbumArt(currentTrack.AlbumArtUrl);
            SetProgress(currentTrack.ProgressMs, currentTrack.DurationMs);
        }
        catch (Exception ex)
        {
            SetMessage("Could not read Spotify", ex.Message);
        }
    }

    private void SetMessage(string title, string subtitle)
    {
        currentTrack = null;
        TitleText.Text = title;
        ArtistText.Text = subtitle;
        AlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
        AlbumPlaceholder.Visibility = Visibility.Visible;
        SetProgress(0, 1);
    }

    private void SetAlbumArt(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            AlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
            AlbumPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        AlbumPlaceholder.Visibility = Visibility.Collapsed;
        AlbumArtBox.Background = new ImageBrush(new BitmapImage(new Uri(url)))
        {
            Stretch = Stretch.UniformToFill
        };
    }

    private void SetProgress(int progressMs, int durationMs)
    {
        var width = Math.Clamp((double)progressMs / Math.Max(durationMs, 1), 0, 1) * 234;
        ProgressFill.Width = width;
    }
}
