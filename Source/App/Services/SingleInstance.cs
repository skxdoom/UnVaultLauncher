using System.Runtime.InteropServices;

namespace UnVault.App.Services;

/// <summary>
/// One launcher per Windows sign-in: starting it again brings the running one's window forward (it may be hidden in the
/// system tray) instead of starting a second copy that would download into the same folders.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string DefaultName = "UnVaultLauncher";

    private readonly Mutex _running;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _waiting;

    private SingleInstance(Mutex running, EventWaitHandle show)
    {
        _running = running;
        _show = show;
    }

    // "Local\": this Windows sign-in only; another user on the same PC runs a launcher of their own.
    private static string RunningName(string name) => $@"Local\{name}.Running";
    private static string ShowName(string name) => $@"Local\{name}.Show";

    /// <summary>This copy becomes the running one; null when another already is. Dispose on the thread that claimed it.</summary>
    /// <param name="name">Tests use names of their own, so they never meet a launcher that's really running.</param>
    public static SingleInstance? Claim(string name = DefaultName)
    {
        var running = new Mutex(initiallyOwned: true, RunningName(name), out bool created);
        if (!created)
        {
            running.Dispose();
            return null;
        }
        return new SingleInstance(running, new EventWaitHandle(false, EventResetMode.AutoReset, ShowName(name)));
    }

    /// <summary>Asks the running copy to show its window.</summary>
    public static void ShowRunning(string name = DefaultName)
    {
        if (!OperatingSystem.IsWindows())
            return;
        // Windows lets only the app the user just started come to the front; this one hands that on before it exits.
        AllowSetForegroundWindow(AnyProcess);
        if (EventWaitHandle.TryOpenExisting(ShowName(name), out var show))
        {
            using (show)
                show.Set();
        }
    }

    /// <summary>Runs <paramref name="show"/>, on a pool thread, each time the app is started again.</summary>
    public void WhenStartedAgain(Action show) =>
        _waiting = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _waiting?.Unregister(null);
        _show.Dispose();
        _running.ReleaseMutex();
        _running.Dispose();
    }

    private const int AnyProcess = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processID);
}
