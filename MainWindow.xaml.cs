// File: MainWindow.xaml.cs
// What it does: Connects the overlay UI to Spotify playback data.
// Why it exists: Keeps window behavior, refresh timing, and display updates together.
// RELATED FILES: MainWindow.xaml, Services/SpotifyAuthService.cs, Services/SpotifyPlaybackService.cs, Models/SpotifyNowPlaying.cs

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpotiFloat.Models;
using SpotiFloat.Services;

namespace SpotiFloat;

public partial class MainWindow : Window
{
    private readonly SpotifyAuthService authService = new();
    private readonly SpotifyPlaybackService playbackService;
    private readonly DispatcherTimer refreshTimer = new();
    private SpotifyNowPlaying? currentTrack;

    public MainWindow()
    {
        InitializeComponent();

        playbackService = new SpotifyPlaybackService(authService);
        refreshTimer.Interval = TimeSpan.FromSeconds(2);
        refreshTimer.Tick += async (_, _) => await RefreshPlaybackAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ConnectButton.Visibility = authService.IsConfigured ? Visibility.Visible : Visibility.Collapsed;

        if (!authService.IsConfigured)
        {
            SetMessage("Spotify setup needed", "Set SPOTIFLOAT_SPOTIFY_CLIENT_ID");
            return;
        }

        await authService.LoadSavedTokenAsync();
        ConnectButton.Visibility = authService.HasToken ? Visibility.Collapsed : Visibility.Visible;
        refreshTimer.Start();
        await RefreshPlaybackAsync();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        SetMessage("Connecting Spotify", "Approve access in your browser");

        try
        {
            await authService.SignInAsync();
            ConnectButton.Visibility = Visibility.Collapsed;
            refreshTimer.Start();
            await RefreshPlaybackAsync();
        }
        catch (Exception ex)
        {
            SetMessage("Spotify connection failed", ex.Message);
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task RefreshPlaybackAsync()
    {
        if (!authService.HasToken)
        {
            ConnectButton.Visibility = Visibility.Visible;
            SetMessage("SpotiFloat", "Connect Spotify to start");
            return;
        }

        try
        {
            currentTrack = await playbackService.GetNowPlayingAsync();

            if (currentTrack is null)
            {
                SetMessage("Spotify is not playing", "Other apps are ignored");
                return;
            }

            ConnectButton.Visibility = Visibility.Collapsed;
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
        AlbumArtBox.Background = new SolidColorBrush(Color.FromRgb(35, 35, 40));
        AlbumPlaceholder.Visibility = Visibility.Visible;
        SetProgress(0, 1);
    }

    private void SetAlbumArt(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            AlbumArtBox.Background = new SolidColorBrush(Color.FromRgb(35, 35, 40));
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
