namespace ListaMateriais.Pages;

/// <summary>Picks several values of a column at once, with a search box over the list.</summary>
internal sealed class ValuePickerForm : Form
{
    private readonly TextBox _search = new() { PlaceholderText = "Procurar...", Dock = DockStyle.Top };
    private readonly CheckedListBox _list = new() { CheckOnClick = true, IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly Label _count = new() { AutoSize = true, ForeColor = Ui.Muted, Dock = DockStyle.Bottom, Padding = new Padding(0, 6, 0, 0) };
    private readonly IReadOnlyList<string> _values;
    private readonly HashSet<string> _picked;
    private bool _filling;

    /// <summary>The values checked, in the order of the list.</summary>
    public IReadOnlyList<string> Picked => [.. _values.Where(_picked.Contains)];

    /// <param name="selected">Checked from the start; those not among the values are listed too.</param>
    public ValuePickerForm(string column, IReadOnlyList<string> values, IReadOnlyList<string> selected)
    {
        _picked = new HashSet<string>(selected, StringComparer.CurrentCultureIgnoreCase);
        _values = [.. values.Concat(selected).Distinct(StringComparer.CurrentCultureIgnoreCase)];

        Text = $"Valores de \"{column}\"";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Ui.BodyFont;
        BackColor = Color.White;
        ClientSize = new Size(440, 520);
        MinimumSize = new Size(420, 320);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(16);

        var all = Ui.Secondary(new Button { Text = "Marcar os mostrados", AutoSize = true, Padding = new Padding(6, 2, 6, 2) });
        var none = Ui.Secondary(new Button { Text = "Desmarcar todos", AutoSize = true, Padding = new Padding(6, 2, 6, 2) });
        var marking = Stack.Flow(all, none);
        marking.Dock = DockStyle.Bottom;
        marking.Padding = new Padding(0, 6, 0, 0);

        var ok = Ui.Primary(new Button { Text = "OK", Width = 110, Height = 30, DialogResult = DialogResult.OK });
        var cancel = Ui.Secondary(new Button { Text = "Cancelar", Width = 110, Height = 30, DialogResult = DialogResult.Cancel });
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true,
            Padding = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.AddRange([cancel, ok]);
        AcceptButton = ok;
        CancelButton = cancel;

        // Docking goes from the last control added to the first.
        Controls.AddRange([_list, _search, _count, marking, buttons]);

        _search.TextChanged += (_, _) => Fill();
        _list.ItemCheck += (_, e) =>
        {
            if (_filling || _list.Items[e.Index] is not string value)
                return;
            if (e.NewValue == CheckState.Checked)
                _picked.Add(value);
            else
                _picked.Remove(value);
            ShowCount();
        };
        all.Click += (_, _) =>
        {
            for (var i = 0; i < _list.Items.Count; i++)
                _list.SetItemChecked(i, true);
        };
        none.Click += (_, _) =>
        {
            _picked.Clear();
            Fill();
        };
        Fill();
    }

    /// <summary>Lists the values that match the search; what was checked stays checked.</summary>
    private void Fill()
    {
        var wanted = Kmz.SheetFilters.Normalize(_search.Text);
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var value in _values.Where(v => Kmz.SheetFilters.Normalize(v).Contains(wanted, StringComparison.Ordinal)))
            _list.Items.Add(value, _picked.Contains(value));
        _list.EndUpdate();
        _filling = false;
        ShowCount();
    }

    private void ShowCount() => _count.Text = $"{_picked.Count:N0} marcado(s)";
}
