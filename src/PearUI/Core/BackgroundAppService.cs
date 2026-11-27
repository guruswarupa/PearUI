namespace PearUI.Core;

/// <summary>
/// Keeps the list of background programs up to date (every few seconds, off the UI thread) for everything that shows it:
/// the icons in the menu bar and the Background apps popup. One scan serves all menu bars.
/// </summary>
public sealed class BackgroundAppService
{
    public static BackgroundAppService Instance { get; } = new();

    private volatile List<BackgroundApp> _apps = new();
    private readonly SemaphoreSlim _now = new(0, int.MaxValue);
    private int _started;
    private string _signature = "";

    public IReadOnlyList<BackgroundApp> Apps => _apps;

    /// <summary>Raised on a background thread when the list changed.</summary>
    public event Action Changed;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(Loop);
    }

    /// <summary>Scan again right away (after quitting something, or when a popup opens).</summary>
    public void RefreshNow()
    {
        if (_now.CurrentCount == 0) _now.Release();
    }

    private async Task Loop()
    {
        while (true)
        {
            try
            {
                var running = WindowTracker.Instance.Apps;
                var list = BackgroundAppManager.Find(WindowTracker.SnapshotProcesses(), running);
                string sig = string.Join("|", list.Select(a => a.Target ?? a.ExePath));
                if (sig != _signature)
                {
                    _signature = sig;
                    _apps = list;
                    Changed?.Invoke();
                }
            }
            catch { }
            await Task.WhenAny(Task.Delay(4000), _now.WaitAsync());
        }
    }
}
