[English](README.md) | [简体中文](README.zh-Hans.md)

<img src="branding/logo.png" />

# TiktokLiveRec Danmu Edition

[![GitHub license](https://img.shields.io/github/license/LUOYUANYUAN12138/TiktokLiveRec-Danmu)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/blob/master/LICENSE) [![Actions](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/actions/workflows/build.yml/badge.svg)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/actions/workflows/build.yml) [![Platform](https://img.shields.io/badge/platform-Windows-blue?logo=windowsxp&color=1E9BFA)](https://dotnet.microsoft.com/en-us/download/dotnet/latest/runtime) [![GitHub downloads](https://img.shields.io/github/downloads/LUOYUANYUAN12138/TiktokLiveRec-Danmu/total)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/releases)
[![GitHub downloads](https://img.shields.io/github/downloads/LUOYUANYUAN12138/TiktokLiveRec-Danmu/latest/total)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/releases)

With a graphical UI, unattended operation, and live streaming recording capabilities.

This repository is a secondary development based on [emako/TiktokLiveRec](https://github.com/emako/TiktokLiveRec). In addition to live recording, it adds Douyin danmu support.

Runtime playback and recording depend on FFmpeg and FFplay.

## Release Packages

Windows releases are provided in two forms:

- Installer `.exe`: recommended for most users. Run the installer and launch the app from the Start menu or desktop shortcut.
- Portable `.7z`: unzip the entire package and run `TiktokLiveRec.exe` inside the extracted folder.

Important: the distributable unit is the whole published folder, not just a single `TiktokLiveRec.exe`. The app also needs files such as `ffmpeg.exe`, `ffplay.exe`, `Assets/Danmu/sign.js`, and `Assets/Danmu/a_bogus.js`.

## Runtime Requirements

For end users who download the Release package:

- Windows portable package: no separate .NET installation is normally required because the release is published as `SelfContained` + `PublishSingleFile`.
- Windows installer package: same runtime behavior as the portable package. The installer only packages the published folder.

For developers building from source:

- Windows: [.NET SDK 9.0](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
- Other platforms under development: [.NET SDK 9.0](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)

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

Support following live site.

| Site          | Status    |
| ------------- | --------- |
| Douyin (抖音) | Available |
| Tiktok        | Available |

How to add live room:

```bash
# Douyin room URL like following:
https://live.douyin.com/XXX
https://www.douyin.com/root/live/XXX

# Tiktok room URL like following:
https://www.tiktok.com/@XXX/live
```

## Support OS

For rapid development, first implement WPF-based windows support.

Implementing other OS's based on my personal needs or other user reactions.

BTW macOS may will be the next supported OS.

| OS      | Framework | Status            |
| ------- | --------- | ----------------- |
| Windows | WPF       | Available         |
| macOS   | Avalonia  | Under Development |
| Ubuntu  | Avalonia  | TBD               |
| Android | Avalonia  | TBD               |
| iOS     | Avalonia  | TBD               |
| tvOS    | TBD       | TBD               |

## Your Cookie Can

Check it from [GETCOOKIE_DOUYIN.md](doc/GETCOOKIE_DOUYIN.md) or [GETCOOKIE_TIKTOK.md](doc/GETCOOKIE_TIKTOK.md).

## Privacy Policy

See the [Privacy Policy](PrivacyPolicy.md).

## License

This project keeps the upstream [MIT License](LICENSE). Please retain the original license notice and attribution when redistributing or modifying it.

## Thanks

To save maintenance costs, refer to the specific string data form [DouyinLiveRecorder](https://github.com/ihmily/DouyinLiveRecorder), just like regex and so on.

