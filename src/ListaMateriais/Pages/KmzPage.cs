using System.Diagnostics;
using System.Globalization;
using ListaMateriais.Kmz;

namespace ListaMateriais.Pages;

/// <summary>
/// Turns a list of coordinates into a KMZ with one placemark per point. The points are typed or
/// pasted (plain lines or a Python dict) or come from the columns of a spreadsheet; the user picks
/// the icon.
/// </summary>
internal sealed class KmzPage : Page
{
    private const int MaxPreviewRows = 1000;
    private const int CustomImage = -1;
    private static readonly double[] Scales = [0.8, 1.1, 1.5];

    /// <param name="Color">Null for the entry that opens the color dialog.</param>
    private sealed record ColorChoice(string Name, Color? Color)
    {
        public override string ToString() => Name;
    }

    private static readonly ColorChoice OtherColor = new("Outra cor...", null);

    /// <summary>The controls of one filter condition: one row of the filter grid.</summary>
    private sealed class FilterRuleControls : IDisposable
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
    }

    private readonly RadioButton _fromText = new() { Text = "Digitar ou colar", AutoSize = true, Checked = true, Margin = new Padding(0, 0, 18, 0) };
    private readonly RadioButton _fromSheet = new() { Text = "Planilha do Excel (.xlsx)", AutoSize = true };
    private readonly Stack _textPanel = new() { AutoSize = true };
    private readonly Stack _sheetPanel = new() { AutoSize = true, Visible = false };

    private readonly TextBox _text = new()
    {
        Multiline = true, AcceptsReturn = true, WordWrap = false, ScrollBars = ScrollBars.Vertical, Height = 88,
        Font = new Font("Consolas", 10),
    };
    private readonly ComboBox _order = List("Latitude, Longitude", "Longitude, Latitude");

    private readonly Label _sheetFile = new() { AutoSize = true, ForeColor = Ui.Muted, Text = "Nenhuma planilha escolhida.", Margin = new Padding(10, 7, 0, 0) };
    private readonly ComboBox _sheet = List();
    private readonly ComboBox _nameColumn = List();
    private readonly ComboBox _latitudeColumn = List();
    private readonly ComboBox _longitudeColumn = List();
    private readonly ComboBox _streetColumn = List();
    private readonly ComboBox _numberColumn = List();
    private readonly ComboBox _typeColumn = List();
    private readonly ComboBox _districtColumn = List();
    private readonly ComboBox _cityColumn = List();
    private readonly ComboBox _stateColumn = List();
    private readonly ComboBox _postalColumn = List();
    private readonly TextBox _cityText = new() { Width = 220, PlaceholderText = "ex.: Lavras" };
    private readonly ComboBox _stateText = List();
    private readonly WrapLabel _guessed = new() { ForeColor = Ui.Muted };
    private readonly Button _locate = Ui.Secondary(new Button { Text = "Localizar endereços", AutoSize = true, Visible = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly Label _locateStatus = new() { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(10, 7, 0, 0) };
    private readonly CheckBox _header = new() { Text = "A primeira linha tem os nomes das colunas", AutoSize = true, Checked = true };
    private readonly TableLayoutPanel _filterGrid = new()
    {
        ColumnCount = 6, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false,
        BackColor = Color.FromArgb(0xF7, 0xFA, 0xFC), Padding = new Padding(8, 6, 8, 6),
    };
    private readonly Label[] _filterHeaders =
        [.. new[] { "", "", "Coluna", "Condição", "Valor", "" }.Select(text => new Label
        {
            Text = text, AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 0, 8, 1),
        })];
    private readonly Button _addFilter = Ui.Secondary(new Button { Text = "+ Adicionar filtro", AutoSize = true, Padding = new Padding(8, 2, 8, 2) });
    private readonly Label _filterSummary = new() { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(10, 7, 0, 0) };
    private readonly ToolTip _tips = new();
    private readonly List<FilterRuleControls> _filterRules = [];

    private readonly PictureBox _preview = new() { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.CenterImage, Margin = new Padding(0, 4, 14, 0) };
    private readonly ComboBox _shape = List();
    private readonly ComboBox _color = new() { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly ComboBox _size = List("Pequeno", "Médio", "Grande");

    private readonly Label _status = new() { AutoSize = true, ForeColor = Ui.Muted };
    private readonly ListView _points = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly WrapLabel _problems = new() { ForeColor = Ui.Bad };
    private readonly WrapLabel _fixes = new() { ForeColor = Ui.Muted };
    private readonly Button _generate = Ui.Primary(new Button { Text = "Gerar KMZ", Font = Ui.BigButtonFont, Height = 44, Enabled = false });
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Height = 4, Visible = false };
    private readonly Button _openOutput = Ui.Secondary(new Button { Text = "Abrir arquivo", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly Button _showOutput = Ui.Secondary(new Button { Text = "Mostrar na pasta", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly System.Windows.Forms.Timer _typing = new() { Interval = 300 };

    private IReadOnlyList<SheetTable> _tables = [];
    private string? _sheetPath;
    private string? _imagePath;
    private string? _lastOutput;
    private PointList _current = PointList.Empty;
    private SheetReading _reading = SheetReading.Empty;
    private CancellationTokenSource? _locating;
    private readonly TableLayoutPanel _iconFields;
    private int _lastShape;
    private bool _loading;
    private bool _busy;

    public override string Title => "&Mapa (KMZ)";
    public override string Glyph => "";

    /// <summary>The button Enter triggers while this page is showing.</summary>
    public Button MainButton => _generate;

    public KmzPage()
    {
        var options = Config.LoadKmzOptions();
        _order.Width = 170;
        _order.SelectedIndex = options.LongitudeFirst ? 1 : 0;
        _size.SelectedIndex = options.Size;
        foreach (var (_, name) in MarkerIcon.Shapes)
            _shape.Items.Add(name);
        _shape.Items.Add("Imagem do computador...");
        _shape.SelectedIndex = _lastShape = MarkerIcon.Shapes.ToList().FindIndex(s => s.Shape == options.Shape);
        FillColors(Color.FromArgb(0xFF << 24 | options.Color));

        var chooseSheet = Ui.Secondary(new Button { Text = "Escolher planilha...", AutoSize = true, Padding = new Padding(8, 2, 8, 2) });
        chooseSheet.Click += (_, _) => Browse();

        _textPanel
            .Add(_text)
            .Add(Ui.Hint("Um ponto por linha: nome, latitude, longitude  (ex.: Poste 12, -23.5505, -46.6333). O nome é opcional. " +
                         "Também aceita um dicionário Python: {\"Poste 12\": (-23.5505, -46.6333), \"CTO 3\": (-23.5510, -46.6340)}"),
                top: 4, bottom: 8)
            .Add(Stack.Flow(new Label { Text = "Ordem das coordenadas:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _order));
        _stateText.Items.Add(string.Empty);
        _stateText.Items.AddRange([.. AddressText.States]);
        _stateText.Width = 70;
        _sheetPanel
            .Add(Stack.Flow(chooseSheet, _sheetFile), bottom: 10)
            .Add(Fields(("Aba", _sheet), ("Coluna do nome", _nameColumn)), bottom: 8)
            .Add(new Label { Text = "Onde fica cada ponto", AutoSize = true, Font = Ui.BoldFont }, bottom: 2)
            .Add(_guessed, bottom: 6)
            .Add(Fields(("Latitude", _latitudeColumn), ("Longitude", _longitudeColumn), ("Endereço ou rua", _streetColumn),
                ("Número", _numberColumn)), bottom: 4)
            .Add(Fields(("Tipo (Rua, Av.)", _typeColumn), ("Bairro", _districtColumn), ("Cidade", _cityColumn), ("UF", _stateColumn),
                ("CEP", _postalColumn)), bottom: 6)
            .Add(Stack.Flow(
                new Label { Text = "Cidade, se a planilha não disser:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _cityText,
                new Label { Text = "UF:", AutoSize = true, Margin = new Padding(12, 6, 6, 0) }, _stateText), bottom: 6)
            .Add(_header)
            .Add(new Label { Text = "Filtrar linhas", AutoSize = true, Font = Ui.BoldFont }, top: 12, bottom: 4)
            .Add(_filterGrid)
            .Add(Stack.Flow(_addFilter, _filterSummary), top: 6)
            .Add(Stack.Flow(_locate, _locateStatus), top: 12)
            .Add(Ui.Hint("Linhas com latitude e longitude usam as coordenadas; as outras são procuradas pelo endereço no " +
                         "OpenStreetMap (só rua, número, bairro, cidade e UF são enviados), uma por segundo. O que for achado " +
                         "fica guardado neste computador."), top: 4);
        _filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        _filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        _filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var icon = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true };
        icon.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        icon.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        icon.Controls.Add(_preview, 0, 0);
        _iconFields = Fields(("Formato", _shape), ("Cor", _color), ("Tamanho no mapa", _size));
        icon.Controls.Add(_iconFields, 1, 0);

        _points.Columns.Add("Nome", 240);
        _points.Columns.Add("Latitude", 110, HorizontalAlignment.Right);
        _points.Columns.Add("Longitude", 110, HorizontalAlignment.Right);
        _points.MinimumSize = new Size(0, 90);
        // The name column takes whatever width the other two leave.
        _points.ClientSizeChanged += (_, _) => _points.Columns[0].Width =
            Math.Max(120, _points.ClientSize.Width - _points.Columns[1].Width - _points.Columns[2].Width);

        SetContent(new Stack()
            .Title("Mapa de pontos (KMZ)", "Cria um arquivo com um marcador para cada coordenada. Ele abre no Google Earth " +
                                          "e pode ser importado no Google Maps (Meus mapas).")
            .Caption("De onde vêm os pontos")
            .Add(Stack.Flow(_fromText, _fromSheet), bottom: 8)
            .Add(_textPanel)
            .Add(_sheetPanel)
            .Caption("Ícone dos marcadores", top: 14)
            .Add(icon)
            .Caption("Pontos encontrados", top: 14)
            .Add(_status, bottom: 4)
            .Add(_points, fill: true)
            .Add(_problems, top: 4)
            .Add(_fixes, top: 2)
            .Add(_generate, top: 10)
            .Add(_progress, top: 4)
            .Add(Stack.Flow(_openOutput, _showOutput), top: 8), 700);

        _fromText.CheckedChanged += (_, _) => ShowSource();
        _typing.Tick += (_, _) => Reparse();
        _text.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
        };
        _order.SelectedIndexChanged += (_, _) => Reparse();
        _sheet.SelectedIndexChanged += (_, _) => ShowColumns();
        foreach (var column in new[] { _nameColumn, _latitudeColumn, _longitudeColumn, _streetColumn, _numberColumn, _typeColumn,
                     _districtColumn, _cityColumn, _stateColumn, _postalColumn, _stateText })
            column.SelectedIndexChanged += (_, _) => Reparse();
        _cityText.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
        };
        _locate.Click += (_, _) => Locate();
        _header.CheckedChanged += (_, _) => Reparse();
        _addFilter.Click += (_, _) => AddFilterRule();

        _shape.SelectedIndexChanged += (_, _) => ShapeChanged();
        _color.DrawItem += DrawColor;
        _color.SelectedIndexChanged += (_, _) => ColorChanged();
        _size.SelectedIndexChanged += (_, _) => SaveOptions();

        _generate.Click += (_, _) => Generate();
        _openOutput.Click += (_, _) => OpenOutput();
        _showOutput.Click += (_, _) =>
        {
            if (_lastOutput is not null)
                Ui.ShowInFolder(_lastOutput);
        };
        Reparse();
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        ShowIcon();
    }

    private static ComboBox List(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(items);
        return combo;
    }

    /// <summary>Labelled controls side by side, in equal columns.</summary>
    private static TableLayoutPanel Fields(params (string Label, Control Control)[] fields)
    {
        var panel = new TableLayoutPanel { ColumnCount = fields.Length, RowCount = 2, AutoSize = true, Dock = DockStyle.Top };
        for (var i = 0; i < fields.Length; i++)
        {
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / fields.Length));
            var last = i == fields.Length - 1;
            panel.Controls.Add(new Label { Text = fields[i].Label, AutoSize = true, Margin = new Padding(0, 0, 0, 3) }, i, 0);
            fields[i].Control.Dock = DockStyle.Top;
            fields[i].Control.Margin = new Padding(0, 0, last ? 0 : 10, 3);
            panel.Controls.Add(fields[i].Control, i, 1);
        }
        return panel;
    }

    // ---- Where the points come from ------------------------------------------------------------

    public override bool Accepts(string path) => HasExtension(path, ".xlsx");

    public override void Browse()
    {
        if (_busy)
            return;
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione a planilha com as coordenadas",
            Filter = "Planilha Excel (*.xlsx)|*.xlsx",
            InitialDirectory = _sheetPath is null ? "" : Path.GetDirectoryName(_sheetPath),
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            Open(dialog.FileName);
    }

    public override void Open(string path)
    {
        if (_busy)
            return;
        IReadOnlyList<SheetTable> tables;
        try
        {
            tables = [.. XlsxTable.Read(path).Where(t => t.Rows.Count > 0)];
        }
        catch (AppException e)
        {
            Dialogs.Warn(this, "Não foi possível ler a planilha", e.Message);
            return;
        }
        if (tables.Count == 0)
        {
            Dialogs.Warn(this, "A planilha está vazia", $"\"{Path.GetFileName(path)}\" não tem nenhuma linha preenchida.");
            return;
        }

        _tables = tables;
        _sheetPath = path;
        _sheetFile.Text = Path.GetFileName(path);
        _sheetFile.ForeColor = ForeColor;
        _fromSheet.Checked = true;

        _loading = true;
        _sheet.Items.Clear();
        foreach (var table in tables)
            _sheet.Items.Add(table.Name);
        _loading = false;
        _sheet.SelectedIndex = 0;
    }

    private void ShowSource()
    {
        _textPanel.Visible = _fromText.Checked;
        _sheetPanel.Visible = !_fromText.Checked;
        Reparse();
    }

    private SheetTable? Table => _sheet.SelectedIndex >= 0 && _sheet.SelectedIndex < _tables.Count ? _tables[_sheet.SelectedIndex] : null;

    /// <summary>Lists the columns of the chosen sheet and preselects the ones whose header says what they are.</summary>
    private void ShowColumns()
    {
        if (_loading || Table is not { } table)
            return;
        _loading = true;
        foreach (var rule in _filterRules)
            rule.Dispose();
        _filterRules.Clear();
        LayoutFilters();
        var header = table.Rows[0];
        var guessed = ColumnGuesser.Guess(table, hasHeader: true);
        var named = guessed != SheetColumns.None;

        var labels = Enumerable.Range(0, table.Columns).Select(i =>
        {
            var letter = SheetTable.ColumnLetter(i);
            var title = i < header.Length ? header[i] : null;
            return title is null ? letter : $"{letter} — {(title.Length > 40 ? title[..40] + "…" : title)}";
        }).ToArray();
        foreach (var (combo, role) in RoleCombos)
        {
            combo.Items.Clear();
            combo.Items.Add(role == ColumnRole.Name ? "(sem nome: numerar os pontos)" : "(nenhuma)");
            combo.Items.AddRange(labels);
            combo.SelectedIndex = guessed[role] + 1;
        }
        _header.Checked = named || !table.Rows[0].Any(cell => Coordinate.TryParse(cell, out _));
        _guessed.Text = Guessed(guessed, labels);
        _loading = false;
        Reparse();
    }

    private (ComboBox Combo, ColumnRole Role)[] RoleCombos =>
    [
        (_nameColumn, ColumnRole.Name), (_latitudeColumn, ColumnRole.Latitude), (_longitudeColumn, ColumnRole.Longitude),
        (_streetColumn, ColumnRole.Street), (_numberColumn, ColumnRole.Number), (_typeColumn, ColumnRole.StreetType),
        (_districtColumn, ColumnRole.District), (_cityColumn, ColumnRole.City), (_stateColumn, ColumnRole.State),
        (_postalColumn, ColumnRole.PostalCode),
    ];

    /// <summary>The columns picked, as the combos say (the first item of each is "none").</summary>
    private SheetColumns Columns => RoleCombos.Aggregate(SheetColumns.None, (columns, pair) => columns.With(pair.Role, pair.Combo.SelectedIndex - 1));

    /// <summary>One sentence on what the sheet was read as, so the user checks it before anything else.</summary>
    private static string Guessed(SheetColumns columns, string[] labels)
    {
        string Letter(int column) => SheetTable.ColumnLetter(column);
        var parts = new List<string>();
        if (columns.HasCoordinates)
            parts.Add($"coordenadas em {Letter(columns.Latitude)} e {Letter(columns.Longitude)}");
        if (columns.Street >= 0)
            parts.Add(columns.Number >= 0 ? $"rua em {Letter(columns.Street)} e número em {Letter(columns.Number)}"
                : $"endereço junto (rua e número) em {Letter(columns.Street)}");
        if (columns.StreetType >= 0)
            parts.Add($"tipo da rua em {Letter(columns.StreetType)}");
        if (columns.District >= 0)
            parts.Add($"bairro em {Letter(columns.District)}");
        if (columns.City >= 0)
            parts.Add($"cidade em {Letter(columns.City)}");
        if (columns.State >= 0)
            parts.Add($"UF em {Letter(columns.State)}");
        if (columns.PostalCode >= 0)
            parts.Add($"CEP em {Letter(columns.PostalCode)}");
        if (parts.Count == 0)
            return labels.Length == 0 ? string.Empty : "Não reconheci as colunas: escolha abaixo onde estão as coordenadas ou o endereço.";
        var text = "Previsto pela planilha: " + string.Join(", ", parts) + ".";
        if (columns.HasAddress && columns.City < 0)
            text += " Não há coluna de cidade: escreva a cidade abaixo.";
        return text + " Confira e troque se precisar.";
    }

    private void AddFilterRule()
    {
        if (Table is not { } table)
            return;
        var header = table.Rows[0];
        var columns = Enumerable.Range(0, table.Columns).Select(i =>
        {
            var title = _header.Checked && i < header.Length ? header[i] : null;
            return title is null ? SheetTable.ColumnLetter(i) : $"{SheetTable.ColumnLetter(i)} — {title}";
        }).ToArray();
        var rule = new FilterRuleControls(columns, ColumnValues, _tips);
        _filterRules.Add(rule);
        rule.Active.CheckedChanged += (_, _) => Reparse();
        rule.Join.SelectedIndexChanged += (_, _) => Reparse();
        rule.Column.SelectedIndexChanged += (_, _) => Reparse();
        rule.Operator.SelectedIndexChanged += (_, _) => Reparse();
        rule.Value.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
        };
        rule.Remove.Click += (_, _) =>
        {
            _filterRules.Remove(rule);
            LayoutFilters();
            rule.Dispose();
            Reparse();
        };
        LayoutFilters();
        rule.Value.Focus();
        Reparse();
    }

    private const int MaxValueChoices = 1000;

    /// <summary>The distinct values of a column of the chosen sheet, sorted, for the filter value lists.</summary>
    private IReadOnlyList<string> ColumnValues(int column)
    {
        if (Table is not { } table || column < 0)
            return [];
        return [.. table.Rows.Skip(_header.Checked ? 1 : 0)
            .Select(row => column < row.Length ? row[column]?.Trim() : null)
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxValueChoices)];
    }

    /// <summary>Puts the conditions in the grid, one per row; the first reads "Onde", the others E / OU.</summary>
    private void LayoutFilters()
    {
        _filterGrid.SuspendLayout();
        _filterGrid.Controls.Clear();
        _filterGrid.RowStyles.Clear();
        _filterGrid.RowCount = _filterRules.Count + 1;
        for (var column = 0; column < _filterHeaders.Length; column++)
            _filterGrid.Controls.Add(_filterHeaders[column], column, 0);
        for (var i = 0; i < _filterRules.Count; i++)
        {
            var rule = _filterRules[i];
            _filterGrid.Controls.Add(rule.Active, 0, i + 1);
            _filterGrid.Controls.Add(i == 0 ? rule.Lead : rule.Join, 1, i + 1);
            _filterGrid.Controls.Add(rule.Column, 2, i + 1);
            _filterGrid.Controls.Add(rule.Operator, 3, i + 1);
            _filterGrid.Controls.Add(rule.Value, 4, i + 1);
            _filterGrid.Controls.Add(rule.Remove, 5, i + 1);
        }
        for (var row = 0; row < _filterGrid.RowCount; row++)
            _filterGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _filterGrid.Visible = _filterRules.Count > 0;
        _addFilter.Text = _filterRules.Count == 0 ? "+ Adicionar filtro" : "+ Adicionar condição";
        _filterGrid.ResumeLayout();
    }

    private SheetReading ReadSheet()
    {
        var columns = Columns;
        if (Table is not { } table || !columns.HasCoordinates && !columns.HasAddress)
            return SheetReading.Empty;
        var filters = _filterRules.Select(rule => rule.ToRule())
            .Where(rule => rule.Column >= 0).ToArray();
        var filtered = SheetFilters.Apply(table, filters, _header.Checked);
        var skip = _header.Checked ? 1 : 0;
        _filterSummary.Text = filters.Any(rule => rule.Active)
            ? $"{filtered.Rows.Count - skip} de {table.Rows.Count - skip} linhas atendem aos filtros."
            : _filterRules.Count == 0 ? "Sem filtros: todas as linhas entram." : "Nenhuma condição ativa: todas as linhas entram.";
        var defaults = new AddressDefaults(_cityText.Text, _stateText.SelectedItem as string);
        return SheetPoints.Read(filtered, columns, _header.Checked, defaults, GeoCache.Get);
    }

    /// <summary>Reads the points again and shows what was found, so mistakes show before the file is made.</summary>
    private void Reparse()
    {
        _typing.Stop();
        if (_loading)
            return;
        var order = _order.SelectedIndex == 1 ? CoordinateOrder.LongitudeFirst : CoordinateOrder.LatitudeFirst;
        _reading = _fromText.Checked ? SheetReading.Empty : ReadSheet();
        _current = _fromText.Checked ? PointText.Parse(_text.Text, order) : _reading.Points;
        if (_fromText.Checked)
            SaveOptions();

        var points = _current.Points;
        _points.BeginUpdate();
        _points.Items.Clear();
        foreach (var point in points.Take(MaxPreviewRows))
            _points.Items.Add(new ListViewItem([point.Name, Degrees(point.Latitude), Degrees(point.Longitude)]));
        _points.EndUpdate();

        var problems = _current.Problems;
        var activeFilterCount = _filterRules.Count(rule => rule.Active.Checked);
        _status.Text = points.Count == 0 && problems.Count == 0
            ? _fromText.Checked
                ? "Digite ou cole as coordenadas acima."
                : Table is null ? "Escolha a planilha."
                : !Columns.HasCoordinates && !Columns.HasAddress
                    ? "Escolha as colunas da latitude e da longitude, ou a do endereço."
                    : _reading.Pending.Count > 0
                        ? "Nenhum ponto pronto ainda: localize os endereços."
                        : _filterRules.Any(rule => rule.Active.Checked)
                            ? "Nenhum ponto corresponde aos filtros ativos."
                            : "Escolha as colunas da latitude e da longitude, ou a do endereço."
            : (points.Count == 1 ? "1 ponto pronto" : $"{points.Count} pontos prontos") + Origins()
              + (points.Count > MaxPreviewRows ? $" (mostrando os primeiros {MaxPreviewRows})" : string.Empty)
              + (activeFilterCount > 0 ? $" · {activeFilterCount} filtro(s) ativo(s)" : string.Empty)
              + (problems.Count == 0 ? "." : problems.Count == 1 ? " · 1 entrada ficou de fora:" : $" · {problems.Count} entradas ficaram de fora:");
        const int shown = 4;
        _problems.Text = string.Join("\n", problems.Take(shown))
                         + (problems.Count > shown ? $"\n... e mais {problems.Count - shown}." : string.Empty);
        _problems.Visible = problems.Count > 0;
        ShowFixes(_current.Fixes);
        ShowLocate();
        _generate.Enabled = !_busy && _locating is null && points.Count > 0;
    }

    /// <summary>" (3 pelas coordenadas, 5 pelo endereço: 1 no número, 4 na rua)".</summary>
    private string Origins()
    {
        var byAddress = _reading.Located.Where(p => p.Key != GeoPrecision.City).Sum(p => p.Value);
        if (byAddress == 0)
            return string.Empty;
        var detail = new List<string>();
        foreach (var (precision, words) in new[] { (GeoPrecision.Number, "no número"), (GeoPrecision.Street, "só na rua"), (GeoPrecision.District, "só no bairro") })
        {
            if (_reading.Located.GetValueOrDefault(precision) is var count and > 0)
                detail.Add($"{count} {words}");
        }
        return $" ({_reading.FromCoordinates} pelas coordenadas, {byAddress} pelo endereço: {string.Join(", ", detail)})";
    }

    /// <summary>Offers to look for the addresses not found yet, with how long it takes.</summary>
    private void ShowLocate()
    {
        if (_locating is not null)
            return;
        var pending = _fromText.Checked ? 0 : _reading.Pending.Count;
        _locate.Visible = pending > 0;
        _locate.Text = pending == 1 ? "Localizar 1 endereço" : $"Localizar {pending:N0} endereços";
        _locateStatus.Text = pending > 0 ? $"leva cerca de {Duration(AddressLocator.Estimate(pending))}" : string.Empty;
    }

    private static string Duration(TimeSpan time) =>
        time.TotalMinutes < 1 ? "1 minuto"
        : time.TotalHours < 1 ? $"{Math.Ceiling(time.TotalMinutes):0} minutos"
        : $"{Math.Floor(time.TotalHours):0} h {time.Minutes:0} min";

    /// <summary>Looks for the pending addresses, a second each, showing the points as they are found. Clicking again stops.</summary>
    private async void Locate()
    {
        if (_locating is { } running)
        {
            running.Cancel();
            return;
        }
        var pending = _reading.Pending;
        if (pending.Count == 0)
            return;
        if (pending.Count > 500 && !Dialogs.Confirm(this, $"{pending.Count:N0} endereços para localizar",
                $"O serviço gratuito do OpenStreetMap responde um endereço por segundo: isso leva cerca de " +
                $"{Duration(AddressLocator.Estimate(pending.Count))}, e ele pede uso moderado.\n\n" +
                "Dá para parar quando quiser: o que já foi achado fica guardado e não é procurado de novo. Se puder, " +
                "use os filtros para localizar só as linhas que precisa.", "Localizar", "Cancelar"))
            return;

        using var cancel = new CancellationTokenSource();
        _locating = cancel;
        _locate.Text = "Parar";
        _generate.Enabled = false;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            _locateStatus.Text = $"Localizando: {p.Done:N0} de {p.Total:N0}...";
            if (p.Done % 10 == 0)
                Reparse();
        });
        string? stopped = null;
        try
        {
            using var geocoder = new NominatimGeocoder();
            var done = await AddressLocator.RunAsync(pending, geocoder, progress, cancel.Token);
            _locating = null;
            Reparse();
            if (cancel.IsCancellationRequested)
                stopped = $"Parado depois de {done:N0}; o que foi achado ficou guardado.";
        }
        catch (AppException e)
        {
            _locating = null;
            Reparse();
            Dialogs.Warn(this, "A busca dos endereços parou", e.Message);
        }
        finally
        {
            _locating = null;
            ShowLocate();
            if (stopped is not null)
                _locateStatus.Text = stopped;
            _generate.Enabled = !_busy && _current.Points.Count > 0;
        }
    }

    /// <summary>One line on what was corrected; the details are in its tooltip.</summary>
    private void ShowFixes(IReadOnlyList<string> fixes)
    {
        var repeated = fixes.Count(fix => fix.EndsWith("repetida, ignorada.", StringComparison.Ordinal));
        var swapped = fixes.Count(fix => fix.Contains("trocadas", StringComparison.Ordinal));
        var typos = fixes.Count(fix => fix.Contains("digitada", StringComparison.Ordinal));
        var decimals = fixes.Count(fix => fix.Contains("lida como", StringComparison.Ordinal)) - typos;
        var parts = new List<string>();
        if (typos > 0)
            parts.Add(typos == 1 ? "1 coordenada com erro de digitação" : $"{typos} coordenadas com erro de digitação");
        if (decimals > 0)
            parts.Add(decimals == 1 ? "1 coordenada sem vírgula decimal" : $"{decimals} coordenadas sem vírgula decimal");
        if (swapped > 0)
            parts.Add(swapped == 1 ? "1 linha com latitude e longitude trocadas" : $"{swapped} linhas com latitude e longitude trocadas");
        if (repeated > 0)
            parts.Add(repeated == 1 ? "1 linha repetida ignorada" : $"{repeated} linhas repetidas ignoradas");
        _fixes.Text = parts.Count == 0 ? string.Empty : "Corrigido automaticamente: " + string.Join(", ", parts) + ".";
        _fixes.Visible = parts.Count > 0;
        _tips.SetToolTip(_fixes, string.Join("\n", fixes.Take(30)) + (fixes.Count > 30 ? $"\n... e mais {fixes.Count - 30}." : string.Empty));
    }

    private static string Degrees(double value) => value.ToString("0.000000", CultureInfo.CurrentCulture);

    // ---- Icon ----------------------------------------------------------------------------------

    private void FillColors(Color selected)
    {
        ColorChoice[] palette =
        [
            new("Vermelho", Color.FromArgb(0xE5, 0x39, 0x35)), new("Laranja", Color.FromArgb(0xFB, 0x8C, 0x00)),
            new("Amarelo", Color.FromArgb(0xFD, 0xD8, 0x35)), new("Verde", Color.FromArgb(0x43, 0xA0, 0x47)),
            new("Azul", Color.FromArgb(0x1E, 0x88, 0xE5)), new("Roxo", Color.FromArgb(0x8E, 0x24, 0xAA)),
            new("Rosa", Color.FromArgb(0xD8, 0x1B, 0x60)), new("Marrom", Color.FromArgb(0x6D, 0x4C, 0x41)),
            new("Cinza", Color.FromArgb(0x75, 0x75, 0x75)), new("Preto", Color.FromArgb(0x21, 0x21, 0x21)),
            new("Branco", Color.White),
        ];
        _loading = true;
        _color.Items.Clear();
        _color.Items.AddRange(palette);
        var index = Array.FindIndex(palette, c => c.Color!.Value.ToArgb() == selected.ToArgb());
        if (index < 0)
        {
            _color.Items.Add(new ColorChoice("Personalizada", selected));
            index = _color.Items.Count - 1;
        }
        _color.Items.Add(OtherColor);
        _color.SelectedIndex = index;
        _loading = false;
    }

    private Color MarkerColor => (_color.SelectedItem as ColorChoice)?.Color ?? Color.FromArgb(0xE5, 0x39, 0x35);

    private bool UsesImage => _shape.SelectedIndex == MarkerIcon.Shapes.Count;

    private void DrawColor(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _color.Items[e.Index] is not ColorChoice choice)
            return;
        e.DrawBackground();
        var side = e.Bounds.Height - 6;
        var swatch = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top + 3, side, side);
        if (choice.Color is { } color)
        {
            using var fill = new SolidBrush(color);
            e.Graphics.FillRectangle(fill, swatch);
            e.Graphics.DrawRectangle(Pens.Gray, swatch);
        }
        var text = Rectangle.FromLTRB(swatch.Right + 6, e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, choice.Name, e.Font ?? Font, text, e.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private void ColorChanged()
    {
        if (_loading)
            return;
        if (ReferenceEquals(_color.SelectedItem, OtherColor))
        {
            using var dialog = new ColorDialog { FullOpen = true, Color = MarkerColor };
            // Back to the list either way: with the new color, or with the first one when cancelled.
            var picked = dialog.ShowDialog(FindForm()) == DialogResult.OK ? Color.FromArgb(255, dialog.Color) : (Color?)null;
            FillColors(picked ?? (_color.Items[0] as ColorChoice)!.Color!.Value);
        }
        ShowIcon();
        SaveOptions();
    }

    private void ShapeChanged()
    {
        if (_loading)
            return;
        if (UsesImage && !ChooseImage())
        {
            _loading = true;
            _shape.SelectedIndex = _lastShape;
            _loading = false;
        }
        _lastShape = _shape.SelectedIndex;
        ShowColorField();
        ShowIcon();
        SaveOptions();
    }

    /// <summary>The color only paints the drawn shapes, so it is hidden while the icon is an image.</summary>
    private void ShowColorField()
    {
        var shown = !UsesImage;
        _color.Visible = shown;
        if (_iconFields.GetControlFromPosition(1, 0) is { } label)
            label.Visible = shown;
        _iconFields.ColumnStyles[1] = shown ? new ColumnStyle(SizeType.Percent, 100f / 3) : new ColumnStyle(SizeType.Absolute, 0);
    }

    private bool ChooseImage()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione a imagem do ícone",
            Filter = "Imagens (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return false;
        try
        {
            MarkerIcon.FromFile(dialog.FileName, 1);
        }
        catch (AppException e)
        {
            Dialogs.Warn(this, "Essa imagem não pode ser usada", e.Message);
            return false;
        }
        _imagePath = dialog.FileName;
        return true;
    }

    private void ShowIcon()
    {
        if (!IsHandleCreated)
            return;
        var pixels = LogicalToDeviceUnits(40);
        Image? picture = null;
        try
        {
            if (UsesImage && _imagePath is not null)
            {
                using var loaded = MarkerIcon.Load(_imagePath);
                picture = new Bitmap(loaded, Fit(loaded.Size, pixels));
            }
            else if (!UsesImage)
            {
                picture = MarkerIcon.Draw(MarkerIcon.Shapes[_shape.SelectedIndex].Shape, MarkerColor, pixels);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            Trace.TraceWarning("Não foi possível mostrar o ícone: {0}", e.Message);
        }
        _preview.Image?.Dispose();
        _preview.Image = picture;
    }

    private static Size Fit(Size size, int box)
    {
        var ratio = (double)box / Math.Max(size.Width, size.Height);
        return new Size(Math.Max(1, (int)(size.Width * ratio)), Math.Max(1, (int)(size.Height * ratio)));
    }

    private void SaveOptions()
    {
        if (_loading || _shape.SelectedIndex < 0 || _size.SelectedIndex < 0)
            return;
        var saved = Config.LoadKmzOptions();
        var options = new KmzOptions(UsesImage ? saved.Shape : MarkerIcon.Shapes[_shape.SelectedIndex].Shape,
            MarkerColor.ToArgb() & 0xFFFFFF, _size.SelectedIndex, _order.SelectedIndex == 1);
        if (options != saved)
            Config.TrySave(() => Config.SaveKmzOptions(options));
    }

    // ---- Output --------------------------------------------------------------------------------

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _generate.Text = busy ? "Gerando..." : "Gerar KMZ";
        _generate.Enabled = !busy && _current.Points.Count > 0;
        _progress.Visible = busy;
        if (FindForm() is { } window)
            window.UseWaitCursor = busy;
    }

    private async void Generate()
    {
        if (_busy)
            return;
        Reparse();
        var points = _current.Points;
        if (_reading.Pending.Count > 0 && !_fromText.Checked)
        {
            var heading = $"{_reading.Pending.Count:N0} endereço(s) ainda não localizado(s)";
            var text = $"Eles não entram no mapa até serem localizados (leva cerca de {Duration(AddressLocator.Estimate(_reading.Pending.Count))}).";
            var answer = points.Count == 0
                ? Dialogs.Choose(this, TaskDialogIcon.Warning, heading, text, "Localizar agora", "Cancelar") is 0 ? 0 : 2
                : Dialogs.Choose(this, TaskDialogIcon.Warning, heading, text, "Localizar agora", $"Gerar só com os {points.Count} prontos", "Cancelar");
            if (answer == 0)
            {
                Locate();
                return;
            }
            if (answer != 1 || points.Count == 0)
                return;
        }
        if (points.Count == 0)
            return;
        if (_current.Problems.Count > 0 && !Dialogs.Confirm(this, "Algumas entradas não foram entendidas",
                $"{_current.Problems.Count} entrada(s) com problema ficarão de fora do mapa:\n\n" +
                string.Join("\n", _current.Problems.Take(6)) + (_current.Problems.Count > 6 ? "\n..." : string.Empty),
                $"Gerar com {(points.Count == 1 ? "1 ponto" : points.Count + " pontos")}", "Voltar e corrigir", warning: true))
            return;

        var fromSheet = !_fromText.Checked && _sheetPath is not null;
        using var dialog = new SaveFileDialog
        {
            Title = "Salvar KMZ",
            InitialDirectory = fromSheet ? Path.GetDirectoryName(_sheetPath) : "",
            FileName = (fromSheet ? Path.GetFileNameWithoutExtension(_sheetPath) : "pontos") + ".kmz",
            DefaultExt = "kmz",
            Filter = "Google Earth (*.kmz)|*.kmz",
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        var output = dialog.FileName;
        var scale = Scales[_size.SelectedIndex];
        var image = UsesImage ? _imagePath : null;
        var shape = UsesImage ? MarkerShape.Pin : MarkerIcon.Shapes[_shape.SelectedIndex].Shape;
        var color = MarkerColor;

        SetBusy(true);
        _openOutput.Enabled = _showOutput.Enabled = false;
        try
        {
            await Task.Run(() =>
            {
                var icon = image is null ? MarkerIcon.Create(shape, color, scale) : MarkerIcon.FromFile(image, scale);
                KmzWriter.Write(output, Path.GetFileNameWithoutExtension(output), points, icon);
            });
            _lastOutput = output;
            _openOutput.Enabled = _showOutput.Enabled = true;
            SetBusy(false);
            var answer = Dialogs.Choose(this, TaskDialogIcon.ShieldSuccessGreenBar, "KMZ gerado",
                $"{(points.Count == 1 ? "1 ponto" : points.Count + " pontos")} no mapa.\n\n{output}", "Abrir", "Mostrar na pasta", "Fechar");
            if (answer == 0)
                OpenOutput();
            else if (answer == 1)
                Ui.ShowInFolder(output);
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao gerar o KMZ: {0}", e.InnerException ?? e);
            Dialogs.Warn(this, "Não foi possível gerar o KMZ", e.Message);
        }
        catch (Exception e)
        {
            Trace.TraceError("Falha ao gerar o KMZ: {0}", e);
            Dialogs.Error(this, "Ocorreu um erro inesperado ao gerar o KMZ", "Se continuar acontecendo, avise quem mantém o programa.", e.ToString());
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenOutput()
    {
        if (_lastOutput is not null && Ui.Open(_lastOutput) is { } error)
            Dialogs.Warn(this, "Não foi possível abrir o KMZ",
                "Nenhum programa está associado a arquivos .kmz. Instale o Google Earth, ou importe o arquivo no " +
                "Google Maps (Meus mapas > Importar).", error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _locating?.Cancel();
            _typing.Dispose();
            _tips.Dispose();
            _preview.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
