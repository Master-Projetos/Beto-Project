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
    private readonly CheckBox _header = new() { Text = "A primeira linha tem os nomes das colunas", AutoSize = true, Checked = true };

    private readonly PictureBox _preview = new() { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.CenterImage, Margin = new Padding(0, 4, 14, 0) };
    private readonly ComboBox _shape = List();
    private readonly ComboBox _color = new() { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly ComboBox _size = List("Pequeno", "Médio", "Grande");

    private readonly Label _status = new() { AutoSize = true, ForeColor = Ui.Muted };
    private readonly ListView _points = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly WrapLabel _problems = new() { ForeColor = Ui.Bad };
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
        _sheetPanel
            .Add(Stack.Flow(chooseSheet, _sheetFile), bottom: 10)
            .Add(Fields(("Aba", _sheet), ("Coluna do nome", _nameColumn), ("Coluna da latitude", _latitudeColumn),
                ("Coluna da longitude", _longitudeColumn)), bottom: 6)
            .Add(_header);

        var icon = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true };
        icon.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        icon.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        icon.Controls.Add(_preview, 0, 0);
        icon.Controls.Add(Fields(("Formato", _shape), ("Cor", _color), ("Tamanho no mapa", _size)), 1, 0);

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
        foreach (var column in new[] { _nameColumn, _latitudeColumn, _longitudeColumn })
            column.SelectedIndexChanged += (_, _) => Reparse();
        _header.CheckedChanged += (_, _) => Reparse();

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
        var header = table.Rows[0];
        var (name, latitude, longitude) = SheetPoints.GuessColumns(table);
        var named = name >= 0 || latitude >= 0 || longitude >= 0;

        _nameColumn.Items.Clear();
        _latitudeColumn.Items.Clear();
        _longitudeColumn.Items.Clear();
        _nameColumn.Items.Add("(sem nome: numerar os pontos)");
        for (var i = 0; i < table.Columns; i++)
        {
            var letter = SheetTable.ColumnLetter(i);
            var title = i < header.Length ? header[i] : null;
            var label = title is null ? letter : $"{letter} — {(title.Length > 40 ? title[..40] + "…" : title)}";
            _nameColumn.Items.Add(label);
            _latitudeColumn.Items.Add(label);
            _longitudeColumn.Items.Add(label);
        }
        _nameColumn.SelectedIndex = name + 1;
        _latitudeColumn.SelectedIndex = latitude;
        _longitudeColumn.SelectedIndex = longitude;
        _header.Checked = named || (latitude < 0 && longitude < 0);
        _loading = false;
        Reparse();
    }

    private PointList ReadSheet()
    {
        if (Table is not { } table || _latitudeColumn.SelectedIndex < 0 || _longitudeColumn.SelectedIndex < 0)
            return PointList.Empty;
        return SheetPoints.Read(table, _nameColumn.SelectedIndex - 1, _latitudeColumn.SelectedIndex,
            _longitudeColumn.SelectedIndex, _header.Checked);
    }

    /// <summary>Reads the points again and shows what was found, so mistakes show before the file is made.</summary>
    private void Reparse()
    {
        _typing.Stop();
        if (_loading)
            return;
        var order = _order.SelectedIndex == 1 ? CoordinateOrder.LongitudeFirst : CoordinateOrder.LatitudeFirst;
        _current = _fromText.Checked ? PointText.Parse(_text.Text, order) : ReadSheet();
        if (_fromText.Checked)
            SaveOptions();

        var points = _current.Points;
        _points.BeginUpdate();
        _points.Items.Clear();
        foreach (var point in points.Take(MaxPreviewRows))
            _points.Items.Add(new ListViewItem([point.Name, Degrees(point.Latitude), Degrees(point.Longitude)]));
        _points.EndUpdate();

        var problems = _current.Problems;
        _status.Text = points.Count == 0 && problems.Count == 0
            ? _fromText.Checked
                ? "Digite ou cole as coordenadas acima."
                : Table is null ? "Escolha a planilha." : "Escolha as colunas da latitude e da longitude."
            : (points.Count == 1 ? "1 ponto pronto" : $"{points.Count} pontos prontos")
              + (points.Count > MaxPreviewRows ? $" (mostrando os primeiros {MaxPreviewRows})" : string.Empty)
              + (problems.Count == 0 ? "." : problems.Count == 1 ? " · 1 entrada com problema:" : $" · {problems.Count} entradas com problema:");
        const int shown = 4;
        _problems.Text = string.Join("\n", problems.Take(shown))
                         + (problems.Count > shown ? $"\n... e mais {problems.Count - shown}." : string.Empty);
        _problems.Visible = problems.Count > 0;
        _generate.Enabled = !_busy && points.Count > 0;
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
        _color.Enabled = !UsesImage;
        ShowIcon();
        SaveOptions();
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
            _typing.Dispose();
            _preview.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
