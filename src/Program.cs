namespace AudioRouter;

static class Program
{
    [STAThread]
    static void Main()
    {
        // One copy only; a second launch just exits.
        using var mutex = new Mutex(true, @"Local\AudioRouter.SingleInstance", out bool first);
        if (!first) return;

        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
