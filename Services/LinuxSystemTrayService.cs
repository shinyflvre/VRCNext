#if !WINDOWS
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Photino.NET;
using VRCNext.Services;

namespace VRCNext;

public sealed class LinuxSystemTrayService : IDisposable
{
    private static readonly string HelperScript = """
        import os
        import queue
        import shutil
        import socket
        import sys
        import threading
        import json

        socket_path, icon_path = sys.argv[1:3]

        def send_command(command):
            try:
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                    client.connect(socket_path)
                    client.sendall(command.encode())
            except OSError as ex:
                print(f"Tray command failed: {ex}", file=sys.stderr, flush=True)

        commands = queue.Queue()
        quit_requested = threading.Event()
        state = {
            "name": "",
            "status": "offline",
            "description": "",
            "logged_in": False,
            "vrc_running": False,
        }

        def display_profile(item=None):
            if not state["logged_in"]:
                return "Not logged in"
            name = state["name"] or "VRChat"
            description = state["description"]
            return f"{name} - {state['status']}" + (f": {description}" if description else "")

        status_options = (
            ("join me", "Join Me"),
            ("active", "Online"),
            ("ask me", "Ask Me"),
            ("busy", "Do Not Disturb"),
        )

        def read_commands():
            for line in sys.stdin:
                try:
                    command = json.loads(line)
                except json.JSONDecodeError:
                    command = line.strip()
                commands.put(command)
            commands.put("quit")

        threading.Thread(target=read_commands, daemon=True).start()

        def send_action(command):
            send_command(command)

        def run_pystray():
            import pystray
            from PIL import Image

            image = Image.open(icon_path).convert("RGBA")

            status_menu = pystray.Menu(*(
                pystray.MenuItem(
                    label,
                    lambda icon, item, value=status: send_action(f"status:{value}"),
                    checked=lambda item, value=status: state["status"] == value,
                )
                for status, label in status_options
            ))
            menu = pystray.Menu(
                pystray.MenuItem(display_profile, lambda icon, item: send_action("show"), default=True),
                pystray.MenuItem("Set Status", status_menu),
                pystray.MenuItem(
                    "Launch VRChat (Desktop)",
                    lambda icon, item: send_action("launch:desktop"),
                    visible=lambda item: not state["vrc_running"],
                ),
                pystray.MenuItem(
                    "Launch VRChat (VR)",
                    lambda icon, item: send_action("launch:vr"),
                    visible=lambda item: not state["vrc_running"],
                ),
                pystray.MenuItem("Close VRCN", lambda icon, item: send_action("quit")),
            )
            icon = pystray.Icon(
                "vrcnext",
                image,
                "VRCNext",
                menu,
            )

            def apply_commands():
                while True:
                    command = commands.get()
                    if isinstance(command, dict) and command.get("type") == "state":
                        state.update(command.get("data", {}))
                        icon.update_menu()
                        continue
                    if command == "show":
                        icon.visible = True
                    elif command == "hide":
                        icon.visible = False
                    elif command == "quit":
                        quit_requested.set()
                        icon.stop()
                        return

            def setup(icon):
                icon.visible = True
                send_command("ready")
                threading.Thread(target=apply_commands, daemon=True).start()

            icon.run(setup=setup)

        def run_appindicator():
            import gi
            gi.require_version("Gtk", "3.0")
            try:
                gi.require_version("AyatanaAppIndicator3", "0.1")
                from gi.repository import AyatanaAppIndicator3 as AppIndicator
            except (ImportError, ValueError):
                gi.require_version("AppIndicator3", "0.1")
                from gi.repository import AppIndicator3 as AppIndicator
            from gi.repository import Gtk, GLib

            icon_directory = os.path.dirname(icon_path)
            icon_name = "vrcnext"
            if icon_path and os.path.isfile(icon_path):
                extension = os.path.splitext(icon_path)[1].lower()
                if extension not in (".png", ".svg", ".xpm"):
                    extension = ".png"
                themed_icon_path = os.path.join(icon_directory, icon_name + extension)
                if os.path.abspath(icon_path) != os.path.abspath(themed_icon_path):
                    shutil.copyfile(icon_path, themed_icon_path)

            indicator = AppIndicator.Indicator.new(
                "vrcnext",
                icon_name,
                AppIndicator.IndicatorCategory.APPLICATION_STATUS,
            )
            indicator.set_icon_theme_path(icon_directory)
            menu = Gtk.Menu()
            profile_item = Gtk.MenuItem(label=display_profile())
            profile_item.connect("activate", lambda item: send_action("show"))
            menu.append(profile_item)
            status_item = Gtk.MenuItem(label="Set Status")
            status_menu = Gtk.Menu()
            status_item.set_submenu(status_menu)
            status_items = {}
            updating_status = [False]
            for status, label in status_options:
                item = Gtk.CheckMenuItem(label=label)
                item.connect(
                    "toggled",
                    lambda selected, value=status: send_action(f"status:{value}")
                    if selected.get_active() and not updating_status[0] else None,
                )
                status_menu.append(item)
                status_items[status] = item
            menu.append(status_item)
            menu.append(Gtk.SeparatorMenuItem())

            desktop_item = Gtk.MenuItem(label="Launch VRChat (Desktop)")
            desktop_item.connect("activate", lambda item: send_action("launch:desktop"))
            menu.append(desktop_item)
            vr_item = Gtk.MenuItem(label="Launch VRChat (VR)")
            vr_item.connect("activate", lambda item: send_action("launch:vr"))
            menu.append(vr_item)
            close_item = Gtk.MenuItem(label="Close VRCN")
            close_item.connect("activate", lambda item: send_action("quit"))
            menu.append(close_item)
            menu.show_all()
            indicator.set_menu(menu)
            indicator.set_title("VRCNext")
            indicator.set_status(AppIndicator.IndicatorStatus.ACTIVE)
            updating_status[0] = True
            for status, item in status_items.items():
                item.set_active(state["status"] == status)
            updating_status[0] = False

            def apply_commands():
                while not commands.empty():
                    command = commands.get_nowait()
                    if isinstance(command, dict) and command.get("type") == "state":
                        state.update(command.get("data", {}))
                        profile_item.set_label(display_profile())
                        launch_visible = not state["vrc_running"]
                        desktop_item.set_visible(launch_visible)
                        vr_item.set_visible(launch_visible)
                        updating_status[0] = True
                        for status, item in status_items.items():
                            item.set_active(state["status"] == status)
                        updating_status[0] = False
                        continue
                    if command == "show":
                        indicator.set_status(AppIndicator.IndicatorStatus.ACTIVE)
                    elif command == "hide":
                        indicator.set_status(AppIndicator.IndicatorStatus.PASSIVE)
                    elif command == "quit":
                        quit_requested.set()
                        Gtk.main_quit()
                        return False
                return True

            GLib.timeout_add(100, apply_commands)
            GLib.timeout_add(500, lambda: (send_command("ready"), False)[1])
            Gtk.main()
            return quit_requested.is_set()

        try:
            run_pystray()
        except Exception as ex:
            if quit_requested.is_set():
                sys.exit(0)
            print(f"pystray tray failed: {ex}", file=sys.stderr, flush=True)

        if quit_requested.is_set():
            sys.exit(0)

        try:
            run_appindicator()
        except Exception as ex:
            print(
                "Linux tray requires Python pystray and Pillow, or GTK 3 with "
                f"AppIndicator bindings: {ex}",
                file=sys.stderr,
                flush=True,
            )
            raise
        """;

    private readonly object _processLock = new();
    private Socket? _server;
    private Thread? _listenerThread;
    private Process? _helper;
    private string? _workingDirectory;
    private string? _socketPath;
    private Timer? _stateTimer;
    private readonly object _stateLock = new();
    private string _displayName = "";
    private string _status = "offline";
    private string _statusDescription = "";
    private bool _loggedIn;
    private volatile bool _pendingVisible;
    private volatile bool _stopping;
    private volatile bool _ready;

    public Action? OnShowWindow { get; set; }
    public Action? OnClose { get; set; }
    public Action<string>? OnStatusChange { get; set; }
    public Action<bool>? OnLaunchVRChat { get; set; }
    public Func<bool>? IsVrcRunning { get; set; }
    public Action? OnReady { get; set; }
    public Action<string>? OnStatus { get; set; }
    public Action<string>? OnUnavailable { get; set; }
    public bool IsReady => _ready;

    public void Initialize(PhotinoWindow window)
    {
        if (!OperatingSystem.IsLinux() || _stopping || _server != null) return;

        try
        {
            CleanupOrphanedHelpers();
            var socketPath = Path.Combine(Path.GetTempPath(), $"vrcnext-{Guid.NewGuid():N}.sock");
            _socketPath = socketPath;
            _workingDirectory = Path.Combine(Path.GetTempPath(), $"vrcnext-tray-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_workingDirectory);
            File.SetUnixFileMode(_workingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var scriptPath = Path.Combine(_workingDirectory, "tray.py");
            File.WriteAllText(scriptPath, HelperScript, Encoding.UTF8);
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            _server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _server.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _server.Listen(4);
            _listenerThread = new Thread(ListenForCommands) { IsBackground = true, Name = "LinuxTrayCommands" };
            _listenerThread.Start();

            var bundledIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frontend", "logo.png");
            if (!File.Exists(bundledIconPath))
                throw new FileNotFoundException("The bundled Linux tray icon is missing.", bundledIconPath);
            var iconPath = Path.Combine(_workingDirectory, "vrcnext.png");
            File.Copy(bundledIconPath, iconPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = "python3",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add(socketPath);
            startInfo.ArgumentList.Add(iconPath);

            var helper = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            helper.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) OnStatus?.Invoke(e.Data); };
            helper.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) OnStatus?.Invoke(e.Data); };
            helper.Exited += (_, _) =>
            {
                if (_stopping) return;
                _ready = false;
                var exitCode = helper.ExitCode;
                if (exitCode != 0)
                {
                    var message = $"Linux tray helper exited with code {exitCode}.";
                    OnStatus?.Invoke(message);
                    OnUnavailable?.Invoke(message);
                }
            };

            if (!helper.Start())
                throw new InvalidOperationException("python3 did not start the Linux tray helper.");
            _helper = helper;
            helper.BeginOutputReadLine();
            helper.BeginErrorReadLine();
            _stateTimer = new Timer(_ => SendTrayState(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"Linux tray unavailable: {ex.Message}");
            OnUnavailable?.Invoke($"Linux tray unavailable: {ex.Message}");
            CrashHandler.WriteEntry("LinuxSystemTrayService.Initialize", ex);
            StopHelper();
        }
    }

    private void CleanupOrphanedHelpers()
    {
        var tempDirectory = Path.GetFullPath(Path.GetTempPath());
        var helperDirectoryPrefix = Path.Combine(tempDirectory, "vrcnext-tray-");

        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(processDirectory), out var processId) ||
                processId == Environment.ProcessId)
                continue;

            var procRoot = Path.Combine("/proc", processId.ToString());
            try
            {
                var args = File.ReadAllText(Path.Combine(procRoot, "cmdline"))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length < 3) continue;

                var scriptPath = Path.GetFullPath(args[1]);
                var helperDirectory = Path.GetDirectoryName(scriptPath);
                if (helperDirectory == null ||
                    !helperDirectory.StartsWith(helperDirectoryPrefix, StringComparison.Ordinal) ||
                    !string.Equals(Path.GetFileName(scriptPath), "tray.py", StringComparison.Ordinal) ||
                    !File.Exists(scriptPath))
                    continue;

                var script = File.ReadAllText(scriptPath);
                if (!script.Contains("socket_path, icon_path = sys.argv[1:3]", StringComparison.Ordinal))
                    continue;

                var parentId = GetParentProcessId(procRoot);
                if (parentId > 1 && IsVrcNextProcess(parentId))
                    continue;

                using var process = Process.GetProcessById(processId);
                process.Kill();
                if (!process.WaitForExit(2000))
                {
                    OnStatus?.Invoke($"Orphaned Linux tray helper {processId} did not exit after termination.");
                    continue;
                }

                if (args.Length > 2)
                {
                    var socketPath = Path.GetFullPath(args[2]);
                    if (socketPath.StartsWith(tempDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                        Path.GetFileName(socketPath).StartsWith("vrcnext-", StringComparison.Ordinal) &&
                        socketPath.EndsWith(".sock", StringComparison.Ordinal))
                        File.Delete(socketPath);
                }

                Directory.Delete(helperDirectory, recursive: true);
                OnStatus?.Invoke($"Removed orphaned Linux tray helper {processId}.");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or
                                       UnauthorizedAccessException or ArgumentException or InvalidOperationException or
                                       System.ComponentModel.Win32Exception)
            {
                if (ex is not FileNotFoundException and not DirectoryNotFoundException)
                {
                    OnStatus?.Invoke($"Could not clean Linux tray helper {processId}: {ex.Message}");
                    CrashHandler.WriteEntry("LinuxSystemTrayService.CleanupOrphanedHelpers", ex);
                }
            }
        }
    }

    private static int GetParentProcessId(string procRoot)
    {
        foreach (var line in File.ReadLines(Path.Combine(procRoot, "status")))
        {
            if (line.StartsWith("PPid:", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(5).Trim(), out var parentId))
                return parentId;
        }
        return 0;
    }

    private static bool IsVrcNextProcess(int processId)
    {
        var procRoot = Path.Combine("/proc", processId.ToString());
        var commandLine = File.ReadAllText(Path.Combine(procRoot, "cmdline"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (commandLine.Any(arg =>
                string.Equals(Path.GetFileName(arg), "VRCNext", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(arg), "VRCNext.dll", StringComparison.OrdinalIgnoreCase)))
            return true;

        var processName = File.ReadAllText(Path.Combine(procRoot, "comm")).Trim();
        return string.Equals(processName, "VRCNext", StringComparison.OrdinalIgnoreCase);
    }

    public void SetVisible(bool visible)
    {
        _pendingVisible = visible;
        if (_ready) SendHelperCommand(visible ? "show" : "hide");
    }

    public void UpdateUserInfo(string name, string status, string statusDescription, string imageUrl)
    {
        lock (_stateLock)
        {
            _displayName = name;
            _status = status;
            _statusDescription = statusDescription;
            _loggedIn = true;
        }
        SendTrayState();
    }

    public void SetLoggedIn(bool loggedIn)
    {
        lock (_stateLock) _loggedIn = loggedIn;
        SendTrayState();
    }

    private void SendTrayState()
    {
        if (!_ready) return;
        string name, status, description;
        bool loggedIn;
        lock (_stateLock)
        {
            name = _displayName;
            status = _status;
            description = _statusDescription;
            loggedIn = _loggedIn;
        }

        SendHelperCommand(JsonSerializer.Serialize(new
        {
            type = "state",
            data = new
            {
                name,
                status,
                description,
                logged_in = loggedIn,
                vrc_running = IsVrcRunning?.Invoke() ?? false,
            },
        }));
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _stateTimer?.Dispose();
        _stateTimer = null;
        StopHelper();

        var directory = _workingDirectory;
        _workingDirectory = null;
        if (directory != null)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashHandler.WriteEntry("LinuxSystemTrayService.Cleanup", ex);
            }
        }

        var socketPath = _socketPath;
        _socketPath = null;
        if (socketPath != null)
        {
            try { File.Delete(socketPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashHandler.WriteEntry("LinuxSystemTrayService.CleanupSocket", ex);
            }
        }
    }

    private void ListenForCommands()
    {
        while (!_stopping)
        {
            Socket? client = null;
            try
            {
                client = _server?.Accept();
                if (client == null) return;
                using var buffer = new MemoryStream();
                var chunk = new byte[1024];
                int count;
                while ((count = client.Receive(chunk)) > 0)
                {
                    if (buffer.Length + count > 64 * 1024)
                        throw new InvalidDataException("Linux tray command exceeded the size limit.");
                    buffer.Write(chunk, 0, count);
                }
                var command = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).Trim();
                switch (command)
                {
                    case "ready":
                        _ready = true;
                        SendHelperCommand(_pendingVisible ? "show" : "hide");
                        OnReady?.Invoke();
                        break;
                    case "show":
                        OnShowWindow?.Invoke();
                        break;
                    default:
                        if (command.StartsWith("status:", StringComparison.Ordinal))
                        {
                            var status = command["status:".Length..];
                            lock (_stateLock) _status = status;
                            SendTrayState();
                            OnStatusChange?.Invoke(status);
                        }
                        else if (command.StartsWith("launch:", StringComparison.Ordinal))
                            OnLaunchVRChat?.Invoke(command["launch:".Length..] == "vr");
                        else if (command == "quit")
                            OnClose?.Invoke();
                        break;
                }
            }
            catch (SocketException) when (_stopping) { return; }
            catch (ObjectDisposedException) when (_stopping) { return; }
            catch (Exception ex)
            {
                if (!_stopping)
                {
                    OnStatus?.Invoke($"Linux tray command failed: {ex.Message}");
                    CrashHandler.WriteEntry("LinuxSystemTrayService.ListenForCommands", ex);
                }
            }
            finally
            {
                client?.Dispose();
            }
        }
    }

    private void SendHelperCommand(string command)
    {
        lock (_processLock)
        {
            if (_helper is not { HasExited: false }) return;
            try
            {
                _helper.StandardInput.WriteLine(command);
                _helper.StandardInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                if (!_stopping)
                {
                    OnStatus?.Invoke($"Linux tray command failed: {ex.Message}");
                    CrashHandler.WriteEntry("LinuxSystemTrayService.SendHelperCommand", ex);
                }
            }
        }
    }

    private void StopHelper()
    {
        _ready = false;
        SendHelperCommand("quit");
        try { _server?.Dispose(); }
        catch (SocketException ex) { CrashHandler.WriteEntry("LinuxSystemTrayService.StopSocket", ex); }
        _server = null;
        _listenerThread?.Join(1000);
        _listenerThread = null;

        lock (_processLock)
        {
            var helper = _helper;
            _helper = null;
            if (helper == null) return;
            try
            {
                if (!helper.HasExited && !helper.WaitForExit(1500))
                {
                    helper.Kill();
                    helper.WaitForExit(1500);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                CrashHandler.WriteEntry("LinuxSystemTrayService.StopHelper", ex);
            }
            finally
            {
                helper.Dispose();
            }
        }
    }
}

internal static class LinuxGtkWindow
{
    public static void SetVisible(PhotinoWindow window, bool visible)
    {
        window.Invoke(() =>
        {
            var handle = FindWindow(window.Title);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Unable to find the VRCNext GTK window.");

            if (visible)
            {
                gtk_widget_show_all(handle);
                gtk_window_present(handle);
            }
            else
            {
                gtk_widget_hide(handle);
            }
        });
    }

    private static IntPtr FindWindow(string title)
    {
        var windows = gtk_window_list_toplevels();
        try
        {
            var current = windows;
            while (current != IntPtr.Zero)
            {
                var listItem = Marshal.PtrToStructure<GList>(current);
                if (listItem.Data != IntPtr.Zero)
                {
                    var nativeTitle = gtk_window_get_title(listItem.Data);
                    if (string.Equals(Marshal.PtrToStringUTF8(nativeTitle), title, StringComparison.Ordinal))
                        return listItem.Data;
                }
                current = listItem.Next;
            }
            return IntPtr.Zero;
        }
        finally
        {
            if (windows != IntPtr.Zero) g_list_free(windows);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GList
    {
        public IntPtr Data;
        public IntPtr Next;
        public IntPtr Previous;
    }

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_window_list_toplevels();

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_window_get_title(IntPtr window);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_widget_hide(IntPtr widget);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_widget_show_all(IntPtr widget);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_window_present(IntPtr window);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_list_free(IntPtr list);
}
#endif
