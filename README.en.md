English | [简体中文](README.md)

<img src="branding/logo.png" />

# TiktokLiveRec Danmu Edition

[![GitHub license](https://img.shields.io/github/license/LUOYUANYUAN12138/TiktokLiveRec-Danmu)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/blob/master/LICENSE) [![Actions](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/actions/workflows/build.yml/badge.svg)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/actions/workflows/build.yml) [![Platform](https://img.shields.io/badge/platform-Windows-blue?logo=windowsxp&color=1E9BFA)](https://dotnet.microsoft.com/en-us/download/dotnet/latest/runtime) [![GitHub downloads](https://img.shields.io/github/downloads/LUOYUANYUAN12138/TiktokLiveRec-Danmu/total)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/releases)
[![GitHub downloads](https://img.shields.io/github/downloads/LUOYUANYUAN12138/TiktokLiveRec-Danmu/latest/total)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/releases)

A desktop tool with a graphical UI for unattended Douyin / TikTok live recording and real-time danmu (chat) reception.

This repository is a secondary development based on [emako/TiktokLiveRec](https://github.com/emako/TiktokLiveRec). In addition to live recording, it adds **real-time Douyin danmu** (chat, gifts, likes, entering, follows, etc.) and a **live preview** window.

Runtime playback and recording depend on FFmpeg and FFplay.

## Features

- 🎬 **Auto Recording**: Detects when a streamer goes live and records automatically. Supports HLS / FLV, optional segmentation, and post-recording conversion (TS/FLV → MP4/MKV).
- 💬 **Real-time Danmu**: Receives Douyin live-room messages via WebSocket — chat, gifts, likes, entering, follows, emoji, room stats, rankings, fan clubs. Filterable by type and exportable to logs.
- 🎁 **Gift Display**: Parses gift name, icon, count, and diamond value. Gift names are resolved via a local gift catalog (static table + Douyin gift-list API).
- 👀 **Live Preview**: An embedded playback window to preview the live room while recording.
- 🔔 **Live Notifications**: Windows toast, custom sound, email, or auto-open the room URL.
- 🖥️ **System Tray**: Minimize to tray; the tray icon reflects recording status.
- 🌐 **Multilingual**: Simplified Chinese, Traditional Chinese, English, Japanese.
- ⚡ **Keep-awake / Auto-shutdown**: Prevent system sleep while recording; optional scheduled shutdown after recording.

## Release Packages

Windows releases are provided in two forms:

- Installer `.exe`: recommended for most users. Run the installer and launch the app from the Start menu or desktop shortcut.
- Portable `.7z`: unzip the entire package and run `TiktokLiveRec.exe` inside the extracted folder.

> **Important**: the distributable unit is the whole published folder, not just a single `TiktokLiveRec.exe`. The app also needs files such as `ffmpeg.exe`, `ffplay.exe`, `Assets/Danmu/sign.js`, and `Assets/Danmu/a_bogus.js`.

## Runtime Requirements

For end users who download the Release package:

- Windows portable / installer: no separate .NET installation is normally required because the release is published as `SelfContained`.

For developers building from source:

- Windows: [.NET SDK 9.0](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)

## Quick Start

1. Download the [latest Release](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/releases), extract (portable) or install (installer).
2. Run `TiktokLiveRec.exe`.
3. Add a live-room URL in the main window (see [Supported platforms](#live-streaming)).
4. Enable **Recording**, **Notifications**, **Danmu**, etc. in **Settings** as needed.
5. To receive **gift** messages, fill in your logged-in Douyin Cookie in Settings (see [Cookie Configuration](#cookie-configuration-important)).

## Cookie Configuration (Important)

Douyin delivers live-room messages by sensitivity tier. **Without a logged-in Cookie you can only receive basic messages (chat, likes, entering) — gifts are not delivered.** To receive full danmu (including gifts), configure a logged-in Douyin Cookie:

1. Open `https://live.douyin.com/` in a browser and log in to your Douyin account.
2. Press `F12` → Network panel, then refresh the page.
3. Click any request → Request Headers → copy the full `Cookie` header value.
4. Open the app's **Settings**, paste the Cookie into the "Douyin Cookie" field, and save.

See [GETCOOKIE_DOUYIN.md](doc/GETCOOKIE_DOUYIN.md) or [GETCOOKIE_TIKTOK.md](doc/GETCOOKIE_TIKTOK.md) for detailed steps.

> Note: the app automatically adds the auxiliary Cookie fields Douyin requires (e.g. `x-web-secsdk-uid`). The Cookie is stored locally and never uploaded.

## Build And Publish

To build from source:

```bash
dotnet restore src/TiktokLiveRec.WPF/TiktokLiveRec.WPF.csproj
dotnet build src/TiktokLiveRec.WPF/TiktokLiveRec.WPF.csproj --configuration Release
```

To create Windows release packages:

1. Place `ffmpeg.exe` and `ffplay.exe` in the `build/` directory.
2. Run `build/publish_win-x64.cmd`.
3. The script publishes the app, verifies required runtime files, creates `publish.7z`, and then creates the installer `.exe` with MicaSetup.

## Screen Shot

<img src="assets/image-20241113165355466.png" alt="image-20241113165355466" style="transform:scale(0.5);" />

## Live Streaming

Supported platforms:

| Site          | Status    |
| ------------- | --------- |
| Douyin (抖音) | Available |
| Tiktok        | Available |

How to add a live room:

```bash
# Douyin room URL like following:
https://live.douyin.com/XXX
https://www.douyin.com/root/live/XXX

# Tiktok room URL like following:
https://www.tiktok.com/@XXX/live
```

> Note: the danmu feature currently supports Douyin only; TikTok is not yet supported.

## Support OS

Current active target is Windows only.

| OS      | Framework | Status    |
| ------- | --------- | --------- |
| Windows | WPF       | Available |

## Privacy Policy

See the [Privacy Policy](PrivacyPolicy.md).

## License

This project keeps the upstream [MIT License](LICENSE). Please retain the original license notice and attribution when redistributing or modifying it.

## Thanks

- [emako/TiktokLiveRec](https://github.com/emako/TiktokLiveRec): upstream project.
- [DouyinLiveRecorder](https://github.com/ihmily/DouyinLiveRecorder): referenced string data (e.g. regex patterns).
