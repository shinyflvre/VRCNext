using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VRCNext.Services.Helpers;

public static class ForegroundAppHelper
{
#if WINDOWS
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static uint   _cachedPid;
    private static string _cachedName = "";
#else
    private static int _cachedPid;
    private static string _cachedName = "";
#endif

    /// <summary>Display name of the app that currently owns the foreground window, or "" when unknown.</summary>
    public static string GetActiveAppName()
    {
#if WINDOWS
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return "";
            if (pid == _cachedPid) return _cachedName;

            using var proc = Process.GetProcessById((int)pid);
            var name = "";
            try
            {
                var path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                    name = FileVersionInfo.GetVersionInfo(path).FileDescription ?? "";
            }
            catch { }
            if (string.IsNullOrWhiteSpace(name)) name = proc.ProcessName;

            _cachedPid  = pid;
            _cachedName = name.Trim();
            return _cachedName;
        }
        catch { return ""; }
#else
        try
        {
            var pid = GetActiveWindowPid();
            if (pid <= 0) return "";
            if (pid == _cachedPid) return _cachedName;

            using var proc = Process.GetProcessById(pid);
            var name = "";
            try
            {
                var path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                    name = FileVersionInfo.GetVersionInfo(path).FileDescription ?? "";
            }
            catch { }
            if (string.IsNullOrWhiteSpace(name)) name = proc.ProcessName;

            _cachedPid = pid;
            _cachedName = name.Trim();
            return _cachedName;
        }
        catch { return ""; }
#endif
    }

#if !WINDOWS
    private static int GetActiveWindowPid()
    {
        var windowId = RunWindowTool("xdotool", "getactivewindow");
        if (!string.IsNullOrWhiteSpace(windowId)
            && int.TryParse(RunWindowTool("xdotool", "getwindowpid", windowId), out var xdotoolPid)
            && xdotoolPid > 0)
            return xdotoolPid;

        var activeWindow = RunWindowTool("xprop", "-root", "_NET_ACTIVE_WINDOW");
        var marker = activeWindow.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return 0;
        var windowHex = activeWindow[(marker + 2)..].Trim();
        if (string.IsNullOrWhiteSpace(windowHex)) return 0;

        var pidProperty = RunWindowTool("xprop", "-id", $"0x{windowHex}", "_NET_WM_PID");
        var equals = pidProperty.IndexOf('=');
        return equals >= 0 && int.TryParse(pidProperty[(equals + 1)..].Trim(), out var xpropPid)
            ? xpropPid
            : 0;
    }

    private static string RunWindowTool(string fileName, params string[] args)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) return "";

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1000))
            {
                process.Kill(entireProcessTree: true);
                return "";
            }
            return process.ExitCode == 0 ? output.Trim() : "";
        }
        catch (System.ComponentModel.Win32Exception) { return ""; }
    }
#endif
}
