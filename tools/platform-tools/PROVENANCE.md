# platform-tools 来源与授权

本目录**不再存放任何预编译二进制**。`adb.exe` / `fastboot.exe` /
`magiskboot.exe` / `AdbWinApi.dll` / `AdbWinUsbApi.dll` 全部由
`build.ps1` 在构建工具时从固定上游源码编译，产物写入
`artifacts/platform-tools/`（git 忽略）。

## 构建定义

| 文件 | 作用 |
| --- | --- |
| `build.ps1` | 拉取源码、应用补丁、编译、组装产物 |
| `sources.json` | 每个组件的上游仓库与固定提交 |
| `patches/` | 我方对上游源码的适配补丁 |
| `README.md` | 前置工具链与用法 |

## 各组件来源

### adb 套件

| | |
| --- | --- |
| 源码 | [prife/adb](https://github.com/prife/adb) `bfb2c9ef…`（AOSP adb 1.0.40） |
| 版本 | `Android Debug Bridge version 1.0.40` |
| 许可 | Apache-2.0（AOSP adb） |
| 加密库 | Debian `android-platform-external-boringssl` 的 `debian/8.1.0+r23-3`（`c924680…`），219 个 C 文件编译为静态 `libcrypto.a`（`OPENSSL_NO_ASM`），许可为 ISC 风格 |
| USB 层 | AOSP `development/host/windows/usb/{api,winusb}` 源码编译为 `AdbWinApi.dll` / `AdbWinUsbApi.dll`（Apache-2.0） |

prife/adb 是目前唯一能在 MSYS2 mingw32 下不依赖 soong/libc++ 直接编译的
adb host 移植；其源码是 2017 年的 AOSP 8.1 快照，但 `devices` / `shell` /
`push` / `pull` / `reboot` 等应用实际使用的命令与当前设备仍兼容。无线调试
配对（`adb pair`）不在应用功能范围内。

### fastboot 套件

| | |
| --- | --- |
| 源码 | [MeowArchMobile_Android_Tools](https://github.com/MeowArch-open/MeowArchMobile_Android_Tools) `952ca577…` |
| 版本 | `fastboot version 36.0.0-meowarch-fastboot` |
| AOSP 快照 | 2025-03 AOSP main；`system/core` @ `a3b721a…`（fastboot 本体 BSD-3） |
| 许可 | fastboot 本体 BSD-3；MeowArch 胶水层（`fb_progress` / `fb_color` / CMake / shim）GPL-3.0，链接后整体按 GPL-3.0 分发 |

新增能力（相对官方 37.0.1）：`flash-x`（直接从 `payload.bin` 解出分区）、
单行自适应进度条、`FORCE_PROGRESS` 管道模式、多语言、`fastboot-m` 命令别名。
应用仍以 `fastboot.exe` 调用。

### magiskboot

| | |
| --- | --- |
| 源码 | [svoboda18/android_tools](https://github.com/svoboda18/android_tools) `f9f56d0b…`，`boot/` 子模块为 [svoboda18/magiskboot](https://github.com/svoboda18/magiskboot)（Magisk 原生源码） |
| 位宽 | x86-64（clang64 构建） |
| 许可 | Magisk GPL-3.0；`libnt` BSD-3-Clause |

## 我方补丁

| 补丁 | 解决的问题 |
| --- | --- |
| `patches/adb/0001-sailtool-windows-host-build.patch` | 旧版 `cutils/trace.h` 的 `using namespace std;` 使 `std::byte` 与 Windows SDK 的 `byte` 二义；host 构建缺 `chrono_literals`；`SetThreadDescription` 与新版 MinGW 头重定义；AdbWinApi 对 ATL/WDK 头的依赖（提供最小替身）；`return false` 误用于返回指针 |
| `patches/fastboot/0001-sailtool-pipe-progress.patch` | `FORCE_PROGRESS` 管道模式：管道下逐帧纯文本输出、160 列虚拟宽度保住字节计数字段、25 ms 节奏、`Finish()` 不重写 prolog |
| `patches/fastboot/0002-msys2-binutils.patch` | MSYS2 没有带前缀的 `ar` / `ranlib` / `windres`，改为 `find_program` 解析绝对路径 |
| `patches/magiskboot/0001-libnt-line-displacement.patch` | clang 23 下 `SymGetLineFromAddr64` 要求 `DWORD*`，原代码传 `DWORD64*` |

## 授权义务（分发前必须处理）

- **fastboot** 链接了 GPL-3.0 的 MeowArch 胶水层，整体按 GPL-3.0 分发：
  对外分发安装包时需随附 GPL-3.0 全文、提供对应源码或书面获取要约（至少
  3 年）、保留原始版权与许可声明。
- **magiskboot** 同样源自 GPL-3.0 的 Magisk。
- adb / AdbWinApi / BoringSSL 为 Apache-2.0 / ISC 许可，需保留版权与许可
  声明；AOSP 源码树自带 `NOTICE`。
- 本项目自身目前未附带许可证文件，也未包含 GPL-3.0 全文；上述义务需要补齐。

另：自编译的 `fastboot.exe` 与 `adb.exe` 没有 Authenticode 签名，SmartScreen
对未签名二进制的拦截概率高于 Google 官方发行包。
