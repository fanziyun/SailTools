# SailTool

Windows 上的安卓刷机工具箱，WinUI 3 / .NET 10 桌面应用。设备检测、分区刷写、
boot 镜像一键修补（Magisk / AK3）、OEM 线刷包执行，全部走本机 `fastboot` / `adb`。

界面为中文。

## 功能

- **设备检测** — 同时识别 fastboot 与 adb 设备，同一台设备以 fastboot 为准；
  自动解析国产 ROM 版本（HyperOS、MIUI、ColorOS 系）与 BL 解锁状态
- **自定义刷入** — 选分区或手输分区名，刷入 `.img`；刷写过程显示百分比与速度
- **一键修补** — 用 Magisk APK 或 AK3 ZIP 修补 boot / init_boot / vendor_boot /
  vbmeta，修补后可直接刷入并重启
- **线刷** — 选 OEM 线刷包目录，执行其中的 `flash_all.bat` 一类脚本，
  自动把 `platform-tools` 注入子进程 PATH
- **高级工具** — 原始 fastboot 命令、擦除分区、OEM 解锁、宽容模式等
- **主题** — 跟随系统 / 浅色 / 深色，设置持久化到应用目录

## 技术栈

| 组件 | 版本 |
| --- | --- |
| .NET | 10（`net10.0-windows10.0.26100.0`，最低系统 Windows 10 1809） |
| Windows App SDK | 2.5.1 |
| MVVM | CommunityToolkit.Mvvm 8.4.2（source generators） |
| 打包 | 框架依赖发布 + Inno Setup 6 |

应用是**非打包**（`WindowsPackageType=None`）构建，不依赖 MSIX 安装。

## 前置依赖

**编译**

- .NET 10 SDK。注意本机 PATH 上的 `dotnet` 只有运行时没有 SDK，能编译的 SDK 在
  `%USERPROFILE%\.dotnet`；见 `CLAUDE.md`。

**运行（开发机直接跑）**

- .NET 10 Desktop Runtime
- Windows App Runtime 2.5（安装版会自动下载；开发机通常已随 SDK 装好）

**运行（安装版）**

安装程序会按需联网下载并安装缺少的前置：VC++ 运行库、.NET 10 桌面运行时、
Windows App Runtime 2.5。离线环境需事先装好这三项。

**外部工具** — `tools/platform-tools/` 已随仓库提供（Platform-Tools 37.0.1）：
`adb.exe`、`fastboot.exe`、`magiskboot.exe` 及各自的运行库依赖。构建时自动复制到
输出目录的 `platform-tools/` 子目录，运行时从那里加载，无需另行配置。

## 构建与运行

```bash
"$USERPROFILE/.dotnet/dotnet.exe" build SailTool.sln
"$USERPROFILE/.dotnet/dotnet.exe" test tests/SailTool.Tests/SailTool.Tests.csproj
```

跑起来：直接执行 `src/SailTool/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SailTool.exe`。

三个发布配置在 `src/SailTool/Properties/PublishProfiles/`，均为目录输出
（`SelfContained=false`、`PublishSingleFile=false`），与安装包的框架依赖发布方式一致。

## 打包安装包

```bash
pwsh -File installer/build-setup.ps1
```

产物落在 `dist/SailTool-Setup-<版本>-x64.exe`，脚本最后打印大小与 SHA256。
版本号取自 `src/SailTool/SailTool.csproj` 的 `<Version>`，脚本不写死。

脚本需要 Inno Setup 6，默认路径 `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`；
dotnet 与 ISCC 路径都可用 `-DotNetExe` / `-IsccExe` 覆盖。

`installer/test-runtime-bootstrap.ps1` 用于验证安装包的前置运行时引导：它会卸载指定
运行库、跑安装包、确认应用能起来。会改动系统状态，只在需要时用。

## 目录结构

```
src/SailTool/          应用源码
  Enums/  Models/  Services/  Services/Parsing/  ViewModels/  Pages/  Helpers/
tests/SailTool.Tests/  xUnit 测试（156 个）
installer/             Inno Setup 脚本与打包/测试脚本
tools/platform-tools/  adb / fastboot / magiskboot（随仓库提供）
artifacts/  dist/      构建与打包产物，不纳入版本控制
```

## 已知限制

- 官方 fastboot 不输出百分比，只有镜像超过设备 `max-download-size` 被自动分块
  （如 super 分区）时才能从 `Sending sparse i/N` 拿到进度；小分区显示不定进度条。
  详见 `CLAUDE.md`。
- 通过 `adb tcpip` 连接的设备（`192.168.1.50:5555`）目前识别不到，只支持 USB 与
  `emulator-5554` 形式的序列号。
- 部分机型上报的 bootloader 百分比不会被解析，进度条在该机型上保持不定状态。

后两条是既有缺陷，已在 `CLAUDE.md` 记录成因与影响范围。

## 开发约定

见 `CLAUDE.md`：构建命令、不能碰的 MSBuild 属性、工具寻址方式、测试规则、
以及为什么有几处看着该改却没改。

## 第三方组件

`tools/platform-tools/` 下是 Google Android SDK Platform-Tools 37.0.1 的发行内容，
其中 `magiskboot.exe` 来自 Magisk。授权文本随发行包提供，见
[`tools/platform-tools/NOTICE.txt`](tools/platform-tools/NOTICE.txt)。

本仓库自身未附带许可证文件。