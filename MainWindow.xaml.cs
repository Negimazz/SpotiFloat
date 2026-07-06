// File: MainWindow.xaml.cs
// What it does: Connects the overlay UI to Spotify playback data.
// Why it exists: Keeps window behavior, tray actions, refresh timing, and display updates together.
// RELATED FILES: MainWindow.xaml, Services/SpotifyPlaybackService.cs, Models/SpotifyNowPlaying.cs

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using SpotiFloat.Models;
using SpotiFloat.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SpotiFloat;

public partial class MainWindow : Window
{
    private const int HotkeyId = 7001;
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int VirtualKeyM = 0x4D;
    private const int WmHotkey = 0x0312;
    private const double CompactWidth = 342;
    private const double CompactHeight = 92;
    private const double MenuWidth = 656;
    private const double MenuHeight = 424;

    private readonly SpotifyPlaybackService playbackService = new();
    private readonly DispatcherTimer refreshTimer = new();
    private readonly DispatcherTimer progressTimer = new();
    private readonly DispatcherTimer visualizerTimer = new();
    private readonly Forms.NotifyIcon trayIcon = new();
    private readonly AudioVisualizerService audioVisualizerService = new();

    private DateTime progressUpdatedAtUtc;
    private int lastProgressMs;
    private int lastDurationMs = 1;
    private string currentTrackKey = "";
    private bool isMenuOpen;
    private bool isPlaybackMoving;
    private bool isSeeking;
    private bool isExitRequested;
    private SpotifyNowPlaying? currentTrack;

    public MainWindow()
    {
        InitializeComponent();

        refreshTimer.Interval = TimeSpan.FromSeconds(2);
        refreshTimer.Tick += async (_, _) => await RefreshPlaybackAsync();
        progressTimer.Interval = TimeSpan.FromMilliseconds(100);
        progressTimer.Tick += (_, _) => UpdateSmoothProgress();
        visualizerTimer.Interval = TimeSpan.FromMilliseconds(115);
        visualizerTimer.Tick += (_, _) => UpdateVisualizer();

        ConfigureTrayIcon();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
        RegisterHotKey(source?.Handle ?? IntPtr.Zero, HotkeyId, ModControl | ModAlt, VirtualKeyM);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        refreshTimer.Start();
        progressTimer.Start();
        visualizerTimer.Start();
        audioVisualizerService.Start();
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
        audioVisualizerService.Dispose();
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ToggleMenu();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && isMenuOpen)
        {
            CloseMenu();
            return;
        }

        if (e.Key == Key.M && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ToggleMenu();
        }
    }

    private void ConfigureTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
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

    private void ToggleMenu()
    {
        if (isMenuOpen)
        {
            CloseMenu();
            return;
        }

        OpenMenu();
    }

    private void OpenMenu()
    {
        isMenuOpen = true;
        Width = MenuWidth;
        Height = MenuHeight;
        MenuPanel.Visibility = Visibility.Visible;
        CompactOverlay.Visibility = Visibility.Collapsed;

        Animate(MenuPanel, OpacityProperty, 0, 1, 180);
        Animate(MenuScale, ScaleTransform.ScaleXProperty, 0.88, 1, 220);
        Animate(MenuScale, ScaleTransform.ScaleYProperty, 0.88, 1, 220);
        Animate(MenuTranslate, TranslateTransform.XProperty, -18, 0, 220);
        Animate(MenuTranslate, TranslateTransform.YProperty, -14, 0, 220);
        Animate(VisualizerGrid, OpacityProperty, 0.35, 1, 300);
    }

    private void CloseMenu()
    {
        isMenuOpen = false;
        Animate(MenuPanel, OpacityProperty, MenuPanel.Opacity, 0, 130);
        Animate(MenuScale, ScaleTransform.ScaleXProperty, MenuScale.ScaleX, 0.9, 130);
        Animate(MenuScale, ScaleTransform.ScaleYProperty, MenuScale.ScaleY, 0.9, 130);
        Animate(MenuTranslate, TranslateTransform.XProperty, MenuTranslate.X, -10, 130);
        Animate(MenuTranslate, TranslateTransform.YProperty, MenuTranslate.Y, -8, 130);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            MenuPanel.Visibility = Visibility.Collapsed;
            CompactOverlay.Visibility = Visibility.Visible;
            Width = CompactWidth;
            Height = CompactHeight;
        };
        timer.Start();
    }

    private void ExitApplication()
    {
        isExitRequested = true;
        Close();
    }

    private async Task RefreshPlaybackAsync()
    {
        try
        {
            currentTrack = await playbackService.GetNowPlayingAsync();

            if (currentTrack is null)
            {
                SetMessage("Spotify is not playing", "Play music in Spotify");
                return;
            }

            var previousTrackKey = currentTrackKey;
            var nextTrackKey = GetTrackKey(currentTrack);
            TitleText.Text = currentTrack.Title;
            ArtistText.Text = currentTrack.Artist;
            MenuTitleText.Text = currentTrack.Title;
            MenuArtistText.Text = $"BY {currentTrack.Artist}";
            PauseIcon.Visibility = currentTrack.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
            PlayIcon.Visibility = currentTrack.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
            isPlaybackMoving = currentTrack.IsPlaying;
            SetAlbumArt(currentTrack.AlbumArtBytes);
            SetProgressSource(currentTrack.ProgressMs, currentTrack.DurationMs, nextTrackKey == previousTrackKey);
            currentTrackKey = nextTrackKey;
            UpdateSmoothProgress();
        }
        catch (Exception ex)
        {
            SetMessage("Could not read Spotify", ex.Message);
        }
    }

    private void SetMessage(string title, string subtitle)
    {
        currentTrack = null;
        currentTrackKey = "";
        isPlaybackMoving = false;
        TitleText.Text = title;
        ArtistText.Text = subtitle;
        MenuTitleText.Text = title;
        MenuArtistText.Text = subtitle;
        PauseIcon.Visibility = Visibility.Collapsed;
        PlayIcon.Visibility = Visibility.Visible;
        AlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
        MenuAlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
        AlbumPlaceholder.Visibility = Visibility.Visible;
        SetProgressSource(0, 1, false);
        UpdateSmoothProgress();
    }

    private void SetAlbumArt(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            AlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
            MenuAlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
            AlbumPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        AlbumPlaceholder.Visibility = Visibility.Collapsed;
        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        var compactBrush = new ImageBrush(image)
        {
            Stretch = Stretch.UniformToFill
        };
        var menuBrush = new ImageBrush(image)
        {
            Stretch = Stretch.UniformToFill
        };

        AlbumArtBox.Background = compactBrush;
        MenuAlbumArtBox.Background = menuBrush;
    }

    private void SetProgressSource(int progressMs, int durationMs, bool isSameTrack, bool acceptRollback = false)
    {
        var visibleProgressMs = GetVisibleProgressMs();
        lastProgressMs = isSameTrack && !acceptRollback
            ? Math.Max(progressMs, visibleProgressMs)
            : progressMs;
        lastDurationMs = Math.Max(durationMs, 1);
        progressUpdatedAtUtc = DateTime.UtcNow;
    }

    private void UpdateSmoothProgress()
    {
        var progressMs = GetVisibleProgressMs();
        var scale = Math.Clamp((double)progressMs / lastDurationMs, 0, 1);
        ProgressScale.ScaleX = currentTrack is null ? 0 : Math.Max(scale, 0.02);
        if (!isSeeking)
        {
            MenuSeekSlider.Maximum = lastDurationMs;
            MenuSeekSlider.Value = currentTrack is null ? 0 : progressMs;
        }

        CurrentTimeText.Text = FormatTime(progressMs);
        DurationText.Text = FormatTime(lastDurationMs);
    }

    private int GetVisibleProgressMs()
    {
        var elapsedMs = currentTrack is null || !isPlaybackMoving
            ? 0
            : (DateTime.UtcNow - progressUpdatedAtUtc).TotalMilliseconds;
        return (int)Math.Min(lastProgressMs + elapsedMs, lastDurationMs);
    }

    private static string GetTrackKey(SpotifyNowPlaying track)
    {
        return $"{track.Title}\n{track.Artist}\n{track.DurationMs}";
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        await playbackService.TogglePlayPauseAsync();
        await RefreshPlaybackAsync();
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await playbackService.SkipPreviousAsync();
        await RefreshAfterCommandAsync();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await playbackService.SkipNextAsync();
        await RefreshAfterCommandAsync();
    }

    private async Task RefreshAfterCommandAsync()
    {
        await Task.Delay(350);
        await RefreshPlaybackAsync();
    }

    private void MenuSeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        isSeeking = true;
    }

    private async void MenuSeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        isSeeking = false;
        var positionMs = (int)Math.Clamp(MenuSeekSlider.Value, 0, lastDurationMs);
        await playbackService.SeekAsync(positionMs);
        SetProgressSource(positionMs, lastDurationMs, true, true);
        UpdateSmoothProgress();
        await RefreshAfterCommandAsync();
    }

    private void UpdateVisualizer()
    {
        var bars = GetVisualizerBars();
        var levels = audioVisualizerService.GetBands(bars.Length);
        var isSilent = levels.All(level => level <= 0);
        for (var i = 0; i < bars.Length; i++)
        {
            var level = i < levels.Length ? levels[i] : 0;
            var target = isPlaybackMoving
                ? isSilent ? 0.18 : 0.16 + level * 1.04
                : 0.12;
            target = Math.Clamp(target, 0.12, 1.0);
            Animate(bars[i], ScaleTransform.ScaleYProperty, bars[i].ScaleY, target, 105);
        }
    }

    private ScaleTransform[] GetVisualizerBars()
    {
        return new[]
        {
            Viz01, Viz02, Viz03, Viz04, Viz05, Viz06, Viz07, Viz08, Viz09, Viz10,
            Viz11, Viz12, Viz13, Viz14, Viz15, Viz16, Viz17, Viz18, Viz19, Viz20
        };
    }

    private static string FormatTime(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(milliseconds, 0));
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    private static void Animate(DependencyObject target, DependencyProperty property, double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (target is UIElement element)
        {
            element.BeginAnimation(property, animation);
            return;
        }

        if (target is Animatable animatable)
        {
            animatable.BeginAnimation(property, animation);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ToggleMenu();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
