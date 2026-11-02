# SailTool

刷机工具箱，WinUI 3 / .NET 10 桌面应用。设备检测、分区刷写、
boot 镜像一键修补、OEM 线刷包执行，全部走本机 `fastboot` / `adb`。

界面为中文。

## 功能

- **设备检测**
- **自定义刷入** 
- **一键修补**
- **线刷** 
  自动把 `platform-tools` 注入子进程 PATH；脚本内的 fastboot 进度行同样限流采样
- **高级工具**
- **主题** 

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

**运行**

- .NET 10 Desktop Runtime
- Windows App Runtime 2.5（安装版会自动下载；开发机通常已随 SDK 装好）

**运行**

安装程序会按需联网下载并安装缺少的前置：VC++ 运行库、.NET 10 桌面运行时、
Windows App Runtime 2.5。离线环境需事先装好这三项。

**外部工具** — `tools/platform-tools/` 只有构建配方，不含预编译二进制。
`tools/platform-tools/build.ps1` 会从固定上游源码编译 `adb.exe`、`fastboot.exe`、
`magiskboot.exe` 与 `AdbWinApi.dll` / `AdbWinUsbApi.dll`，产物写入
`artifacts/platform-tools/`；构建应用时自动复制到输出目录的 `platform-tools/`
子目录，运行时从那里加载，无需另行配置。

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
tests/SailTool.Tests/  xUnit 测试（314 个）
installer/             Inno Setup 脚本与打包/测试脚本
tools/platform-tools/  源码构建配方：sources.json / build.ps1 / patches/
artifacts/platform-tools/  编译出的 adb / fastboot / magiskboot（不纳入版本控制）
artifacts/  dist/      构建与打包产物，不纳入版本控制
```

## 第三方组件

外部工具全部在构建时从源码编译：`adb.exe` 来自 prife/adb（AOSP adb 1.0.40）
与 BoringSSL 8.1，`fastboot.exe` 来自 MeowArchMobile_Android_Tools（AOSP
fastboot + MeowArch 胶水层），`magiskboot.exe` 来自 svoboda18/android_tools
（Magisk 原生源码）。固定提交、补丁与授权说明见
[`tools/platform-tools/PROVENANCE.md`](tools/platform-tools/PROVENANCE.md)。

本仓库自身未附带许可证文件。