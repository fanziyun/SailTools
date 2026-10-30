using SailTool.ViewModels;

namespace SailTool.Services;

/// <summary>
/// Global accessor for the singleton <see cref="FastbootService"/> and
/// <see cref="FastbootProcessService"/>.
/// Provides factory methods used by pages to create their ViewModels.
/// ViewModel 实例被缓存：切页面不丢状态，重启自动清空。
/// </summary>
internal static class FastbootServiceAccessor
{
    private static readonly Lazy<FastbootProcessService> _processService = new(
        () => new FastbootProcessService(),
        isThreadSafe: true);

    private static readonly Lazy<FastbootService> _instance = new(
        () => new FastbootService(_processService.Value),
        isThreadSafe: true);

    public static FastbootService Instance => _instance.Value;

    // 缓存 ViewModel 实例，实现会话内持久化（切页面不丢失），重启自然清空
    private static FlashPageViewModel?    _cachedFlashVM;
    private static ROMFlashPageViewModel? _cachedROMFlashVM;

    public static HomePageViewModel CreateHomeViewModel()
        => new(Instance);

    public static FlashPageViewModel CreateFlashViewModel()
        => _cachedFlashVM ??= new(Instance);

    public static ROMFlashPageViewModel CreateROMFlashViewModel()
        => _cachedROMFlashVM ??= new(Instance);
}
