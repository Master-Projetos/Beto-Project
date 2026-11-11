using ListaMateriais.Pages;

namespace ListaMateriais;

/// <summary>
/// The main window: a navigation pane on the left and the selected page on the right. Each page lives
/// in its own file under Pages/; this class only moves between them and routes files and shortcuts.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly List<(NavButton Button, Page Page)> _pages = [];
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly GeneratePage _generate;
    private readonly KmzPage _kmz;

    public MainForm()
    {
        Text = Dialogs.AppTitle;
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
        {
            if (iconStream is not null)
                Icon = new Icon(iconStream);
        }
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Ui.BodyFont;
        ClientSize = new Size(960, 720);
        MinimumSize = SizeFromClientSize(new Size(720, 520));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var templates = new TemplatesPage();
        var headers = new HeadersPage();
        _generate = new GeneratePage(templates, headers);
        _kmz = new KmzPage();

        var pane = new TableLayoutPanel
        {
            Dock = DockStyle.Left, Width = 200, ColumnCount = 1, BackColor = Ui.Pane, Padding = new Padding(0, 10, 0, 8),
        };
        pane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pane.Paint += (_, e) =>
        {
            using var line = new Pen(Color.FromArgb(0xE0, 0xE0, 0xE0));
            e.Graphics.DrawLine(line, pane.Width - 1, 0, pane.Width - 1, pane.Height);
        };
        foreach (var page in new Page[] { _generate, _kmz, templates, headers, new SettingsPage(), new AboutPage() })
        {
            var button = new NavButton(page.Glyph, page.Title);
            button.CheckedChanged += (_, _) =>
            {
                if (button.Checked)
                    Show(page);
            };
            _pages.Add((button, page));
            pane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pane.Controls.Add(button);
            _content.Controls.Add(page);
        }
        // An empty last row takes the height left over, keeping the buttons together at the top.
        pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pane.Controls.Add(new Panel { Height = 0, BackColor = Ui.Pane });

        // Added first so it docks last and takes what the pane leaves.
        Controls.Add(_content);
        Controls.Add(pane);

        // OLE drag and drop only works on an STA thread.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            EnableDrop(this);
        RestoreWindow();
        _pages[0].Button.Checked = true;
    }

    private void Show(Page page)
    {
        foreach (var (_, other) in _pages)
            other.Visible = other == page;
        // Enter generates on the pages that have something to generate.
        AcceptButton = page == _generate ? _generate.MainButton : page == _kmz ? _kmz.MainButton : null;
    }

    private Page Current => _pages.First(p => p.Button.Checked).Page;

    private void GoTo(Page page) => _pages.First(p => p.Page == page).Button.Checked = true;

    /// <summary>Accepts a file dropped anywhere on the window and hands it to the page that reads it.</summary>
    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
            e.Effect = DroppedFile(e) is { } path && PageFor(path) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += (_, e) =>
        {
            if (DroppedFile(e) is not { } path || PageFor(path) is not { } page)
                return;
            GoTo(page);
            // The drop is still in progress in Explorer: answer it first, open the file (and its dialogs) after.
            BeginInvoke(() => page.Open(path));
        };
        foreach (Control child in control.Controls)
            EnableDrop(child);
    }

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files ? files[0] : null;

    /// <summary>The current page when it takes the file, otherwise the first one that does.</summary>
    private Page? PageFor(string path) =>
        Current.Accepts(path) ? Current : _pages.Select(p => p.Page).FirstOrDefault(p => p.Accepts(path));

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.O))
        {
            if (Current != _generate && Current != _kmz)
                GoTo(_generate);
            Current.Browse();
            return true;
        }
        var number = keyData - (Keys.Control | Keys.D1);
        if (number >= 0 && number < _pages.Count && (keyData & Keys.Control) != 0 && (keyData & (Keys.Alt | Keys.Shift)) == 0)
        {
            _pages[(int)number].Button.Checked = true;
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Puts the window back where it was, as long as that spot is still on a connected screen.</summary>
    private void RestoreWindow()
    {
        if (Config.LoadWindow() is not var (left, top, width, height, maximized))
            return;
        var bounds = new Rectangle(left, top, Math.Max(width, MinimumSize.Width), Math.Max(height, MinimumSize.Height));
        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Rectangle.Inflate(bounds, -40, -40))))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }
        if (maximized)
            WindowState = FormWindowState.Maximized;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || WindowState == FormWindowState.Minimized)
            return;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        Config.TrySave(() => Config.SaveWindow(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WindowState == FormWindowState.Maximized));
    }
}
