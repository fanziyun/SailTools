namespace FastTool.Models;

/// <summary>
/// 详细设备信息（通过 fastboot getvar / adb getprop 获取）。
/// 任何字段如果获取失败，默认值为 "--"。
/// </summary>
public sealed record DetailedDeviceInfo(
    string Serial,              // 序列号
    string ConnectionType,      // 检测方式: "ADB" / "Fastboot"
    string DeviceCodename,      // 设备代号 (ro.product.device / product)
    string Brand,               // 品牌 (ro.product.brand / brand)
    string Model,               // 型号/代号 (ro.product.model / model)
    string BootloaderStatus,    // BL 状态 (unlocked/locked)
    string Slot,                // a/b 槽位
    string AndroidVersion,      // 安卓版本 (ro.build.version.release)
    string SystemVersion,       // 系统版本 (ro.build.display.id)
    string KernelVersion        // 内核版本 (uname -r)
)
{
    public static DetailedDeviceInfo Empty(string serial, string connectionType) => new(
        Serial:           serial,
        ConnectionType:   connectionType,
        DeviceCodename:   "--",
        Brand:            "--",
        Model:            "--",
        BootloaderStatus: "--",
        Slot:             "--",
        AndroidVersion:   "--",
        SystemVersion:    "--",
        KernelVersion:    "--"
    );

    public string DisplayName => $"{Serial} [{DeviceCodename}] ({ConnectionType})";
}
