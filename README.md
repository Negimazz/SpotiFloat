<!--
File: README.md
What it does: Explains how to run and configure SpotiFloat.
Why it exists: Gives the usage steps for the Spotify overlay app.
RELATED FILES: SpotiFloat.csproj, MainWindow.xaml, Services/SpotifyPlaybackService.cs
-->

# SpotiFloat

Spotifyデスクトップアプリで再生中の曲だけを表示する、小さなWindows用オーバーレイアプリです。

## できること

- 画面の最前面に小さく表示します。
- Windowsのメディアセッションから現在再生中の曲を取得します。
- Spotifyのセッションだけを対象にします。
- ウィンドウはドラッグで移動できます。
- タスクトレイの右クリックメニューから表示切替と終了ができます。

## 必要なもの

- Spotifyデスクトップアプリ
- Spotifyで再生中の曲

Spotify Developer Dashboard、Client ID、ログイン連携は不要です。

## 起動

```powershell
dotnet run
```
