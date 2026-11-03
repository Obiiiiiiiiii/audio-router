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
    readonly ToolStripMenuItem _updateItem;
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30_000 };
    Updater.Release? _available;
    bool _updating;

    public TrayApp()
    {
        _store.Load();
        Updater.CleanUp();

        // Start with Windows by default on first run; afterwards respect the menu toggle.
        // Re-writing the entry each launch keeps it pointing at the exe if it was moved.
        if (!_store.Data.StartupConfigured)
        {
            Startup.Set(true);
            _store.Data.StartupConfigured = true;
            _store.SaveNow();
        }
        else if (Startup.IsEnabled)
        {
            Startup.Set(true);
        }

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
        var hidden = new ToolStripMenuItem("Hidden apps");
        menu.Items.Add(hidden);
        menu.Opening += (_, _) =>
        {
            hidden.DropDownItems.Clear();
            var apps = _engine.HiddenApps;
            hidden.Enabled = apps.Count > 0;
            foreach (var (key, name) in apps)
                hidden.DropDownItems.Add(new ToolStripMenuItem($"Show {name}", null, (_, _) => _engine.Unhide(key)));
        };
        menu.Items.Add("Open settings folder", null, (_, _) => OpenFolder());
        _updateItem = new ToolStripMenuItem("Check for updates", null, async (_, _) => await UpdateClicked());
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripMenuItem($"Version {Updater.CurrentVersionText}") { Enabled = false });
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

        // Quietly check for updates 30s after start, then every 6 hours.
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 6 * 60 * 60 * 1000;
            await CheckForUpdate(userAsked: false);
        };
        _updateTimer.Start();
        _tray.BalloonTipClicked += async (_, _) => { if (_available != null) await Install(_available); };

        _engine.Start();

        // A newer copy being launched asks this one to step aside (see Program.Main).
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
        new Thread(() =>
        {
            quit.WaitOne();
            Log.Info("Another copy was launched; handing over");
            try { invoker.BeginInvoke(() => ExitThread()); } catch { }
        }) { IsBackground = true, Name = "QuitListener" }.Start();
        Log.Info("Audio Router started");
    }

    async Task UpdateClicked()
    {
        if (_available != null) await Install(_available);
        else await CheckForUpdate(userAsked: true);
    }

    async Task CheckForUpdate(bool userAsked)
    {
        if (_updating) return;
        try
        {
            _available = await Updater.CheckAsync();
            if (_available != null)
            {
                _updateItem.Text = $"Install update {_available.Tag}";
                _tray.ShowBalloonTip(5000, "Audio Router update available",
                    $"{_available.Tag} is out (you have {Updater.CurrentVersionText}). Click here to install.", ToolTipIcon.Info);
            }
            else if (userAsked)
            {
                _tray.ShowBalloonTip(3000, "Audio Router", $"You're up to date ({Updater.CurrentVersionText}).", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            if (userAsked)
                _tray.ShowBalloonTip(5000, "Audio Router", "Couldn't check for updates: " + ex.Message, ToolTipIcon.Warning);
        }
    }

    async Task Install(Updater.Release release)
    {
        if (_updating) return;
        _updating = true;
        _updateItem.Enabled = false;
        _updateItem.Text = $"Downloading {release.Tag}…";
        _tray.ShowBalloonTip(3000, "Audio Router", $"Downloading {release.Tag}…", ToolTipIcon.Info);
        try
        {
            _store.SaveNow();
            await Updater.InstallAsync(release); // the new copy closes this one
        }
        catch (Exception ex)
        {
            Log.Crash("Update failed", ex);
            _tray.ShowBalloonTip(5000, "Audio Router", "Update failed: " + ex.Message, ToolTipIcon.Error);
            _updateItem.Text = $"Install update {release.Tag}";
            _updateItem.Enabled = true;
            _updating = false;
        }
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
        _store.SaveNow();

        // Releasing Core Audio objects can block if Windows' audio service is busy. Rules are already
        // saved, so never let that keep the process (and the exe file) alive.
        new Thread(() => { Thread.Sleep(3000); Environment.Exit(0); }) { IsBackground = true }.Start();

        try { _engine.Dispose(); } catch { }
        try { _window.Dispose(); } catch { }
        base.ExitThreadCore();
    }

    /// <summary>
    /// Bold filled speaker with two blue sound waves, drawn edge to edge so it reads clearly at tray size
    /// and doesn't look like Windows' own (thin, white) volume icon sitting next to it.
    /// </summary>
    static Icon MakeTrayIcon()
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float u = size / 16f; // design on a 16x16 grid

            using var body = new SolidBrush(Color.White);
            g.FillPolygon(body, new[]
            {
                new PointF(0.5f * u, 5f * u), new PointF(4f * u, 5f * u), new PointF(8.5f * u, 1f * u),
                new PointF(8.5f * u, 15f * u), new PointF(4f * u, 11f * u), new PointF(0.5f * u, 11f * u),
            });

            using var wave = new Pen(Theme.Accent, 1.8f * u);
            g.DrawArc(wave, 6.4f * u, 5.9f * u, 4.2f * u, 4.2f * u, -55, 110);
            g.DrawArc(wave, 5.9f * u, 2.4f * u, 8.2f * u, 11.2f * u, -55, 110);
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
