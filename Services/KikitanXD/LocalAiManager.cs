using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VRCNext.Services.KikitanXD;

public sealed class LocalAiManager
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "LocalLLM");

    public event Action<string, int, long, long>? OnProgress;
    public event Action<string, bool, string?>? OnFinished;
    public event Action<string>? OnLog;

    private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public static string PathFor(LocalAiItem item) =>
        Path.Combine(Root, item.RelPath.Replace('/', Path.DirectorySeparatorChar));

    public bool IsInstalled(LocalAiItem item)
    {
        var p = PathFor(item);
        if (item.IsArchive) return Directory.Exists(p) && Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Any();
        return File.Exists(p);
    }

    public long SizeOnDisk(LocalAiItem item)
    {
        try
        {
            var p = PathFor(item);
            if (item.IsArchive)
            {
                if (!Directory.Exists(p)) return 0;
                return Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            }
            return File.Exists(p) ? new FileInfo(p).Length : 0;
        }
        catch { return 0; }
    }

    public static bool VulkanAvailable()
    {
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("vulkan-1.dll", out var handle)) return false;
        System.Runtime.InteropServices.NativeLibrary.Free(handle);
        return true;
    }

    public object GetState()
    {
        var items = LocalAiCatalog.Items.Select(i => new
        {
            id          = i.Id,
            kind        = i.Kind.ToString().ToLowerInvariant(),
            name        = i.Name,
            detail      = i.Detail,
            approxBytes = i.ApproxBytes,
            installed   = IsInstalled(i),
            sizeOnDisk  = SizeOnDisk(i),
            busy        = _active.ContainsKey(i.Id),
        }).ToArray();

        return new { root = Root, items, vulkanAvailable = VulkanAvailable() };
    }

    public bool IsBusy(string id) => _active.ContainsKey(id);

    public void CleanObsolete()
    {
        foreach (var rel in LocalAiCatalog.ObsoleteDirs)
        {
            try
            {
                var p = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(p)) continue;
                Directory.Delete(p, true);
                Log($"Local AI: removed obsolete runtime {rel}");
            }
            catch { }
        }
    }

    public void Cancel(string id)
    {
        if (_active.TryGetValue(id, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
    }

    public bool Uninstall(string id)
    {
        var item = LocalAiCatalog.Find(id);
        if (item == null) return false;
        if (_active.ContainsKey(id)) return false;

        try
        {
            var p = PathFor(item);
            if (item.IsArchive)
            {
                if (Directory.Exists(p)) Directory.Delete(p, true);
            }
            else if (File.Exists(p))
            {
                File.Delete(p);
            }
            Log($"Local AI: removed {item.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Local AI: could not remove {item.Name} — {ex.Message}");
            return false;
        }
    }

    public async Task DownloadAsync(string id)
    {
        var item = LocalAiCatalog.Find(id);
        if (item == null) return;

        var cts = new CancellationTokenSource();
        if (!_active.TryAdd(id, cts)) return;

        var target = PathFor(item);
        var tmp    = target + ".part";
        bool ok = false;
        string? error = null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var resp = await _http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
                throw new IOException($"HTTP {(int)resp.StatusCode}");

            long total = resp.Content.Headers.ContentLength ?? item.ApproxBytes;
            long done  = 0;
            int  lastPct = -1;
            var  lastTick = Environment.TickCount;

            await using (var stream = await resp.Content.ReadAsStreamAsync(cts.Token))
            await using (var fs = File.Create(tmp))
            {
                var buf = new byte[262144];
                int read;
                while ((read = await stream.ReadAsync(buf, cts.Token)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), cts.Token);
                    done += read;

                    int pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != lastPct && unchecked(Environment.TickCount - lastTick) > 120)
                    {
                        lastPct  = pct;
                        lastTick = Environment.TickCount;
                        try { OnProgress?.Invoke(id, pct, done, total); } catch { }
                    }
                }
            }

            if (item.IsArchive)
            {
                try { OnProgress?.Invoke(id, 100, total, total); } catch { }
                ExtractArchive(tmp, target, item.ArchivePrefix);
                File.Delete(tmp);

                if (item.ExtraUrl.Length > 0)
                    await FetchExtraAsync(id, item, target, cts.Token);
            }
            else
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
            }

            ok = true;
            Log($"Local AI: {item.Name} ready");
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmp);
            error = "cancelled";
            Log($"Local AI: {item.Name} cancelled");
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            error = ex.Message;
            Log($"Local AI: {item.Name} failed — {ex.Message}");
        }
        finally
        {
            _active.TryRemove(id, out _);
            cts.Dispose();
        }

        Finish(id, ok, error);
    }

    private async Task FetchExtraAsync(string id, LocalAiItem item, string targetDir, CancellationToken ct)
    {
        var tmp = Path.Combine(targetDir, "_extra.part");
        try
        {
            using var resp = await _http.GetAsync(item.ExtraUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new IOException($"extra HTTP {(int)resp.StatusCode}");

            long total = resp.Content.Headers.ContentLength ?? 0;
            long done = 0;
            var lastTick = Environment.TickCount;

            await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs = File.Create(tmp))
            {
                var buf = new byte[262144];
                int read;
                while ((read = await stream.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0 && unchecked(Environment.TickCount - lastTick) > 120)
                    {
                        lastTick = Environment.TickCount;
                        try { OnProgress?.Invoke(id, (int)(done * 100 / total), done, total); } catch { }
                    }
                }
            }

            int written = 0;
            using (var zip = ZipFile.OpenRead(tmp))
            {
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith(item.ExtraPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!entry.FullName.EndsWith(item.ExtraFile, StringComparison.OrdinalIgnoreCase)) continue;
                    entry.ExtractToFile(Path.Combine(targetDir, item.ExtraFile), true);
                    written++;
                }
            }

            if (written == 0)
                throw new InvalidOperationException($"'{item.ExtraFile}' not found in the companion archive.");
        }
        finally { TryDelete(tmp); }
    }

    private static void ExtractArchive(string zipPath, string targetDir, string prefix)
    {
        Directory.CreateDirectory(targetDir);
        var rootFull = Path.GetFullPath(targetDir);
        int written = 0;

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.Length == 0 || entry.FullName.EndsWith('/')) continue;
            if (prefix.Length > 0 && !entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rel = prefix.Length > 0 ? entry.FullName[prefix.Length..] : entry.FullName;
            if (rel.Length == 0) continue;

            var dest = Path.GetFullPath(Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
            written++;
        }

        if (written == 0)
            throw new InvalidOperationException($"Archive contained no files under '{prefix}'.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void Finish(string id, bool ok, string? error)
    {
        try { OnFinished?.Invoke(id, ok, error); } catch { }
    }

    private void Log(string msg)
    {
        try { OnLog?.Invoke(msg); } catch { }
    }
}
