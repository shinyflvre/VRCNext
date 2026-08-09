using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VRCNext.Services;

/// <summary>
/// Global crash handler — catches unhandled exceptions and writes detailed
/// crash reports to %AppData%/VRCNext/Logs/Crashes/crash_yyyy-MM-dd_HH-mm-ss.txt
/// </summary>
internal static class CrashHandler
{
    private static string _crashDir        = "";
    private static string _sentinelPath    = "";
    private static string _stderrPath      = "";
    private static string _pendingCrashPath = "";

    // Breadcrumb trail — last 40 operations before crash
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _breadcrumbs = new();
    private const int MaxBreadcrumbs = 40;

    public static void AddBreadcrumb(string msg)
    {
        _breadcrumbs.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {msg}");
        while (_breadcrumbs.Count > MaxBreadcrumbs)
            _breadcrumbs.TryDequeue(out _);
    }

    /// <summary>Manually write a crash entry for caught exceptions that would otherwise be silent.</summary>
    public static void WriteEntry(string context, Exception ex)
        => WriteCrashReport(ex, $"ManualEntry:{context}", isTerminating: false);

    public static void Register()
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "Logs");
        _crashDir         = Path.Combine(logsDir, "Crashes");
        _sentinelPath     = Path.Combine(logsDir, "session.sentinel");
        _pendingCrashPath = Path.Combine(logsDir, "pending_crash.txt");
        Directory.CreateDirectory(_crashDir);

        // Redirect Win32 STD_ERROR_HANDLE
        RedirectStderr(logsDir);
        WriteSentinel();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteSessionFiles();
        SpawnWatchdog();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteCrashReport(e.ExceptionObject as Exception, "AppDomain.UnhandledException", e.IsTerminating);
            DeleteSessionFiles();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashReport(e.Exception, "TaskScheduler.UnobservedTaskException", isTerminating: false);
            e.SetObserved();
        };
    }
    public static void OnCleanShutdown() => DeleteSessionFiles();

#if WINDOWS
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, nint hHandle);
#endif

    private static void RedirectStderr(string logsDir)
    {
        try
        {
            // Clean up old stderr files — keep the last 5
            foreach (var old in Directory.GetFiles(logsDir, "stderr_*.txt")
                         .OrderByDescending(f => f).Skip(5))
                try { File.Delete(old); } catch { }
        }
        catch { }

#if WINDOWS
        try
        {
            _stderrPath = Path.Combine(logsDir, $"stderr_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

            const uint GENERIC_WRITE         = 0x40000000;
            const uint FILE_SHARE_READ       = 0x00000001;
            const uint FILE_SHARE_WRITE      = 0x00000002;
            const uint CREATE_ALWAYS         = 2;
            const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
            const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
            const int  STD_ERROR_HANDLE        = -12;

            var handle = CreateFile(
                _stderrPath,
                GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                nint.Zero,
                CREATE_ALWAYS,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH,
                nint.Zero);

            if (handle != new nint(-1))
                SetStdHandle(STD_ERROR_HANDLE, handle);
        }
        catch { }
#endif
    }

    private static void WriteSentinel()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var selfExe = VRCNext.AppInfo.SelfExecutable;
            if (string.IsNullOrEmpty(selfExe)) selfExe = asm.Location;
            File.WriteAllText(_sentinelPath, new StringBuilder()
                .AppendLine($"Started   : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}")
                .AppendLine($"PID       : {Environment.ProcessId}")
                .AppendLine($"Version   : {asm.GetName().Version}")
                .AppendLine($"Exe       : {selfExe}")
                .AppendLine($"Stderr    : {_stderrPath}")
                .ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private static void DeleteSessionFiles()
    {
        try { if (File.Exists(_sentinelPath)) File.Delete(_sentinelPath); } catch { }
        // Delete the stderr log — nothing crashed, no need to keep it
        try { if (!string.IsNullOrEmpty(_stderrPath) && File.Exists(_stderrPath)) File.Delete(_stderrPath); } catch { }
    }

    private static void SpawnWatchdog()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            var psi = new ProcessStartInfo
            {
                FileName        = exe,
                UseShellExecute = false,
                CreateNoWindow  = true,
                WindowStyle     = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("--watchdog");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(_sentinelPath);
            psi.ArgumentList.Add(_crashDir);

            Process.Start(psi);
        }
        catch { }
    }

    private static void WriteCrashReport(Exception? ex, string source, bool isTerminating)
    {
        try
        {
            var timestamp = DateTime.Now;
            var path = Path.Combine(_crashDir, $"crash_{timestamp:yyyy-MM-dd_HH-mm-ss}.txt");

            var sb = new StringBuilder();

            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("                    VRCNext Crash Report");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine();

            sb.AppendLine($"Timestamp (local) : {timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Timestamp (UTC)   : {timestamp.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Source            : {source}");
            sb.AppendLine($"Is Terminating    : {isTerminating}");
            sb.AppendLine();

            sb.AppendLine("─── Application ───────────────────────────────────────────────");
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            sb.AppendLine($"Name              : {asm.GetName().Name}");
            sb.AppendLine($"Version           : {asm.GetName().Version}");
            sb.AppendLine($"Informational     : {asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "N/A"}");
            sb.AppendLine($"Location          : {Environment.ProcessPath ?? asm.Location}");
            sb.AppendLine($"Working Dir       : {Environment.CurrentDirectory}");
            sb.AppendLine($"Command Line      : {Environment.CommandLine}");
            sb.AppendLine($"Process ID        : {Environment.ProcessId}");
            sb.AppendLine($"Uptime            : {(DateTime.Now - Process.GetCurrentProcess().StartTime):hh\\:mm\\:ss}");
            sb.AppendLine();

            sb.AppendLine("─── Runtime ───────────────────────────────────────────────────");
            sb.AppendLine($".NET Version      : {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Runtime ID        : {RuntimeInformation.RuntimeIdentifier}");
            sb.AppendLine($"Architecture      : {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"Debug Build       : {Debugger.IsAttached}");
            sb.AppendLine();

            sb.AppendLine("─── System ────────────────────────────────────────────────────");
            sb.AppendLine($"OS                : {RuntimeInformation.OSDescription}");
            sb.AppendLine($"OS Architecture   : {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"Machine Name      : {Environment.MachineName}");
            sb.AppendLine($"User Name         : {Environment.UserName}");
            sb.AppendLine($"Processors        : {Environment.ProcessorCount}");
            sb.AppendLine();

            sb.AppendLine("─── Memory ────────────────────────────────────────────────────");
            var proc = Process.GetCurrentProcess();
            sb.AppendLine($"Working Set       : {FormatBytes(proc.WorkingSet64)}");
            sb.AppendLine($"Private Memory    : {FormatBytes(proc.PrivateMemorySize64)}");
            sb.AppendLine($"GC Total Memory   : {FormatBytes(GC.GetTotalMemory(false))}");
            sb.AppendLine($"GC Gen0 Count     : {GC.CollectionCount(0)}");
            sb.AppendLine($"GC Gen1 Count     : {GC.CollectionCount(1)}");
            sb.AppendLine($"GC Gen2 Count     : {GC.CollectionCount(2)}");
            sb.AppendLine($"Thread Count      : {proc.Threads.Count}");
            sb.AppendLine();

            sb.AppendLine("─── Exception ─────────────────────────────────────────────────");
            if (ex != null)
                WriteException(sb, ex, depth: 0);
            else
                sb.AppendLine("(No exception object available)");
            sb.AppendLine();

            sb.AppendLine("─── Recent Activity (breadcrumbs) ─────────────────────────────");
            var crumbs = _breadcrumbs.ToArray();
            if (crumbs.Length == 0)
                sb.AppendLine("  (no breadcrumbs recorded)");
            else
                foreach (var c in crumbs)
                    sb.AppendLine($"  {c}");
            sb.AppendLine();

            sb.AppendLine("─── Threads ───────────────────────────────────────────────────");
            try
            {
                foreach (ProcessThread t in proc.Threads)
                {
                    try { sb.AppendLine($"  ID={t.Id,-6} State={t.ThreadState,-15} Priority={t.CurrentPriority,-4} CPU={t.TotalProcessorTime.TotalMilliseconds:F0}ms"); }
                    catch { sb.AppendLine($"  ID={t.Id} (info unavailable)"); }
                }
            }
            catch { sb.AppendLine("  (thread dump failed)"); }
            sb.AppendLine();

            sb.AppendLine("─── Possible Causes ───────────────────────────────────────────");
            sb.AppendLine("  Known crash-prone areas in VRCNext:");
            sb.AppendLine("  • async void OnNewFile (PhotosController) — webhook/post exception");
            sb.AppendLine("  • MemoryTrimService Task.Run — GC.Collect / P/Invoke failure");
            sb.AppendLine("  • SystemTray STA thread — WinForms pump exception");
            sb.AppendLine("  • HttpListener fire-and-forget — unhandled IO/OOM");
            sb.AppendLine("  • TimelineService OOM — all events loaded in RAM");
            sb.AppendLine("  • PlayerProfileCache OOM — unbounded, grows per session");
            sb.AppendLine($"  Working Set at crash: {FormatBytes(proc.WorkingSet64)}");
            try
            {
                var drive = new System.IO.DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))!);
                sb.AppendLine($"  AppData drive free:  {FormatBytes(drive.AvailableFreeSpace)} of {FormatBytes(drive.TotalSize)}");
            }
            catch { }
            sb.AppendLine();

            sb.AppendLine("─── Loaded Assemblies ─────────────────────────────────────────");
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
            {
                var name = a.GetName();
                sb.AppendLine($"  {name.Name,-45} {name.Version}");
            }
            sb.AppendLine();

            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("                      End of Crash Report");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            // Mark crash as pending so the next startup shows the report modal
            if (isTerminating) WritePendingCrashMarker(path);
        }
        catch (Exception writeEx)
        {
            try { Console.Error.WriteLine($"CrashHandler.WriteCrashReport failed: {writeEx}"); } catch { }
        }
    }

    // ── Pending-crash helpers (called from AppShell on next startup) ────────

    private static void WritePendingCrashMarker(string crashFilePath)
    {
        try { File.WriteAllText(_pendingCrashPath, crashFilePath, Encoding.UTF8); } catch { }
    }

    /// <summary>Returns the path to the pending crash file, or null if none exists.</summary>
    public static string? GetPendingCrashFilePath()
    {
        try
        {
            if (!File.Exists(_pendingCrashPath)) return null;
            var path = File.ReadAllText(_pendingCrashPath, Encoding.UTF8).Trim();
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    public static void ClearPendingCrash()
    {
        try { if (File.Exists(_pendingCrashPath)) File.Delete(_pendingCrashPath); } catch { }
    }

    /// <summary>
    /// Returns the key sections of a crash log as plain text for display in the modal preview.
    /// Extracts Exception + Breadcrumbs for managed crashes; falls back to the first 80 lines.
    /// </summary>
    public static string GetPreviewText(string crashContent, int maxLines = 80)
    {
        var exception   = ExtractCrashSection(crashContent, "─── Exception");
        var breadcrumbs = ExtractCrashSection(crashContent, "─── Recent Activity");
        var combined = string.Concat(exception, breadcrumbs);
        if (string.IsNullOrWhiteSpace(combined))
            combined = crashContent; // watchdog report — show full content

        return string.Join('\n', combined.Split('\n').Take(maxLines));
    }

    /// <summary>
    /// Strips personal data from a crash report and extracts the sections useful for developers.
    /// Safe to send to an external endpoint.
    /// </summary>
    public static string SanitizeForReport(string crashContent)
    {
        var sb = new StringBuilder();
        foreach (var header in new[] { "─── Exception", "─── Recent Activity", "─── CLR stderr", "─── Windows Application Event Log" })
        {
            var section = ExtractCrashSection(crashContent, header);
            if (!string.IsNullOrWhiteSpace(section)) sb.Append(section);
        }
        var result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result)) result = crashContent;

        // Strip Windows absolute paths (may contain username in C:\Users\<name>\...)
        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"[A-Za-z]:\\[^\s\n,""']+", "<path-redacted>");
        // Redact Machine Name / User Name fields
        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"(Machine Name\s*:\s*)\S+", "$1<redacted>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"(User Name\s*:\s*)\S+", "$1<redacted>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return result;
    }

    private static string ExtractCrashSection(string report, string sectionHeader)
    {
        var start = report.IndexOf(sectionHeader, StringComparison.Ordinal);
        if (start < 0) return "";
        var nextSection = report.IndexOf("\n─── ", start + sectionHeader.Length, StringComparison.Ordinal);
        var end = nextSection >= 0 ? nextSection + 1 : report.Length;
        return report[start..end].TrimEnd() + "\n\n";
    }

    private static void WriteException(StringBuilder sb, Exception ex, int depth)
    {
        var prefix = depth == 0 ? "" : $"[Inner Exception {depth}] ";

        sb.AppendLine($"{prefix}Type              : {ex.GetType().FullName}");
        sb.AppendLine($"{prefix}Message           : {ex.Message}");
        if (ex.HResult != 0)
            sb.AppendLine($"{prefix}HResult           : 0x{ex.HResult:X8}");
        if (!string.IsNullOrEmpty(ex.Source))
            sb.AppendLine($"{prefix}Source            : {ex.Source}");
        if (ex.TargetSite != null)
            sb.AppendLine($"{prefix}Target Site       : {ex.TargetSite.DeclaringType?.FullName}.{ex.TargetSite.Name}");

        if (ex is AggregateException agg)
            sb.AppendLine($"{prefix}Inner Exceptions  : {agg.InnerExceptions.Count}");

        if (ex.Data.Count > 0)
        {
            sb.AppendLine($"{prefix}Data              :");
            foreach (System.Collections.DictionaryEntry entry in ex.Data)
                sb.AppendLine($"  {entry.Key} = {entry.Value}");
        }

        sb.AppendLine($"{prefix}Stack Trace       :");
        if (!string.IsNullOrEmpty(ex.StackTrace))
            foreach (var line in ex.StackTrace.Split('\n'))
                sb.AppendLine($"  {line.TrimEnd()}");
        else
            sb.AppendLine("  (No stack trace available)");

        if (ex is AggregateException aggEx)
        {
            foreach (var inner in aggEx.InnerExceptions)
            {
                sb.AppendLine();
                WriteException(sb, inner, depth + 1);
            }
        }
        else if (ex.InnerException != null)
        {
            sb.AppendLine();
            WriteException(sb, ex.InnerException, depth + 1);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.##} {sizes[order]} ({bytes:N0} bytes)";
    }
}
