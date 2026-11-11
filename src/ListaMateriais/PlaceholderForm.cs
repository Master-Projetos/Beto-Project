namespace ListaMateriais;

/// <summary>A highlighted (yellow) spot of the Word template that nothing filled in.</summary>
internal sealed record Placeholder(int Id, string Before, string Current, string After);

/// <summary>
/// Lists the highlighted spots of the template the app could not fill, so the user can type them now.
/// What is left blank stays highlighted in the Word document.
/// </summary>
internal sealed class PlaceholderForm : Form
{
    private static readonly Color Muted = Color.FromArgb(0x66, 0x66, 0x66);
    private readonly List<(Placeholder Item, TextBox Box)> _boxes = [];

    /// <summary>Text typed for each placeholder (by id); blank ones are left out.</summary>
    public Dictionary<int, string> Values =>
        _boxes.Where(b => b.Box.Text.Trim().Length > 0).ToDictionary(b => b.Item.Id, b => b.Box.Text.Trim());

    public PlaceholderForm(IReadOnlyList<Placeholder> items)
    {
        Text = "Campos não identificados";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(760, 600);
        MinimumSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(16);

        var intro = new Label
        {
            Text = $"O modelo tem {items.Count} trecho(s) em amarelo que não vieram do PDF. Digite o que quiser " +
                   "preencher agora; o que ficar em branco continua em amarelo para você editar no Word.",
            AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Muted, Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 8),
        };

        var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var item in items)
        {
            var context = new Label
            {
                Text = Trim(item.Before, atEnd: true) + "[" + item.Current + "]" + Trim(item.After, atEnd: false),
                AutoSize = true, MaximumSize = new Size(690, 0), Margin = new Padding(0, 10, 0, 2), ForeColor = Muted,
            };
            var box = new TextBox { Dock = DockStyle.Top, PlaceholderText = item.Current };
            grid.Controls.Add(context);
            grid.Controls.Add(box);
            _boxes.Add((item, box));
        }
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(grid);

        var ok = new Button { Text = "Continuar", DialogResult = DialogResult.OK, Width = 110, Height = 30 };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Width = 110, Height = 30 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true,
            Margin = new Padding(0, 8, 0, 0), Padding = new Padding(0, 10, 0, 0),
        };
        Ui.Primary(ok);
        Ui.Secondary(cancel);
        buttons.Controls.AddRange([ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(scroll);
        Controls.Add(buttons);
        Controls.Add(intro);
    }

    private static string Trim(string text, bool atEnd) =>
        text.Length <= 70 ? text : atEnd ? "…" + text[^70..] : text[..70] + "…";
}
