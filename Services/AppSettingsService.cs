// File: Services/AppSettingsService.cs
// What it does: Reads and saves local SpotiFloat settings.
// Why it exists: Lets users configure Spotify without editing environment variables.
// RELATED FILES: Services/SpotifyAuthService.cs, MainWindow.xaml.cs, SettingsWindow.xaml.cs

using System.IO;
using System.Text.Json;

namespace SpotiFloat.Services;

public sealed class AppSettingsService
{
    private const string ClientIdEnvName = "SPOTIFLOAT_SPOTIFY_CLIENT_ID";

    private readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpotiFloat",
        "settings.json");

    public string ClientId
    {
        get
        {
            var settings = Load();
            if (!string.IsNullOrWhiteSpace(settings.ClientId))
            {
                return settings.ClientId;
            }

            return Environment.GetEnvironmentVariable(ClientIdEnvName) ?? "";
        }
    }

    public void SaveClientId(string clientId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var settings = new AppSettings(clientId.Trim());
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsPath, json);
    }

    private AppSettings Load()
    {
        if (!File.Exists(settingsPath))
        {
            return new AppSettings("");
        }

        var json = File.ReadAllText(settingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings("");
    }

    private sealed record AppSettings(string ClientId);
}
