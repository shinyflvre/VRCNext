using System.Diagnostics;

namespace VRCNext.Services.Helpers;

public static class FilePicker
{
    public struct PickResult
    {
        public bool IsOk;
        public string Path;
    }

    public static PickResult FileOpen(string? filterList = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var r = NativeFileDialogSharp.Dialog.FileOpen(filterList);
            return new PickResult { IsOk = r.IsOk, Path = r.Path ?? "" };
        }
        return Zenity(WithFilters("--file-selection", filterList));
    }

    public static PickResult FileSave(string? filterList = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var r = NativeFileDialogSharp.Dialog.FileSave(filterList);
            return new PickResult { IsOk = r.IsOk, Path = r.Path ?? "" };
        }
        return Zenity(WithFilters("--file-selection --save --confirm-overwrite", filterList));
    }

    public static PickResult FolderPicker(string? defaultPath = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var r = NativeFileDialogSharp.Dialog.FolderPicker(defaultPath);
            return new PickResult { IsOk = r.IsOk, Path = r.Path ?? "" };
        }
        var args = "--file-selection --directory";
        if (!string.IsNullOrEmpty(defaultPath)) args += $" --filename=\"{defaultPath}/\"";
        return Zenity(args);
    }

    private static string WithFilters(string baseArgs, string? filterList)
    {
        if (string.IsNullOrWhiteSpace(filterList)) return baseArgs;
        var exts = filterList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (exts.Length == 0) return baseArgs;
        var pattern = string.Join(' ', exts.Select(e => "*." + e));
        return $"{baseArgs} --file-filter=\"{pattern}\" --file-filter=\"All files | *\"";
    }

    private static PickResult Zenity(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "zenity",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p == null) return new PickResult { IsOk = false, Path = "" };
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (p.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return new PickResult { IsOk = true, Path = output };
        }
        catch { }
        return new PickResult { IsOk = false, Path = "" };
    }
}
