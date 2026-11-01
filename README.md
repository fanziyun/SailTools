# SailTool

Windows 上的安卓刷机工具箱，WinUI 3 / .NET 10 桌面应用。设备检测、分区刷写、
boot 镜像一键修补（Magisk / AK3）、OEM 线刷包执行，全部走本机 `fastboot` / `adb`。

界面为中文。

## 功能

- **设备检测** — 同时识别 fastboot 与 adb 设备，同一台设备以 fastboot 为准；
  自动解析国产 ROM 版本（HyperOS、MIUI、ColorOS 系）与 BL 解锁状态
- **自定义刷入** — 选分区或手输分区名，刷入 `.img`；刷写过程实时显示百分比与
  写入速度，进度行按 100ms 采样、内容不变不刷新，不刷屏也不卡界面
- **一键修补** — 用 Magisk APK 或 AK3 ZIP 修补 boot / init_boot / vendor_boot /
  vbmeta，修补后可直接刷入并重启
- **线刷** — 选 OEM 线刷包目录，执行其中的 `flash_all.bat` 一类脚本，
  自动把 `platform-tools` 注入子进程 PATH；脚本内的 fastboot 进度行同样限流采样
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
tests/SailTool.Tests/  xUnit 测试（304 个）
installer/             Inno Setup 脚本与打包/测试脚本
tools/platform-tools/  adb / fastboot / magiskboot（随仓库提供）
artifacts/  dist/      构建与打包产物，不纳入版本控制
```

## 关于 fastboot.exe

随包的 `fastboot.exe` **不是** Google 的官方构建，而是我们从源码编译的 AOSP
fastboot 36.0.0（带 MeowArch 的进度条），**未签名**。同目录下的 `adb.exe`、
`AdbWinApi.dll` 等仍是 Google 签名的 37.0.1。

原因有两条。其一，进度输出是**编译进二进制**的，不是开关：官方 37.0.1 与 34.0.5
里搜索 `%3d%%` / `(%.1f%%)` 都是 0 次，`--verbose` 与 `--unbuffered` 也不产生进度行。
其二，进度条只在 stderr 是真终端时才画，而本应用始终通过管道读。

所以程序给子进程设了 `FORCE_PROGRESS=1`——这是我们给上游源码加的一个开关（另一处
改动是修 MSYS2 下的 binutils 解析），语义与既有的 `FORCE_COLOR` 对称，`NO_PROGRESS=1`
仍然优先。管道下颜色自动关闭，落到日志里是纯文本。

日志面板把整次传输压在**一行**上原地刷新（0% → 100%），不再逐帧追加把前面的信息刷掉。

程序会在启动刷写前扫描二进制判断能力（`FastbootCapability`），不支持进度时往日志面板
写一条提示，而不是留一个空进度条让人以为程序坏了。界面布局没有为此改动。

**分发前请阅读** [`tools/platform-tools/PROVENANCE.md`](tools/platform-tools/PROVENANCE.md)：
该文件记录了上游提交、这四处补丁、实测行为矩阵、DLL 兼容性核对结果，以及
GPL-3.0 授权义务（胶水层是 GPL-3.0；需随附许可证全文与源码获取方式，本仓库目前都没有）。

## 已知限制

- **线刷页的百分比进度条不会动。** `OverallProgress` 只在刷写开始时被重置为 0，
  没有任何推进逻辑，所以该页全程显示不定进度环。这是既有情况，不是本次改动引入的。
  实现它需要预扫 OEM 脚本里所有 `fastboot flash <分区> <镜像>` 并累加镜像字节数，
  详见 `CLAUDE.md`。
- **通过 `adb tcpip` 连接的设备识别不到。** 序列号正则的字符集不含点和冒号，
  `192.168.1.50:5555` 这种形式进不了设备列表；USB 序列号（含 `-` 与 `_`）和
  `emulator-5554` 不受影响。
- **官方 fastboot 构建下只有稀疏分块能给出中间进度。** 镜像超过设备
  `max-download-size` 被自动分块时（如 super 分区）可从 `Sending sparse i/N`
  推算；单块传输只能显示不定进度条，速度则由 `OKAY [Ns]` 耗时反推的平均值。

三条都已在 `CLAUDE.md` 记录成因、影响范围和修法。

## 开发约定

见 `CLAUDE.md`：构建命令、不能碰的 MSBuild 属性、工具寻址方式、测试规则、
以及为什么有几处看着该改却没改。

## 第三方组件

`tools/platform-tools/` 下是 Google Android SDK Platform-Tools 37.0.1 的发行内容，
其中 `magiskboot.exe` 来自 Magisk。授权文本随发行包提供，见
[`tools/platform-tools/NOTICE.txt`](tools/platform-tools/NOTICE.txt)。

本仓库自身未附带许可证文件。