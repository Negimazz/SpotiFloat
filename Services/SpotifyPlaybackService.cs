// File: Services/SpotifyPlaybackService.cs
// What it does: Reads the currently playing Spotify track from the Spotify Web API.
// Why it exists: Keeps Spotify API parsing out of the overlay window.
// RELATED FILES: Services/SpotifyAuthService.cs, Models/SpotifyNowPlaying.cs, MainWindow.xaml.cs

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SpotiFloat.Models;

namespace SpotiFloat.Services;

public sealed class SpotifyPlaybackService
{
    private readonly HttpClient httpClient = new();
    private readonly SpotifyAuthService authService;

    public SpotifyPlaybackService(SpotifyAuthService authService)
    {
        this.authService = authService;
    }

    public async Task<SpotifyNowPlaying?> GetNowPlayingAsync()
    {
        var accessToken = await authService.GetAccessTokenAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.spotify.com/v1/me/player/currently-playing?market=from_token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        if (!root.TryGetProperty("item", out var item) || item.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var title = item.GetProperty("name").GetString() ?? "Unknown track";
        var artists = GetArtists(item);
        var imageUrl = GetAlbumImage(item);
        var progressMs = root.TryGetProperty("progress_ms", out var progress)
            ? progress.GetInt32()
            : 0;
        var durationMs = item.TryGetProperty("duration_ms", out var duration)
            ? duration.GetInt32()
            : 1;

        return new SpotifyNowPlaying(title, artists, imageUrl, progressMs, durationMs);
    }

    private static string GetArtists(JsonElement item)
    {
        if (!item.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
        {
            return "Spotify";
        }

        var names = artists
            .EnumerateArray()
            .Select(GetArtistName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        return names.Length > 0 ? string.Join(", ", names) : "Spotify";
    }

    private static string? GetArtistName(JsonElement artist)
    {
        return artist.TryGetProperty("name", out var name) ? name.GetString() : null;
    }

    private static string? GetAlbumImage(JsonElement item)
    {
        if (!item.TryGetProperty("album", out var album)
            || !album.TryGetProperty("images", out var images)
            || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return images.EnumerateArray()
            .Select(image => image.GetProperty("url").GetString())
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
    }
}
