using System.Diagnostics;
using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

public partial class MainForm
{
    // Voice Fight helpers
    private object VfBuildItemsPayload() =>
        _vfSettings.Items.Select((item, i) => new
        {
            index = i,
            word = item.Word,
            files = item.Files.Select((f, si) => new
            {
                soundIndex = si,
                filePath = f.FilePath,
                fileName = Path.GetFileName(f.FilePath),
                durationMs = (int)VoiceFightService.GetDuration(f.FilePath).TotalMilliseconds,
                volumePercent = f.VolumePercent
            }).ToList()
        }).ToList();

    private void VfSendChatbox(string text)
    {
        try
        {
            using var udp = new System.Net.Sockets.UdpClient();
            udp.Connect("127.0.0.1", 9000);
            var buf = new List<byte>();
            VfOscString(buf, "/chatbox/input");
            VfOscString(buf, ",sTF"); // string, sendImmediate=true, notifySound=false
            VfOscString(buf, text.Length > 144 ? text[..144] : text);
            var pkt = buf.ToArray();
            udp.Send(pkt, pkt.Length);
        }
        catch { }
    }

    private static void VfOscString(List<byte> buf, string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        buf.AddRange(b);
        int pad = 4 - (b.Length % 4);
        if (pad == 0) pad = 4;
        buf.AddRange(new byte[pad]);
    }

    // Relay Control
    private void StartRelay()
    {
        var folders = _settings.WatchFolders.Where(Directory.Exists).ToList();
        if (folders.Count == 0)
        {
            SendToJS("log", new { msg = "No valid watch folders configured!", color = "err" });
            return;
        }
        var whs = _settings.Webhooks.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Url)).ToList();
        if (whs.Count == 0)
        {
            SendToJS("log", new { msg = "No webhooks active!", color = "err" });
            return;
        }

        _fileWatcher.Start(folders);
        _relayRunning = true;
        _relayStart = DateTime.Now;

        SendToJS("relayState", new { running = true, streams = whs.Count });
        SendToJS("log", new { msg = "Relay started successfully", color = "ok" });
        foreach (var f in folders)
            SendToJS("log", new { msg = $"  Watching: {f}", color = "sec" });
        foreach (var w in whs)
            SendToJS("log", new { msg = $"  Webhook: {w.Name}", color = "accent" });
    }

    private void StopRelay()
    {
        _fileWatcher.Stop();
        _relayRunning = false;

        SendToJS("relayState", new { running = false, streams = 0 });
        SendToJS("log", new { msg = "Relay stopped", color = "warn" });
    }

    // VRCVideoCacher
    private static readonly string VcExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "Tools", "VRCVideoCacher", "VRCVideoCacher.exe");

    private object GetVcState()
    {
        bool installed = File.Exists(VcExePath);
        bool running   = _vcProcess != null && !_vcProcess.HasExited;
        return new { installed, running };
    }

    private void StartVcProcess()
    {
        if (!File.Exists(VcExePath)) return;
        if (_vcProcess != null && !_vcProcess.HasExited) return;

        try
        {
            _vcProcess = Process.Start(new ProcessStartInfo
            {
                FileName         = VcExePath,
                WorkingDirectory = Path.GetDirectoryName(VcExePath)!,
                UseShellExecute  = false,
                CreateNoWindow   = false,
            })!;
            _vcProcess.EnableRaisingEvents = true;
            _vcProcess.Exited += (_, _) =>
            {
                _vcProcess = null;
                try { Invoke(() => SendToJS("vcState", GetVcState())); } catch { }
            };
            SendToJS("vcState", GetVcState());
        }
        catch { }
    }

    private void StopVcProcess()
    {
        try { _vcProcess?.Kill(entireProcessTree: true); } catch { }
        _vcProcess = null;
        SendToJS("vcState", GetVcState());
    }

    private async Task InstallVcAsync()
    {
        try
        {
            Invoke(() => SendToJS("vcState", new { installed = false, running = false, downloading = true, progress = 0 }));

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "VRCNext");

            var apiResp = await http.GetAsync("https://api.github.com/repos/EllyVR/VRCVideoCacher/releases/latest");
            if (!apiResp.IsSuccessStatusCode)
            {
                Invoke(() => SendToJS("vcState", new { installed = false, running = false, error = $"GitHub API: HTTP {(int)apiResp.StatusCode}" }));
                return;
            }

            var json     = JObject.Parse(await apiResp.Content.ReadAsStringAsync());
            var version  = json["tag_name"]?.ToString() ?? "?";
            var assets   = json["assets"] as JArray;
            var exeAsset = assets?.FirstOrDefault(a => a["name"]?.ToString().EndsWith(".exe") == true);
            var dlUrl    = exeAsset?["browser_download_url"]?.ToString();

            if (string.IsNullOrEmpty(dlUrl))
            {
                Invoke(() => SendToJS("vcState", new { installed = false, running = false, error = "No .exe asset in latest release" }));
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(VcExePath)!);

            using var dlResp = await http.GetAsync(dlUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            var total = dlResp.Content.Headers.ContentLength ?? -1L;
            await using var stream = await dlResp.Content.ReadAsStreamAsync();
            await using var fs     = File.Create(VcExePath);

            var buf        = new byte[65536];
            long downloaded = 0;
            int  read;
            while ((read = await stream.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, read));
                downloaded += read;
                if (total > 0)
                {
                    int pct = (int)(downloaded * 100 / total);
                    try { Invoke(() => SendToJS("vcState", new { installed = false, running = false, downloading = true, progress = pct })); } catch { }
                }
            }

            Invoke(() =>
            {
                SendToJS("vcLog",   new { msg = $"VRCVideoCacher {version} installed", color = "ok" });
                SendToJS("vcState", GetVcState());
            });
        }
        catch (Exception ex)
        {
            try { Invoke(() => SendToJS("vcState", new { installed = false, running = false, error = ex.Message })); } catch { }
        }
    }

    // Launch VRChat + extra apps
    private void LaunchVRChat()
    {
        try
        {
            var vrcPath = _settings.VrcPath;
            if (string.IsNullOrWhiteSpace(vrcPath) || !File.Exists(vrcPath))
            {
                SendToJS("log", new { msg = "VRChat path not set or invalid. Configure in Settings.", color = "err" });
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = vrcPath,
                WorkingDirectory = Path.GetDirectoryName(vrcPath) ?? "",
                UseShellExecute = true
            });
            SendToJS("log", new { msg = "Launched VRChat", color = "ok" });

            foreach (var exe in _settings.ExtraExe)
            {
                try
                {
                    if (!File.Exists(exe))
                    {
                        SendToJS("log", new { msg = $"Not found: {Path.GetFileName(exe)}", color = "warn" });
                        continue;
                    }
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                        UseShellExecute = true
                    });
                    SendToJS("log", new { msg = $"Launched: {Path.GetFileName(exe)}", color = "ok" });
                }
                catch (Exception ex)
                {
                    SendToJS("log", new { msg = $"Failed to launch {Path.GetFileName(exe)}: {ex.Message}", color = "err" });
                }
            }
        }
        catch (Exception ex)
        {
            SendToJS("log", new { msg = $"Launch error: {ex.Message}", color = "err" });
        }
    }

    private static bool IsVrcRunning() =>
        Process.GetProcessesByName("VRChat").Any(p => { try { return !p.HasExited; } catch { return false; } });

    private static bool IsSteamVrRunning() =>
        Process.GetProcessesByName("vrserver").Any(p => { try { return !p.HasExited; } catch { return false; } });

    // WebSocket helpers

    private System.Threading.Timer? _wsFallbackTimer;

    private void StartWebSocket()
    {
        var (auth, tfa) = _vrcApi.GetCookies();
        if (string.IsNullOrEmpty(auth)) return;

        _wsService?.Dispose();
        _wsService = new VRChatWebSocketService();

        // friend-active: state changed but we have no user object — just push store as-is
        _wsService.FriendsChanged += (_, _) =>
        {
            if (_vrcApi.IsLoggedIn && _friendStateSeeded)
                PushFriendsFromStore();
        };

        // friend-add / friend-delete: list membership changed — need authoritative REST data
        _wsService.FriendListChanged += (_, _) =>
        {
            if (_vrcApi.IsLoggedIn)
                _ = VrcRefreshFriendsAsync(true);
        };

        _wsService.NotificationArrived += (_, args) =>
        {
            if (!_vrcApi.IsLoggedIn) return;
            // update/delete or missing payload → full REST refresh
            if (args.WsType is "notification-v2-update" or "notification-v2-delete" || args.Data == null)
            {
                _ = VrcGetNotificationsAsync();
                return;
            }
            // notification/notification-v2: process the WS payload directly — no REST needed
            _ = Task.Run(() =>
            {
                try
                {
                    var n = args.WsType == "notification-v2"
                        ? NormalizeNotifV2(args.Data)
                        : NormalizeNotifV1(args.Data);
                    ProcessSingleNotif(n, prependToJs: true);
                }
                catch (Exception ex)
                {
                    Invoke(() => SendToJS("log", new { msg = $"[WS Notif] parse error: {ex.Message}", color = "err" }));
                    _ = VrcGetNotificationsAsync();
                }
            });
        };

        // Small delay so the VRC API reflects the new location before we query it
        _wsService.OwnLocationChanged += (_, _) =>
        {
            if (_vrcApi.IsLoggedIn)
                _ = Task.Delay(3000).ContinueWith(_ => VrcGetCurrentInstanceAsync());
        };

        // All log calls must use Invoke(); these fire on the WebSocket background thread
        _wsService.Connected += (_, _) =>
        {
            Invoke(() =>
            {
                SendToJS("wsStatus", new { connected = true });
                SendToJS("log", new { msg = "[WS] Connected to pipeline.vrchat.cloud", color = "ok" });
            });
            // Only do a full refresh if disconnected long enough to have missed real events.
            // Brief 90s idle-disconnects from VRChat not sending heartbeats don't need a refresh —
            // the 5-min fallback timer is the safety net for those.
            if (_vrcApi.IsLoggedIn && (DateTime.UtcNow - _wsDisconnectedAt).TotalSeconds > 5)
            {
                _ = VrcRefreshFriendsAsync(true);
                _ = VrcGetNotificationsAsync();
            }
        };

        _wsService.Disconnected += (_, _) =>
        {
            _wsDisconnectedAt = DateTime.UtcNow;
            Invoke(() =>
            {
                SendToJS("wsStatus", new { connected = false });
                SendToJS("log", new { msg = "[WS] Disconnected — reconnecting...", color = "warn" });
            });
        };

        _wsService.ConnectError += (_, err) =>
            Invoke(() => SendToJS("log", new { msg = $"[WS] Error: {err}", color = "err" }));

        // Friends Timeline: typed WebSocket events
        _wsService.FriendLocationChanged += OnWsFriendLocation;
        _wsService.FriendWentOffline     += OnWsFriendOffline;
        _wsService.FriendWentOnline      += OnWsFriendOnline;
        _wsService.FriendUpdated         += OnWsFriendUpdated;

        // Pass a delegate so the service fetches fresh cookies on every internal reconnect
        _wsService.Start(auth, tfa ?? "", () =>
        {
            var (a, t) = _vrcApi.GetCookies();
            return (a ?? "", t ?? "");
        });

        // Fallback: safety-net refresh every 5 min in case a WebSocket event was missed.
        // WS events now update the live store directly, so REST is only a last resort.
        _wsFallbackTimer?.Dispose();
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, 30));
        _wsFallbackTimer = new System.Threading.Timer(_ =>
        {
            if (!_vrcApi.IsLoggedIn) return;
            _ = VrcRefreshFriendsAsync(true);
            _ = VrcGetNotificationsAsync();
        }, null, TimeSpan.FromMinutes(5) + jitter, TimeSpan.FromMinutes(5));
    }
}
