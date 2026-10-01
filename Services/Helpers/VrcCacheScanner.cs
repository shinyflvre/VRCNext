using System.Buffers;
using System.Text;

namespace VRCNext.Services.Helpers;

public static class VrcCacheScanner
{
    private const int HeadBytes = 131072;
    private const int AvatarIdLength = 41;

    public static string CacheDir()
    {
        return Path.Combine(VrcPathsHelper.AppDataDir(), "Cache-WindowsPlayer");
    }

    public static int Scan(HashSet<string> scannedFiles, Action<string> onAvatarId, CancellationToken ct = default)
    {
        var dir = CacheDir();
        if (!Directory.Exists(dir)) return 0;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "__data", SearchOption.AllDirectories); }
        catch { return 0; }

        var buffer = ArrayPool<byte>.Shared.Rent(HeadBytes);
        int scanned = 0;

        try
        {
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;
                lock (scannedFiles) { if (!scannedFiles.Add(file)) continue; }
                scanned++;
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    int read = fs.Read(buffer, 0, HeadBytes);
                    if (read <= 0) continue;
                    FindAvatarIds(buffer.AsSpan(0, read), onAvatarId);
                }
                catch { }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return scanned;
    }

    private static void FindAvatarIds(ReadOnlySpan<byte> data, Action<string> onAvatarId)
    {
        var prefix = "avtr_"u8;
        int pos = 0;
        while (pos < data.Length)
        {
            int idx = data[pos..].IndexOf(prefix);
            if (idx < 0) return;
            int start = pos + idx;
            if (IsGuidAt(data, start + prefix.Length))
            {
                onAvatarId(Encoding.ASCII.GetString(data.Slice(start, AvatarIdLength)));
                pos = start + AvatarIdLength;
            }
            else pos = start + 1;
        }
    }

    private static bool IsGuidAt(ReadOnlySpan<byte> data, int at)
    {
        if (at + 36 > data.Length) return false;
        for (int i = 0; i < 36; i++)
        {
            byte b = data[at + i];
            bool dash = i == 8 || i == 13 || i == 18 || i == 23;
            if (dash ? b != (byte)'-' : !IsHex(b)) return false;
        }
        return true;
    }

    private static bool IsHex(byte b) =>
        (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');
}
