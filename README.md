<!--
File: README.md
What it does: Explains how to run and configure SpotiFloat.
Why it exists: Gives the setup steps needed for the Spotify overlay app.
RELATED FILES: SpotiFloat.csproj, Services/SpotifyAuthService.cs, Services/SpotifyPlaybackService.cs
-->

# SpotiFloat

Spotifyで再生中の曲だけを表示する、小さなWindows用オーバーレイアプリです。

## できること

- 画面の最前面に小さく表示します。
- Spotify Web APIから現在再生中の曲を取得します。
- YouTubeなどSpotify以外の再生は対象外です。
- ウィンドウはドラッグで移動できます。

## 必要な設定

1. Spotify Developer Dashboardでアプリを作成します。
2. Redirect URIに次を追加します。

```text
http://127.0.0.1:54321/callback/
```

3. 環境変数にClient IDを設定します。

```powershell
[Environment]::SetEnvironmentVariable("SPOTIFLOAT_SPOTIFY_CLIENT_ID", "your_client_id", "User")
```

新しいターミナルやIDEを開き直すと反映されます。

## 起動

```powershell
dotnet run
```

初回起動時は `Connect Spotify` を押してSpotifyにログインしてください。
