using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace VRCNext.Services;

public class MemoryTrimService : IDisposable
{
    private System.Threading.Timer? _timer;
    private const int IntervalMs = 10 * 60 * 1000; // 10 minutes

    public void SetEnabled(bool enabled)
    {
        _timer?.Dispose();
        _timer = null;
        if (enabled)
        {
            TrimNow();
            _timer = new System.Threading.Timer(_ => TrimNow(), null, IntervalMs, IntervalMs);
        }
    }

    public void TrimNow()
    {
        Task.Run(() =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            TrimWorkingSet();
        });
    }

    private static void TrimWorkingSet()
    {
#if WINDOWS
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
#endif
    }

#if WINDOWS
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(nint hProcess);
#endif

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
