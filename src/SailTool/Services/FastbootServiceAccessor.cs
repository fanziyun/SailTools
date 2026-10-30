using SailTool.ViewModels;

namespace SailTool.Services;

/// <summary>
/// Global accessor for the service singletons and the factory methods pages use
/// to obtain their ViewModels.
/// </summary>
/// <remarks>
/// Construction order is fixed by the dependency chain:
/// <see cref="FastbootProcessService"/> owns the child processes,
/// <see cref="DeviceDetectionService"/> reads device state through it, and
/// <see cref="FastbootService"/> re-exposes both while adding flashing.
/// ViewModel 实例被缓存：切页面不丢状态，重启自动清空。
/// </remarks>
internal static class FastbootServiceAccessor
{
    private static readonly Lazy<FastbootProcessService> _processService = new(
        () => new FastbootProcessService(),
        isThreadSafe: true);

    private static readonly Lazy<DeviceDetectionService> _deviceDetection = new(
        () => new DeviceDetectionService(_processService.Value),
        isThreadSafe: true);

    private static readonly Lazy<FastbootService> _instance = new(
        () => new FastbootService(_processService.Value, _deviceDetection.Value),
        isThreadSafe: true);

    public static FastbootService Instance => _instance.Value;

    public static DeviceDetectionService DeviceDetection => _deviceDetection.Value;

    // 缓存 ViewModel 实例，实现会话内持久化（切页面不丢失），重启自然清空
    private static FlashPageViewModel?      _cachedFlashVM;
    private static PatchPageViewModel?      _cachedPatchVM;
    private static ROMFlashPageViewModel?   _cachedROMFlashVM;

    public static HomePageViewModel CreateHomeViewModel()
        => new(Instance);

    public static FlashPageViewModel CreateFlashViewModel()
        => _cachedFlashVM ??= new(Instance);

    /// <summary>
    /// The patch card's ViewModel. Cached on its own so that
    /// <see cref="CreateFlashViewModel"/> composing with it stays idempotent:
    /// rebuilding the flash page reuses the same patch state rather than
    /// replacing it, which is what keeps the picked files across navigation.
    /// </summary>
    public static PatchPageViewModel CreatePatchViewModel()
        => _cachedPatchVM ??= new(Instance);

    public static ROMFlashPageViewModel CreateROMFlashViewModel()
        => _cachedROMFlashVM ??= new(Instance);
}
