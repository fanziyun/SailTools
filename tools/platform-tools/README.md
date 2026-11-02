# platform-tools —— 从源码构建

本目录**不含任何预编译二进制**。`build.ps1` 按 `sources.json` 里固定的上游提交
拉取源码、应用 `patches/` 下的补丁，用 MSYS2 工具链编译，产物写入
`artifacts/platform-tools/`（已有 `.gitignore`，不入库）。

## 组成

| 产物 | 源码 | 说明 |
| --- | --- | --- |
| `adb.exe` | [prife/adb](https://github.com/prife/adb)（AOSP adb 1.0.40） | 唯一能在 MSYS2 mingw32 下直接编译的 adb host 移植 |
| `fastboot.exe` | [MeowArchMobile_Android_Tools](https://github.com/MeowArch-open/MeowArchMobile_Android_Tools) | AOSP fastboot + MeowArch 的进度条/UX 胶水层 |
| `magiskboot.exe` | [svoboda18/android_tools](https://github.com/svoboda18/android_tools)（Magisk 原生源码） | 64 位，clang64 构建 |
| `AdbWinApi.dll` | AOSP `development/host/windows/usb/api` | 源码编译，非 Google 预编译 |
| `AdbWinUsbApi.dll` | AOSP `development/host/windows/usb/winusb` | 源码编译 |

adb 的 BoringSSL 8.1 由 Debian 源码包里的 `sources.mk` 列出的 219 个 C 文件
编译成静态 `libcrypto.a`（`OPENSSL_NO_ASM`，纯 C 回退）。fastboot 的 AOSP 片段
由 MeowArch 的 `fetch-aosp.sh` 拉取到 `artifacts/tool-cache/aosp-adbd`。

## 前置条件

MSYS2 64 位（本机在 `%USERPROFILE%\toolchain\msys64`），先安装：

```bash
pacman -S --needed base-devel patch \
  mingw-w64-i686-gcc mingw-w64-i686-cmake mingw-w64-i686-ninja \
  mingw-w64-clang-x86_64-clang mingw-w64-clang-x86_64-libc++ \
  mingw-w64-clang-x86_64-make mingw-w64-clang-x86_64-xz \
  mingw-w64-clang-x86_64-lz4 mingw-w64-clang-x86_64-bzip2 \
  mingw-w64-clang-x86_64-zlib mingw-w64-clang-x86_64-pkgconf
```

另需 PowerShell 7 与 `git`。MSYS2 目录按以下顺序探测：`-Msys2Root` 参数、
`MSYS2_ROOT` 环境变量、`C:\msys64`、`%USERPROFILE%\toolchain\msys64`、`C:\tools\msys64`。

## 用法

```powershell
pwsh -File tools/platform-tools/build.ps1
```

首次运行会克隆上游（约 100 MB，含 BoringSSL 与 AOSP 片段）并编译，耗时约
10–20 分钟；之后命中 `build-info.json` 戳记会立即返回。参数：

| 参数 | 作用 |
| --- | --- |
| `-Force` | 忽略戳记与缓存，全部重建 |
| `-Msys2Root <path>` | 指定 MSYS2 |
| `-CacheDir <path>` | 源码缓存目录（默认 `artifacts/tool-cache`） |
| `-OutputDir <path>` | 产物目录（默认 `artifacts/platform-tools`） |
| `-SkipAdb` / `-SkipFastboot` / `-SkipMagiskBoot` | 只构建其余部分 |

`dotnet build` 会在 `AssignTargetPaths` 之前调用本脚本；`installer/build-setup.ps1`
在 publish 之前也会调用。输出缺失时构建不会悄悄成功——脚本失败即构建失败。

## 补丁

| 补丁 | 内容 |
| --- | --- |
| `patches/adb/0001-sailtool-windows-host-build.patch` | `std::byte` 与 Windows SDK 的 `byte` 冲突、host 构建缺 `chrono_literals`、`SetThreadDescription` 重定义、AdbWinApi 的 ATL/WDK 替代头、`return false` 误返回指针 |
| `patches/fastboot/0001-sailtool-pipe-progress.patch` | `FORCE_PROGRESS` 管道模式（逐帧纯文本、160 列虚拟宽度、25 ms 节奏） |
| `patches/fastboot/0002-msys2-binutils.patch` | MSYS2 下 `ar`/`ranlib`/`windres` 的绝对路径解析 |
| `patches/magiskboot/0001-libnt-line-displacement.patch` | clang 23 下 `SymGetLineFromAddr64` 的 `DWORD` 指针类型修复 |

## 已知取舍

adb 固定在 prife/adb 的最后一个提交，其源码是 2017 年的 AOSP 8.1 adb（1.0.40）。
这是目前唯一能在 MSYS2 mingw32 下不依赖 soong/libc++ 直接编译的 adb host 移植；
应用只使用 `devices` / `shell` / `push` / `pull` / `reboot` 等稳定命令，旧版协议
对这些命令与当前设备仍然兼容。无线调试配对（`adb pair`）不在本应用的功能范围内。

产物不保证逐字节可复现（编译器版本、时间戳会影响 PE 头）；要判断是否需要重建，
以 `sources.json` 的提交号与 `build-info.json` 的戳记为准。
