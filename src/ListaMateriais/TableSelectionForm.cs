using System.Globalization;

namespace ListaMateriais;

/// <summary>
/// Lists the tables found in the document so the user picks which ones go into the
/// spreadsheet. Tables with a registered title start checked.
/// </summary>
internal sealed class TableSelectionForm : Form
{
    private static readonly Font TitleFont = new("Segoe UI", 14, FontStyle.Bold);
    private static readonly Color Muted = Color.FromArgb(0x66, 0x66, 0x66);

    private readonly IReadOnlyList<MaterialTable> _tables;
    private readonly IReadOnlySet<string> _registered;
    private readonly CheckedListBox _list = new()
    {
        Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true,
        Font = new Font("Segoe UI", 10),
    };
    private readonly ListView _preview = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _previewTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Left };
    private readonly CheckBox _sum = new() { Text = "Somar as quantidades dos materiais repetidos (recomendado)", AutoSize = true, Checked = true };
    private readonly Label _repeated = new() { AutoSize = true, ForeColor = Muted };
    private readonly CheckBox _remember = new() { AutoSize = true, Checked = true };
    private readonly Button _ok = new() { Text = "Continuar", DialogResult = DialogResult.OK, Width = 110, Height = 30 };

    public TableSelectionForm(IReadOnlyList<MaterialTable> tables, IEnumerable<string> registered)
    {
        _tables = tables;
        _registered = registered.ToHashSet(StringComparer.Ordinal);

        Text = "Tabelas encontradas";
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(760, 540);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(16);

        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Width = 110, Height = 30 };
        AcceptButton = _ok;
        CancelButton = cancel;

        var countByTitle = tables.GroupBy(t => t.Title).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        foreach (var table in tables)
        {
            var number = seen[table.Title] = seen.GetValueOrDefault(table.Title) + 1;
            var name = countByTitle[table.Title] > 1 ? $"{table.Title} ({number})" : table.Title;
            var label = (IsRegistered(table) ? name : $"{name} (nova)") + $" — {Items(table.Items.Count)}";
            _list.Items.Add(label, IsRegistered(table));
        }

        _preview.Columns.Add("Material", 200);
        _preview.Columns.Add("Unid.", 55);
        _preview.Columns.Add("Qtd.", 70, HorizontalAlignment.Right);
        // The name column takes whatever width the other two leave.
        _preview.ClientSizeChanged += (_, _) => _preview.Columns[0].Width =
            Math.Max(120, _preview.ClientSize.Width - _preview.Columns[1].Width - _preview.Columns[2].Width);

        // ItemCheck fires before the state changes, so refresh once it is applied.
        _list.ItemCheck += (_, _) => BeginInvoke(UpdateSummary);
        _list.SelectedIndexChanged += (_, _) => ShowPreview();

        var markAll = new Button { Text = "Marcar todas", AutoSize = true };
        var clearAll = new Button { Text = "Desmarcar todas", AutoSize = true };
        markAll.Click += (_, _) => SetAll(true);
        clearAll.Click += (_, _) => SetAll(false);

        var listButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        listButtons.Controls.AddRange([markAll, clearAll, _status]);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, 8, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(new Label { Text = "Tabelas", AutoSize = true, Font = _previewTitle.Font }, 0, 0);
        left.Controls.Add(_list, 0, 1);
        left.Controls.Add(listButtons, 0, 2);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(8, 0, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(_previewTitle, 0, 0);
        right.Controls.Add(_preview, 0, 1);

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 12) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(right, 1, 0);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.AddRange([_ok, cancel]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var fill in new[] { false, false, true, false, false, false, false })
            layout.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Quais tabelas entram na planilha?", Font = TitleFont, AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Marque as tabelas que devem ser lidas. Clique no nome de uma tabela para ver os itens dela. " +
                   "\"(nova)\" indica um título que não está na aba Cabeçalhos.",
            MaximumSize = new Size(720, 0),
            AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 2, 0, 12),
        }, 0, 1);
        layout.Controls.Add(columns, 0, 2);
        layout.Controls.Add(_sum, 0, 3);
        layout.Controls.Add(_repeated, 0, 4);
        layout.Controls.Add(_remember, 0, 5);
        layout.Controls.Add(buttons, 0, 6);
        _repeated.Margin = new Padding(20, 0, 0, 6);
        Controls.Add(layout);

        if (_list.Items.Count > 0)
            _list.SelectedIndex = Math.Max(0, _list.CheckedIndices.Count > 0 ? _list.CheckedIndices[0] : 0);
        UpdateSummary();
    }

    public IReadOnlyList<MaterialTable> Selected =>
        _list.CheckedIndices.Cast<int>().Select(i => _tables[i]).ToList();

    public bool Sum => _sum.Checked;

    /// <summary>Checked titles that are not registered yet, when the user wants them remembered.</summary>
    public IReadOnlyList<string> TitlesToRemember => _remember.Checked ? NewTitles() : [];

    private bool IsRegistered(MaterialTable table) => _registered.Contains(table.Title);

    private List<string> NewTitles() => Selected
        .Where(t => !IsRegistered(t) && t.Title != MaterialTable.Untitled)
        .Select(t => t.Title)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    private static string Items(int count) => count == 1 ? "1 item" : $"{count} itens";

    private static string Quantity(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private void SetAll(bool check)
    {
        for (var i = 0; i < _list.Items.Count; i++)
            _list.SetItemChecked(i, check);
    }

    private void ShowPreview()
    {
        _preview.BeginUpdate();
        _preview.Items.Clear();
        if (_list.SelectedIndex is var index and >= 0)
        {
            var table = _tables[index];
            _previewTitle.Text = $"Itens de {_list.Items[index].ToString()!.Split(" — ")[0].Replace(" (nova)", "")}";
            foreach (var item in table.Items)
                _preview.Items.Add(new ListViewItem([item.Name, item.Material.Unit, Quantity(item.Material.Quantity)]));
        }
        else
        {
            _previewTitle.Text = "Itens";
        }
        _preview.EndUpdate();
    }

    private void UpdateSummary()
    {
        var selected = Selected;
        var items = selected.Sum(t => t.Items.Count);
        _status.Text = $"{selected.Count} de {_tables.Count} marcada(s), {Items(items)}";
        _ok.Enabled = selected.Count > 0;

        var repeated = MaterialGroups.Group(selected).Where(g => g.Count > 1).ToList();
        _sum.Visible = _repeated.Visible = repeated.Count > 0;
        if (repeated.Count > 0)
        {
            const int maxShown = 3;
            var names = repeated.Take(maxShown).Select(g =>
                $"{g[0].Name} ({string.Join(" + ", g.Select(o => Quantity(o.Material.Quantity)))})");
            _repeated.Text = $"{repeated.Count} material(is) aparece(m) mais de uma vez: {string.Join(", ", names)}" +
                             (repeated.Count > maxShown ? "..." : ".");
        }

        var newTitles = NewTitles();
        _remember.Visible = newTitles.Count > 0;
        _remember.Text = newTitles.Count == 1
            ? $"Marcar \"{newTitles[0]}\" automaticamente nos próximos documentos"
            : $"Marcar estas {newTitles.Count} tabelas automaticamente nos próximos documentos";
    }
}
