# platform-tools 来源与授权

## adb.exe / DLL / 其余工具

Google Android SDK Platform-Tools **37.0.1**（`source.properties`: `Pkg.Revision=37.0.1`）。
Authenticode 签名 `CN=Google LLC`，验证状态 Valid。授权见同目录 `NOTICE.txt`。

| 文件 | 签名 |
| --- | --- |
| `adb.exe` | Valid (Google LLC) |
| `AdbWinApi.dll` | Valid (Google LLC) |
| `AdbWinUsbApi.dll` | Valid (Google LLC) |
| 其余 `etc1tool` / `make_f2fs` / `mke2fs` / `sqlite3` / `hprof-conv` | 随发行包 |

## fastboot.exe —— 自建，不是 Google 官方构建

**已替换。** 官方 37.0.1 的 `fastboot.exe`（sha256 `b2d9cbff…3b8d86`，2,429,080 B，
签名 Valid）换成了从源码自行编译的 AOSP fastboot：

| | |
| --- | --- |
| 版本 | `fastboot version 36.0.0-meowarch-fastboot` |
| 大小 | 2,802,688 B（Release 后 strip） |
| sha256 | `9a88afbb9525d160901d1023f7b6d8451c35df8e70ad2cf2f8bee29dd2582891` |
| Authenticode | **NotSigned** |
| 源码仓库 | [MeowArchMobile_Android_Tools](https://github.com/MeowArch-open/MeowArchMobile_Android_Tools) 的 `better_fastboot/` |
| 上游提交 | `952ca57`（2026-11-01，`flash-x: extract a partition from payload.bin and flash it`） |
| AOSP 快照 | 2025-03 AOSP main；`system/core` @ `a3b721a32242006b59cb12bd62c9133632af3a2d`（fastboot 本体 BSD-3）；另取 `external/xz-embedded` 与 `external/bzip2` 供 `flash-x` 解压 payload |

上一份自建产物（2026-11-01，上游 `9fd4736`）的 sha256 是
`14e996ce…d13c5e`，2,569,728 B；本次同步把上游 2026-11-01 的 `a1cb51b`
（单行进度条、Windows VT）、`2d9cb03`（命令名）、`7068b29`（传输分片）、
`952ca57`（`flash-x`）全部带进了二进制。

### 为什么要自己构建

两件事叠在一起，官方的签名二进制都做不到：

1. **进度输出本身。** AOSP fastboot 没有进度条，那条线是 MeowArch 的
   `fb_progress.cpp` 加的。官方 37.0.1 / 34.0.5 / 本仓库原版里搜 `%3d%%` 与
   `(%.1f%%)` 都是 0 次，`--verbose`、`--unbuffered` 也开不出来。
   上一版用的是从 GPL-3.0 项目 VioletToolBox 提取的 35.0.2-eng 二进制——
   来路是第三方 blob，而 GPL-3.0 的义务一点没少。
2. **管道里要收得到。** 进度条只在 stderr 是真终端时才画（`GetConsoleMode`
   判定），因为它用 `\r` 原地重画，对脚本输出是污染。本应用始终通过管道读，
   默认一条进度都收不到。

自己构建之后，这两条都落在源码里：源码在、补丁在、义务可履行。

### 我们对上游源码的改动

本轮同步后只剩**两处**，其余原样：

**1. `better_fastboot/src/fb_progress.cpp` —— 新增 `FORCE_PROGRESS` 管道模式**

上游 `Detect()` 要求 stderr 是真终端，且 `Draw()` 在非 ANSI 终端（管道即
是）只输出 100% 那一帧——那是为“无法擦除行”的旧控制台设计的。本应用需要
的是管道里的逐帧数据，因此补丁做了四件事：

- `Detect()` 在原逻辑前插一条：`FORCE_PROGRESS` 非空且非 `"0"` 时直接返回真，
  绕过 tty 判定并置 `g_pipe`；`NO_PROGRESS` 仍先检查，作为显式关闭依旧优先。
  语义与 `fb_color.cpp` 里既有的 `FORCE_COLOR` 对称。`fb_progress.h` 顶部补了
  对应说明。
- `Draw()` 的 `!EscapesOk()` 分支在 `g_pipe` 时不再早退，每个采样帧都输出纯
  文本行（`\r` 开头、`\n` 结尾，无任何 ANSI 转义）；同时把 `g_bar_active`
  置真，让 `Update()` 的字节/时间闸生效。
- `Draw()` 在 `g_pipe` 时用 160 列的虚拟终端宽度（真实终端仍用自己的宽度）：
  上游按终端宽度降级字段，80 列下会先丢掉速度、再丢掉字节计数，而应用正靠
  字节计数驱动进度条。
- `Update()` 的管道节奏改为 25 ms / `total÷2000`（下限 16 KiB），终端仍走
  80 ms / `total÷400`（下限 64 KiB）。应用按 100 ms 采样，80 ms 的帧源会拍出
  80/160 交替的节拍。
- `Finish()` 在 `g_pipe` 时跳过“重写 padded prolog”这一步：管道帧自带 `\n`，
  没有屏幕行要擦，重写只会往日志里多塞一条没有换行的 prolog。

这个开关**只**绕过 tty 判定。颜色不跟着打开——`fb_color` 有自己的检测，
管道下 `Code()` 返回空串、`ClearToEol()` 返回空串，所以落到日志里的是纯文本，
没有任何 ANSI 转义。

**2. `better_fastboot/cmake/mingw-i686.cmake` —— binutils 解析**

原文件按 Linux 交叉工具链的命名硬写 `i686-w64-mingw32-ar`（Debian
`gcc-mingw-w64-i686` 确实有这个名字）。MSYS2 的 mingw-w64 没有：无前缀的
`ar.exe` / `ranlib.exe` / `windres.exe` 与前缀 gcc 同目录，带前缀的 binutils
在 `<bindir>/i686-w64-mingw32/bin` 下。裸名字会原样进 ninja 的归档规则，
而 ninja 经 `cmd.exe` 执行，`cmd.exe` 的 PATH 里没有它，归档步骤报
"'i686-w64-mingw32-ar' 不是内部或外部命令"。改为用 `find_program` 解析成
绝对路径（并补上原先没设的 `CMAKE_RANLIB`）。

**已移交给上游的补丁：稀疏写入分片。** 上一版我们改的是
`aosp-adbd/system/core/fastboot/fastboot_driver.cpp` 的 `SparseWriteCallback`，
按 1 MiB 调一次 `SendBuffer`。上游提交 `7068b29` 把同样的分片放进了
`SendBuffer(const void*, size_t)`——所有调用者（包括 sparse extents）都经过
那里，覆盖面更完整。本次同步后不再对 AOSP 源码做这项修改。

### 构建方式

工具链：MSYS2 便携版，`mingw-w64-i686-gcc 16.2.0` + `cmake 4.4.5` + `ninja 1.13.2`
（i686 是 soong 的 `x86_windows_host.go` 对 Windows host fastboot 的规定，
`AdbWinApi` 的 `compile_multilib` 同样是 "32"）。AOSP 片段由仓库里的
`scripts/fetch-aosp.sh` 浅克隆到 `../aosp-adbd`；本轮新增
`external/xz-embedded` 与 `external/bzip2`，供 `flash-x` 解压 payload.bin。

```bash
export PATH="$MSYS2/mingw32/bin:$PATH"
cd better_fastboot
cmake -B build-rel -G Ninja \
      -DCMAKE_TOOLCHAIN_FILE=cmake/mingw-i686.cmake \
      -DCMAKE_BUILD_TYPE=Release
cmake --build build-rel
"$MSYS2/mingw32/bin/strip.exe" -s build-rel/fastboot.exe
```

产物 `build-rel/fastboot.exe` 即本目录的这份。构建全程约 40 秒（Release，
`-static -static-libgcc -static-libstdc++`，无运行库依赖）。

### 行为矩阵（TCP 模拟设备实测）

对同一个 19 MiB 镜像、stderr 重定向到文件（与应用的管道同属非 tty），
四种组合逐一跑过：

| `FORCE_PROGRESS` | `NO_PROGRESS` | 结果 |
| --- | --- | --- |
| 未设 | 未设 | 无进度帧，输出与上游 AOSP 逐字节相同 |
| `1` | 未设 | 18 帧，0% → 100%，含速率与 ETA，每帧带字节计数 |
| `1` | `1` | 无进度帧（`NO_PROGRESS` 优先，是显式关闭） |
| `0` | 未设 | 无进度帧 |

第三、四行说明这个开关不会抢走别人已有的关闭手段。真实输出（`cat -A` 中的
`^M` 是 `\r`）：

```
Sending 'boot' (19456 KB)                          ^MSending 'boot' (19456 KB) [#>--------------------------]    5%  1.0 MiB/19.0 MiB^M
^MSending 'boot' (19456 KB) [####>-----------------------]   16%  3.0 MiB/19.0 MiB 29.8 MiB/s ETA 0:01^M
…
^MSending 'boot' (19456 KB) [############################]  100%  19.0 MiB/19.0 MiB 15.0 MiB/s^M
OKAY [  1.267s]^M
Writing 'boot'                                     OKAY [  0.200s]^M
Finished. Total time: 1.470s^M
```

与上一版的帧格式差异：上游把 prolog 从 50 列 padding 收成一个分隔空格，进度条
宽度按终端宽度预算（管道下我们给 160 列，条本身仍封顶 28 格），帧以 `\r`
开头、`\n` 结尾。应用侧只依赖捕获组，间距变化不影响解析，测试里同时保留了
两版真实帧样例。

每一帧都重印 `Sending` 前缀，所以按 `\r` 切分后每帧都是可独立理解的一行——
日志面板靠这一点把整次传输压成一行原地刷新（见
`FastbootOutputParser.BarProgressRegex` 与 `LogBuffer`）。

面板里存的是去掉图形条之后的帧：`RemoveProgressBar` 把 `[####>----]` 连同它
前面的空白一起删掉，只留百分比和字节数。页面本身就有进度条控件，日志里再画
一条字符条等于同一信息出现两遍，而且它是整行最宽的一段。速度同理，只留在
进度条右侧的标签上，状态行只写 `正在刷写... NN%`。

应用侧由 `FastbootService.FlashImageAsync` 与
`ROMFlashPageViewModel.ExecuteBatAsync` 给子进程设 `FORCE_PROGRESS=1`；
线刷那条路径上，`cmd.exe` 会把变量继承给 bat 里每一条 `fastboot flash`。

### PE 与依赖

x86 32-bit，与替换前一致；作为子进程调用不受宿主位宽影响。
从 `AdbWinApi.dll` 导入的函数与旧构建**完全相同**的 12 个
（`AdbEnumInterfaces`、`AdbCreateInterfaceByName`、`AdbReadEndpointSync` 等），
已逐个比对导入表，我们保留的**已签名** `AdbWinApi.dll` 全部导出，
因此只需替换 `fastboot.exe` 一个未签名文件。`AdbWinApi.dll` 不换成源码树
`win-prebuilt/` 里的未签名版本。

运行库从旧构建的 UCRT（`api-ms-win-crt-*`）换成了 `msvcrt.dll`——Windows
自带，依赖面反而更小。除 `AdbWinApi.dll` 外只依赖 `KERNEL32` / `SHELL32` /
`WS2_32` / `msvcrt`。

### 相对旧构建的能力变化

旧构建是 35.0.2-eng，缺两项官方能力；这份 36.0.0 全部具备：

- `--exclude-dynamic-partitions`
- `--dtb` 与 `flash vendor_boot:RAMDISK`

相比上一份自建产物（上游 `9fd4736`），本轮同步新增：

- **`flash-x <partition> <payload.bin> [<entry-name>]`**：直接从 OTA 的
  `payload.bin` 解出分区并刷写，支持 `REPLACE_XZ` / `REPLACE_BZ`。
- **单行自适应进度条**：整帧在内存里组好、按显示列宽截断后一次写出，CJK
  翻译不再撑破 80 列；Windows 终端会在需要时打开 VT processing。
- **`fastboot-m` 命令名与 bash/zsh/fish 补全**：包名仍是 `fastboot-meowarch`。
  本仓库仍把二进制命名并调用为 `fastboot.exe`，不受影响。
- 多语言（en / zh_CN / zh_TW / ja）、`--lang`、`--list-langs`、
  `--no-color` / `--color` / `--no-progress`。
- “Did you want 'flash'?” 拼写建议。

### 授权义务（分发前必须处理）

AOSP 源码保持上游授权（fastboot 本体 BSD-3，`system/libbase`、`liblog`、
`libziparchive`、`libavb` 等为 Apache-2.0 / BSD-3；`flash-x` 链入的
`external/xz-embedded` 为 public domain，`external/bzip2` 为 BSD 风格许可），
**但** MeowArch 的胶水层（`fb_progress.cpp`、`fb_color.cpp`、CMake、shim、
打包脚本）是 **GPL-3.0**，二者链接成同一个可执行文件，整体按 GPL-3.0
分发。也就是说：

- 换成这份自建产物**并没有**去掉 GPL-3.0 义务，只是把它变成可履行的：
  源码、精确的补丁、构建脚本都拿得到。
- 对外分发安装包时仍需：随附 GPL-3.0 全文、提供对应源码或书面获取要约
  （有效期至少 3 年）、保留原始版权与许可声明。
- 本项目自身目前未附带许可证文件，也未包含 GPL-3.0 全文；上述三项需要补齐。

另外：官方 `fastboot.exe` 有 Google 签名，这份没有，SmartScreen 对未签名
二进制的拦截概率更高。
