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
| 大小 | 2,569,728 B（Release 后 strip） |
| sha256 | `14e996ce8e214d996e54ba3a60b83f89f7de08024d9fed7da27e388af9d13c5e` |
| Authenticode | **NotSigned** |
| 源码仓库 | [MeowArchMobile_Android_Tools](https://github.com/MeowArch-open/MeowArchMobile_Android_Tools) 的 `better_fastboot/` |
| AOSP 快照 | 2025-03 AOSP main；`system/core` @ `a3b721a32242006b59cb12bd62c9133632af3a2d`（fastboot 本体 BSD-3） |

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

四处，其余原样：

**1. `better_fastboot/src/fb_progress.cpp` —— 新增 `FORCE_PROGRESS` 开关**

`Detect()` 在原逻辑前插一条：`FORCE_PROGRESS` 非空且非 `"0"` 时直接返回真，
绕过 tty 判定；`NO_PROGRESS` 仍先检查，作为显式关闭依旧优先。语义与
`fb_color.cpp` 里既有的 `FORCE_COLOR` 对称。`fb_progress.h` 顶部补了对应说明。

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

**3. `aosp-adbd/system/core/fastboot/fastboot_driver.cpp` —— 稀疏写入按 1 MiB 分片**

`SparseWriteCallback` 原先把一段 sparse 数据一次性交给
`SendBuffer(data + total, nbytes)`，而进度回调挂在 `SendBuffer` 末尾——一段
数据（可以是几十上百 MiB）只回调一次。实测 32 MiB 的 sparse 块，整段只出现
0% 和 100% 两帧，中间近 2 秒日志一片空白；`super` 那种多块大分区因此一格一格
跳。改为每 1 MiB 调一次 `SendBuffer`，与已有的 `SendBuffer(fd, size)` 分片
一致。分片对协议透明：设备在 `DATA` 之后按 `size` 连续消费，不关心分几条消息。

**4. `better_fastboot/src/fb_progress.cpp` —— 管道消费者用更密的画帧节奏**

`Update()` 的时间闸 80 ms、字节闸 `total/400`（下限 64 KiB）是按终端里人眼看的
节奏选的。管道消费者（本应用）按自己的 100 ms 时钟采样，80 ms 的帧源叠上
100 ms 的采样会拍出 80/160 交替的节拍，看起来就是"一顿一顿"。`FORCE_PROGRESS`
为真时改为 25 ms / `total/2000`（下限 16 KiB）：帧源稳在采样间隔以内，节拍由
应用一侧单独决定；终端仍走原来的 80 ms。

### 构建方式

工具链：MSYS2 便携版，`mingw-w64-i686-gcc 16.2.0` + `cmake 4.4.5` + `ninja 1.13.2`
（i686 是 soong 的 `x86_windows_host.go` 对 Windows host fastboot 的规定，
`AdbWinApi` 的 `compile_multilib` 同样是 "32"）。AOSP 片段由仓库里的
`scripts/fetch-aosp.sh` 浅克隆到 `../aosp-adbd`。

```bash
export PATH="$MSYS2/mingw32/bin:$PATH"
cd better_fastboot
cmake -B build-rel -G Ninja \
      -DCMAKE_TOOLCHAIN_FILE=cmake/mingw-i686.cmake \
      -DCMAKE_BUILD_TYPE=Release
cmake --build build-rel
"$MSYS2/mingw32/bin/strip.exe" -s build-rel/fastboot.exe
```

产物 `build-rel/fastboot.exe` 即本目录的这份。构建全程 32 秒（Release，
`-static -static-libgcc -static-libstdc++`，无运行库依赖）。

### 行为矩阵（TCP 模拟设备实测）

对同一个 19 MiB 镜像、stderr 走管道，四种组合逐一跑过：

| `FORCE_PROGRESS` | `NO_PROGRESS` | 结果 |
| --- | --- | --- |
| 未设 | 未设 | 无进度帧，输出与上游 AOSP 逐字节相同 |
| `1` | 未设 | 19 帧（每 1 MiB 一片），0% → 100%，含速率与 ETA |
| `1` | `1` | 无进度帧（`NO_PROGRESS` 优先，是显式关闭） |
| `0` | 未设 | 无进度帧 |

第三、四行说明这个开关不会抢走别人已有的关闭手段。真实输出：

```
Sending 'boot' (19531 KB)                    [#>----------------------]   5%  1.0 MiB/19.1 MiB
Sending 'boot' (19531 KB)                    [#####>------------------]  21%  4.0 MiB/19.1 MiB 22.6 MiB/s ETA 0:01
…
Sending 'boot' (19531 KB)                    [########################] 100%  19.1 MiB/19.1 MiB 14.0 MiB/s
Sending 'boot' (19531 KB)                    OKAY [  1.272s]
Writing 'boot'                               OKAY [  0.200s]
Finished. Total time: 1.475s
```

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

另新增 `--no-color` / `--color` / `--no-progress` 三个开关（`NO_PROGRESS=1`
与 `NO_COLOR=1` 环境变量等价）。`fastboot-info.txt`、virtual A/B、`super`、
`resize-logical-partition`、`update-super`、`wipe-super`、`otp`、`stage`/`fetch`
继续可用。

### 授权义务（分发前必须处理）

AOSP 源码保持上游授权（fastboot 本体 BSD-3，`system/libbase`、`liblog`、
`libziparchive`、`libavb` 等为 Apache-2.0 / BSD-3），**但** MeowArch 的胶水层
（`fb_progress.cpp`、`fb_color.cpp`、CMake、shim、打包脚本）是 **GPL-3.0**，
二者链接成同一个可执行文件，整体按 GPL-3.0 分发。也就是说：

- 换成这份自建产物**并没有**去掉 GPL-3.0 义务，只是把它变成可履行的：
  源码、精确的补丁、构建脚本都拿得到。
- 对外分发安装包时仍需：随附 GPL-3.0 全文、提供对应源码或书面获取要约
  （有效期至少 3 年）、保留原始版权与许可声明。
- 本项目自身目前未附带许可证文件，也未包含 GPL-3.0 全文；上述三项需要补齐。

另外：官方 `fastboot.exe` 有 Google 签名，这份没有，SmartScreen 对未签名
二进制的拦截概率更高。
