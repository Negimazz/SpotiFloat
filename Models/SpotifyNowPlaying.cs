// File: Models/SpotifyNowPlaying.cs
// What it does: Stores the Spotify track data shown by the overlay.
// Why it exists: Keeps playback data separate from UI and API parsing code.
// RELATED FILES: Services/SpotifyPlaybackService.cs, MainWindow.xaml.cs

namespace SpotiFloat.Models;

public sealed record SpotifyNowPlaying(
    string Title,
    string Artist,
    string? AlbumArtUrl,
    int ProgressMs,
    int DurationMs);
