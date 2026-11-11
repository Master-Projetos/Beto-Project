namespace ListaMateriais.Pages;

/// <summary>One screen of the app, shown in the main window when its entry of the navigation pane is selected.</summary>
internal abstract class Page : Panel
{
    private int _minHeight;

    protected Page()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(28, 22, 28, 20);
        BackColor = Color.White;
        AutoScroll = true;
        Visible = false;
    }

    /// <summary>Name in the navigation pane; "&amp;" marks the Alt shortcut.</summary>
    public abstract string Title { get; }

    /// <summary>Character of the Windows icon font shown next to the name.</summary>
    public abstract string Glyph { get; }

    /// <summary>True when a file dropped on the window belongs on this page.</summary>
    public virtual bool Accepts(string path) => false;

    /// <summary>Takes a file dropped on the window or picked with Ctrl+O.</summary>
    public virtual void Open(string path)
    {
    }

    /// <summary>Ctrl+O: lets the user pick the file this page works on.</summary>
    public virtual void Browse()
    {
    }

    /// <summary>Puts the page's controls in place.</summary>
    /// <param name="minHeight">Below this height (at 100% scale) the page scrolls instead of squeezing its controls.</param>
    protected void SetContent(Stack content, int minHeight)
    {
        _minHeight = minHeight;
        Controls.Add(content);
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        AutoScrollMinSize = new Size(0, LogicalToDeviceUnits(_minHeight));
    }

    protected static bool HasExtension(string path, string extension) =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
