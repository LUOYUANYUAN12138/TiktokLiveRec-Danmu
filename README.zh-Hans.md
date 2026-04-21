[English](README.md) | [简体中文](README.zh-Hans.md)

<img src="branding/logo.png" />

# TiktokLiveRec 弹幕增强版

[![GitHub license](https://img.shields.io/github/license/LUOYUANYUAN12138/TiktokLiveRec-Danmu)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/blob/master/LICENSE) [![Actions](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/actions/workflows/build.yml/badge.svg)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/actions/workflows/build.yml) [![Platform](https://img.shields.io/badge/platform-Windows-blue?logo=windowsxp&color=1E9BFA)](https://dotnet.microsoft.com/en-us/download/dotnet/latest/runtime) [![GitHub downloads](https://img.shields.io/github/downloads/LUOYUANYUAN12138/TiktokLiveRec-Danmu/total)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/releases)
[![GitHub downloads](https://img.shields.io/github/downloads/LUOYUANYUAN12138/TiktokLiveRec-Danmu/latest/total)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/releases)

具有用户界面、无人值守操作和直播流录制功能。

本仓库基于 [emako/TiktokLiveRec](https://github.com/emako/TiktokLiveRec) 二次开发，在原有直播录制能力之外，增加了抖音弹幕功能。

运行时的录制和播放依赖 FFmpeg 与 FFplay。

## 发布包说明

Windows 发布建议同时提供两种形式：

- 安装版 `.exe`：适合普通用户，下载安装后直接从开始菜单或桌面快捷方式启动。
- 便携版 `.7z`：适合绿色软件用户，解压整个压缩包后，在目录内直接运行 `TiktokLiveRec.exe`。

重要：对外分发时，发布单位应是整个 publish 目录，而不是单独拎出一个 `TiktokLiveRec.exe`。程序还依赖 `ffmpeg.exe`、`ffplay.exe`、`Assets/Danmu/sign.js`、`Assets/Danmu/a_bogus.js` 等文件。

## 运行环境说明

对于下载 Release 的普通用户：

- Windows 便携版：通常不需要单独安装 .NET，当前 Release 按 `SelfContained` + `PublishSingleFile` 方式发布。
- Windows 安装版：运行时要求与便携版一致，安装器本质上只是把完整 publish 目录打包安装。

对于需要自行编译源码的开发者：

- Windows：需要 [.NET SDK 9.0](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
- 其他仍在开发中的平台：需要 [.NET SDK 9.0](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)

## 构建与发布

源码构建：

```bash
dotnet restore src/TiktokLiveRec.WPF/TiktokLiveRec.WPF.csproj
dotnet build src/TiktokLiveRec.WPF/TiktokLiveRec.WPF.csproj --configuration Release
```

制作 Windows 发布包：

1. 将 `ffmpeg.exe` 和 `ffplay.exe` 放到 `build/` 目录。
2. 运行 `build/publish_win-x64.cmd`。
3. 脚本会发布程序、校验关键运行文件、生成 `publish.7z`，然后通过 MicaSetup 生成安装版 `.exe`。

## 截图

<img src="assets/image-20241113165448238.png" alt="image-20241113165448238" style="transform:scale(0.5);" />

## 直播录制

支持以下直播平台

| 平台              | 状态 |
| ----------------- | ---- |
| Douyin (中国抖音) | 支持 |
| Tiktok (海外抖音) | 支持 |

怎么添加直播间：

```bash
# 国内抖音直播间链接类似如下：
https://live.douyin.com/XXX
https://www.douyin.com/root/live/XXX

# 海外抖音直播间链接类似如下：
https://www.tiktok.com/@XXX/live
```

## 支持系统

为了加快初版开发实现，首版基于 WPF 开发了 Windows 版本。

其他系统的实现会基于我个人需求或其他用户的反响。

另外 macOS 估计会是下一个支持的系统。

| 操作系统 | 开发框架 | 状态   |
| -------- | -------- | ------ |
| Windows  | WPF      | 支持   |
| macOS    | Avalonia | 开发中 |
| Ubuntu   | Avalonia | 待开发 |
| Android  | Avalonia | 待开发 |
| iOS      | Avalonia | 待开发 |
| tvOS     | 待定     | 待开发 |

## 自有Cookie

来看看 [GETCOOKIE_DOUYIN.md](doc/GETCOOKIE_DOUYIN.md) 或 [GETCOOKIE_TIKTOK.md](doc/GETCOOKIE_TIKTOK.md)。

## 隐私政策

[查看隐私政策](PrivacyPolicy.zh-Hans.md)。

## 许可证

本项目沿用上游的 [MIT 许可证](LICENSE)。分发或继续修改时，请保留原始许可证与来源说明。

## 鸣谢

为了节约后续维护成本，直接参考了部分来自 [DouyinLiveRecorder](https://github.com/ihmily/DouyinLiveRecorder) 的字符串数据比如正则表达式。
