namespace AudioRouter;

/// <summary>Tiny append-only log in %AppData%\AudioRouter\log.txt, trimmed when it grows past ~1 MB.</summary>
static class Log
{
    static readonly object Gate = new();
    static string PathName => System.IO.Path.Combine(RuleStore.Folder, "log.txt");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(RuleStore.Folder);
                var info = new FileInfo(PathName);
                if (info.Exists && info.Length > 1_000_000)
                {
                    File.Move(PathName, PathName + ".old", overwrite: true);
                }
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
