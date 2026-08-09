#if WINDOWS
using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Media.Control;
#endif

namespace VRCNext.Services;

public static class WindowsFixes
{
    public static Action<string>? Log;

    private static readonly object _lock = new();
    private static System.Threading.Timer? _timer;
    private static int _busy;

    public static void SetEnabled(bool enabled)
    {
#if WINDOWS
        lock (_lock)
        {
            if (enabled)
            {
                _timer ??= new System.Threading.Timer(_ => _ = TickAsync(), null,
                    TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));
            }
            else
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
#endif
    }

    public static void ForceFix()
    {
#if WINDOWS
        _ = ForceFixAsync();
#endif
    }

#if WINDOWS
    private static async Task ForceFixAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            Log?.Invoke("WIFX - [WINFIX] - a fix is already running, please wait...");
            return;
        }
        try
        {
            Log?.Invoke("WIFX - [WINFIX] - manual fix requested — restarting NPSMSvc...");
            if (!RepairNpsm())
            {
                Log?.Invoke("WIFX - [WINFIX] - could not locate the NPSMSvc service host.");
                return;
            }
            await Task.Delay(2500);
            if (await IsSmtcHungAsync())
                Log?.Invoke("WIFX - [WINFIX] - NPSMSvc restarted but still unresponsive — try again or reboot.");
            else
                Log?.Invoke("WIFX - [WINFIX] - resolved and stable!");
        }
        catch (Exception ex) { Log?.Invoke($"WIFX - [WINFIX] - fix error: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (!await IsSmtcHungAsync()) return;
            Log?.Invoke("WIFX - [WINFIX] - NPSMSvc (Windows 'Now Playing') is hung — resolving...");
            if (!RepairNpsm())
            {
                Log?.Invoke("WIFX - [WINFIX] - could not locate the NPSMSvc service host — skipping.");
                return;
            }
            await Task.Delay(2500);
            if (await IsSmtcHungAsync())
                Log?.Invoke("WIFX - [WINFIX] - NPSMSvc restarted but still unresponsive — will retry next cycle.");
            else
                Log?.Invoke("WIFX - [WINFIX] - resolved and stable!");
        }
        catch (Exception ex) { Log?.Invoke($"WIFX - [WINFIX] - tick error: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static async Task<bool> IsSmtcHungAsync()
    {
        try
        {
            var task = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(6)));
            if (winner == task) { _ = task.Exception; return false; }
            return true;
        }
        catch { return false; }
    }

    private static bool RepairNpsm()
    {
        try
        {
            var pid = FindNpsmHostPid();
            if (pid <= 0) return false;
            var proc = Process.GetProcessById(pid);
            if (!proc.ProcessName.Equals("svchost", StringComparison.OrdinalIgnoreCase)) return false;
            proc.Kill();
            return true;
        }
        catch (Exception ex) { Log?.Invoke($"WIFX - [WINFIX] - repair error: {ex.Message}"); return false; }
    }

    private static int FindNpsmHostPid()
    {
        try
        {
            var psi = new ProcessStartInfo("tasklist.exe", "/svc /fi \"imagename eq svchost.exe\" /fo list")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return 0;
            var outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return 0; }

            foreach (Match m in Regex.Matches(outp,
                @"PID:\s*(\d+)\s*[\r\n]+Services:\s*([^\r\n]+)", RegexOptions.IgnoreCase))
            {
                var services = m.Groups[2].Value.Trim();
                if (services.StartsWith("NPSMSvc", StringComparison.OrdinalIgnoreCase) && !services.Contains(','))
                    return int.Parse(m.Groups[1].Value);
            }
        }
        catch (Exception ex) { Log?.Invoke($"WIFX - [WINFIX] - service lookup error: {ex.Message}"); }
        return 0;
    }
#endif
}
