using Velopack;
using Velopack.Sources;

namespace VRCNext.Services;

public class UpdateService
{
    private const string RepoUrl = "https://github.com/shinyflvre/VRCNext";
    private const string ApiLatest = "https://api.github.com/repos/shinyflvre/VRCNext/releases/latest";

    private UpdateManager? _mgr;
    private UpdateInfo?    _pending;
    private string?        _ghReleaseUrl;

    public UpdateService()
    {
        try { _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
        catch (Exception ex) { CrashHandler.WriteEntry("UpdateService.Ctor", ex); }
    }

    public async Task<string?> CheckAsync()
    {
        if (_mgr != null)
        {
            try
            {
                _pending = await _mgr.CheckForUpdatesAsync();
                return _pending?.TargetFullRelease.Version.ToString();
            }
            catch { return null; }
        }
        if (!OperatingSystem.IsWindows())
            return await CheckGithubAsync();
        return null;
    }

    private async Task<string?> CheckGithubAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestVersion = System.Net.HttpVersion.Version20;
            http.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
            var body = await http.GetStringAsync(ApiLatest);
            var json = Newtonsoft.Json.Linq.JObject.Parse(body);
            var tag = (json["tag_name"]?.ToString() ?? "").TrimStart('v', 'V');
            if (string.IsNullOrEmpty(tag)) return null;
            if (!Version.TryParse(tag, out var latest)) return null;
            if (!Version.TryParse(AppInfo.Version, out var current)) return null;
            if (latest <= current) return null;

            _ghReleaseUrl = json["html_url"]?.ToString() ?? $"{RepoUrl}/releases/latest";
            return tag;
        }
        catch { return null; }
    }

    public async Task DownloadAsync(Action<int> onProgress)
    {
        if (_mgr != null && _pending != null)
        {
            await _mgr.DownloadUpdatesAsync(_pending, onProgress);
            return;
        }
        onProgress(100);
    }

    public void ApplyAndRestart()
    {
        if (_mgr != null && _pending != null)
        {
            _mgr.ApplyUpdatesAndRestart(_pending);
            return;
        }

        var url = _ghReleaseUrl ?? $"{RepoUrl}/releases/latest";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
