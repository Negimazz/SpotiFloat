// File: MainWindow.xaml.cs
// What it does: Connects the overlay UI to Spotify playback data.
// Why it exists: Keeps window behavior, tray actions, refresh timing, and display updates together.
// RELATED FILES: MainWindow.xaml, Services/SpotifyPlaybackService.cs, Models/SpotifyNowPlaying.cs

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
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
    private const int ProgressRollbackToleranceMs = 900;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly bool UseTaskbarOverlay = true;
    private const double TaskbarCompactWidth = 250;
    private const double TaskbarVerticalMargin = 3;
    private const double TaskbarWidgetGap = 8;
    private const double TaskbarFallbackWidgetWidth = 152;
    private const double CompactWidth = 342;
    private const double CompactHeight = 92;
    private const double MenuWidth = 516;
    private const double MenuHeight = 346;

    private readonly SpotifyPlaybackService playbackService = new();
    private readonly DispatcherTimer refreshTimer = new();
    private readonly DispatcherTimer progressTimer = new();
    private readonly DispatcherTimer visualizerTimer = new();
    private readonly DispatcherTimer taskbarTimer = new();
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
    private bool isAlbumRotating;
    private Rect taskbarBounds;
    private Rect taskbarScreenBounds;
    private double taskbarCompactLeft;
    private IntPtr lastTaskbarHandle;
    private Rect? cachedWidgetsBounds;
    private DateTime widgetsBoundsCheckedAtUtc;
    private SpotifyNowPlaying? currentTrack;

    public MainWindow()
    {
        InitializeComponent();

        refreshTimer.Interval = TimeSpan.FromSeconds(2);
        refreshTimer.Tick += async (_, _) => await RefreshPlaybackAsync();
        progressTimer.Interval = TimeSpan.FromMilliseconds(100);
        progressTimer.Tick += (_, _) => UpdateSmoothProgress();
        visualizerTimer.Interval = TimeSpan.FromMilliseconds(65);
        visualizerTimer.Tick += (_, _) => UpdateVisualizer();
        taskbarTimer.Interval = TimeSpan.FromMilliseconds(250);
        taskbarTimer.Tick += (_, _) => PositionTaskbarOverlay();

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
        taskbarTimer.Start();
        audioVisualizerService.Start();
        ShowCompactSurface();
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
        taskbarTimer.Stop();
        audioVisualizerService.Dispose();
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (UseTaskbarOverlay && !isMenuOpen && TaskbarOverlay.Visibility == Visibility.Visible)
        {
            OpenMenu();
            return;
        }

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
        if (!isMenuOpen)
        {
            ShowCompactSurface();
        }
    }

    private void ShowCompactSurface()
    {
        MenuPanel.Visibility = Visibility.Collapsed;
        if (UseTaskbarOverlay)
        {
            CompactOverlay.Visibility = Visibility.Collapsed;
            TaskbarOverlay.Visibility = Visibility.Visible;
            Width = TaskbarCompactWidth;
            PositionTaskbarOverlay();
            return;
        }

        TaskbarOverlay.Visibility = Visibility.Collapsed;
        CompactOverlay.Visibility = Visibility.Visible;
        Width = CompactWidth;
        Height = CompactHeight;
    }

    private void PositionTaskbarOverlay()
    {
        if (!UseTaskbarOverlay || isMenuOpen || !IsVisible)
        {
            return;
        }

        if (!TryUpdateTaskbarLayout(out var widgetsBounds))
        {
            ShowLegacyOverlayFallback();
            return;
        }

        var isHorizontal = taskbarBounds.Width >= taskbarBounds.Height;
        if (!isHorizontal)
        {
            ShowLegacyOverlayFallback();
            return;
        }

        var isAutoHidden = taskbarBounds.Top >= taskbarScreenBounds.Bottom - 2
            || taskbarBounds.Bottom <= taskbarScreenBounds.Top + 2;
        if (isAutoHidden)
        {
            TaskbarOverlay.Opacity = 0;
            return;
        }

        var compactHeight = Math.Clamp(
            taskbarBounds.Height - TaskbarVerticalMargin * 2,
            32,
            44);
        var anchorRight = widgetsBounds?.Right ?? taskbarBounds.Left + TaskbarFallbackWidgetWidth;
        taskbarCompactLeft = Math.Clamp(
            anchorRight + TaskbarWidgetGap,
            taskbarBounds.Left + 4,
            taskbarBounds.Right - TaskbarCompactWidth - 4);

        CompactOverlay.Visibility = Visibility.Collapsed;
        TaskbarOverlay.Visibility = Visibility.Visible;
        Width = TaskbarCompactWidth;
        Height = compactHeight;
        Left = taskbarCompactLeft;
        Top = taskbarBounds.Top + (taskbarBounds.Height - compactHeight) / 2;
        TaskbarOverlay.Opacity = 1;
        KeepTaskbarOverlayAboveTaskbar();
    }

    private void KeepTaskbarOverlayAboveTaskbar()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            windowHandle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void ShowLegacyOverlayFallback()
    {
        var wasAlreadyVisible = CompactOverlay.Visibility == Visibility.Visible;
        TaskbarOverlay.Opacity = 0;
        TaskbarOverlay.Visibility = Visibility.Collapsed;
        CompactOverlay.Visibility = Visibility.Visible;
        Width = CompactWidth;
        Height = CompactHeight;

        if (wasAlreadyVisible)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 8, workArea.Right - CompactWidth - 16);
        Top = workArea.Top + 16;
    }

    private void PositionExpandedMenu()
    {
        if (!UseTaskbarOverlay)
        {
            return;
        }

        if ((taskbarBounds.Width <= 0 || taskbarBounds.Height <= 0)
            && !TryUpdateTaskbarLayout(out _))
        {
            return;
        }

        var taskbarIsAtTop = Math.Abs(taskbarBounds.Top - taskbarScreenBounds.Top)
            < Math.Abs(taskbarScreenBounds.Bottom - taskbarBounds.Bottom);
        Left = Math.Clamp(
            taskbarCompactLeft,
            taskbarScreenBounds.Left + 8,
            taskbarScreenBounds.Right - MenuWidth - 8);
        var desiredTop = taskbarIsAtTop
            ? taskbarBounds.Bottom + 8
            : taskbarBounds.Top - MenuHeight - 8;
        Top = Math.Clamp(
            desiredTop,
            taskbarScreenBounds.Top + 8,
            taskbarScreenBounds.Bottom - MenuHeight - 8);
    }

    private bool TryUpdateTaskbarLayout(out Rect? widgetsBounds)
    {
        widgetsBounds = null;
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        if (taskbarHandle == IntPtr.Zero || !GetWindowRect(taskbarHandle, out var nativeBounds))
        {
            return false;
        }

        var dpiScale = Math.Max(GetDpiForWindow(taskbarHandle) / 96.0, 1);
        taskbarBounds = ToDeviceIndependentRect(nativeBounds, dpiScale);

        var screen = Forms.Screen.FromHandle(taskbarHandle);
        taskbarScreenBounds = ToDeviceIndependentRect(screen.Bounds, dpiScale);

        if (taskbarHandle != lastTaskbarHandle)
        {
            lastTaskbarHandle = taskbarHandle;
            cachedWidgetsBounds = null;
            widgetsBoundsCheckedAtUtc = DateTime.MinValue;
        }

        if (DateTime.UtcNow - widgetsBoundsCheckedAtUtc > TimeSpan.FromSeconds(5))
        {
            cachedWidgetsBounds = FindWidgetsButtonBounds(taskbarHandle, dpiScale, taskbarBounds);
            widgetsBoundsCheckedAtUtc = DateTime.UtcNow;
        }

        widgetsBounds = cachedWidgetsBounds;
        return true;
    }

    private static Rect? FindWidgetsButtonBounds(IntPtr taskbarHandle, double dpiScale, Rect taskbar)
    {
        try
        {
            var root = AutomationElement.FromHandle(taskbarHandle);
            var elements = root.FindAll(
                TreeScope.Descendants,
                System.Windows.Automation.Condition.TrueCondition);
            Rect? bestMatch = null;

            foreach (AutomationElement element in elements)
            {
                var name = element.Current.Name ?? "";
                var automationId = element.Current.AutomationId ?? "";
                var isWidgetsElement = automationId.Contains("widget", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("widget", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("ウィジェット", StringComparison.OrdinalIgnoreCase)
                    || name.Contains('°');
                if (!isWidgetsElement)
                {
                    continue;
                }

                var physicalBounds = element.Current.BoundingRectangle;
                var bounds = new Rect(
                    physicalBounds.Left / dpiScale,
                    physicalBounds.Top / dpiScale,
                    physicalBounds.Width / dpiScale,
                    physicalBounds.Height / dpiScale);
                if (bounds.Width < 24 || bounds.Height < 20 || !bounds.IntersectsWith(taskbar))
                {
                    continue;
                }

                if (bestMatch is null || bounds.Width > bestMatch.Value.Width)
                {
                    bestMatch = bounds;
                }
            }

            return bestMatch;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static Rect ToDeviceIndependentRect(NativeRect bounds, double dpiScale)
    {
        return new Rect(
            bounds.Left / dpiScale,
            bounds.Top / dpiScale,
            (bounds.Right - bounds.Left) / dpiScale,
            (bounds.Bottom - bounds.Top) / dpiScale);
    }

    private static Rect ToDeviceIndependentRect(Drawing.Rectangle bounds, double dpiScale)
    {
        return new Rect(
            bounds.Left / dpiScale,
            bounds.Top / dpiScale,
            bounds.Width / dpiScale,
            bounds.Height / dpiScale);
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
        TaskbarOverlay.Visibility = Visibility.Collapsed;
        CompactOverlay.Visibility = Visibility.Collapsed;
        PositionExpandedMenu();

        MenuControls.Opacity = 0;
        VisualizerGrid.Opacity = 0;
        StartRingRotation();
        UpdateAlbumRotation();

        var smooth = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        Animate(MenuPanel, OpacityProperty, 0, 1, 320, smooth);
        Animate(MenuScale, ScaleTransform.ScaleXProperty, 0.92, 1, 440, smooth);
        Animate(MenuScale, ScaleTransform.ScaleYProperty, 0.92, 1, 440, smooth);
        Animate(MenuTranslate, TranslateTransform.XProperty, -12, 0, 440, smooth);
        Animate(MenuTranslate, TranslateTransform.YProperty, 12, 0, 440, smooth);
        Animate(MenuAlbumScale, ScaleTransform.ScaleXProperty, 0.78, 1, 560, pop, 60);
        Animate(MenuAlbumScale, ScaleTransform.ScaleYProperty, 0.78, 1, 560, pop, 60);
        Animate(MenuAlbumTranslate, TranslateTransform.XProperty, -30, 0, 520, smooth, 60);
        Animate(MenuAlbumTranslate, TranslateTransform.YProperty, 8, 0, 520, smooth, 60);
        Animate(MenuContentTranslate, TranslateTransform.XProperty, 28, 0, 500, smooth, 130);
        Animate(MenuProgressTranslate, TranslateTransform.YProperty, 12, 0, 460, pop, 210);
        Animate(MenuControls, OpacityProperty, 0, 1, 360, smooth, 240);
        Animate(MenuControlsTranslate, TranslateTransform.YProperty, 15, 0, 480, pop, 240);
        Animate(VisualizerGrid, OpacityProperty, 0, 1, 360, smooth, 310);
        Animate(VisualizerTranslate, TranslateTransform.YProperty, 8, 0, 420, smooth, 310);
    }

    private void CloseMenu()
    {
        isMenuOpen = false;
        Animate(MenuPanel, OpacityProperty, MenuPanel.Opacity, 0, 130);
        Animate(MenuScale, ScaleTransform.ScaleXProperty, MenuScale.ScaleX, 0.94, 150);
        Animate(MenuScale, ScaleTransform.ScaleYProperty, MenuScale.ScaleY, 0.94, 150);
        Animate(MenuTranslate, TranslateTransform.XProperty, MenuTranslate.X, -8, 150);
        Animate(MenuTranslate, TranslateTransform.YProperty, MenuTranslate.Y, 8, 150);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            MenuPanel.Visibility = Visibility.Collapsed;
            StopRingRotation();
            StopAlbumRotation();
            ShowCompactSurface();
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
            TaskbarTitleText.Text = currentTrack.Title;
            TaskbarArtistText.Text = currentTrack.Artist;
            MenuTitleText.Text = currentTrack.Title;
            MenuArtistText.Text = $"BY {currentTrack.Artist}";
            PauseIcon.Visibility = currentTrack.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
            PlayIcon.Visibility = currentTrack.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
            isPlaybackMoving = currentTrack.IsPlaying;
            UpdateAlbumRotation();
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
        TaskbarTitleText.Text = title;
        TaskbarArtistText.Text = subtitle;
        MenuTitleText.Text = title;
        MenuArtistText.Text = subtitle;
        PauseIcon.Visibility = Visibility.Collapsed;
        PlayIcon.Visibility = Visibility.Visible;
        UpdateAlbumRotation();
        AlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
        TaskbarAlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
        MenuAlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
        MenuBackdrop.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
        AlbumPlaceholder.Visibility = Visibility.Visible;
        TaskbarAlbumPlaceholder.Visibility = Visibility.Visible;
        SetProgressSource(0, 1, false);
        UpdateSmoothProgress();
    }

    private void SetAlbumArt(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            AlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
            TaskbarAlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
            MenuAlbumArtBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
            MenuBackdrop.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 40));
            AlbumPlaceholder.Visibility = Visibility.Visible;
            TaskbarAlbumPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        AlbumPlaceholder.Visibility = Visibility.Collapsed;
        TaskbarAlbumPlaceholder.Visibility = Visibility.Collapsed;
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
        var taskbarBrush = new ImageBrush(image)
        {
            Stretch = Stretch.UniformToFill
        };
        var backdropBrush = new ImageBrush(image)
        {
            Stretch = Stretch.UniformToFill
        };

        AlbumArtBox.Background = compactBrush;
        TaskbarAlbumArtBox.Background = taskbarBrush;
        MenuAlbumArtBox.Background = menuBrush;
        MenuBackdrop.Background = backdropBrush;
    }

    private void SetProgressSource(int progressMs, int durationMs, bool isSameTrack, bool acceptRollback = false)
    {
        var visibleProgressMs = GetVisibleProgressMs();
        var rollbackMs = visibleProgressMs - progressMs;
        var isMinorPollingRollback = isSameTrack
            && !acceptRollback
            && rollbackMs > 0
            && rollbackMs <= ProgressRollbackToleranceMs;
        lastProgressMs = isMinorPollingRollback ? visibleProgressMs : progressMs;
        lastDurationMs = Math.Max(durationMs, 1);
        progressUpdatedAtUtc = DateTime.UtcNow;
    }

    private void UpdateSmoothProgress()
    {
        var progressMs = GetVisibleProgressMs();
        var scale = Math.Clamp((double)progressMs / lastDurationMs, 0, 1);
        ProgressScale.ScaleX = currentTrack is null ? 0 : Math.Max(scale, 0.02);
        TaskbarProgressScale.ScaleX = currentTrack is null ? 0 : Math.Max(scale, 0.02);
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
        var levels = isPlaybackMoving ? audioVisualizerService.GetBands(bars.Length) : new double[bars.Length];
        var isSilent = levels.All(level => level <= 0);

        for (var i = 0; i < bars.Length; i++)
        {
            var target = isSilent ? 0.12 : 0.14 + levels[i] * 0.86;
            Animate(bars[i], ScaleTransform.ScaleYProperty, bars[i].ScaleY, target, 90);
        }
    }

    private ScaleTransform[] GetVisualizerBars()
    {
        return new[] { Viz01, Viz02, Viz03, Viz04, Viz05, Viz06, Viz07, Viz08, Viz09, Viz10 };
    }

    private void StartRingRotation()
    {
        var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(5))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        RingRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
        PopupBorderRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void StopRingRotation()
    {
        RingRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        PopupBorderRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        RingRotate.Angle = 0;
        PopupBorderRotate.Angle = 0;
    }

    private void UpdateAlbumRotation()
    {
        if (isMenuOpen && isPlaybackMoving)
        {
            StartAlbumRotation();
            return;
        }

        StopAlbumRotation();
    }

    private void StartAlbumRotation()
    {
        if (isAlbumRotating)
        {
            return;
        }

        isAlbumRotating = true;
        var startAngle = AlbumRotate.Angle % 360;
        var animation = new DoubleAnimation(startAngle, startAngle + 360, TimeSpan.FromSeconds(8))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        AlbumRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void StopAlbumRotation()
    {
        if (!isAlbumRotating)
        {
            return;
        }

        var angle = AlbumRotate.Angle % 360;
        AlbumRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        AlbumRotate.Angle = angle;
        isAlbumRotating = false;
    }

    private static string FormatTime(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(milliseconds, 0));
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    private static void Animate(
        DependencyObject target,
        DependencyProperty property,
        double from,
        double to,
        int milliseconds,
        IEasingFunction? easingFunction = null,
        int delayMilliseconds = 0)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
            EasingFunction = easingFunction ?? new CubicEase { EasingMode = EasingMode.EaseOut }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
