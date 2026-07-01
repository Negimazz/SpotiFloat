// File: Services/SpotifyAuthService.cs
// What it does: Handles Spotify OAuth sign-in, token refresh, and token storage.
// Why it exists: Lets the overlay read Spotify playback without storing a client secret.
// RELATED FILES: Services/SpotifyPlaybackService.cs, MainWindow.xaml.cs, README.md

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotiFloat.Services;

public sealed class SpotifyAuthService
{
    private const string ClientIdEnvName = "SPOTIFLOAT_SPOTIFY_CLIENT_ID";
    private const string RedirectUri = "http://127.0.0.1:54321/callback/";
    private const string Scope = "user-read-currently-playing user-read-playback-state";

    private readonly HttpClient httpClient = new();
    private readonly string tokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpotiFloat",
        "spotify-token.json");

    private SpotifyToken? token;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
    public bool HasToken => token is not null;

    private string ClientId => Environment.GetEnvironmentVariable(ClientIdEnvName) ?? "";

    public async Task LoadSavedTokenAsync()
    {
        if (!File.Exists(tokenPath))
        {
            return;
        }

        var json = await File.ReadAllTextAsync(tokenPath);
        token = JsonSerializer.Deserialize<SpotifyToken>(json);
    }

    public async Task SignInAsync()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException($"Set {ClientIdEnvName} first.");
        }

        var verifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(verifier);
        var state = CreateCodeVerifier();
        var authorizeUrl = BuildAuthorizeUrl(challenge, state);

        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();

        Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

        var context = await listener.GetContextAsync();
        await WriteBrowserResponseAsync(context.Response);

        var request = context.Request.QueryString;
        if (request["state"] != state)
        {
            throw new InvalidOperationException("Spotify sign-in state did not match.");
        }

        var code = request["code"];
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Spotify did not return an authorization code.");
        }

        token = await RequestTokenAsync(code, verifier);
        await SaveTokenAsync();
    }

    public async Task<string> GetAccessTokenAsync()
    {
        if (token is null)
        {
            throw new InvalidOperationException("Spotify is not connected.");
        }

        if (token.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            token = await RefreshTokenAsync(token.RefreshToken);
            await SaveTokenAsync();
        }

        return token.AccessToken;
    }

    private string BuildAuthorizeUrl(string challenge, string state)
    {
        return "https://accounts.spotify.com/authorize"
            + $"?client_id={Uri.EscapeDataString(ClientId)}"
            + "&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + $"&scope={Uri.EscapeDataString(Scope)}"
            + $"&state={Uri.EscapeDataString(state)}"
            + "&code_challenge_method=S256"
            + $"&code_challenge={Uri.EscapeDataString(challenge)}";
    }

    private async Task<SpotifyToken> RequestTokenAsync(string code, string verifier)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier
        });

        return await SendTokenRequestAsync(content);
    }

    private async Task<SpotifyToken> RefreshTokenAsync(string refreshToken)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });

        var refreshed = await SendTokenRequestAsync(content);
        return refreshed.RefreshToken.Length > 0 ? refreshed : refreshed with { RefreshToken = refreshToken };
    }

    private async Task<SpotifyToken> SendTokenRequestAsync(HttpContent content)
    {
        using var response = await httpClient.PostAsync("https://accounts.spotify.com/api/token", content);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Spotify token request failed: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new SpotifyToken(
            root.GetProperty("access_token").GetString() ?? "",
            root.TryGetProperty("refresh_token", out var refreshToken)
                ? refreshToken.GetString() ?? ""
                : "",
            DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()));
    }

    private async Task SaveTokenAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        var json = JsonSerializer.Serialize(token);
        await File.WriteAllTextAsync(tokenPath, json);
    }

    private static async Task WriteBrowserResponseAsync(HttpListenerResponse response)
    {
        const string html = "<html><body><h2>SpotiFloat is connected.</h2>You can close this tab.</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static string CreateCodeVerifier()
    {
        return Base64Url(RandomNumberGenerator.GetBytes(64));
    }

    private static string CreateCodeChallenge(string verifier)
    {
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed record SpotifyToken(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAtUtc);
}
