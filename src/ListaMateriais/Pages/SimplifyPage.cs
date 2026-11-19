using System.Diagnostics;
using ListaMateriais.Kmz;

namespace ListaMateriais.Pages;

/// <summary>
/// Turns a busy technical KMZ (thousands of points, lines in short pieces, dozens of colors, tables of
/// data) into a map anyone can read. By default only the cables stay, thick and in one color; the other
/// mode keeps every group, each in its own color, with a legend.
/// </summary>
internal sealed class SimplifyPage : Page
{
    private static readonly int[] Widths = [6, 10, 14];

    private readonly DropZone _drop = new() { Prompt = "Arraste o KMZ aqui" };
    private readonly RadioButton _cablesOnly = new() { Text = "Só os cabos, numa cor só", AutoSize = true, Margin = new Padding(0, 0, 18, 0) };
    private readonly RadioButton _everything = new() { Text = "Tudo, um grupo por pasta e cor", AutoSize = true };
    private readonly ComboBox _color = new() { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, Width = 150 };
    private readonly ComboBox _width = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly FlowLayoutPanel _cableOptions;
    private readonly ListView _groups = new()
    {
        View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, MinimumSize = new Size(0, 120),
    };
    private readonly WrapLabel _groupsHint = Ui.Hint(string.Empty);
    private readonly ImageList _swatches = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _legend = new() { Text = "Desenhar a legenda no canto do mapa", AutoSize = true, Checked = true };
    private readonly WrapLabel _summary = new() { ForeColor = Ui.Muted };
    private readonly Button _generate = Ui.Primary(new Button { Text = "Gerar mapa simples", Font = Ui.BigButtonFont, Height = 44, Enabled = false });
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Height = 4, Visible = false };
    private readonly Button _openOutput = Ui.Secondary(new Button { Text = "Abrir arquivo", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly Button _showOutput = Ui.Secondary(new Button { Text = "Mostrar na pasta", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });

    private string? _path;
    private string? _lastOutput;
    private MapContent? _content;
    private MapGrouping? _grouping;
    private SimpleMap? _map;

    /// <summary>Per group of <see cref="_grouping"/>: the name it gets and whether it is on the map.</summary>
    private string[] _names = [];
    private bool[] _shown = [];
    private bool _loading;
    private bool _busy;

    public override string Title => "Simplificar &KMZ";
    public override string Glyph => "";

    /// <summary>The button Enter triggers while this page is showing.</summary>
    public Button MainButton => _generate;

    public SimplifyPage()
    {
        var (cablesOnly, colorName, width) = Config.LoadSimplifyOptions();
        _cablesOnly.Checked = cablesOnly;
        _everything.Checked = !cablesOnly;
        foreach (var (name, _) in MapColors.Choices)
            _color.Items.Add(char.ToUpperInvariant(name[0]) + name[1..]);
        var colorIndex = MapColors.Choices.ToList().FindIndex(c => c.Name == colorName);
        _color.SelectedIndex = colorIndex >= 0 ? colorIndex : MapColors.Choices.ToList().FindIndex(c => c.Name == MapColors.NeonName);
        _width.Items.AddRange(["Grossos", "Bem grossos", "Grossíssimos"]);
        var widthIndex = Array.IndexOf(Widths, width);
        _width.SelectedIndex = widthIndex >= 0 ? widthIndex : 1;
        _cableOptions = Stack.Flow(
            new Label { Text = "Cor:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _color,
            new Label { Text = "Espessura:", AutoSize = true, Margin = new Padding(18, 6, 6, 0) }, _width);

        _level.Items.AddRange(["Pouco (só junta e limpa)", "Médio", "Bastante (bem resumido)"]);
        _level.SelectedIndex = 1;
        _groups.SmallImageList = _swatches;
        _groups.Columns.Add("Nome no mapa novo", 240);
        _groups.Columns.Add("O que é", 90);
        _groups.Columns.Add("Quantos", 80, HorizontalAlignment.Right);
        _groups.Columns.Add("No mapa original", 220);
        _groups.ClientSizeChanged += (_, _) => _groups.Columns[3].Width =
            Math.Max(120, _groups.ClientSize.Width - _groups.Columns[0].Width - _groups.Columns[1].Width - _groups.Columns[2].Width);

        SetContent(new Stack()
            .Title("Simplificar mapa (KMZ)", "Transforma um KMZ técnico, cheio de pontos, pedaços de linha e cores, num mapa " +
                                             "que qualquer pessoa entende. Descrições, tabelas e códigos ficam de fora.")
            .Add(_drop, bottom: 14)
            .Caption("O que fica no mapa")
            .Add(Stack.Flow(_cablesOnly, _everything), bottom: 6)
            .Add(_cableOptions, bottom: 10)
            .Add(_groupsHint, bottom: 6)
            .Add(_groups, fill: true)
            .Add(Stack.Flow(new Label { Text = "Quanto simplificar:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _level), top: 10)
            .Add(_legend, top: 6)
            .Add(_summary, top: 8)
            .Add(_generate, top: 10)
            .Add(_progress, top: 4)
            .Add(Stack.Flow(_openOutput, _showOutput), top: 8), 680);

        _drop.Activated += (_, _) => Browse();
        _cablesOnly.CheckedChanged += (_, _) =>
        {
            ShowMode();
            FillGroups();
            SaveOptions();
        };
        _color.DrawItem += DrawColor;
        _color.SelectedIndexChanged += (_, _) =>
        {
            Recompute();
            SaveOptions();
        };
        _width.SelectedIndexChanged += (_, _) =>
        {
            Recompute();
            SaveOptions();
        };
        _groups.ItemChecked += (_, e) =>
        {
            if (_loading || e.Item.Tag is not int group)
                return;
            _shown[group] = e.Item.Checked;
            Recompute();
        };
        _groups.AfterLabelEdit += (_, e) =>
        {
            if (e.Label is null || _groups.Items[e.Item].Tag is not int group)
                return;
            if (string.IsNullOrWhiteSpace(e.Label))
            {
                e.CancelEdit = true;
                return;
            }
            _names[group] = e.Label.Trim();
            // The list shows the new name only after this event: recompute once it is in.
            BeginInvoke(Recompute);
        };
        _level.SelectedIndexChanged += (_, _) => Recompute();
        _legend.CheckedChanged += (_, _) => Recompute();
        _generate.Click += (_, _) => Generate();
        _openOutput.Click += (_, _) => OpenOutput();
        _showOutput.Click += (_, _) =>
        {
            if (_lastOutput is not null)
                Ui.ShowInFolder(_lastOutput);
        };
        ShowMode();
        ShowSummary();
    }

    private bool CablesOnly => _cablesOnly.Checked;

    private Color CableColor => MapColors.Choices[Math.Max(0, _color.SelectedIndex)].Color;

    private void ShowMode()
    {
        _cableOptions.Visible = CablesOnly;
        _groups.LabelEdit = !CablesOnly;
        _groupsHint.Text = CablesOnly
            ? "Todas as linhas do mapa entram como cabos; pontos (CTO, CEO, caixas, postes) e áreas ficam de fora. " +
              "Desmarque as linhas que não são cabos."
            : "Cada linha vira um item da legenda. Para trocar o nome, selecione e aperte F2. Itens com o mesmo " +
              "nome viram um só. Desmarque o que não precisa aparecer.";
    }

    private void SaveOptions()
    {
        if (_color.SelectedIndex < 0 || _width.SelectedIndex < 0)
            return;
        Config.TrySave(() => Config.SaveSimplifyOptions(CablesOnly, MapColors.Choices[_color.SelectedIndex].Name, Widths[_width.SelectedIndex]));
    }

    private void DrawColor(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;
        e.DrawBackground();
        var side = e.Bounds.Height - 6;
        var swatch = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top + 3, side, side);
        using (var fill = new SolidBrush(MapColors.Choices[e.Index].Color))
            e.Graphics.FillRectangle(fill, swatch);
        e.Graphics.DrawRectangle(Pens.Gray, swatch);
        var text = Rectangle.FromLTRB(swatch.Right + 6, e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, _color.Items[e.Index]?.ToString(), e.Font ?? Font, text, e.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    public override bool Accepts(string path) => HasExtension(path, ".kmz") || HasExtension(path, ".kml");

    public override void Browse()
    {
        if (_busy)
            return;
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione o mapa a simplificar",
            Filter = "Mapas do Google Earth (*.kmz;*.kml)|*.kmz;*.kml",
            InitialDirectory = _path is null ? "" : Path.GetDirectoryName(_path),
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            Open(dialog.FileName);
    }

    public override async void Open(string path)
    {
        if (_busy)
            return;
        SetBusy(true, "Lendo...");
        MapContent content;
        try
        {
            content = await Task.Run(() => KmlReader.Read(path));
        }
        catch (AppException e)
        {
            SetBusy(false);
            Dialogs.Warn(this, "Não foi possível ler o mapa", e.Message);
            return;
        }
        SetBusy(false);
        if (content.Features.Count == 0)
        {
            Dialogs.Warn(this, "Nenhum ponto, linha ou área no mapa",
                $"\"{Path.GetFileName(path)}\" não tem nada desenhado que dê para simplificar." +
                (content.Skipped > 0 ? $"\n\n{content.Skipped} item(ns) são imagens, fotos ou links para outros arquivos." : string.Empty));
            return;
        }

        _path = path;
        _content = content;
        _grouping = MapGrouping.Of(content);
        _names = [.. _grouping.Groups.Select(g => g.SuggestedName)];
        _shown = [.. _grouping.Groups.Select(_ => true)];
        _drop.SetFile(Path.GetFileName(path), Describe(content));
        _openOutput.Enabled = _showOutput.Enabled = false;
        FillGroups();
    }

    /// <summary>Lists the groups that matter for the mode: only the lines when the map keeps only the cables.</summary>
    private void FillGroups()
    {
        if (_grouping is null)
            return;
        _loading = true;
        _groups.BeginUpdate();
        _groups.Items.Clear();
        for (var i = 0; i < _grouping.Groups.Count; i++)
        {
            var group = _grouping.Groups[i];
            if (CablesOnly && group.Kind != FeatureKind.Line)
                continue;
            var origin = group.Folder.Length == 0 ? "(sem pasta)" : group.Folder;
            if (group.ColorName.Length > 0)
                origin += $" · {group.ColorName}";
            _groups.Items.Add(new ListViewItem([_names[i], KindName(group.Kind), group.Count.ToString("N0"), origin])
                { Checked = _shown[i], Tag = i });
        }
        _groups.EndUpdate();
        _loading = false;
        Recompute();
    }

    private static string KindName(FeatureKind kind) => kind switch
    {
        FeatureKind.Point => "Pontos",
        FeatureKind.Line => "Linhas",
        _ => "Áreas",
    };

    private static string Describe(MapContent content)
    {
        var points = content.Features.Count(f => f.Kind == FeatureKind.Point);
        var lines = content.Features.Count(f => f.Kind == FeatureKind.Line);
        var areas = content.Features.Count(f => f.Kind == FeatureKind.Area);
        var colors = content.Features.Where(f => f.Color is not null).Select(f => f.Color!.Value.ToArgb() & 0xFFFFFF).Distinct().Count();
        return Count(points, lines, areas) + $", {colors} {(colors == 1 ? "cor" : "cores")}";
    }

    private static string Count(int points, int lines, int areas)
    {
        var parts = new List<string>();
        if (points > 0 || lines == 0)
            parts.Add(points == 1 ? "1 ponto" : $"{points:N0} pontos");
        parts.Add(lines == 1 ? "1 linha" : $"{lines:N0} linhas");
        if (areas > 0)
            parts.Add(areas == 1 ? "1 área" : $"{areas:N0} áreas");
        return string.Join(", ", parts);
    }

    /// <summary>Simplifies again with the current choices and shows the result in numbers and colors.</summary>
    private void Recompute()
    {
        if (_loading || _content is null || _grouping is null || _width.SelectedIndex < 0)
            return;
        var level = (SimplifyLevel)_level.SelectedIndex;
        var width = Widths[_width.SelectedIndex];
        _map = CablesOnly
            ? MapSimplifier.Cables(_content, _grouping, _shown, level, CableColor, width)
            : MapSimplifier.Simplify(_content, _grouping,
                [.. _names.Select((name, i) => new GroupChoice(name, _shown[i]))], level) with { LineWidth = width };

        // Each row shows the color its group gets on the new map.
        foreach (ListViewItem item in _groups.Items)
        {
            var layer = !item.Checked ? null
                : CablesOnly ? _map.Layers.FirstOrDefault()
                : _map.Layers.FirstOrDefault(l => string.Equals(l.Name, item.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
            item.ImageKey = Swatch(layer?.Color);
            item.ForeColor = item.Checked ? _groups.ForeColor : Ui.Muted;
        }
        ShowSummary();
    }

    private string Swatch(Color? color)
    {
        var key = color is { } c ? c.ToArgb().ToString("x8") : "none";
        if (_swatches.Images.ContainsKey(key))
            return key;
        var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (color is { } fill)
            {
                using var brush = new SolidBrush(fill);
                g.FillEllipse(brush, 1, 1, 13, 13);
            }
            else
            {
                g.DrawEllipse(Pens.Silver, 1, 1, 13, 13);
            }
        }
        _swatches.Images.Add(key, bitmap);
        return key;
    }

    private void ShowSummary()
    {
        if (_content is null || _map is null)
        {
            _summary.Text = "Escolha um KMZ ou KML para começar.";
            _generate.Enabled = false;
            return;
        }
        var skipped = _content.Skipped > 0
            ? $"\n{_content.Skipped} item(ns) que não são pontos, linhas ou áreas (fotos, imagens, links) ficam de fora."
            : string.Empty;
        string after;
        if (_map.Layers.Count == 0)
            after = CablesOnly && _content.Features.All(f => f.Kind != FeatureKind.Line)
                ? "Este mapa não tem nenhuma linha (cabo). Escolha \"Tudo\" para manter os pontos."
                : "Nada marcado: marque pelo menos um item da lista.";
        else if (CablesOnly)
            after = $"Antes: {Describe(_content)}.\nDepois: {(_map.Lines == 1 ? "1 cabo" : $"{_map.Lines:N0} cabos")} " +
                    $"(os pedaços que se encontram viram um só), em {_color.Text.ToLowerInvariant()}.";
        else
            after = $"Antes: {Describe(_content)}.\nDepois: {Count(_map.Points, _map.Lines, _map.Areas)}, " +
                    $"{_map.Layers.Count} {(_map.Layers.Count == 1 ? "cor" : "cores")}.";
        _summary.Text = after + skipped;
        _generate.Enabled = !_busy && _map.Layers.Count > 0;
    }

    private void SetBusy(bool busy, string text = "Gerando...")
    {
        _busy = busy;
        _generate.Text = busy ? text : "Gerar mapa simples";
        _generate.Enabled = !busy && _map is { Layers.Count: > 0 };
        _progress.Visible = busy;
        if (FindForm() is { } window)
            window.UseWaitCursor = busy;
    }

    private async void Generate()
    {
        if (_busy || _map is not { Layers.Count: > 0 } map || _path is null)
            return;
        using var dialog = new SaveFileDialog
        {
            Title = "Salvar mapa simples",
            InitialDirectory = Path.GetDirectoryName(_path),
            FileName = Path.GetFileNameWithoutExtension(_path) + (CablesOnly ? " - cabos.kmz" : " - simples.kmz"),
            DefaultExt = "kmz",
            Filter = "Google Earth (*.kmz)|*.kmz",
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        var output = dialog.FileName;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            Dialogs.Warn(this, "Escolha outro nome", "O mapa simples não pode substituir o original: salve com outro nome.");
            return;
        }
        var legend = _legend.Checked;
        var simple = map with { Title = Path.GetFileNameWithoutExtension(output) };
        var done = CablesOnly
            ? (simple.Lines == 1 ? "1 cabo" : $"{simple.Lines:N0} cabos") + $" em {_color.Text.ToLowerInvariant()}."
            : $"{Count(simple.Points, simple.Lines, simple.Areas)} em {simple.Layers.Count} {(simple.Layers.Count == 1 ? "cor" : "cores")}.";

        SetBusy(true);
        _openOutput.Enabled = _showOutput.Enabled = false;
        try
        {
            await Task.Run(() => SimpleKmzWriter.Write(output, simple, legend));
            _lastOutput = output;
            _openOutput.Enabled = _showOutput.Enabled = true;
            SetBusy(false);
            var answer = Dialogs.Choose(this, TaskDialogIcon.ShieldSuccessGreenBar, "Mapa simples gerado",
                $"{done}\n\n{output}", "Abrir", "Mostrar na pasta", "Fechar");
            if (answer == 0)
                OpenOutput();
            else if (answer == 1)
                Ui.ShowInFolder(output);
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao gerar o mapa simples: {0}", e.InnerException ?? e);
            Dialogs.Warn(this, "Não foi possível gerar o mapa", e.Message);
        }
        catch (Exception e)
        {
            Trace.TraceError("Falha ao gerar o mapa simples: {0}", e);
            Dialogs.Error(this, "Ocorreu um erro inesperado ao gerar o mapa", "Se continuar acontecendo, avise quem mantém o programa.", e.ToString());
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
            _swatches.Dispose();
        base.Dispose(disposing);
    }
}
