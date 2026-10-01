#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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

    [DllImport("combase.dll")]
    private static extern int CoIncrementMTAUsage(out IntPtr cookie);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    private const string SmtcManagerClass = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";
    private static Guid _smtcManagerStaticsIid = new("2050C4EE-11A0-57DE-AED7-C97C70338245");
    private static Guid _asyncInfoIid = new("00000036-0000-0000-C000-000000000046");
    private static int _mtaUsage;

    private static async Task<bool> IsSmtcHungAsync()
    {
        IntPtr op = IntPtr.Zero, info = IntPtr.Zero;
        try
        {
            if (!StartSmtcRequest(out op, out info)) return false;
            long deadline = Environment.TickCount64 + 6000;
            while (true)
            {
                if (GetAsyncStatus(info) != 0) return false;
                if (Environment.TickCount64 >= deadline) return true;
                await Task.Delay(10);
            }
        }
        catch { return false; }
        finally
        {
            if (info != IntPtr.Zero) Marshal.Release(info);
            if (op != IntPtr.Zero) Marshal.Release(op);
        }
    }

    private static unsafe bool StartSmtcRequest(out IntPtr op, out IntPtr info)
    {
        op = IntPtr.Zero; info = IntPtr.Zero;
        if (Interlocked.Exchange(ref _mtaUsage, 1) == 0) CoIncrementMTAUsage(out _);
        IntPtr hstring = IntPtr.Zero, factory = IntPtr.Zero;
        try
        {
            if (WindowsCreateString(SmtcManagerClass, SmtcManagerClass.Length, out hstring) < 0) return false;
            if (RoGetActivationFactory(hstring, ref _smtcManagerStaticsIid, out factory) < 0 || factory == IntPtr.Zero) return false;
            IntPtr result;
            var vtbl = *(IntPtr**)factory;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[6])(factory, &result) < 0 || result == IntPtr.Zero) return false;
            op = result;
            if (Marshal.QueryInterface(op, ref _asyncInfoIid, out info) < 0) { info = IntPtr.Zero; return false; }
            return info != IntPtr.Zero;
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
        }
    }

    private static unsafe int GetAsyncStatus(IntPtr info)
    {
        int status;
        var vtbl = *(IntPtr**)info;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtbl[7])(info, &status) < 0 ? -1 : status;
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
