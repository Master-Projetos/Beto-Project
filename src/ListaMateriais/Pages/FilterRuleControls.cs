using ListaMateriais.Kmz;

namespace ListaMateriais.Pages;

/// <summary>The controls of one filter condition: one row of the filter grid.</summary>
internal sealed class FilterRuleControls : IDisposable
{
    public CheckBox Active { get; } = new() { Checked = true, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 0, 6, 0) };
    public Label Lead { get; } = new() { Text = "Onde", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ui.Muted, Margin = new Padding(3, 0, 8, 0) };
    public ComboBox Join { get; } = List("E", "OU");
    public ComboBox Column { get; } = List();
    public ComboBox Operator { get; } = List(SheetFilters.Operators);
    public ComboBox Value { get; } = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems, MaxDropDownItems = 20,
    };

    private const string ListOperator = "É um destes";

    /// <summary>Opens the list where several values are checked at once.</summary>
    private readonly Button _pick = Ui.Secondary(new Button
    {
        Text = "Marcar vários...", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 0, 3),
    });

    /// <summary>The value box and the button beside it: what goes in the value column of the grid.</summary>
    public TableLayoutPanel ValueCell { get; } = new()
    {
        ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, 8, 0),
    };

    private readonly Func<int, IReadOnlyList<string>> _valuesOf;
    private string _typed = string.Empty;
    public Button Remove { get; } = Ui.Secondary(new Button
    {
        Text = "×", Width = 28, Height = 25, Anchor = AnchorStyles.Left, Margin = new Padding(6, 3, 0, 3),
        AccessibleName = "Remover condição",
    });

    /// <param name="valuesOf">The distinct values of a column, offered in the value list.</param>
    public FilterRuleControls(IReadOnlyList<string> columns, Func<int, IReadOnlyList<string>> valuesOf, ToolTip tips)
    {
        _valuesOf = valuesOf;
        Join.Width = 58;
        Join.Margin = new Padding(0, 3, 8, 3);
        Column.Dock = Value.Dock = DockStyle.Fill;
        Column.Margin = new Padding(0, 3, 8, 3);
        Value.Margin = new Padding(0, 3, 4, 3);
        ValueCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ValueCell.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ValueCell.Controls.Add(Value, 0, 0);
        ValueCell.Controls.Add(_pick, 1, 0);
        _pick.Click += (_, _) => PickSeveral();
        tips.SetToolTip(_pick, "Marque vários valores de uma vez, para pegar várias linhas.");
        Operator.Width = 140;
        Operator.Margin = new Padding(0, 3, 8, 3);
        foreach (var column in columns)
            Column.Items.Add(column);
        if (Column.Items.Count > 0)
            Column.SelectedIndex = 0;
        Operator.SelectedIndex = 0;
        Join.SelectedIndex = 0;
        FillValues();
        tips.SetToolTip(Active, "Usar esta condição");
        tips.SetToolTip(Join, "E: a linha precisa atender também a esta condição. OU: basta atender a esta ou ao grupo anterior.");
        tips.SetToolTip(Remove, "Remover condição");
        tips.SetToolTip(Value, "Escolha um valor da coluna ou digite. Em \"É um destes\", cada valor escolhido entra na lista, separado por \";\".");
        Operator.SelectedIndexChanged += (_, _) => ShowValue();
        Column.SelectedIndexChanged += (_, _) => FillValues();
        Value.TextUpdate += (_, _) => _typed = Value.Text;
        Value.SelectionChangeCommitted += (_, _) => AddToList();
        ShowValue();
    }

    private bool IsList => Operator.SelectedItem?.ToString() == ListOperator;

    /// <summary>Offers the values found in the chosen column, keeping what is typed.</summary>
    private void FillValues()
    {
        var text = Value.Text;
        Value.BeginUpdate();
        Value.Items.Clear();
        Value.Items.AddRange([.. _valuesOf(Column.SelectedIndex)]);
        Value.EndUpdate();
        Value.Text = text;
    }

    /// <summary>For "É um destes", picking a value adds it to the list instead of replacing it.</summary>
    private void AddToList()
    {
        if (Value.SelectedItem?.ToString() is not { } picked)
        {
            _typed = Value.Text;
            return;
        }
        if (!IsList)
        {
            _typed = picked;
            return;
        }
        var parts = _typed.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!parts.Contains(picked, StringComparer.CurrentCultureIgnoreCase))
            parts.Add(picked);
        var joined = string.Join("; ", parts);
        _typed = joined;
        // The combo writes the picked item after this event, so the list is put back right after it.
        Value.BeginInvoke(() =>
        {
            Value.Text = joined;
            Value.SelectionStart = joined.Length;
        });
    }

    private void ShowValue()
    {
        var op = Operator.SelectedItem?.ToString() ?? "";
        Value.Enabled = SheetFilters.NeedsValue(op);
        // Several values only make sense when the cell has to be one of them.
        _pick.Enabled = op is "É igual a" or ListOperator;
    }

    private void PickSeveral()
    {
        var column = Column.SelectedItem?.ToString() ?? "";
        IReadOnlyList<string> current = IsList
            ? Value.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : string.IsNullOrWhiteSpace(Value.Text) ? [] : [Value.Text.Trim()];
        using var dialog = new ValuePickerForm(column, _valuesOf(Column.SelectedIndex), current);
        if (dialog.ShowDialog(_pick.FindForm()) != DialogResult.OK)
            return;
        if (Combine(dialog.Picked) is not { } combined)
        {
            Dialogs.Info(_pick, "Não dá para juntar esses valores",
                "Um dos valores marcados tem \";\", que é o separador da lista. Marque-o sozinho, em uma condição própria com OU.");
            return;
        }
        Operator.SelectedItem = combined.Operator;
        Value.Text = _typed = combined.Value;
    }

    /// <summary>
    /// The operator and value that stand for the picked values; null when they cannot be written as a list.
    /// </summary>
    internal static (string Operator, string Value)? Combine(IReadOnlyList<string> picked)
    {
        if (picked.Count <= 1)
            return ("É igual a", picked.Count == 0 ? string.Empty : picked[0]);
        return picked.Any(v => v.Contains(';', StringComparison.Ordinal)) ? null : (ListOperator, string.Join("; ", picked));
    }

    public SheetFilterRule ToRule() =>
        new(Column.SelectedIndex, Operator.SelectedItem?.ToString() ?? "É igual a", Value.Text, Active.Checked, Join.SelectedIndex == 1);

    public void Dispose()
    {
        foreach (Control control in new Control[] { Active, Lead, Join, Column, Operator, Value, _pick, ValueCell, Remove })
            control.Dispose();
    }

    private static ComboBox List(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(items);
        return combo;
    }
}
