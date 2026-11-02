using System.Runtime.InteropServices;

namespace AudioRouter;

/// <summary>
/// The tray popup: one row per app (not per device), each with volume, mute, output and backup output.
/// </summary>
sealed class MixerWindow : Form
{
    readonly AudioEngine _engine;
    readonly Panel _header = new();
    readonly Panel _list = new();
    readonly Label _footer = new();
    readonly Label _defaultDevice = new();
    readonly VolumeSlider _master = new();
    readonly Label _masterPct = new();
    readonly GlyphButton _masterMute = new();
    readonly Dictionary<string, AppRow> _rows = new();
    readonly Dictionary<string, Image?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    string _signature = "";

    public DateTime LastHidden { get; private set; }

    public MixerWindow(AudioEngine engine)
    {
        _engine = engine;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Text = "Audio Router";
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;

        _header.BackColor = Theme.Background;
        _list.BackColor = Theme.Background;
        _list.AutoScroll = true;
        _footer.ForeColor = Theme.Warning;
        _footer.Font = Theme.Small;
        _footer.Visible = false;

        var title = new Label { Text = "Audio Router", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, BackColor = Theme.Background };
        _defaultDevice.ForeColor = Theme.TextDim;
        _defaultDevice.Font = Theme.Small;
        _defaultDevice.AutoEllipsis = true;
        _masterPct.TextAlign = ContentAlignment.MiddleRight;
        _masterPct.ForeColor = Theme.Text;
        _masterMute.Text = Theme.GlyphVolume;

        _header.Controls.AddRange(new Control[] { title, _defaultDevice, _master, _masterPct, _masterMute });
        Controls.AddRange(new Control[] { _header, _list, _footer });

        _master.ValueChanged += v => { _engine.SetMasterVolume(v / 100f); _masterPct.Text = v.ToString(); };
        _masterMute.Click += (_, _) =>
        {
            var m = _engine.GetMaster();
            if (m != null) { _engine.SetMasterMuted(!m.Value.muted); _engine.Reconcile(); }
        };

        // Lay out the header once we know the DPI.
        HandleCreated += (_, _) => LayoutHeader(title);
        DpiChanged += (_, _) => { LayoutHeader(title); _signature = ""; RefreshFromEngine(); };
    }

    int S(int px) => (int)Math.Round(px * DeviceDpi / 96f);

    void LayoutHeader(Label title)
    {
        int w = S(380);
        Width = w;
        _header.SetBounds(0, 0, w, S(84));
        title.Location = new Point(S(14), S(10));
        _defaultDevice.SetBounds(S(14), S(34), w - S(28), S(18));
        _masterMute.SetBounds(S(10), S(54), S(28), S(24));
        _master.SetBounds(S(44), S(54), w - S(44) - S(56), S(24));
        _masterPct.SetBounds(w - S(52), S(54), S(38), S(24));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Keep open while a device drop-down list is showing.
        if (_rows.Values.Any(r => r.Interacting)) return;
        HidePopup();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) HidePopup();
    }

    public void HidePopup()
    {
        if (!Visible) return;
        Hide();
        LastHidden = DateTime.UtcNow;
    }

    public void ShowPopup()
    {
        _signature = "";
        _engine.Reconcile(); // also triggers RefreshFromEngine via Changed
        RefreshFromEngine();
        PositionNearCursor();
        Show();
        Activate();
        TryForceForeground();
    }

    void PositionNearCursor()
    {
        var cursor = Cursor.Position;
        var wa = Screen.FromPoint(cursor).WorkingArea;
        int margin = S(8);
        int x = Math.Clamp(cursor.X - Width / 2, wa.Left + margin, wa.Right - Width - margin);
        int y = cursor.Y > wa.Top + wa.Height / 2 ? wa.Bottom - Height - margin : wa.Top + margin;
        Location = new Point(x, y);
    }

    /// <summary>Called on every engine reconcile. Rebuilds rows only when the shape changes and the user isn't mid-gesture.</summary>
    public void RefreshFromEngine()
    {
        if (!IsHandleCreated) CreateHandle();

        var master = _engine.GetMaster();
        _defaultDevice.Text = "Default output: " + (_engine.DefaultDeviceId == null ? "none" : _engine.DeviceName(_engine.DefaultDeviceId));
        _master.Enabled = master != null;
        if (master != null && !_master.Dragging)
        {
            _master.Value = (int)Math.Round(master.Value.volume * 100);
            _masterPct.Text = _master.Value.ToString();
            _masterMute.Text = master.Value.muted ? Theme.GlyphMute : Theme.GlyphVolume;
            _master.Dimmed = master.Value.muted;
            _master.Invalidate();
        }

        bool busy = _master.Dragging || _rows.Values.Any(r => r.Interacting);
        var sig = Signature();
        if (sig != _signature && !busy)
        {
            _signature = sig;
            Rebuild();
        }
        else
        {
            foreach (var app in _engine.Apps)
                if (_rows.TryGetValue(app.Key, out var row)) row.UpdateLive(app);
        }

        _footer.Visible = !_engine.RoutingAvailable;
        _footer.Text = "Per-app output switching isn't available on this Windows build. Volume rules still work.";
    }

    string Signature() => string.Join("|",
        _engine.Apps.Select(a => $"{a.Key}:{a.Running}:{a.Rule?.ManageOutput}:{a.Rule?.OutputDeviceId}:{a.Rule?.BackupDeviceId}:{a.Rule != null}")
        .Concat(_engine.Devices.Select(d => $"{d.Id}:{d.Connected}")));

    void Rebuild()
    {
        _list.SuspendLayout();
        // Snapshot first: disposing a control removes it from the collection we'd be iterating.
        var old = _list.Controls.Cast<Control>().ToList();
        _list.Controls.Clear();
        foreach (var c in old) c.Dispose();
        _rows.Clear();

        int w = S(380);
        int y = 0;
        bool shownSavedHeader = false;
        foreach (var app in _engine.Apps)
        {
            if (!app.Running && !shownSavedHeader)
            {
                shownSavedHeader = true;
                var lbl = new Label
                {
                    Text = "SAVED, NOT PLAYING",
                    Font = Theme.Small,
                    ForeColor = Theme.TextDim,
                    BackColor = Theme.Background,
                };
                lbl.SetBounds(S(14), y + S(10), w - S(28), S(18));
                _list.Controls.Add(lbl);
                y += S(32);
            }

            var row = new AppRow(_engine, app, GetIcon(app), this, S);
            row.SetBounds(S(8), y, w - S(16) - SystemInformation.VerticalScrollBarWidth / 2, row.PreferredRowHeight);
            _list.Controls.Add(row);
            _rows[app.Key] = row;
            y += row.PreferredRowHeight + S(6);
        }

        if (_engine.Apps.Count == 0)
        {
            var empty = new Label
            {
                Text = "Nothing is playing sound right now.",
                ForeColor = Theme.TextDim,
                BackColor = Theme.Background,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            empty.SetBounds(0, 0, w, S(60));
            _list.Controls.Add(empty);
            y = S(60);
        }
        _list.ResumeLayout();

        // Size the window to content, capped to most of the screen.
        int footerH = _footer.Visible ? S(36) : 0;
        int maxH = (int)(Screen.FromPoint(Cursor.Position).WorkingArea.Height * 0.85);
        int listH = Math.Min(y + S(8), maxH - _header.Height - footerH);
        _list.SetBounds(0, _header.Bottom, w, listH);
        _footer.SetBounds(S(14), _list.Bottom, w - S(28), footerH);
        Height = _header.Height + listH + footerH + S(2);
        if (Visible) PositionNearCursor();
        Invalidate();
    }

    Image? GetIcon(AppView app)
    {
        if (app.ExePath == null) return null;
        if (_iconCache.TryGetValue(app.ExePath, out var img)) return img;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(app.ExePath);
            img = icon == null ? null : new Bitmap(icon.ToBitmap(), S(20), S(20));
        }
        catch { img = null; }
        _iconCache[app.ExePath] = img;
        return img;
    }

    void TryForceForeground()
    {
        try { SetForegroundWindow(Handle); } catch { }
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
}

/// <summary>One app: name, mute, volume slider, output + backup output.</summary>
sealed class AppRow : Panel
{
    readonly AudioEngine _engine;
    readonly AppView _app;
    readonly VolumeSlider _slider = new();
    readonly Label _pct = new();
    readonly GlyphButton _mute = new();
    readonly DeviceCombo? _output;
    readonly DeviceCombo? _backup;
    readonly Label? _backupLabel;
    bool _updating;

    public int PreferredRowHeight { get; }

    public bool Interacting => _slider.Dragging || (_output?.Interacting ?? false) || (_backup?.Interacting ?? false);

    public AppRow(AudioEngine engine, AppView app, Image? icon, MixerWindow owner, Func<int, int> S)
    {
        _engine = engine;
        _app = app;
        BackColor = Theme.Surface;
        int w = S(380) - S(16) - SystemInformation.VerticalScrollBarWidth / 2;

        var iconBox = new PictureBox { Image = icon, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Theme.Surface };
        iconBox.SetBounds(S(10), S(10), S(22), S(22));

        var name = new Label
        {
            Text = app.DisplayName,
            AutoEllipsis = true,
            ForeColor = app.Running ? Theme.Text : Theme.TextDim,
            BackColor = Theme.Surface,
            Font = Theme.Body,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        name.SetBounds(S(40), S(10), w - S(40) - S(70), S(22));
        new ToolTip().SetToolTip(name, app.Key == AudioEngine.SystemSoundsKey ? "Windows system sounds" : app.Key);

        var forget = new GlyphButton { Text = "", ForeColor = Theme.TextDim, Font = new Font("Segoe MDL2 Assets", 8f) };
        forget.SetBounds(w - S(30), S(10), S(22), S(22));
        forget.Visible = app.Rule != null;
        new ToolTip().SetToolTip(forget, "Forget this app's saved settings");
        forget.Click += (_, _) => _engine.Forget(app.Key);

        _mute.SetBounds(S(8), S(38), S(28), S(24));
        _slider.SetBounds(S(40), S(38), w - S(40) - S(50), S(24));
        _pct.SetBounds(w - S(46), S(38), S(36), S(24));
        _pct.TextAlign = ContentAlignment.MiddleRight;
        _pct.ForeColor = Theme.Text;
        _pct.BackColor = Theme.Surface;

        Controls.AddRange(new Control[] { iconBox, name, forget, _mute, _slider, _pct });

        int y = S(68);
        if (!app.IsSystemSounds && engine.RoutingAvailable)
        {
            var outLabel = MakeCaption("Output", S(40), y, S);
            _output = new DeviceCombo();
            _output.SetBounds(S(40), y + S(16), (w - S(40) - S(16)) / 2, S(24));

            _backupLabel = MakeCaption("If unavailable", S(40) + (w - S(40) - S(16)) / 2 + S(6), y, S);
            _backup = new DeviceCombo();
            _backup.SetBounds(_backupLabel.Left, y + S(16), (w - S(40) - S(16)) / 2, S(24));

            Controls.AddRange(new Control[] { outLabel, _output, _backupLabel, _backup });
            y += S(46);

            _output.SelectionChangeCommitted += (_, _) =>
            {
                if (_updating) return;
                _engine.SetOutput(app.Key, app.DisplayName, _output.SelectedId);
            };
            _backup.SelectionChangeCommitted += (_, _) =>
            {
                if (_updating) return;
                _engine.SetBackup(app.Key, app.DisplayName, _backup.SelectedId);
            };
            FillCombos();
        }
        PreferredRowHeight = y + S(8);

        _slider.ValueChanged += v =>
        {
            _pct.Text = v.ToString();
            _engine.SetVolume(app.Key, app.DisplayName, v / 100f);
        };
        _slider.DragEnded += () => owner.RefreshFromEngine();
        _mute.Click += (_, _) =>
        {
            bool muted = _mute.Text == Theme.GlyphVolume;
            _engine.SetMuted(app.Key, app.DisplayName, muted);
            SetMuteVisual(muted);
        };

        UpdateLive(app);
    }

    static Label MakeCaption(string text, int x, int y, Func<int, int> S)
    {
        var l = new Label { Text = text, Font = Theme.Small, ForeColor = Theme.TextDim, BackColor = Theme.Surface };
        l.SetBounds(x, y, S(140), S(16));
        return l;
    }

    void FillCombos()
    {
        if (_output == null || _backup == null) return;
        _updating = true;

        var devices = _engine.Devices;
        DeviceChoice Choice(AudioDevice d) => new(d.Id, d.Connected ? d.Name : d.Name + " (off)", d.Connected);

        var rule = _app.Rule;
        var outputs = new List<DeviceChoice> { new(null, "System default", true) };
        outputs.AddRange(devices.Select(Choice));
        _output.SetChoices(outputs, rule?.ManageOutput == true ? rule.OutputDeviceId : null);

        var backups = new List<DeviceChoice> { new(null, "System default", true) };
        backups.AddRange(devices.Where(d => !string.Equals(d.Id, rule?.OutputDeviceId, StringComparison.OrdinalIgnoreCase)).Select(Choice));
        _backup.SetChoices(backups, rule?.BackupDeviceId);

        // A backup only matters once a specific device is chosen.
        bool hasSpecific = rule?.ManageOutput == true && rule.OutputDeviceId != null;
        _backup.Enabled = hasSpecific;
        _backupLabel!.ForeColor = hasSpecific ? Theme.TextDim : Theme.Border;

        _updating = false;
    }

    public void UpdateLive(AppView app)
    {
        if (_slider.Dragging) return;
        var v = (int)Math.Round(app.Volume * 100);
        _slider.Value = v;
        _pct.Text = v.ToString();
        SetMuteVisual(app.Muted);
    }

    void SetMuteVisual(bool muted)
    {
        _mute.Text = muted ? Theme.GlyphMute : Theme.GlyphVolume;
        _slider.Dimmed = muted;
        _slider.Invalidate();
        _mute.Invalidate();
    }
}
