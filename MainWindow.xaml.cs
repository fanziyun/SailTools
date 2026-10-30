using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using FastTool.Pages;

namespace FastTool;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> PageMap = new()
    {
        ["home"]     = typeof(HomePage),
        ["flash"]    = typeof(FlashPage),
        ["romflash"] = typeof(ROMFlashPage),
        ["tools"]    = typeof(ToolsPage),
        ["settings"] = typeof(SettingsPage),
    };

    // FIFO queue: every click is enqueued and will be animated in order
    private readonly Queue<string> _navigationQueue = new();
    private bool _isNavigating;

    public MainWindow()
    {
        InitializeComponent();

        App.Window = this;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 750));

        // Drain the queue every time the Frame finishes a navigation
        ContentFrame.Navigated += OnContentFrameNavigated;

        // Initial navigation
        ContentFrame.Navigate(typeof(HomePage));
        if (NavView.MenuItems.Count > 0)
            NavView.SelectedItem = NavView.MenuItems[0];
    }

    // ── Queue drain: process the next queued navigation ────────────────
    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        _isNavigating = false;
        DrainQueue();
    }

    private void DrainQueue()
    {
        if (_isNavigating || _navigationQueue.Count == 0)
            return;

        var tag = _navigationQueue.Dequeue();
        if (!PageMap.TryGetValue(tag, out var pageType))
        {
            // Invalid tag — move on to the next queued item
            DrainQueue();
            return;
        }

        _isNavigating = true;
        SyncMenuSelection(tag);
        ContentFrame.Navigate(pageType);
    }

    // ── Menu click ────────────────────────────────────────────────────
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        string tag;
        if (args.IsSettingsInvoked)
        {
            tag = "settings";
        }
        else if (args.InvokedItemContainer is NavigationViewItem item)
        {
            tag = item.Tag as string ?? string.Empty;
        }
        else
        {
            return;
        }

        NavigateAndSync(tag);
    }

    /// <summary>
    /// Public navigation entry point used by quick-action buttons on other pages.
    /// Navigates the Frame AND syncs the sidebar selection highlight.
    /// </summary>
    public void NavigateToTag(string tag)
    {
        NavigateAndSync(tag);
    }

    // ── Core: enqueue and process ─────────────────────────────────────
    private void NavigateAndSync(string tag)
    {
        if (!PageMap.ContainsKey(tag))
            return;

        // Sync highlight IMMEDIATELY so the user gets instant visual feedback
        // even while the previous navigation is still animating.
        SyncMenuSelection(tag);

        // Enqueue the navigation request
        _navigationQueue.Enqueue(tag);

        // Try to process the queue now; if a navigation is in flight,
        // the item will sit in the queue until OnContentFrameNavigated fires.
        DrainQueue();
    }

    // ── Menu selection sync ───────────────────────────────────────────
    private void SyncMenuSelection(string tag)
    {
        if (tag == "settings")
        {
            NavView.SelectedItem = null;
            return;
        }

        foreach (var obj in NavView.MenuItems)
        {
            if (obj is NavigationViewItem { Tag: string t } && t == tag)
            {
                NavView.SelectedItem = obj;
                return;
            }
        }
    }
}
