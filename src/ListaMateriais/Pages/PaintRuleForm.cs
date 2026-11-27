using ListaMateriais.Planilhas;

namespace ListaMateriais.Pages;

/// <summary>The dialog of one painting rule: which columns, which color, and in which rows.</summary>
internal sealed class PaintRuleForm : Form
{
    /// <param name="Color">Null with <paramref name="Custom"/> false takes the fill away.</param>
    private sealed record ColorChoice(string Name, Color? Color, bool Custom = false)
    {
        public override string ToString() => Name;
    }

    private static readonly ColorChoice[] Presets =
    [
        new("Amarelo", Color.FromArgb(0xFF, 0xEB, 0x9C)), new("Amarelo forte", Color.FromArgb(0xFF, 0xFF, 0x00)),
        new("Verde", Color.FromArgb(0xC6, 0xEF, 0xCE)), new("Verde forte", Color.FromArgb(0x92, 0xD0, 0x50)),
        new("Vermelho", Color.FromArgb(0xFF, 0xC7, 0xCE)), new("Vermelho forte", Color.FromArgb(0xFF, 0x50, 0x50)),
        new("Azul", Color.FromArgb(0xBD, 0xD7, 0xEE)), new("Laranja", Color.FromArgb(0xF8, 0xCB, 0xAD)),
        new("Roxo", Color.FromArgb(0xE2, 0xD0, 0xF0)), new("Cinza", Color.FromArgb(0xD9, 0xD9, 0xD9)),
        new("Sem cor (limpar a pintura)", null),
    ];

    private readonly CheckBox _wholeRow = new() { Text = "Pintar a linha inteira", AutoSize = true };
    private readonly CheckedListBox _columns = new() { CheckOnClick = true, IntegralHeight = false, Height = 150, MultiColumn = true, ColumnWidth = 200 };
    private readonly ComboBox _color = new() { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, Width = 240, MaxDropDownItems = 14 };
    private readonly CheckBox _header = new() { Text = "Pintar também o cabeçalho", AutoSize = true };
    private readonly ConditionList _when = new("+ Adicionar condição");
    private ColorChoice _custom = new("Outra cor...", null, Custom: true);
    private int _lastColor;

    /// <summary>The rule as filled in; valid once the dialog returns OK.</summary>
    public PaintRule Rule => new(
        _wholeRow.Checked ? [] : [.. _columns.CheckedItems.Cast<string>()],
        (_color.SelectedItem as ColorChoice)?.Color,
        _when.Conditions, _header.Checked);

    /// <param name="rule">The rule being edited; null for a new one.</param>
    public PaintRuleForm(IReadOnlyList<string> columns, Func<string, IReadOnlyList<string>> valuesOf, PaintRule? rule)
    {
        Text = rule is null ? "Nova pintura" : "Editar pintura";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Ui.BodyFont;
        BackColor = Color.White;
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(560, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(16);

        // A rule may name a column this file does not have: it stays listed so it is not lost.
        var all = columns.ToList();
        foreach (var name in rule?.Columns ?? [])
        {
            if (!all.Any(c => SheetData.Key(c) == SheetData.Key(name)))
                all.Add(name);
        }
        foreach (var column in all)
            _columns.Items.Add(column, rule?.Columns.Any(c => SheetData.Key(c) == SheetData.Key(column)) == true);
        _wholeRow.Checked = rule is { Columns.Count: 0 };
        _columns.Enabled = !_wholeRow.Checked;
        _header.Checked = rule?.IncludeHeader == true;

        _color.Items.AddRange(Presets);
        var index = rule is null ? 0 : Array.FindIndex(Presets, p => p.Color?.ToArgb() == rule.Fill?.ToArgb());
        if (index < 0)
            _custom = _custom with { Color = rule!.Fill };
        _color.Items.Add(_custom);
        _color.SelectedIndex = _lastColor = index < 0 ? _color.Items.Count - 1 : index;

        _when.SetColumns(all, valuesOf);
        _when.Load(rule?.When ?? []);

        var content = new Stack { Dock = DockStyle.Top, AutoSize = true };
        content
            .Caption("Colunas a pintar")
            .Add(_wholeRow, bottom: 4)
            .Add(_columns, bottom: 12)
            .Caption("Cor")
            .Add(_color, bottom: 4)
            .Add(_header, bottom: 12)
            .Caption("Pintar só quando (sem condição, pinta todas as linhas)")
            .Add(_when);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(content);

        var ok = Ui.Primary(new Button { Text = "OK", Width = 110, Height = 30 });
        var cancel = Ui.Secondary(new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Width = 110, Height = 30 });
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true,
            Padding = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.AddRange([cancel, ok]);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(scroll);
        Controls.Add(buttons);

        _wholeRow.CheckedChanged += (_, _) => _columns.Enabled = !_wholeRow.Checked;
        _color.DrawItem += DrawColor;
        _color.SelectionChangeCommitted += (_, _) => PickColor();
        ok.Click += (_, _) => Accept();
    }

    private void Accept()
    {
        var rule = Rule;
        if (!_wholeRow.Checked && rule.Columns.Count == 0)
        {
            Dialogs.Info(this, "Escolha as colunas", "Marque ao menos uma coluna ou \"Pintar a linha inteira\".");
            return;
        }
        if (_color.SelectedItem is ColorChoice { Custom: true, Color: null })
        {
            Dialogs.Info(this, "Escolha a cor", "Selecione \"Outra cor...\" novamente e escolha uma cor.");
            return;
        }
        if (rule.When.FirstOrDefault(c => c.Active && Kmz.SheetFilters.NeedsValue(c.Operator) && string.IsNullOrWhiteSpace(c.Value)) is { } empty)
        {
            Dialogs.Info(this, "Falta o valor de uma condição", $"Preencha o valor da condição sobre \"{empty.Column}\" ou remova-a.");
            return;
        }
        DialogResult = DialogResult.OK;
    }

    private void PickColor()
    {
        if (_color.SelectedItem is not ColorChoice { Custom: true })
        {
            _lastColor = _color.SelectedIndex;
            return;
        }
        using var dialog = new ColorDialog { FullOpen = true, Color = _custom.Color ?? Color.Yellow };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            _color.SelectedIndex = _lastColor;
            return;
        }
        _custom = _custom with { Color = dialog.Color };
        _lastColor = _color.Items.Count - 1;
        _color.Items[_lastColor] = _custom;
        _color.SelectedIndex = _lastColor;
    }

    private void DrawColor(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _color.Items[e.Index] is not ColorChoice choice)
            return;
        e.DrawBackground();
        var side = e.Bounds.Height - 6;
        var swatch = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top + 3, side, side);
        using (var fill = new SolidBrush(choice.Color ?? Color.White))
            e.Graphics.FillRectangle(fill, swatch);
        e.Graphics.DrawRectangle(Pens.Gray, swatch);
        var text = Rectangle.FromLTRB(swatch.Right + 6, e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, choice.Name, e.Font ?? Font, text, e.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    /// <summary>The name of a fill, as the list of rules shows it.</summary>
    public static string ColorName(Color? color) =>
        Presets.FirstOrDefault(p => p.Color?.ToArgb() == color?.ToArgb())?.Name
        ?? (color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "Sem cor");
}
