using System.Drawing.Drawing2D;

namespace ListaMateriais;

/// <summary>Shared look of the app: one accent color and two button styles.</summary>
internal static class Ui
{
    public static readonly Color Accent = Color.FromArgb(0x2F, 0x6B, 0x8A);
    public static readonly Color AccentDark = Color.FromArgb(0x24, 0x54, 0x6D);
    public static readonly Color AccentSoft = Color.FromArgb(0xEC, 0xF3, 0xF7);
    public static readonly Color Muted = Color.FromArgb(0x66, 0x66, 0x66);
    public static readonly Color Border = Color.FromArgb(0xC8, 0xC8, 0xC8);

    /// <summary>Filled accent button for the main action of a screen.</summary>
    public static T Primary<T>(T button) where T : Button
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = AccentDark;
        button.FlatAppearance.MouseDownBackColor = AccentDark;
        button.Cursor = Cursors.Hand;
        void Refresh(object? s, EventArgs e)
        {
            // A disabled flat button keeps its fill, so show it as plain gray.
            button.BackColor = button.Enabled ? Accent : Color.FromArgb(0xB8, 0xC4, 0xCC);
            button.ForeColor = button.Enabled ? Color.White : Color.FromArgb(0xF4, 0xF4, 0xF4);
        }
        button.EnabledChanged += Refresh;
        Refresh(null, EventArgs.Empty);
        return button;
    }

    /// <summary>Outlined button for secondary actions.</summary>
    public static T Secondary<T>(T button) where T : Button
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Color.White;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = AccentSoft;
        button.Cursor = Cursors.Hand;
        return button;
    }
}

/// <summary>
/// Big target to drop a file on or click to pick one. Shows the chosen file once there is one,
/// and highlights while a file is dragged over it.
/// </summary>
internal sealed class DropZone : Control
{
    private bool _hot;
    private bool _hover;
    private string? _name;
    private string? _kind;

    public event EventHandler? Activated;

    public DropZone()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Height = 104;
        MinimumSize = new Size(200, 104);
        AllowDrop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "Selecionar documento";
    }

    /// <summary>Shows the chosen file; null goes back to the empty state.</summary>
    public void SetFile(string? name, string? kind)
    {
        _name = name;
        _kind = kind;
        AccessibleDescription = name;
        Invalidate();
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        _hot = true;
        Invalidate();
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _hot = false;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        _hot = false;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Activated?.Invoke(this, e); }

    public override Size GetPreferredSize(Size proposedSize) => new(Math.Max(proposedSize.Width, 200), 104);

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space)
        {
            e.Handled = true;
            Activated?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? SystemColors.Control);

        var area = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = Rounded(area, 10);
        var active = _hot || _hover || Focused;
        using (var fill = new SolidBrush(_hot ? Color.FromArgb(0xDC, 0xEB, 0xF3) : active ? Ui.AccentSoft : Color.FromArgb(0xF7, 0xFA, 0xFC)))
            g.FillPath(fill, path);
        using (var pen = new Pen(active ? Ui.Accent : Ui.Border, 1.5f)
               { DashStyle = _name is null ? DashStyle.Dash : DashStyle.Solid })
            g.DrawPath(pen, path);

        var bounds = new RectangleF(16, 0, Width - 32, Height);
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisPath,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var title = new Font("Segoe UI", 11, FontStyle.Bold);
        using var small = new Font("Segoe UI", 9);
        using var accent = new SolidBrush(Ui.Accent);
        using var muted = new SolidBrush(Ui.Muted);
        var top = new RectangleF(bounds.X, Height / 2f - 22, bounds.Width, 28);
        var middle = new RectangleF(bounds.X, Height / 2f + 6, bounds.Width, 20);
        if (_name is null)
        {
            g.DrawString(_hot ? "Solte o arquivo aqui" : "Arraste o PDF ou o Word do projeto aqui", title, accent, top, format);
            g.DrawString("ou clique para selecionar (Ctrl+O)", small, muted, middle, format);
        }
        else
        {
            g.DrawString(_name, title, accent, top, format);
            g.DrawString(_kind ?? string.Empty, small, muted, middle, format);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
