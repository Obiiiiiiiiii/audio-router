using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace AudioRouter;

/// <summary>Owns the tray icon, the engine and the popup.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly RuleStore _store = new();
    readonly AudioEngine _engine;
    readonly MixerWindow _window;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _startup;

    public TrayApp()
    {
        _store.Load();

        // Hidden control so COM callbacks can marshal onto the UI thread from the start.
        var invoker = new Control();
        invoker.CreateControl();
        _ = invoker.Handle;

        _engine = new AudioEngine(_store, invoker);
        _window = new MixerWindow(_engine);
        _engine.Changed += () => { if (_window.Visible) _window.RefreshFromEngine(); };

        _startup = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = Startup.IsEnabled };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open mixer", null, (_, _) => _window.ShowPopup());
        menu.Items.Add(_startup);
        menu.Items.Add("Open settings folder", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "Audio Router",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            // A click on the tray icon deactivates the popup first; don't immediately reopen it.
            if (_window.Visible || (DateTime.UtcNow - _window.LastHidden).TotalMilliseconds < 250) _window.HidePopup();
            else _window.ShowPopup();
        };

        SystemEvents.SessionEnding += (_, _) => _store.SaveNow();

        _engine.Start();
        Log.Info("Audio Router started");
    }

    void ToggleStartup()
    {
        Startup.Set(!Startup.IsEnabled);
        _startup.Checked = Startup.IsEnabled;
    }

    static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(RuleStore.Folder);
            System.Diagnostics.Process.Start("explorer.exe", RuleStore.Folder);
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        Log.Info("Audio Router exiting");
        _tray.Visible = false;
        _tray.Dispose();
        _engine.Dispose();
        _window.Dispose();
        base.ExitThreadCore();
    }

    /// <summary>Draws a simple speaker-with-arrow icon so we don't need to ship an .ico.</summary>
    static Icon MakeTrayIcon()
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 16f;
            using var brush = new SolidBrush(Color.White);
            g.FillPolygon(brush, new[]
            {
                new PointF(1 * s, 6 * s), new PointF(4 * s, 6 * s), new PointF(8 * s, 2.5f * s),
                new PointF(8 * s, 13.5f * s), new PointF(4 * s, 10 * s), new PointF(1 * s, 10 * s),
            });
            using var pen = new Pen(Theme.Accent, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 10 * s, 8 * s, 15 * s, 8 * s);
            g.DrawLine(pen, 12.5f * s, 5.5f * s, 15 * s, 8 * s);
            g.DrawLine(pen, 12.5f * s, 10.5f * s, 15 * s, 8 * s);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "AudioRouter";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) is string;
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Changing start-with-Windows failed", ex);
        }
    }
}
