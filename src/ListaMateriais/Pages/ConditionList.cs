using ListaMateriais.Planilhas;

namespace ListaMateriais.Pages;

/// <summary>
/// A list of conditions on named columns, one per line, joined by E / OU, with a button to add more.
/// </summary>
internal sealed class ConditionList : TableLayoutPanel
{
    private readonly TableLayoutPanel _grid = new()
    {
        ColumnCount = 6, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(0xF7, 0xFA, 0xFC), Padding = new Padding(8, 6, 8, 6), Margin = Padding.Empty,
    };
    private readonly Label[] _headers =
        [.. new[] { "", "", "Coluna", "Condição", "Valor", "" }.Select(text => new Label
        {
            Text = text, AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 0, 8, 1),
        })];
    private readonly Button _add = Ui.Secondary(new Button { AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 6, 0, 0) });
    private readonly ToolTip _tips = new();
    private readonly List<FilterRuleControls> _rules = [];
    private readonly string _addText;
    private IReadOnlyList<string> _columns = [];
    private bool _loading;

    /// <summary>The distinct values of a column, offered in the value list.</summary>
    private Func<string, IReadOnlyList<string>> _valuesOf = _ => [];

    /// <summary>A condition was added, removed or changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The user is typing a value; the change is not final yet.</summary>
    public event EventHandler? Typing;

    public int Count => _rules.Count;

    public ConditionList(string addText = "+ Adicionar filtro")
    {
        _addText = addText;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _add.Text = addText;
        _add.Enabled = false;
        _add.Click += (_, _) =>
        {
            var rule = AddRule();
            LayoutRules();
            rule.Value.Focus();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        Controls.Add(_grid, 0, 0);
        Controls.Add(_add, 0, 1);
    }

    /// <summary>The columns to choose from. Conditions already there keep their column, even if it is gone.</summary>
    public void SetColumns(IReadOnlyList<string> columns, Func<string, IReadOnlyList<string>> valuesOf)
    {
        var chosen = _rules.Select(r => r.Column.SelectedItem as string).ToList();
        _columns = columns;
        _valuesOf = valuesOf;
        _loading = true;
        for (var i = 0; i < _rules.Count; i++)
        {
            var combo = _rules[i].Column;
            combo.Items.Clear();
            foreach (var column in columns)
                combo.Items.Add(column);
            if (chosen[i] is { } name)
                Select(combo, name);
        }
        _loading = false;
        _add.Enabled = columns.Count > 0;
    }

    public IReadOnlyList<Condition> Conditions => [.. _rules
        .Where(r => r.Column.SelectedItem is string)
        .Select(r => new Condition((string)r.Column.SelectedItem!, r.Operator.SelectedItem?.ToString() ?? "É igual a",
            r.Value.Text, r.Join.SelectedIndex == 1, r.Active.Checked))];

    /// <summary>Replaces the conditions with the ones given.</summary>
    public void Load(IReadOnlyList<Condition> conditions)
    {
        Clear();
        _loading = true;
        foreach (var condition in conditions)
        {
            var rule = AddRule();
            Select(rule.Column, condition.Column);
            rule.Operator.SelectedItem = condition.Operator;
            rule.Join.SelectedIndex = condition.Or ? 1 : 0;
            rule.Active.Checked = condition.Active;
            rule.Value.Text = condition.Value;
        }
        _loading = false;
        LayoutRules();
    }

    public void Clear()
    {
        var old = _rules.ToList();
        _rules.Clear();
        LayoutRules();
        foreach (var rule in old)
            rule.Dispose();
    }

    private static void Select(ComboBox combo, string name)
    {
        var key = SheetData.Key(name);
        var index = combo.Items.Cast<string>().ToList().FindIndex(item => SheetData.Key(item) == key);
        // A column this file does not have stays in the rule, and is reported as not found.
        combo.SelectedIndex = index >= 0 ? index : combo.Items.Add(name);
    }

    private FilterRuleControls AddRule()
    {
        // The value list is asked for by position, which only this list can turn into a name.
        FilterRuleControls? rule = null;
        rule = new FilterRuleControls(_columns,
            index => rule is not null && index >= 0 && rule.Column.Items[index] is string name ? _valuesOf(name) : [], _tips);
        var added = rule;
        // The first fill happened before the list knew the rule: choosing the column again fills it.
        if (added.Column.Items.Count > 0)
        {
            added.Column.SelectedIndex = -1;
            added.Column.SelectedIndex = 0;
        }
        _rules.Add(added);
        void Notify(object? sender, EventArgs e)
        {
            if (!_loading)
                Changed?.Invoke(this, EventArgs.Empty);
        }
        added.Active.CheckedChanged += Notify;
        added.Join.SelectedIndexChanged += Notify;
        added.Column.SelectedIndexChanged += Notify;
        added.Operator.SelectedIndexChanged += Notify;
        added.Value.TextChanged += (_, _) =>
        {
            if (!_loading)
                Typing?.Invoke(this, EventArgs.Empty);
        };
        added.Remove.Click += (_, _) =>
        {
            _rules.Remove(added);
            LayoutRules();
            added.Dispose();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        return added;
    }

    /// <summary>Puts the conditions in the grid, one per row; the first reads "Onde", the others E / OU.</summary>
    private void LayoutRules()
    {
        _grid.SuspendLayout();
        _grid.Controls.Clear();
        _grid.RowStyles.Clear();
        _grid.RowCount = _rules.Count + 1;
        for (var column = 0; column < _headers.Length; column++)
            _grid.Controls.Add(_headers[column], column, 0);
        for (var i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            _grid.Controls.Add(rule.Active, 0, i + 1);
            _grid.Controls.Add(i == 0 ? rule.Lead : rule.Join, 1, i + 1);
            _grid.Controls.Add(rule.Column, 2, i + 1);
            _grid.Controls.Add(rule.Operator, 3, i + 1);
            _grid.Controls.Add(rule.Value, 4, i + 1);
            _grid.Controls.Add(rule.Remove, 5, i + 1);
        }
        for (var row = 0; row < _grid.RowCount; row++)
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.Visible = _rules.Count > 0;
        _add.Text = _rules.Count == 0 ? _addText : "+ Adicionar condição";
        _grid.ResumeLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Clear();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
