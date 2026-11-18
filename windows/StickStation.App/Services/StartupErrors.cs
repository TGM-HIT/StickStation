using System.Runtime.InteropServices;

namespace StickStation.Services;

/// <summary>Makes startup failures visible: writes a crash log and shows a message box.</summary>
public static class StartupErrors
{
    private static int reported;

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { WriteLog(e.Exception); e.SetObserved(); };
    }

    public static void Report(Exception? error)
    {
        WriteLog(error);
        if (Interlocked.Exchange(ref reported, 1) != 0) return;
        MessageBoxW(0, $"StickStation could not start or crashed:\n\n{error}\n\nDetails: {LogPath}", "StickStation", 0x10);
    }

    public static string LogPath => Path.Combine(Path.GetTempPath(), "StickStation-crash.log");

    private static void WriteLog(Exception? error)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {error}{Environment.NewLine}"); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
