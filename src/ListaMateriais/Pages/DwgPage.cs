using System.Diagnostics;
using System.Globalization;
using ListaMateriais.Cad;
using ListaMateriais.Kmz;

namespace ListaMateriais.Pages;

/// <summary>
/// Turns a DWG (or DXF) drawing into a KMZ for Google Earth: reads the drawing, places it on the map from its UTM
/// coordinates, and writes one folder per layer with the colors of the drawing.
/// </summary>
internal sealed class DwgPage : Page
{
    private static readonly double[] UnitScales = [1.0, 0.01, 0.001];

    private readonly DropZone _drop = new() { Prompt = "Arraste o DWG (ou DXF) aqui" };
    private readonly RadioButton _utm = new() { Text = "UTM", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
    private readonly RadioButton _degrees = new() { Text = "Já está em graus (X = longitude, Y = latitude)", AutoSize = true, Margin = new Padding(14, 4, 0, 0) };
    private readonly ComboBox _zone = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly ComboBox _hemisphere = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly ComboBox _unit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly ComboBox _datum = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly CheckBox _blocks = new() { Text = "Desenhar também o conteúdo dos blocos (símbolos), não só um ponto em cada", AutoSize = true };
    private readonly ListView _layers = new()
    {
        View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, MinimumSize = new Size(0, 130),
    };
    private readonly WrapLabel _where = new() { ForeColor = Ui.Muted };
    private readonly WrapLabel _summary = new() { ForeColor = Ui.Muted };
    private readonly Button _generate = Ui.Primary(new Button { Text = "Gerar KMZ", Font = Ui.BigButtonFont, Height = 44, Enabled = false });
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Height = 4, Visible = false };
    private readonly Button _openOutput = Ui.Secondary(new Button { Text = "Abrir arquivo", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly Button _showOutput = Ui.Secondary(new Button { Text = "Mostrar na pasta", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });

    private string? _path;
    private string? _lastOutput;
    private CadDrawing? _drawing;
    private bool _loading;
    private bool _busy;
    private bool _saysSad69;

    public override string Title => "&DWG para KMZ";
    public override string Glyph => "";

    /// <summary>The button Enter triggers while this page is showing.</summary>
    public Button MainButton => _generate;

    public DwgPage()
    {
        var (degrees, zone, south, unit, drawBlocks) = Config.LoadDwgOptions();
        _utm.Checked = !degrees;
        _degrees.Checked = degrees;
        for (var z = 17; z <= 25; z++)
        {
            // Zone z spans longitudes 6z-186 to 6z-180 (west of Greenwich).
            _zone.Items.Add($"{z}  ({186 - 6 * z}° a {180 - 6 * z}° O)");
        }
        for (var z = 1; z <= 60; z++)
        {
            if (z is < 17 or > 25)
                _zone.Items.Add($"{z}");
        }
        _zone.SelectedIndex = zone is >= 17 and <= 25 ? zone - 17 : 9 + Enumerable.Range(1, 60).Where(z => z is < 17 or > 25).ToList().IndexOf(zone);
        _hemisphere.Items.AddRange(["Sul", "Norte"]);
        _hemisphere.SelectedIndex = south ? 0 : 1;
        _unit.Items.AddRange(["Metros", "Centímetros", "Milímetros"]);
        _unit.SelectedIndex = unit;
        _blocks.Checked = drawBlocks;
        // Not remembered: each drawing says (or does not) which datum it is in.
        _datum.Items.AddRange(["SIRGAS 2000 / WGS 84", "SAD 69 (desenhos antigos)"]);
        _datum.SelectedIndex = 0;

        _layers.Columns.Add("Camada", 280);
        _layers.Columns.Add("Elementos", 90, HorizontalAlignment.Right);
        _layers.Columns.Add("No desenho", 160);
        _layers.ClientSizeChanged += (_, _) => _layers.Columns[2].Width =
            Math.Max(100, _layers.ClientSize.Width - _layers.Columns[0].Width - _layers.Columns[1].Width);

        SetContent(new Stack()
            .Title("DWG para KMZ", "Coloca um desenho do AutoCAD no mapa do Google Earth. Lê DWG do AutoCAD 2000 a 2018 " +
                                   "(e DXF em texto) aqui mesmo, sem programas externos nem internet.")
            .Add(_drop, bottom: 14)
            .Caption("Coordenadas do desenho")
            .Add(Stack.Flow(_utm, new Label { Text = "Zona:", AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, _zone,
                new Label { Text = "Hemisfério:", AutoSize = true, Margin = new Padding(12, 7, 6, 0) }, _hemisphere), bottom: 2)
            .Add(_degrees, bottom: 6)
            .Add(Stack.Flow(new Label { Text = "O desenho está em:", AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, _unit,
                new Label { Text = "Datum:", AutoSize = true, Margin = new Padding(12, 7, 6, 0) }, _datum), bottom: 4)
            .Add(Ui.Hint("Para o Brasil: São Paulo e a maior parte do Sudeste ficam na zona 23 Sul; o Rio Grande do Sul, na 22; " +
                         "o Nordeste, entre 23 e 25. Se o desenho cair no lugar errado, troque a zona."), bottom: 8)
            .Add(_where, bottom: 8)
            .Caption("Camadas")
            .Add(_layers, fill: true)
            .Add(_blocks, top: 8)
            .Add(_summary, top: 8)
            .Add(_generate, top: 10)
            .Add(_progress, top: 4)
            .Add(Stack.Flow(_openOutput, _showOutput), top: 8), 760);

        _drop.Activated += (_, _) => Browse();
        // The two options sit in different panels, so they are kept exclusive by hand.
        _utm.CheckedChanged += (_, _) =>
        {
            if (_utm.Checked && _degrees.Checked)
                _degrees.Checked = false;
            OptionsChanged();
        };
        _degrees.CheckedChanged += (_, _) =>
        {
            if (_degrees.Checked && _utm.Checked)
                _utm.Checked = false;
            OptionsChanged();
        };
        _zone.SelectedIndexChanged += (_, _) => OptionsChanged();
        _hemisphere.SelectedIndexChanged += (_, _) => OptionsChanged();
        _unit.SelectedIndexChanged += (_, _) => OptionsChanged();
        _blocks.CheckedChanged += (_, _) => OptionsChanged();
        _datum.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading)
                ShowPlace();
        };
        _layers.ItemChecked += (_, _) =>
        {
            if (!_loading)
                ShowPlace();
        };
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

    private int ZoneNumber => _zone.SelectedIndex switch
    {
        < 0 => 23,
        <= 8 => 17 + _zone.SelectedIndex,
        _ => Enumerable.Range(1, 60).Where(z => z is < 17 or > 25).ElementAt(_zone.SelectedIndex - 9),
    };

    private CadGeoSettings Settings => new(_degrees.Checked, ZoneNumber, _hemisphere.SelectedIndex == 0,
        UnitScales[Math.Max(0, _unit.SelectedIndex)], _blocks.Checked, _datum.SelectedIndex == 1);

    private HashSet<string> CheckedLayers =>
        [.. _layers.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!)];

    private void ShowMode()
    {
        _zone.Enabled = _hemisphere.Enabled = _utm.Checked;
    }

    private void OptionsChanged()
    {
        ShowMode();
        Config.TrySave(() => Config.SaveDwgOptions(_degrees.Checked, ZoneNumber, _hemisphere.SelectedIndex == 0,
            Math.Max(0, _unit.SelectedIndex), _blocks.Checked));
        ShowPlace();
    }

    public override bool Accepts(string path) => HasExtension(path, ".dwg") || HasExtension(path, ".dxf");

    public override void Browse()
    {
        if (_busy)
            return;
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione o desenho",
            Filter = "Desenhos do AutoCAD (*.dwg;*.dxf)|*.dwg;*.dxf",
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
        CadDrawing drawing;
        try
        {
            drawing = await Task.Run(() => HasExtension(path, ".dxf") ? DxfReader.Read(path) : DwgReader.Read(path));
        }
        catch (AppException e)
        {
            SetBusy(false);
            Dialogs.Warn(this, "Não foi possível ler o desenho", e.Message);
            return;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Trace.TraceError("Falha ao ler o desenho: {0}", e);
            SetBusy(false);
            Dialogs.Error(this, "Ocorreu um erro inesperado ao ler o desenho",
                "Se continuar acontecendo, avise quem mantém o programa.", e.ToString());
            return;
        }
        SetBusy(false);
        if (drawing.Model.Count == 0)
        {
            Dialogs.Warn(this, "Nada para colocar no mapa",
                $"\"{Path.GetFileName(path)}\" não tem linhas, pontos, textos ou símbolos no espaço de desenho (modelo)." +
                (drawing.Skipped > 0 ? $"\n\n{drawing.Skipped} elemento(s) são de tipos que o programa ainda não desenha (hachuras, cotas, 3D)." : string.Empty));
            return;
        }

        _path = path;
        _drawing = drawing;
        _drop.SetFile(Path.GetFileName(path), $"{drawing.Version} · {drawing.Model.Count:N0} elementos");
        _openOutput.Enabled = _showOutput.Enabled = false;
        _saysSad69 = drawing.MentionsSad69();
        _loading = true;
        _datum.SelectedIndex = _saysSad69 ? 1 : 0;
        _loading = false;
        FillLayers();
    }

    private void FillLayers()
    {
        if (_drawing is null)
            return;
        _loading = true;
        _layers.BeginUpdate();
        _layers.Items.Clear();
        foreach (var group in _drawing.Model.GroupBy(e => e.Layer).OrderByDescending(g => g.Count()))
        {
            var hidden = _drawing.Layers.TryGetValue(group.Key, out var info) && info.Hidden;
            _layers.Items.Add(new ListViewItem([group.Key, group.Count().ToString("N0"), hidden ? "desligada ou congelada" : string.Empty])
                { Checked = !hidden, Tag = group.Key, ForeColor = hidden ? Ui.Muted : _layers.ForeColor });
        }
        _layers.EndUpdate();
        _loading = false;
        ShowPlace();
    }

    /// <summary>Says where the drawing lands with the coordinate choices made, so a wrong zone is seen before the file is made.</summary>
    private void ShowPlace()
    {
        _where.ForeColor = Ui.Muted;
        if (_drawing is null)
        {
            _where.Text = string.Empty;
            ShowSummary();
            return;
        }
        var layers = CheckedLayers;
        var settings = Settings;
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        var centers = new Dictionary<string, CadPoint?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in _drawing.Model.Where(e => layers.Contains(e.Layer)))
        {
            // Block references count where they show on the map, which is not always where they are inserted.
            IReadOnlyList<CadPoint> points = entity.Kind == CadKind.Insert
                ? [CadMapBuilder.MarkerOf(_drawing, settings, entity, centers)]
                : entity.Points;
            foreach (var p in points)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }
        }
        if (minX > maxX)
        {
            _where.Text = "Nenhuma camada marcada.";
            ShowSummary();
            return;
        }

        (double Lat, double Lon) At(double x, double y) => CadMapBuilder.ToLatLon(settings, x, y);
        var (lat, lon) = At((minX + maxX) / 2, (minY + maxY) / 2);
        var (lat1, lon1) = At(minX, minY);
        var (lat2, lon2) = At(maxX, maxY);
        var widthKm = Math.Sqrt(Math.Pow((lat2 - lat1) * 111.32, 2) + Math.Pow((lon2 - lon1) * 111.32 * Math.Cos(lat * Math.PI / 180), 2));
        var valid = double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
        if (!valid)
        {
            _where.ForeColor = Ui.Bad;
            _where.Text = "Com essas escolhas o desenho não cabe no mapa (coordenadas fora do mundo). Confira a zona e a unidade.";
        }
        else
        {
            var text = $"No mapa, o desenho fica perto de {lat.ToString("0.0000", CultureInfo.InvariantCulture)}°, " +
                       $"{lon.ToString("0.0000", CultureInfo.InvariantCulture)}° e tem uns {widthKm:N1} km de ponta a ponta.";
            if (widthKm > 300)
            {
                _where.ForeColor = Ui.Bad;
                text += " Isso é grande demais para um projeto de rede: confira a unidade do desenho.";
            }
            if (_saysSad69)
                text += " A moldura do desenho indica o datum antigo (SAD 69 / Chuá)" +
                        (settings.Sad69 ? ", já selecionado." : ": sem selecioná-lo, o mapa fica uns 60 m fora do lugar.");
            _where.Text = text;
        }
        ShowSummary();
    }

    private void ShowSummary()
    {
        if (_drawing is null)
        {
            _summary.Text = "Escolha um DWG ou DXF para começar.";
            _generate.Enabled = false;
            return;
        }
        var notes = new List<string>();
        if (_drawing.Skipped > 0)
            notes.Add($"{_drawing.Skipped:N0} elemento(s) de tipos que o programa ainda não desenha (hachuras, cotas, chamadas com seta, sólidos 3D) ficam de fora.");
        if (_drawing.Unreadable > 0)
            notes.Add($"{_drawing.Unreadable:N0} objeto(s) do arquivo não puderam ser lidos.");
        _summary.Text = string.Join("\n", notes);
        _generate.Enabled = !_busy && _layers.CheckedItems.Count > 0;
    }

    private void SetBusy(bool busy, string text = "Gerando...")
    {
        _busy = busy;
        _generate.Text = busy ? text : "Gerar KMZ";
        _generate.Enabled = !busy && _drawing is not null && _layers.CheckedItems.Count > 0;
        _progress.Visible = busy;
        if (FindForm() is { } window)
            window.UseWaitCursor = busy;
    }

    private async void Generate()
    {
        if (_busy || _drawing is null || _path is null)
            return;
        using var dialog = new SaveFileDialog
        {
            Title = "Salvar o mapa",
            InitialDirectory = Path.GetDirectoryName(_path),
            FileName = Path.GetFileNameWithoutExtension(_path) + ".kmz",
            DefaultExt = "kmz",
            Filter = "Google Earth (*.kmz)|*.kmz|KML (*.kml)|*.kml",
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        var output = dialog.FileName;
        var drawing = _drawing;
        var settings = Settings;
        var layers = CheckedLayers;
        var title = Path.GetFileNameWithoutExtension(output);

        SetBusy(true);
        _openOutput.Enabled = _showOutput.Enabled = false;
        try
        {
            var map = await Task.Run(() =>
            {
                var built = CadMapBuilder.Build(drawing, settings, layers);
                if (built.Features.Count == 0)
                    throw new AppException("Nada do desenho coube no mapa com essas escolhas. Confira a zona UTM, o hemisfério e a unidade.");
                CadKmzWriter.Write(output, title, built, MarkerIcon.Create(MarkerShape.Circle, Color.White, 1.0).Png);
                return built;
            });
            _lastOutput = output;
            _openOutput.Enabled = _showOutput.Enabled = true;
            SetBusy(false);
            var lines = map.Features.Count(f => f.Kind == GeoKind.Line);
            var answer = Dialogs.Choose(this, TaskDialogIcon.ShieldSuccessGreenBar, "Mapa gerado",
                $"{lines:N0} linhas, {map.Features.Count - lines:N0} pontos e textos em {map.Features.Select(f => f.Layer).Distinct().Count()} camada(s).\n\n{output}",
                "Abrir", "Mostrar na pasta", "Fechar");
            if (answer == 0)
                OpenOutput();
            else if (answer == 1)
                Ui.ShowInFolder(output);
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao gerar o mapa do desenho: {0}", e.InnerException ?? e);
            Dialogs.Warn(this, "Não foi possível gerar o mapa", e.Message);
        }
        catch (Exception e)
        {
            Trace.TraceError("Falha ao gerar o mapa do desenho: {0}", e);
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
            Dialogs.Warn(this, "Não foi possível abrir o mapa",
                "Nenhum programa está associado a arquivos .kmz. Instale o Google Earth, ou importe o arquivo no " +
                "Google Maps (Meus mapas > Importar).", error);
    }
}
