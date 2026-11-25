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
        Column.Margin = Value.Margin = new Padding(0, 3, 8, 3);
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

    private bool IsList => Operator.SelectedItem?.ToString() == "É um destes";

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
    }

    public SheetFilterRule ToRule() =>
        new(Column.SelectedIndex, Operator.SelectedItem?.ToString() ?? "É igual a", Value.Text, Active.Checked, Join.SelectedIndex == 1);

    public void Dispose()
    {
        foreach (Control control in new Control[] { Active, Lead, Join, Column, Operator, Value, Remove })
            control.Dispose();
    }

    private static ComboBox List(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(items);
        return combo;
    }
}
