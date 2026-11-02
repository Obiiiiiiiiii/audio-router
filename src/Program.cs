using System.Diagnostics;

namespace AudioRouter;

static class Program
{
    const string MutexName = @"Local\AudioRouter.SingleInstance";
    public const string QuitEventName = @"Local\AudioRouter.Quit";

    [STAThread]
    static void Main()
    {
        // One copy at a time. Launching the exe again replaces the running copy, so updating is just
        // "download the new exe and run it".
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first && !TakeOverFromRunningCopy(mutex)) return;

        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }

    static bool TakeOverFromRunningCopy(Mutex mutex)
    {
        try
        {
            // Polite first: ask the running copy to exit.
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
            {
                quit.Set();
                if (WaitFor(mutex, 5000)) return true;
            }

            // Older builds don't listen for that, so end them directly.
            var self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("AudioRouter").Where(p => p.Id != self))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            return WaitFor(mutex, 5000);
        }
        catch
        {
            return false;
        }
    }

    static bool WaitFor(Mutex mutex, int ms)
    {
        try { return mutex.WaitOne(ms); }
        catch (AbandonedMutexException) { return true; } // previous owner died holding it: ours now
    }
}
