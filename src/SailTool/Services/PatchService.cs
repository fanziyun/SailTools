using System.Diagnostics;
using System.IO.Compression;

namespace FastTool.Services;

/// <summary>
/// 修补 boot 镜像的核心服务，支持 Magisk 和 AK3
/// </summary>
public class PatchService
{
    private readonly string _fastbootPath;
    private readonly string _magiskbootPath;

    public PatchService(string fastbootPath)
    {
        _fastbootPath = fastbootPath;
        // magiskboot 应该在 platform-tools 同级目录或 app 目录
        _magiskbootPath = FindMagiskBootPath(fastbootPath);
    }

    private string FindMagiskBootPath(string fastbootPath)
    {
        var possiblePaths = new[]
        {
            Path.Combine(Path.GetDirectoryName(fastbootPath) ?? "", "magiskboot.exe"),
            Path.Combine(AppContext.BaseDirectory, "magiskboot.exe"),
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "magiskboot.exe")
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
                return path;
        }

        // 如果找不到，返回一个标记路径，稍后会下载或提示
        return possiblePaths[0];
    }

    public bool HasMagiskBoot => File.Exists(_magiskbootPath);

    public string MagiskBootPath => _magiskbootPath;

    /// <summary>
    /// 自动检测工具类型并修补 boot 镜像
    /// </summary>
    /// <param name="toolPath">Magisk APK 或 AK3 ZIP 路径</param>
    /// <param name="bootImagePath">原始 boot.img 路径</param>
    /// <param name="outputDir">输出目录</param>
    /// <param name="log">日志回调</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>修补后的镜像路径</returns>
    public async Task<string> PatchBootImageAsync(
        string toolPath,
        string bootImagePath,
        string outputDir,
        Action<string> log,
        CancellationToken ct = default)
    {
        if (!HasMagiskBoot)
        {
            throw new FileNotFoundException(
                "找不到 magiskboot.exe 工具。请将其放在 platform-tools 目录或应用目录下。",
                _magiskbootPath);
        }

        var extension = Path.GetExtension(toolPath).ToLowerInvariant();
        return extension switch
        {
            ".apk" => await PatchWithMagiskAsync(toolPath, bootImagePath, outputDir, log, ct),
            ".zip" => await PatchWithAK3Async(toolPath, bootImagePath, outputDir, log, ct),
            _ => throw new ArgumentException($"不支持的工具文件格式: {extension}（需要 .apk 或 .zip）")
        };
    }

    private async Task<string> PatchWithMagiskAsync(
        string magiskApkPath,
        string bootImagePath,
        string outputDir,
        Action<string> log,
        CancellationToken ct)
    {
        log("开始使用 Magisk 修补 boot 镜像...");

        // 清理并重建工作目录，防止旧文件残留
        var workDir = CleanCreateDir(Path.Combine(outputDir, "boot_work"));

        // 1. 解压 Magisk APK
        log("解压 Magisk APK...");
        var magiskExtractDir = CleanCreateDir(Path.Combine(outputDir, "magisk_extract"));
        ExtractArchive(magiskApkPath, magiskExtractDir, log);

        // 2. 解包 boot.img
        log("解包 boot 镜像...");
        var bootImgFile = Path.Combine(workDir, "boot.img");
        File.Copy(bootImagePath, bootImgFile, true);

        var unpackResult = await RunMagiskBootAsync(new[] { "unpack", "boot.img" }, workDir, log);
        if (!unpackResult.success)
            throw new Exception($"解包 boot.img 失败: {unpackResult.output}");

        // 3. 备份原始镜像和 ramdisk（匹配官方 boot_patch.sh）
        log("备份原始镜像和 ramdisk...");
        await RunMagiskBootAsync(new[] { "sha1", "boot.img" }, workDir, log);
        var ramdiskPath = Path.Combine(workDir, "ramdisk.cpio");
        if (File.Exists(ramdiskPath))
        {
            var origPath = Path.Combine(workDir, "ramdisk.cpio.orig");
            File.Copy(ramdiskPath, origPath, true);
            log("已备份 ramdisk → ramdisk.cpio.orig");
        }
        // stock_boot.img 备份（Magisk 用于恢复原厂镜像）
        var stockBootPath = Path.Combine(workDir, "stock_boot.img");
        File.Copy(bootImgFile, stockBootPath, true);

        // 4. 修补 ramdisk（Magisk 方式）
        log("修补 ramdisk...");
        await PatchRamdiskForMagisk(magiskExtractDir, workDir, log);

        // 5. 重新打包 boot.img
        log("重新打包 boot 镜像...");
        var repackResult = await RunMagiskBootAsync(new[] { "repack", "boot.img" }, workDir, log);
        if (!repackResult.success)
            throw new Exception($"重新打包 boot.img 失败: {repackResult.output}");

        // 6. 复制修补后的镜像到输出目录
        var patchedImageName = $"{Path.GetFileNameWithoutExtension(bootImagePath)}_patched.img";
        var patchedImagePath = Path.Combine(outputDir, patchedImageName);
        var newBootPath = Path.Combine(workDir, "new-boot.img");
        if (!File.Exists(newBootPath))
            throw new FileNotFoundException("修包过程完成，但找不到 new-boot.img");

        File.Copy(newBootPath, patchedImagePath, true);
        log($"修补完成: {patchedImagePath}");

        return patchedImagePath;
    }

    private async Task PatchRamdiskForMagisk(
        string magiskExtractDir,
        string workDir,
        Action<string> log)
    {
        // 检查 ramdisk 是否存在
        var ramdiskPath = Path.Combine(workDir, "ramdisk.cpio");
        if (!File.Exists(ramdiskPath))
        {
            log("未找到 ramdisk.cpio，无需修补 ramdisk");
            return;
        }

        // 根据架构确定文件源路径（v30.7 统一用 libmagisk.so，v26- 用 libmagisk64.so/libmagisk32.so）
        string FindInExtract(string subPath)
        {
            var p = Path.Combine(magiskExtractDir, subPath);
            return File.Exists(p) ? p : null!;
        }

        var arm64 = "lib/arm64-v8a";

        // 复制 Magisk 关键文件到 workDir
        string? CopyFromApk(string apkSubPath, string destName)
        {
            var src = FindInExtract(apkSubPath);
            if (src == null) return null;
            var destPath = Path.Combine(workDir, destName);
            File.Copy(src, destPath, true);
            log($"复制: {Path.GetFileName(src)} -> {destName}");
            return destPath;
        }

        CopyFromApk($"{arm64}/libmagiskinit.so", "magiskinit");
        if (CopyFromApk($"{arm64}/libmagisk.so", "magisk") == null)
            CopyFromApk($"{arm64}/libmagisk64.so", "magisk");
        CopyFromApk($"{arm64}/libinit-ld.so", "init-ld");
        CopyFromApk("assets/stub.apk", "stub.apk");

        // 1. 压缩文件（匹配官方 boot_patch.sh：stub.apk → stub.xz，不是 stub.apk.xz）
        log("压缩 magisk → magisk.xz...");
        await RunMagiskBootAsync(new[] { "compress=xz", "magisk", "magisk.xz" }, workDir, log);
        
        log("压缩 stub.apk → stub.xz...");
        await RunMagiskBootAsync(new[] { "compress=xz", "stub.apk", "stub.xz" }, workDir, log);
        
        log("压缩 init-ld → init-ld.xz...");
        await RunMagiskBootAsync(new[] { "compress=xz", "init-ld", "init-ld.xz" }, workDir, log);

        // 2. 计算 SHA1 并创建 Magisk 配置（匹配官方 boot_patch.sh）
        log("计算 boot 镜像 SHA1...");
        var sha1Result = await RunMagiskBootAsync(new[] { "sha1", "boot.img" }, workDir, log);
        var sha1 = sha1Result.output?.Trim() ?? "";

        var configLines = new List<string>
        {
            "KEEPVERITY=false",
            "KEEPFORCEENCRYPT=false",
            "RECOVERYMODE=false",
            "VENDORBOOT=false",
        };
        if (!string.IsNullOrEmpty(sha1))
            configLines.Add($"SHA1={sha1}");
        
        var configPath = Path.Combine(workDir, "config");
        File.WriteAllLines(configPath, configLines);
        log($"创建 Magisk 配置 (SHA1={(string.IsNullOrEmpty(sha1) ? "无" : sha1)})");

        // 3. 构建 cpio 命令（完全匹配官方 boot_patch.sh）
        var cpioCommands = new List<string>
        {
            "add 0750 init magiskinit",
            "mkdir 0750 overlay.d",
            "mkdir 0750 overlay.d/sbin",
            "add 0644 overlay.d/sbin/magisk.xz magisk.xz",
            "add 0644 overlay.d/sbin/stub.xz stub.xz",
            "add 0644 overlay.d/sbin/init-ld.xz init-ld.xz",
            "patch",
            "backup ramdisk.cpio.orig",
            "mkdir 000 .backup",
            "add 000 .backup/.magisk config",
        };

        log("执行 ramdisk cpio 修补（匹配官方 boot_patch.sh）...");
        var args = new List<string> { "cpio", "ramdisk.cpio" };
        args.AddRange(cpioCommands);

        // 设置 KEEPVERITY/KEEPFORCEENCRYPT 环境变量（patch 命令需要）
        var envVars = new Dictionary<string, string>
        {
            ["KEEPVERITY"] = "false",
            ["KEEPFORCEENCRYPT"] = "false",
        };
        var repackResult = await RunMagiskBootAsync(args.ToArray(), workDir, log, envVars);
        if (!repackResult.success)
            throw new Exception($"修补 ramdisk 失败: {repackResult.output}");

        log("ramdisk 修补完成（已部署 magiskinit + overlay.d/sbin + 备份）");
    }

    private async Task<string> PatchWithAK3Async(
        string ak3ZipPath,
        string bootImagePath,
        string outputDir,
        Action<string> log,
        CancellationToken ct)
    {
        log("开始使用 AK3 修补 boot 镜像...");

        // 1. 解压 AK3 ZIP
        log("解压 AK3 ZIP...");
        var ak3ExtractDir = CleanCreateDir(Path.Combine(outputDir, "ak3_extract"));
        ExtractArchive(ak3ZipPath, ak3ExtractDir, log);

        // 2. 查找 anykernel.sh 脚本（如果外层没有，自动搜索内层嵌套 zip）
        var ak3SourceDir = FindAk3SourceDir(ak3ExtractDir, log);

        // 3. 解包 boot.img
        log("解包 boot 镜像...");
        var workDir = CleanCreateDir(Path.Combine(outputDir, "boot_work"));
        var bootImgFile = Path.Combine(workDir, "boot.img");
        File.Copy(bootImagePath, bootImgFile, true);

        var unpackResult = await RunMagiskBootAsync(new[] { "unpack", "boot.img" }, workDir, log);
        if (!unpackResult.success)
            throw new Exception($"解包 boot.img 失败: {unpackResult.output}");

        // 4. 替换 kernel（如果 AK3 中包含 zImage 或 Image.gz-dtb）
        var kernelFile = FindKernelFile(ak3SourceDir);
        if (!string.IsNullOrEmpty(kernelFile) && File.Exists(kernelFile))
        {
            log($"替换 kernel ({new FileInfo(kernelFile).Length / 1024.0:F0} KB)...");
            var destKernel = Path.Combine(workDir, "kernel");
            File.Copy(kernelFile, destKernel, true);
        }
        else
        {
            log("警告: AK3 中没有找到 kernel 文件，跳过替换");
        }

        // 5. 复制 dtbo（如果存在）
        var dtboFile = Path.Combine(ak3SourceDir, "dtbo");
        if (File.Exists(dtboFile))
        {
            log("复制 dtbo...");
            var destDtbo = Path.Combine(workDir, "dtb");
            File.Copy(dtboFile, destDtbo, true);
        }

        // 6. 重新打包 boot.img
        log("重新打包 boot 镜像...");
        var repackResult = await RunMagiskBootAsync(new[] { "repack", "boot.img" }, workDir, log);
        if (!repackResult.success)
            throw new Exception($"重新打包 boot.img 失败: {repackResult.output}");

        // 7. 复制修补后的镜像到输出目录
        var patchedImageName = $"{Path.GetFileNameWithoutExtension(bootImagePath)}_patched.img";
        var patchedImagePath = Path.Combine(outputDir, patchedImageName);
        var newBootPath = Path.Combine(workDir, "new-boot.img");
        if (!File.Exists(newBootPath))
            throw new FileNotFoundException("修包过程完成，但找不到 new-boot.img");

        File.Copy(newBootPath, patchedImagePath, true);
        log($"修补完成: {patchedImagePath}");

        return patchedImagePath;
    }

    /// <summary>
    /// 查找 AK3 源目录。如果外层有 anykernel.sh 直接返回，否则搜索内层 zip。
    /// </summary>
    private static string FindAk3SourceDir(string extractDir, Action<string> log)
    {
        // 直接检查外层
        if (File.Exists(Path.Combine(extractDir, "anykernel.sh")))
            return extractDir;

        log("外层未找到 anykernel.sh，搜索内层 AK3 zip...");

        // 查找内层 zip 文件
        var innerZips = Directory.GetFiles(extractDir, "*.zip", SearchOption.AllDirectories);
        foreach (var innerZip in innerZips)
        {
            log($"检查内层 zip: {Path.GetFileName(innerZip)}...");
            try
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(innerZip);
                if (archive.Entries.Any(e => e.Name.Equals("anykernel.sh", StringComparison.OrdinalIgnoreCase)))
                {
                    log($"找到内层 AK3 zip: {Path.GetFileName(innerZip)}");
                    var innerExtractDir = Path.Combine(extractDir, "inner_ak3");
                    Directory.CreateDirectory(innerExtractDir);
                    System.IO.Compression.ZipFile.ExtractToDirectory(innerZip, innerExtractDir, overwriteFiles: true);
                    return innerExtractDir;
                }
            }
            catch (Exception ex)
            {
                log($"  跳过 {Path.GetFileName(innerZip)}: {ex.Message}");
            }
        }

        throw new FileNotFoundException("AK3 ZIP 中找不到 anykernel.sh（已搜索内层嵌套 zip）");
    }

    /// <summary>
    /// 在 AK3 目录中查找 kernel 文件
    /// </summary>
    private static string? FindKernelFile(string ak3Dir)
    {
        var candidates = new[] { "zImage", "Image.gz-dtb", "Image", "zImage-dtb" };
        foreach (var name in candidates)
        {
            var path = Path.Combine(ak3Dir, name);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private async Task<(bool success, string output)> RunMagiskBootAsync(
        string[] arguments,
        string workDir,
        Action<string> log,
        Dictionary<string, string>? envVars = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = MagiskBootPath,
            Arguments = string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // 设置环境变量（用于 cpio patch 命令的 KEEPVERITY/KEEPFORCEENCRYPT）
        if (envVars != null)
        {
            foreach (var kv in envVars)
                psi.EnvironmentVariables[kv.Key] = kv.Value;
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;
        var fullOutput = string.IsNullOrWhiteSpace(output) ? error : output;

        log($"[magiskboot {string.Join(" ", arguments)}]");
        if (!string.IsNullOrWhiteSpace(fullOutput))
            log(fullOutput);

        return (process.ExitCode == 0, fullOutput);
    }

    private static void ExtractArchive(string archivePath, string extractDir, Action<string> log)
    {
        log($"解压文件: {Path.GetFileName(archivePath)} ({new FileInfo(archivePath).Length / 1024.0:F1} KB)...");
        System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
        var fileCount = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories).Length;
        log($"解压完成: {fileCount} 个文件");
    }

    /// <summary>
    /// 清理旧目录后重建，防止残留文件干扰 magiskboot（如之前的 kernel 文件）
    /// </summary>
    private static string CleanCreateDir(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
