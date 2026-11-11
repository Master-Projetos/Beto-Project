using System.Drawing.Drawing2D;

namespace ListaMateriais;

/// <summary>Shared look of the app: colors, fonts, the button styles and the small pieces every page uses.</summary>
internal static class Ui
{
    public static readonly Color Accent = Color.FromArgb(0x2F, 0x6B, 0x8A);
    public static readonly Color AccentDark = Color.FromArgb(0x24, 0x54, 0x6D);
    public static readonly Color AccentSoft = Color.FromArgb(0xEC, 0xF3, 0xF7);
    public static readonly Color Muted = Color.FromArgb(0x66, 0x66, 0x66);
    public static readonly Color Border = Color.FromArgb(0xC8, 0xC8, 0xC8);
    public static readonly Color Good = Color.FromArgb(0x1E, 0x7B, 0x34);
    public static readonly Color Bad = Color.FromArgb(0xB3, 0x26, 0x1E);

    /// <summary>Background of the navigation pane, as in the Windows 11 settings.</summary>
    public static readonly Color Pane = Color.FromArgb(0xF3, 0xF3, 0xF3);
    public static readonly Color PaneHover = Color.FromArgb(0xE9, 0xE9, 0xE9);
    public static readonly Color PaneSelected = Color.FromArgb(0xE4, 0xE4, 0xE4);

    public static readonly Font BodyFont = new("Segoe UI", 9);
    public static readonly Font BoldFont = new("Segoe UI", 9.75f, FontStyle.Bold);
    public static readonly Font ListFont = new("Segoe UI", 10);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 15);
    public static readonly Font BigButtonFont = new("Segoe UI", 10.5f, FontStyle.Bold);

    /// <summary>The Windows icon font; null on a system without it (the app then shows no glyphs).</summary>
    public static readonly Font? IconFont = InstalledFont("Segoe Fluent Icons", 11) ?? InstalledFont("Segoe MDL2 Assets", 11);

    private static Font? InstalledFont(string family, float size)
    {
        var font = new Font(family, size);
        if (font.Name.Equals(family, StringComparison.OrdinalIgnoreCase))
            return font;
        font.Dispose();
        return null;
    }

    public static Label Caption(string text) => new() { Text = text, Font = BoldFont, AutoSize = true };

    /// <summary>Gray explanatory text that wraps to the width of its <see cref="Stack"/>.</summary>
    public static WrapLabel Hint(string text) => new() { Text = text, ForeColor = Muted };

    /// <summary>Shows the result of saving a setting next to it.</summary>
    public static void Say(Label feedback, string? error, string saved = "Salvo.")
    {
        feedback.Text = error is null ? saved : $"Não foi possível salvar a configuração: {error}";
        feedback.ForeColor = error is null ? Good : Bad;
    }

    /// <summary>Opens a file or a link with the program Windows has for it. Returns why it could not, or null.</summary>
    public static string? Open(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            System.Diagnostics.Trace.TraceWarning("Não foi possível abrir {0}: {1}", target, e.Message);
            return e.Message;
        }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowInFolder(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Não foi possível abrir a pasta: {0}", e.Message);
        }
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
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

    /// <summary>What the empty zone asks for.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Prompt { get; init; } = "Arraste o arquivo aqui";

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
        using var path = Ui.Rounded(area, 10);
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
        var small = Ui.BodyFont;
        using var accent = new SolidBrush(Ui.Accent);
        using var muted = new SolidBrush(Ui.Muted);
        var top = new RectangleF(bounds.X, Height / 2f - 22, bounds.Width, 28);
        var middle = new RectangleF(bounds.X, Height / 2f + 6, bounds.Width, 20);
        if (_name is null)
        {
            g.DrawString(_hot ? "Solte o arquivo aqui" : Prompt, title, accent, top, format);
            g.DrawString("ou clique para selecionar (Ctrl+O)", small, muted, middle, format);
        }
        else
        {
            g.DrawString(_name, title, accent, top, format);
            g.DrawString(_kind ?? string.Empty, small, muted, middle, format);
        }
    }

}

/// <summary>A label that wraps its text to the width of the <see cref="Stack"/> it is in.</summary>
internal sealed class WrapLabel : Label
{
    public WrapLabel() => AutoSize = true;
}

/// <summary>
/// One column of controls, top to bottom: how every page and dialog of the app is laid out.
/// Rows take the height of their control; <c>fill</c> rows share what is left.
/// </summary>
internal sealed class Stack : TableLayoutPanel
{
    public Stack()
    {
        Dock = DockStyle.Fill;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    /// <param name="top">Space above the control, in pixels at 100% scale.</param>
    /// <param name="indent">Space to its left.</param>
    /// <param name="fill">The row grows with the window.</param>
    public Stack Add(Control control, int top = 0, int bottom = 0, int indent = 0, bool fill = false)
    {
        control.Margin = new Padding(indent, top, 0, bottom);
        // Text and check boxes keep their own size; everything else spans the column.
        if (control is not (Label or CheckBox or RadioButton or FlowLayoutPanel or PictureBox))
            control.Dock = DockStyle.Fill;
        RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        Controls.Add(control, 0, RowCount++);
        return this;
    }

    public Stack Title(string text, string? hint = null)
    {
        Add(new Label { Text = text, Font = Ui.TitleFont, AutoSize = true });
        return hint is null ? this : Add(Ui.Hint(hint), top: 2, bottom: 16);
    }

    public Stack Caption(string text, int top = 0) => Add(Ui.Caption(text), top, bottom: 4);

    /// <summary>An empty row that takes the space left, pushing what comes after it to the bottom.</summary>
    public Stack Spring() => Add(new Panel { Height = 0 }, fill: true);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        // The wrapping width is set before the rows are measured, so they get the wrapped height.
        var width = ClientSize.Width - Padding.Horizontal;
        foreach (Control control in Controls)
        {
            if (control is WrapLabel label && new Size(Math.Max(80, width - label.Margin.Horizontal), 0) is var limit
                && label.MaximumSize != limit)
                label.MaximumSize = limit;
        }
        base.OnLayout(levent);
    }

    /// <summary>Controls side by side in equal columns.</summary>
    public static TableLayoutPanel Row(int height, params Control[] controls)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = controls.Length, RowCount = 1, Height = height,
        };
        // The row is as tall as the panel, whatever its controls would like to be.
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < controls.Length; i++)
        {
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / controls.Length));
            controls[i].Dock = DockStyle.Fill;
            controls[i].Margin = new Padding(i == 0 ? 0 : 4, 0, i == controls.Length - 1 ? 0 : 4, 0);
            panel.Controls.Add(controls[i], i, 0);
        }
        return panel;
    }

    /// <summary>Controls one after the other, each with its own width.</summary>
    public static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }
}

/// <summary>An entry of the navigation pane: an icon and a name, highlighted while its page is showing.</summary>
internal sealed class NavButton : RadioButton
{
    private readonly string _glyph;

    /// <param name="glyph">Character of the Windows icon font.</param>
    /// <param name="text">Name of the page; "&amp;" marks its Alt shortcut.</param>
    public NavButton(string glyph, string text)
    {
        _glyph = glyph;
        Text = text;
        Appearance = Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.CheckedBackColor = Ui.PaneSelected;
        FlatAppearance.MouseOverBackColor = Ui.PaneHover;
        FlatAppearance.MouseDownBackColor = Ui.PaneSelected;
        BackColor = Ui.Pane;
        Font = Ui.ListFont;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(Ui.IconFont is null ? 10 : 36, 0, 4, 0);
        Height = 38;
        Dock = DockStyle.Fill;
        Margin = new Padding(6, 2, 6, 2);
        AutoSize = false;
        Cursor = Cursors.Hand;
    }

    /// <summary>The highlight of the selected entry already shows where the keyboard is.</summary>
    protected override bool ShowFocusCues => false;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var scale = DeviceDpi / 96f;
        if (Ui.IconFont is { } font)
        {
            var box = new Rectangle((int)(8 * scale), 0, (int)(28 * scale), Height);
            TextRenderer.DrawText(g, _glyph, font, box, Checked ? Ui.Accent : Color.FromArgb(0x40, 0x40, 0x40),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        if (!Checked)
            return;
        // The accent pill that marks the current page.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var pill = new RectangleF(1, Height / 2f - 8 * scale, 3 * scale, 16 * scale);
        using var path = Ui.Rounded(pill, pill.Width / 2);
        using var brush = new SolidBrush(Ui.Accent);
        g.FillPath(brush, path);
    }
}
