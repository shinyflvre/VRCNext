using System.Runtime.InteropServices;
using VRCNext.Services;

namespace VRCNext;

static class Program
{
    // Stable AUMID
    // so the pin survives Velopack updates that move the exe to a new versioned folder.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "--watchdog")
        {
            if (!AcquireMutex("Global\\VRCNext_Watchdog", out var wdMutex)) return;
            using (wdMutex) WatchdogRunner.Run(args);
            return;
        }

        if (args.Length >= 1 && args[0] == "--vr-subprocess")
        {
            if (!AcquireMutex("Global\\VRCNext_VROverlay", out var vrMutex)) return;
            using (vrMutex) VRSubprocess.Run();
            return;
        }

        int waitPidIdx = Array.IndexOf(args, "--waitpid");
        if (waitPidIdx >= 0 && waitPidIdx + 1 < args.Length &&
            int.TryParse(args[waitPidIdx + 1], out int waitPid))
        {
            try { System.Diagnostics.Process.GetProcessById(waitPid).WaitForExit(5000); } catch { }
        }

        if (!AcquireMutex("Global\\VRCNext", out var mainMutex, showError: true)) return;
        using (mainMutex)
        {
            if (OperatingSystem.IsWindows())
                SetCurrentProcessExplicitAppUserModelID("com.shinyflvre.vrcnext");
            CrashHandler.Register();
            Velopack.VelopackApp.Build().Run();
            new AppShell(args).Run();
        }
    }

    static bool AcquireMutex(string name, out Mutex mutex, bool showError = false)
    {
        mutex = new Mutex(initiallyOwned: true, name: name, out bool createdNew);
        if (createdNew) return true;

        mutex.Dispose();
        if (showError)
#if WINDOWS
            MessageBox.Show(
                GetAlreadyRunningMessage(),
                "VRCNext", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
#else
            Console.Error.WriteLine(GetAlreadyRunningMessage());
#endif
        return false;
    }

    static string GetAlreadyRunningMessage()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCNext", "settings.json");
            var json = File.ReadAllText(path);
            var match = System.Text.RegularExpressions.Regex.Match(json, "\"[Ll]anguage\"\\s*:\\s*\"([^\"]+)\"");
            var lang = match.Success ? match.Groups[1].Value : "en";
            return lang switch
            {
                "de"    => "VRCNext läuft bereits.\nBitte schließe die laufende Instanz zuerst.",
                "es"    => "VRCNext ya está en ejecución.\nCierra la instancia en ejecución primero.",
                "fr"    => "VRCNext est déjà en cours d'exécution.\nVeuillez d'abord fermer l'instance en cours.",
                "ja"    => "VRCNextはすでに起動しています。\n実行中のインスタンスを先に閉じてください。",
                "zh-CN" => "VRCNext已在运行。\n请先关闭正在运行的实例。",
                _       => "VRCNext is already running.\nPlease close the running instance first.",
            };
        }
        catch
        {
            return "VRCNext is already running.\nPlease close the running instance first.";
        }
    }
}
