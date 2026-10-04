using System.Runtime;
using Avalonia.Threading;

namespace Unvault.App.Services;

/// <summary>
/// Hands memory back to Windows once the app lets go of something big, like an engine's manifest. An idle app
/// allocates nothing, so the GC wouldn't run on its own and Task Manager would keep showing the peak. The collection
/// compacts and returns what's free; it briefly pauses the app, so it's only used after such work, not on a timer.
/// </summary>
internal static class MemoryRelief
{
    public static void Release() => Dispatcher.UIThread.Post(() =>
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }, DispatcherPriority.Background); // after the screen has updated
}
