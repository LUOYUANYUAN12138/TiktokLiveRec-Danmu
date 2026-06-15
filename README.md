[English](README.en.md) | 简体中文

<img src="branding/logo.png" />

# TiktokLiveRec 弹幕增强版

[![GitHub license](https://img.shields.io/github/license/LUOYUANYUAN12138/TiktokLiveRec-Danmu)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/blob/master/LICENSE) [![Actions](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/actions/workflows/build.yml/badge.svg)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/actions/workflows/build.yml) [![Platform](https://img.shields.io/badge/platform-Windows-blue?logo=windowsxp&color=1E9BFA)](https://dotnet.microsoft.com/en-us/download/dotnet/latest/runtime) [![GitHub downloads](https://img.shields.io/github/downloads/LUOYUANYUAN12138/TiktokLiveRec-Danmu/total)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/releases)
[![GitHub downloads](https://img.shields.io/github/downloads/LUOYUANYUAN12138/TiktokLiveRec-Danmu/latest/total)](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/releases)

一个带图形界面、支持无人值守的抖音 / TikTok 直播录制与弹幕接收桌面工具。

本仓库基于 [emako/TiktokLiveRec](https://github.com/emako/TiktokLiveRec) 二次开发，在原有直播录制能力之外，增加了**抖音弹幕实时接收**（聊天、礼物、点赞、进入直播间、关注等）与**直播预览**功能。

运行时的录制和播放依赖 FFmpeg 与 FFplay。

## 功能特性

- 🎬 **自动直播录制**：开播自动检测并录制，支持 HLS / FLV 流，可选分段录制与录制后转码（TS/FLV → MP4/MKV）。
- 💬 **实时弹幕**：通过 WebSocket 接收抖音直播间弹幕，支持文字聊天、礼物、点赞、进入直播间、关注、表情、房间统计、排行榜、粉丝团等消息类型，可按类型筛选显示，弹幕可导出为日志。
- 🎁 **礼物显示**：自动解析礼物名称、图标、数量、钻石价值。礼物名通过本地礼物目录（静态表 + 抖音礼物列表接口）补全。
- 👀 **直播预览**：内嵌播放窗口，可在录制的同时预览直播间画面。
- 🔔 **开播通知**：支持 Windows 系统通知、自定义提示音、邮件通知、自动打开直播间。
- 🖥️ **系统托盘**：最小化到托盘后台运行，录制状态实时反映在托盘图标。
- 🌐 **多语言**：支持简体中文、繁体中文、英文、日文。
- ⚡ **防休眠 / 定时关机**：录制时可阻止系统休眠；支持录制结束后定时关机。

## 发布包说明

Windows 发布提供两种形式：

- 安装版 `.exe`：适合普通用户，下载安装后直接从开始菜单或桌面快捷方式启动。
- 便携版 `.7z`：适合绿色软件用户，解压整个压缩包后，在目录内直接运行 `TiktokLiveRec.exe`。

> **重要**：对外分发时，发布单位应是整个 publish 目录，而不是单独拎出一个 `TiktokLiveRec.exe`。程序还依赖 `ffmpeg.exe`、`ffplay.exe`、`Assets/Danmu/sign.js`、`Assets/Danmu/a_bogus.js` 等文件。

## 运行环境说明

对于下载 Release 的普通用户：

- Windows 便携版 / 安装版：通常不需要单独安装 .NET，当前 Release 按 `SelfContained` 方式发布，自带运行时。

对于需要自行编译源码的开发者：

- Windows：需要 [.NET SDK 9.0](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)

## 快速开始

1. 下载 [最新 Release](https://github.com/LUOYUANYUAN12138/TiktokLiveRec-Danmu/releases)，解压（便携版）或安装（安装版）。
2. 运行 `TiktokLiveRec.exe`。
3. 在主界面添加直播间地址（见下方[支持的直播平台](#直播录制)）。
4. 在「设置」中按需开启**录制**、**通知**、**弹幕**等功能。
5. 如果需要接收**礼物**等弹幕消息，请在设置中填写抖音登录 Cookie（见下方 [Cookie 配置](#cookie-配置重要)）。

## Cookie 配置（重要）

抖音对直播弹幕按消息敏感度分级下发。**未登录状态下只能收到聊天、点赞、进入等基础消息，收不到礼物消息。** 要完整接收弹幕（含礼物），需要配置登录后的抖音 Cookie：

1. 用浏览器打开 `https://live.douyin.com/` 并登录抖音账号。
2. 按 `F12` 打开开发者工具 → Network（网络）面板，刷新页面。
3. 点击任意一个请求 → Request Headers → 复制完整的 `Cookie` 字段值。
4. 打开本程序的「设置」窗口，将 Cookie 粘贴到「抖音 Cookie」输入框，保存。

详细获取步骤可参考 [GETCOOKIE_DOUYIN.md](doc/GETCOOKIE_DOUYIN.md) 或 [GETCOOKIE_TIKTOK.md](doc/GETCOOKIE_TIKTOK.md)。

> 说明：程序会自动补充 `x-web-secsdk-uid` 等抖音要求的辅助 Cookie 字段，无需手动添加。Cookie 仅保存在本地，不会上传到任何服务器。

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

支持以下直播平台：

| 平台              | 状态 |
| ----------------- | ---- |
| Douyin (中国抖音) | 支持 |
| Tiktok (海外抖音) | 支持 |

添加直播间的方式：

```bash
# 国内抖音直播间链接类似如下：
https://live.douyin.com/XXX
https://www.douyin.com/root/live/XXX

# 海外抖音直播间链接类似如下：
https://www.tiktok.com/@XXX/live
```

> 注：弹幕功能目前仅支持抖音（Douyin），TikTok 暂不支持。

## 支持系统

当前仅保留 Windows WPF 版本。

| 操作系统 | 开发框架 | 状态 |
| -------- | -------- | ---- |
| Windows  | WPF      | 支持 |

## 隐私政策

[查看隐私政策](PrivacyPolicy.zh-Hans.md)。

## 许可证

本项目沿用上游的 [MIT 许可证](LICENSE)。分发或继续修改时，请保留原始许可证与来源说明。

## 鸣谢

- [emako/TiktokLiveRec](https://github.com/emako/TiktokLiveRec)：上游项目。
- [DouyinLiveRecorder](https://github.com/ihmily/DouyinLiveRecorder)：参考了部分字符串数据（如正则表达式）。
