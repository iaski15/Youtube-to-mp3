[README.md](https://github.com/user-attachments/files/31573930/README.md)
# YouTube to MP3 & MP4

A small Windows desktop app that turns YouTube links into **MP3** files (or downloads the video as **MP4**) — no browser, no Python setup. Just paste a link and press Enter.

## Features

- **Convert to MP3** — grabs the best available audio stream and encodes it at 192 kbps using Windows Media Foundation
- **Download as MP4** — prefers clean h264/AAC streams (up to 1080p), remuxed via ffmpeg so you get a proper `.mp4`
- **Video preview card** — title, channel name and thumbnail shown before you download
- **Paste or drag & drop** the link straight into the window; `Enter` starts the conversion
- **Live progress bar** with real-time percent from yt-dlp
- **Cancel anytime**, then a one-click "open folder" when done
- **Friendly errors** — private, members-only, age-restricted, unavailable and rate-limited videos all get plain-English messages
- **Zero dependencies to install** — `yt-dlp` (and ffmpeg) are bundled in the `Tools/` folder

## Requirements

- Windows 10 or 11 (WPF app)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run a built copy, or the .NET 10 SDK to build from source

## Building

```powershell
git clone <your-repo-url>
cd Youtube-to-mp3-main
dotnet publish -c Release -r win-x64 --self-contained false
```

The output lands in `bin/Release/net10.0-windows/win-x64/publish/` — the app and its bundled `Tools/yt-dlp.exe` go there together, so you can copy that folder anywhere and run it.

> The build assumes `Tools\yt-dlp.exe` is present (it's committed with the repo). It must stay next to the executable at runtime.

## Usage

1. Copy a YouTube video link
2. Open the app, paste (`Ctrl+V` or the paste button) and hit **Enter**
3. Pick where to save when prompted
4. Watch the progress bar — done! Use *Open folder* to find your file

Tip: if YouTube starts showing bot-checks on a video, download a fresh [`yt-dlp`](https://github.com/yt-dlp/yt-dlp) Windows build and replace `Tools\yt-dlp.exe`. That's almost always the fix.

## How it works

| Step | Tooling |
| --- | --- |
| Video info (title, uploader, thumbnail) | `yt-dlp --dump-single-json` |
| Audio download (best quality) | `yt-dlp -f bestaudio/best` |
| MP3 encoding @ 192 kbps | NAudio + Windows Media Foundation |
| MP4 download & merging | yt-dlp + ffmpeg (bundled with yt-dlp) |

## Project structure

```
YoutubeToMp3.csproj      .NET 10 WPF project (only package: NAudio)
App.xaml(.cs)             Application entry point
MainWindow.xaml(.cs)      Entire UI + download/encoding logic
Tools\yt-dlp.exe          Bundled downloader
```

## Tech stack

- C# / .NET 10, WPF (custom dark UI, no third-party UI libraries)
- [NAudio](https://github.com/naudio/NAudio) — Media Foundation MP3 encoding
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) — downloading
- [ffmpeg](https://ffmpeg.org/) — video merging/remuxing in MP4 mode (bundled alongside yt-dlp)
