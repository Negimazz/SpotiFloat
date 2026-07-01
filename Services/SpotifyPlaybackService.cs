// File: Services/SpotifyPlaybackService.cs
// What it does: Reads the currently playing Spotify desktop session from Windows.
// Why it exists: Keeps local media session parsing out of the overlay window.
// RELATED FILES: Models/SpotifyNowPlaying.cs, MainWindow.xaml.cs

using SpotiFloat.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotiFloat.Services;

public sealed class SpotifyPlaybackService
{
    public async Task<SpotifyNowPlaying?> GetNowPlayingAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetSessions().FirstOrDefault(IsSpotifySession);
        if (session is null)
        {
            return null;
        }

        var media = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();
        var playback = session.GetPlaybackInfo();
        if (playback.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(media.Title) ? "Unknown track" : media.Title;
        var artist = string.IsNullOrWhiteSpace(media.Artist) ? "Spotify" : media.Artist;
        var albumArtBytes = await ReadThumbnailAsync(media.Thumbnail);
        var progressMs = (int)Math.Max(timeline.Position.TotalMilliseconds, 0);
        var durationMs = (int)Math.Max(timeline.EndTime.TotalMilliseconds, 1);

        return new SpotifyNowPlaying(title, artist, albumArtBytes, progressMs, durationMs);
    }

    private static bool IsSpotifySession(GlobalSystemMediaTransportControlsSession session)
    {
        return session.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        using var stream = await thumbnail.OpenReadAsync();
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
