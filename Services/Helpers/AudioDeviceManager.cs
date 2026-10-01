using System.Runtime.InteropServices;

namespace VRCNext.Services.Helpers;

public enum AudioSelectionMode
{
    SystemDefault,
    Endpoint,
    UnresolvedLegacy,
}

public readonly record struct AudioSelection(AudioSelectionMode Mode, string EndpointId, string DisplayName)
{
    public static AudioSelection From(string? id, string? name)
    {
        if (!string.IsNullOrEmpty(id))   return new(AudioSelectionMode.Endpoint, id!, name ?? "");
        if (!string.IsNullOrEmpty(name)) return new(AudioSelectionMode.UnresolvedLegacy, "", name!);
        return new(AudioSelectionMode.SystemDefault, "", "");
    }

    public string ModeString => Mode switch
    {
        AudioSelectionMode.Endpoint         => "endpoint",
        AudioSelectionMode.UnresolvedLegacy => "legacy",
        _                                   => "default",
    };
}

public static class AudioDeviceManager
{
    public const int SystemDefaultIndex = -1;

    public static Action<string>? Log;

#if WINDOWS
    private const int DRV_QUERYFUNCTIONINSTANCEID     = 0x0800 + 17;
    private const int DRV_QUERYFUNCTIONINSTANCEIDSIZE = 0x0800 + 18;

    [DllImport("winmm.dll")]
    private static extern int waveInMessage(nint hWaveIn, int uMsg, nint dwParam1, nint dwParam2);
    [DllImport("winmm.dll")]
    private static extern int waveOutMessage(nint hWaveOut, int uMsg, nint dwParam1, nint dwParam2);

    private static string EndpointIdForWinMmIndex(bool input, int index)
    {
        var sizePtr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(sizePtr, 0);
            int mm = input
                ? waveInMessage(index, DRV_QUERYFUNCTIONINSTANCEIDSIZE, sizePtr, 0)
                : waveOutMessage(index, DRV_QUERYFUNCTIONINSTANCEIDSIZE, sizePtr, 0);
            if (mm != 0) return "";
            int size = Marshal.ReadInt32(sizePtr);
            if (size <= 0 || size > 8192) return "";
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                mm = input
                    ? waveInMessage(index, DRV_QUERYFUNCTIONINSTANCEID, buf, size)
                    : waveOutMessage(index, DRV_QUERYFUNCTIONINSTANCEID, buf, size);
                return mm == 0 ? (Marshal.PtrToStringUni(buf) ?? "") : "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return ""; }
        finally { Marshal.FreeHGlobal(sizePtr); }
    }

    private static int WinMmDeviceCount(bool input)
        => input ? NAudio.Wave.WaveIn.DeviceCount : NAudio.Wave.WaveOut.DeviceCount;
#endif

    public static (string Id, string Name)[] ListInputs()  => ListEndpoints(true);
    public static (string Id, string Name)[] ListOutputs() => ListEndpoints(false);

    private static (string Id, string Name)[] ListEndpoints(bool input)
    {
#if WINDOWS
        try
        {
            var list = new List<(string, string)>();
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var flow = input ? NAudio.CoreAudioApi.DataFlow.Capture : NAudio.CoreAudioApi.DataFlow.Render;
            using var devices = en.EnumerateAudioEndPoints(flow, NAudio.CoreAudioApi.DeviceState.Active);
            foreach (var d in devices)
                using (d) list.Add((d.ID, d.FriendlyName));
            return list.ToArray();
        }
        catch (Exception ex) { Log?.Invoke($"[Audio] Endpoint enumeration failed: {ex.Message}"); }
#endif
        return Array.Empty<(string, string)>();
    }

    public static (string Id, string Name)[] ListCaptureWithLoopback()
    {
        var list = new List<(string, string)>();
        foreach (var (id, name) in ListEndpoints(true))  list.Add((id, name));
        foreach (var (id, name) in ListEndpoints(false)) list.Add(($"loopback:{id}", $"{name} (System Audio)"));
        return list.ToArray();
    }

#if WINDOWS
    public static NAudio.CoreAudioApi.MMDevice? GetWasapiDevice(string deviceId, out bool isLoopback)
    {
        isLoopback = deviceId.StartsWith("loopback:", StringComparison.Ordinal);
        var id = isLoopback ? deviceId["loopback:".Length..] : deviceId;
        if (string.IsNullOrEmpty(id)) return null;
        try { return new NAudio.CoreAudioApi.MMDeviceEnumerator().GetDevice(id); }
        catch (Exception ex)
        {
            Log?.Invoke($"[Audio] WASAPI device '{id}' unavailable: {ex.Message}");
            return null;
        }
    }
#endif

    public static string CurrentName(bool input, string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return "";
        return NameIn(ListEndpoints(input), endpointId);
    }

    private static string NameIn((string Id, string Name)[] endpoints, string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return "";
        foreach (var (id, name) in endpoints)
            if (string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase)) return name;
        return "";
    }

    public static bool IsAvailable(bool input, AudioSelection sel) => sel.Mode switch
    {
        AudioSelectionMode.SystemDefault => true,
        AudioSelectionMode.Endpoint      => CurrentName(input, sel.EndpointId).Length > 0,
        _                                => false,
    };

    public static bool IsAvailable(AudioSelection sel, (string Id, string Name)[] endpoints) => sel.Mode switch
    {
        AudioSelectionMode.SystemDefault => true,
        AudioSelectionMode.Endpoint      => NameIn(endpoints, sel.EndpointId).Length > 0,
        _                                => false,
    };

    public static int? ResolveInputIndex(AudioSelection sel)  => ResolveIndex(true, sel);
    public static int? ResolveOutputIndex(AudioSelection sel) => ResolveIndex(false, sel);

    private static int? ResolveIndex(bool input, AudioSelection sel)
    {
        switch (sel.Mode)
        {
            case AudioSelectionMode.SystemDefault:
                return SystemDefaultIndex;
            case AudioSelectionMode.UnresolvedLegacy:
                Log?.Invoke($"[Audio] {(input ? "Input" : "Output")} '{sel.DisplayName}' is an unresolved legacy selection, refusing to guess a device.");
                return null;
        }
#if WINDOWS
        try
        {
            int count = WinMmDeviceCount(input);
            for (int i = 0; i < count; i++)
            {
                var id = EndpointIdForWinMmIndex(input, i);
                if (id.Length > 0 && string.Equals(id, sel.EndpointId, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        catch (Exception ex) { Log?.Invoke($"[Audio] Resolve failed: {ex.Message}"); }
#endif
        Log?.Invoke($"[Audio] Selected {(input ? "input" : "output")} '{(sel.DisplayName.Length > 0 ? sel.DisplayName : sel.EndpointId)}' is currently unavailable (selection kept).");
        return null;
    }

    public static bool TryReadSelectionFromMessage(Newtonsoft.Json.Linq.JToken? idToken, string? sentName, bool input, string currentName, out string id, out string name)
    {
        id = ""; name = "";
        if (idToken == null) return false;
        id = idToken.ToString();
        if (id.StartsWith("legacy:", StringComparison.Ordinal)) { id = ""; return false; }
        name = id.Length == 0 ? "" : CurrentName(input, id);
        if (id.Length > 0 && name.Length == 0)
            name = !string.IsNullOrEmpty(sentName) ? sentName! : currentName;
        return true;
    }

    public static AudioSelection TryMigrateLegacy(bool input, AudioSelection sel)
    {
        if (sel.Mode != AudioSelectionMode.UnresolvedLegacy) return sel;
#if WINDOWS
        try
        {
            int count = WinMmDeviceCount(input);
            int matchIdx = -1, matchCount = 0;
            for (int i = 0; i < count; i++)
            {
                var product = input
                    ? NAudio.Wave.WaveIn.GetCapabilities(i).ProductName
                    : NAudio.Wave.WaveOut.GetCapabilities(i).ProductName;
                if (string.Equals(product, sel.DisplayName, StringComparison.Ordinal))
                {
                    matchIdx = i;
                    matchCount++;
                }
            }
            if (matchCount != 1) return sel;
            var id = EndpointIdForWinMmIndex(input, matchIdx);
            if (id.Length == 0) return sel;
            var name = CurrentName(input, id);
            Log?.Invoke($"[Audio] Migrated legacy device '{sel.DisplayName}' to its stable endpoint id.");
            return new AudioSelection(AudioSelectionMode.Endpoint, id, name.Length > 0 ? name : sel.DisplayName);
        }
        catch (Exception ex) { Log?.Invoke($"[Audio] Legacy migration failed: {ex.Message}"); }
#endif
        return sel;
    }
}
