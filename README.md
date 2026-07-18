<!--
File: README.md
What it does: Explains how to run and configure SpotiFloat.
Why it exists: Gives the usage steps for the Spotify overlay app.
RELATED FILES: SpotiFloat.csproj, MainWindow.xaml, Services/SpotifyPlaybackService.cs
-->

# SpotiFloat

Spotifyデスクトップアプリで再生中の曲をタスクバーに表示する、Windows用オーバーレイアプリです。

## できること

- タスクバーの左側(ウィジェットの右横)に小さく表示します。
- Windowsのメディアセッションから現在再生中の曲を取得します。
- Spotifyのセッションだけを対象にします。
- ウィンドウはドラッグで移動できます。
- タスクトレイの右クリックメニューから表示切替と終了ができます。

## 起動

```powershell
dotnet run
```
