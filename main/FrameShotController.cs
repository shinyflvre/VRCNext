using System.IO.Compression;
using Newtonsoft.Json.Linq;

namespace VRCNext;

// Owns all FrameShot (in-VR photo) state, logic, and message handling.
// FrameShotService runs in the shared VR subprocess — see VRSubprocessHost / VRSubprocess.

public class FrameShotController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly VROverlayController _vroCtrl;
    private readonly PhotosController _photos;
    private bool _fsEventsWired;

#if WINDOWS
    public bool IsConnected => _core.VrOverlay?.FsConnected ?? false;
#else
    public bool IsConnected => false;
#endif

    public FrameShotController(CoreLibrary core, VROverlayController vroCtrl, PhotosController photos)
    {
        _core    = core;
        _vroCtrl = vroCtrl;
        _photos  = photos;
    }

#if WINDOWS
    private VRSubprocessHost EnsureHost()
    {
        if (_core.VrOverlay == null)
        {
            _core.VrOverlay = new VRSubprocessHost(
                s => _core.SendToJS("log", new { msg = s, color = "sec" }));
        }

        if (!_fsEventsWired)
        {
            _fsEventsWired = true;
            var h = _core.VrOverlay;

            h.OnFsUpdate += d => _core.SendToJS("fsUpdate", d);

            h.OnFsDevices += devices => _core.SendToJS("fsDevices", new { devices, savedDevice = _core.Settings.FsOutputDevice });

            h.OnFsAudioDevices += list =>
            {
                var arr = list.Select(d => new { id = d.id, label = d.label }).ToList();
                _core.SendToJS("fsAudioDevices", new
                {
                    devices  = arr,
                    savedA   = _core.Settings.FsVideoDeviceA,
                    savedB   = _core.Settings.FsVideoDeviceB,
                });
            };

            h.OnFsPhotoSaved += path =>
            {
                if (!string.IsNullOrEmpty(path)) _photos.HandleExternalSave(path);
            };

            h.OnFsQuit += () =>
            {
                _fsEventsWired = false;
                if (!h.VroConnected && !h.SfConnected) _core.VrOverlay = null;
                _core.SendToJS("fsUpdate", new
                {
                    connected = false, framing = false,
                    leftController = false, rightController = false, error = (string?)null
                });
                _vroCtrl.UpdateToolStates();
            };
        }

        return _core.VrOverlay;
    }
#endif

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
#if WINDOWS
            case "fsConnect":
            {
                var host = EnsureHost();
                var (auth, tfa) = _core.VrcApi.GetCookies();
                host.EnsureRunning("", _core.HttpPort, auth, tfa);
                host.FsConnect(_core.Settings.FsLeftButton, _core.Settings.FsRightButton, _core.Settings.FsOutputDevice, _core.Settings.FsActivationRadius, _core.Settings.FsLeftRecordButton, _core.Settings.FsRightRecordButton, _core.Settings.FsGifMaxResolution, _core.Settings.FsGifMaxFps, _core.Settings.FsUseHmdRotations, _core.Settings.FsLeftVideoButton, _core.Settings.FsRightVideoButton, _core.Settings.FsVideoDeviceA, _core.Settings.FsVideoDeviceB, _core.Settings.FsVideoFps, _core.Settings.FsVideoQuality, _core.Settings.FsVideoBitrateQuality, _core.Settings.FsAudioKbps);
                _vroCtrl.UpdateToolStates();
                break;
            }

            case "fsGetDevices":
                EnsureHost();
                var (auth2, tfa2) = _core.VrcApi.GetCookies();
                _core.VrOverlay?.EnsureRunning("", _core.HttpPort, auth2, tfa2);
                _core.VrOverlay?.FsGetDevices();
                break;

            case "fsSetOutput":
                var dev = msg["deviceName"]?.Value<string>() ?? "";
                _core.Settings.FsOutputDevice = dev;
                _core.Settings.Save();
                _core.VrOverlay?.FsSetOutput(dev);
                break;

            case "fsDisconnect":
                if (_core.VrOverlay != null)
                {
                    _fsEventsWired = false;
                    _core.VrOverlay.FsDisconnect();
                    if (!_core.VrOverlay.VroConnected && !_core.VrOverlay.SfConnected) _core.VrOverlay = null;
                }
                _core.SendToJS("fsUpdate", new
                {
                    connected = false, framing = false,
                    leftController = false, rightController = false, error = (string?)null
                });
                _vroCtrl.UpdateToolStates();
                break;

            case "fsConfig":
            {
                var lb  = (uint)(msg["leftButton"]?.Value<int>()        ?? 2);
                var rb  = (uint)(msg["rightButton"]?.Value<int>()       ?? 2);
                var ar  = msg["activationRadius"]?.Value<int>()         ?? 15;
                var lrb = (uint)(msg["leftRecordButton"]?.Value<int>()  ?? 0);
                var rrb = (uint)(msg["rightRecordButton"]?.Value<int>() ?? 0);
                var gmd = msg["gifMaxResolution"]?.Value<int>()         ?? 512;
                var gfp = msg["gifMaxFps"]?.Value<int>()                ?? 10;
                var uhr = msg["useHmdRotations"]?.Value<bool>()         ?? false;
                var lvb = (uint)(msg["leftVideoButton"]?.Value<int>()   ?? 0);
                var rvb = (uint)(msg["rightVideoButton"]?.Value<int>()  ?? 0);
                var vda = msg["videoDeviceA"]?.Value<string>()          ?? "";
                var vdb = msg["videoDeviceB"]?.Value<string>()          ?? "";
                var vfps = msg["videoFps"]?.Value<int>()                ?? 30;
                var vq  = msg["videoQuality"]?.Value<string>()          ?? "1080p";
                var vbq = msg["videoBitrateQuality"]?.Value<string>()   ?? "medium";
                var akb = msg["audioKbps"]?.Value<int>()                ?? 256;
                _core.VrOverlay?.FsConfig(lb, rb, ar, lrb, rrb, gmd, gfp, uhr, lvb, rvb, vda, vdb, vfps, vq, vbq, akb);
                break;
            }

            case "fsGetAudioDevices":
                EnsureHost();
                var (auth3, tfa3) = _core.VrcApi.GetCookies();
                _core.VrOverlay?.EnsureRunning("", _core.HttpPort, auth3, tfa3);
                _core.VrOverlay?.FsGetAudioDevices();
                break;

            case "fsGetFfmpegState":
                _core.SendToJS("fsFfmpegState", GetFfmpegState());
                break;

            case "fsInstallFfmpeg":
                if (_ffmpegInstalling) break;
                _ = InstallFfmpegAsync();
                break;
#endif
        }
    }

    private static string FfmpegExePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    private bool _ffmpegInstalling;

    private object GetFfmpegState() => new
    {
        installed   = File.Exists(FfmpegExePath),
        downloading = _ffmpegInstalling,
        progress    = 0,
    };

#if WINDOWS
    private async Task InstallFfmpegAsync()
    {
        _ffmpegInstalling = true;
        try
        {
            _core.SendToJS("fsFfmpegState", new { installed = false, downloading = true, progress = 0 });

            var tmpZip = Path.Combine(Path.GetTempPath(), $"vrcnext-ffmpeg-{Guid.NewGuid():N}.zip");

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
            http.Timeout = TimeSpan.FromMinutes(10);

            using var resp = await http.GetAsync(
                "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                _core.SendToJS("fsFfmpegState", new { installed = false, error = $"Download: HTTP {(int)resp.StatusCode}" });
                return;
            }

            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using (var stream = await resp.Content.ReadAsStreamAsync())
            await using (var fs = File.Create(tmpZip))
            {
                var buf = new byte[65536];
                long downloaded = 0;
                int read;
                int lastPct = -1;
                while ((read = await stream.ReadAsync(buf)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read));
                    downloaded += read;
                    if (total > 0)
                    {
                        int pct = (int)(downloaded * 100 / total);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            try { _core.SendToJS("fsFfmpegState", new { installed = false, downloading = true, progress = pct }); } catch { }
                        }
                    }
                }
            }

            _core.SendToJS("fsFfmpegState", new { installed = false, downloading = true, progress = 100, installing = true });

            var destDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
            Directory.CreateDirectory(destDir);
            var destExe = Path.Combine(destDir, "ffmpeg.exe");

            using (var archive = System.IO.Compression.ZipFile.OpenRead(tmpZip))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    _core.SendToJS("fsFfmpegState", new { installed = false, error = "ffmpeg.exe not found in archive" });
                    return;
                }
                entry.ExtractToFile(destExe, overwrite: true);
            }

            try { File.Delete(tmpZip); } catch { }

            _core.SendToJS("fsFfmpegState", GetFfmpegState());
        }
        catch (Exception ex)
        {
            try { _core.SendToJS("fsFfmpegState", new { installed = false, error = ex.Message }); } catch { }
        }
        finally
        {
            _ffmpegInstalling = false;
        }
    }
#endif

    public void Toggle()
    {
#if WINDOWS
        if (_core.VrOverlay?.FsConnected == true)
        {
            _fsEventsWired = false;
            _core.VrOverlay.FsDisconnect();
            if (!_core.VrOverlay.VroConnected && !_core.VrOverlay.SfConnected) _core.VrOverlay = null;
            _core.SendToJS("fsUpdate", new
            {
                connected = false, framing = false,
                leftController = false, rightController = false, error = (string?)null
            });
        }
        else
        {
            var host = EnsureHost();
            var (auth, tfa) = _core.VrcApi.GetCookies();
            host.EnsureRunning("", _core.HttpPort, auth, tfa);
            host.FsConnect(_core.Settings.FsLeftButton, _core.Settings.FsRightButton, _core.Settings.FsOutputDevice, _core.Settings.FsActivationRadius, _core.Settings.FsLeftRecordButton, _core.Settings.FsRightRecordButton, _core.Settings.FsGifMaxResolution, _core.Settings.FsGifMaxFps, _core.Settings.FsUseHmdRotations, _core.Settings.FsLeftVideoButton, _core.Settings.FsRightVideoButton, _core.Settings.FsVideoDeviceA, _core.Settings.FsVideoDeviceB, _core.Settings.FsVideoFps, _core.Settings.FsVideoQuality, _core.Settings.FsVideoBitrateQuality, _core.Settings.FsAudioKbps);
        }
        _vroCtrl.UpdateToolStates();
#endif
    }

    public void Dispose()
    {
        _fsEventsWired = false;
    }
}
