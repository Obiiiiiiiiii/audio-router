using System.Drawing.Drawing2D;

namespace AudioRouter;

static class Theme
{
    public static readonly Color Background = Color.FromArgb(32, 32, 32);
    public static readonly Color Surface = Color.FromArgb(43, 43, 43);
    public static readonly Color SurfaceHover = Color.FromArgb(55, 55, 55);
    public static readonly Color Border = Color.FromArgb(62, 62, 62);
    public static readonly Color Text = Color.FromArgb(240, 240, 240);
    public static readonly Color TextDim = Color.FromArgb(150, 150, 150);
    public static readonly Color Accent = Color.FromArgb(76, 194, 255);
    public static readonly Color Track = Color.FromArgb(90, 90, 90);
    public static readonly Color Warning = Color.FromArgb(255, 185, 80);

    public static readonly Font Body = new("Segoe UI", 9f);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font Title = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Glyphs = new("Segoe MDL2 Assets", 11f);

    public const string GlyphVolume = "";
    public const string GlyphMute = "";
}

/// <summary>A flat 0-100 slider. ValueChanged only fires for user input.</summary>
sealed class VolumeSlider : Control
{
    int _value;
    bool _dragging;

    public event Action<int>? ValueChanged;
    public event Action? DragEnded;

    public bool Dragging => _dragging;
    public bool Dimmed { get; set; }

    public int Value
    {
        get => _value;
        set { value = Math.Clamp(value, 0, 100); if (value != _value) { _value = value; Invalidate(); } }
    }

    public VolumeSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    int ThumbRadius => Math.Max(6, Height / 3);

    int ValueFromX(int x)
    {
        int r = ThumbRadius, w = Math.Max(1, Width - 2 * r);
        return (int)Math.Round((x - r) * 100.0 / w);
    }

    void SetFromUser(int v)
    {
        v = Math.Clamp(v, 0, 100);
        if (v == _value) return;
        _value = v;
        Invalidate();
        ValueChanged?.Invoke(v);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _dragging = true;
        Capture = true;
        SetFromUser(ValueFromX(e.X));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromUser(ValueFromX(e.X));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        DragEnded?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetFromUser(_value + Math.Sign(e.Delta) * 2);
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = e.KeyCode switch { Keys.Left or Keys.Down => -2, Keys.Right or Keys.Up => 2, _ => 0 };
        if (step != 0) SetFromUser(_value + step);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Surface);

        int r = ThumbRadius;
        int cy = Height / 2;
        int x0 = r, x1 = Width - r;
        int xv = x0 + (int)((x1 - x0) * _value / 100.0);
        float thick = Math.Max(3f, Height / 8f);

        var accent = Dimmed ? Theme.TextDim : Theme.Accent;
        using (var track = new Pen(Theme.Track, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, x0, cy, x1, cy);
        if (xv > x0)
        {
            using var fill = new Pen(accent, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(fill, x0, cy, xv, cy);
        }
        using (var thumbBg = new SolidBrush(Theme.Text))
            g.FillEllipse(thumbBg, xv - r, cy - r, 2 * r, 2 * r);
        using (var thumbDot = new SolidBrush(accent))
            g.FillEllipse(thumbDot, xv - r / 2f, cy - r / 2f, r, r);

        if (Focused)
        {
            using var focus = new Pen(Theme.TextDim) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>Flat glyph button (mute toggle, forget, etc.).</summary>
sealed class GlyphButton : Control
{
    bool _hover;

    public GlyphButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = Theme.Glyphs;
        ForeColor = Theme.Text;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bg = _hover ? Theme.SurfaceHover : Parent?.BackColor ?? Theme.Surface;
        e.Graphics.Clear(bg);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

sealed record DeviceChoice(string? Id, string Label, bool Connected)
{
    public override string ToString() => Label;
}

/// <summary>Dark drop-down list of output devices; disconnected devices are greyed but still pickable.</summary>
sealed class DeviceCombo : ComboBox
{
    public DeviceCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Small;
        IntegralHeight = false;
        MaxDropDownItems = 12;
    }

    public bool Interacting => DroppedDown;

    public string? SelectedId => (SelectedItem as DeviceChoice)?.Id;

    public void SetChoices(IEnumerable<DeviceChoice> choices, string? selectedId)
    {
        BeginUpdate();
        Items.Clear();
        foreach (var c in choices) Items.Add(c);
        for (int i = 0; i < Items.Count; i++)
        {
            if (string.Equals(((DeviceChoice)Items[i]!).Id, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                SelectedIndex = i;
                break;
            }
        }
        if (SelectedIndex < 0 && Items.Count > 0) SelectedIndex = 0;
        EndUpdate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var item = (DeviceChoice)Items[e.Index]!;
        bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var bg = new SolidBrush(selected ? Theme.SurfaceHover : Theme.Background))
            e.Graphics.FillRectangle(bg, e.Bounds);
        var color = item.Connected ? Theme.Text : Theme.TextDim;
        TextRenderer.DrawText(e.Graphics, item.Label, Font, e.Bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
