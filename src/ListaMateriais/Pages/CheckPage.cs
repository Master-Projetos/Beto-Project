using System.Diagnostics;
using System.Globalization;
using System.Text;
using ListaMateriais.Kmz;

namespace ListaMateriais.Pages;

/// <summary>
/// Reviews a network KMZ before it goes out: lists what looks wrong (cables without a name or drawn
/// twice, loose ends, boxes no cable reaches...) and can save the list as markers to open in Google Earth.
/// </summary>
internal sealed class CheckPage : Page
{
    private readonly DropZone _drop = new() { Prompt = "Arraste o KMZ do projeto aqui" };
    private readonly ListView _folders = new()
    {
        View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Height = 190, MinimumSize = new Size(0, 120),
    };
    private readonly NumericUpDown _join = new() { Minimum = 1, Maximum = 100, Width = 60, TextAlign = HorizontalAlignment.Right };
    private readonly CheckBox _spans = new() { Text = "Avisar vão entre vértices acima de", AutoSize = true, Margin = new Padding(18, 3, 4, 0) };
    private readonly NumericUpDown _maxSpan = new() { Minimum = 10, Maximum = 1000, Increment = 5, Width = 66, TextAlign = HorizontalAlignment.Right };
    private readonly ListView _issues = new()
    {
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MinimumSize = new Size(0, 160),
        ShowItemToolTips = true,
    };
    private readonly ImageList _icons = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private readonly WrapLabel _summary = new() { ForeColor = Ui.Muted };
    private readonly Button _save = Ui.Primary(new Button { Text = "Salvar KMZ com os problemas marcados", Font = Ui.BigButtonFont, Height = 44, Enabled = false });
    private readonly Button _copy = Ui.Secondary(new Button { Text = "Copiar lista (para o Excel)", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly Button _openOutput = Ui.Secondary(new Button { Text = "Abrir no Google Earth", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly Label _feedback = new() { AutoSize = true, ForeColor = Ui.Good, Margin = new Padding(12, 8, 0, 0) };

    private string? _path;
    private string? _lastOutput;
    private MapContent? _content;
    private string[] _folderNames = [];
    private HashSet<string> _skipped = [];
    private List<MapIssue> _found = [];
    private bool _loading;
    private bool _busy;

    public override string Title => "Confe&rir KMZ";
    public override string Glyph => "";

    /// <summary>The button Enter triggers while this page is showing.</summary>
    public Button MainButton => _save;

    public CheckPage()
    {
        var (join, spans, maxSpan) = Config.LoadCheckOptions();
        _join.Value = join;
        _spans.Checked = spans;
        _maxSpan.Value = maxSpan;
        _maxSpan.Enabled = spans;

        _folders.Columns.Add("Pasta", 380);
        _folders.Columns.Add("Itens", 70, HorizontalAlignment.Right);
        _folders.ClientSizeChanged += (_, _) => _folders.Columns[0].Width = Math.Max(160, _folders.ClientSize.Width - _folders.Columns[1].Width - 4);

        _icons.Images.Add("erro", Dot(IssueKmzWriter.ErrorColor));
        _icons.Images.Add("aviso", Dot(IssueKmzWriter.WarningColor));
        _issues.SmallImageList = _icons;
        _issues.Columns.Add("Problema", 150);
        _issues.Columns.Add("Item", 200);
        _issues.Columns.Add("Detalhe", 320);
        _issues.Columns.Add("Pasta", 220);
        // The detail takes what the other columns leave.
        _issues.ClientSizeChanged += (_, _) => _issues.Columns[2].Width = Math.Max(200,
            _issues.ClientSize.Width - _issues.Columns[0].Width - _issues.Columns[1].Width - _issues.Columns[3].Width - 4);

        SetContent(new Stack()
            .Title("Conferir mapa (KMZ)", "Procura o que costuma passar batido antes de entregar o projeto: cabo sem nome ou " +
                                          "desenhado duas vezes, ponta de cabo solta, caixa sem cabo, sobra de desenho, coordenada errada.")
            .Add(_drop, bottom: 14)
            .Caption("Pastas a conferir")
            .Add(Ui.Hint("Desmarque a rede que só está no mapa para referência (backbone, rede existente de outra área): " +
                         "os cabos do projeto podem se ligar nela, mas os problemas dela não entram na lista."), bottom: 6)
            .Add(_folders)
            .Add(Stack.Flow(
                new Label { Text = "Ponta ligada a até", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _join,
                new Label { Text = "m de uma caixa ou cabo", AutoSize = true, Margin = new Padding(4, 6, 0, 0) },
                _spans, _maxSpan, new Label { Text = "m", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }), top: 8)
            .Caption("O que foi encontrado", top: 12)
            .Add(_summary, bottom: 6)
            .Add(_issues, fill: true)
            .Add(Ui.Hint("Clique duas vezes num item para copiar as coordenadas; cole na busca do Google Earth para ir até o lugar."), top: 4)
            .Add(_save, top: 10)
            .Add(Stack.Flow(_openOutput, _copy, _feedback), top: 8), 720);

        _drop.Activated += (_, _) => Browse();
        _folders.ItemChecked += (_, e) =>
        {
            if (_loading || e.Item.Tag is not string folder)
                return;
            if (e.Item.Checked)
                _skipped.Remove(folder);
            else
                _skipped.Add(folder);
            Recheck();
        };
        _join.ValueChanged += (_, _) => OptionsChanged();
        _maxSpan.ValueChanged += (_, _) => OptionsChanged();
        _spans.CheckedChanged += (_, _) =>
        {
            _maxSpan.Enabled = _spans.Checked;
            OptionsChanged();
        };
        _issues.ItemActivate += (_, _) => CopyCoordinates();
        _save.Click += (_, _) => Save();
        _copy.Click += (_, _) => CopyList();
        _openOutput.Click += (_, _) => OpenOutput();
        ShowSummary();
    }

    private static Bitmap Dot(Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 2, 2, 11, 11);
        return bitmap;
    }

    private void OptionsChanged()
    {
        Config.TrySave(() => Config.SaveCheckOptions((int)_join.Value, _spans.Checked, (int)_maxSpan.Value));
        Recheck();
    }

    public override bool Accepts(string path) => HasExtension(path, ".kmz") || HasExtension(path, ".kml");

    public override void Browse()
    {
        if (_busy)
            return;
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione o mapa a conferir",
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
            Dialogs.Warn(this, "Nenhum ponto, linha ou área no mapa", $"\"{Path.GetFileName(path)}\" não tem nada desenhado para conferir.");
            return;
        }

        _path = path;
        _content = content;
        _skipped = [];
        _openOutput.Enabled = false;
        _feedback.Text = string.Empty;
        _drop.SetFile(Path.GetFileName(path), $"{content.Features.Count(f => f.Kind == FeatureKind.Line):N0} linhas, " +
                                              $"{content.Features.Count(f => f.Kind == FeatureKind.Point):N0} pontos");
        FillFolders();
    }

    private void FillFolders()
    {
        if (_content is null)
            return;
        var counts = _content.Features.GroupBy(f => f.Folder).ToDictionary(g => g.Key, g => g.Count());
        _folderNames = [.. counts.Keys.OrderBy(f => f, StringComparer.CurrentCulture)];
        _loading = true;
        _folders.BeginUpdate();
        _folders.Items.Clear();
        foreach (var folder in _folderNames)
        {
            var shown = Shown(folder);
            _folders.Items.Add(new ListViewItem([shown, counts[folder].ToString("N0")]) { Checked = !_skipped.Contains(folder), Tag = folder });
        }
        _folders.EndUpdate();
        _loading = false;
        Recheck();
    }

    /// <summary>
    /// The folder without the one every folder sits in (usually named after the project, so it says
    /// nothing); what is right in that one shows as "(pasta principal)".
    /// </summary>
    private string Shown(string folder)
    {
        if (folder.Length == 0)
            return "(sem pasta)";
        if (_folderNames.Length < 2 || _folderNames.Any(f => f.Length == 0))
            return folder;
        var top = _folderNames[0].Split(" / ")[0];
        if (!_folderNames.All(f => f == top || f.StartsWith(top + " / ", StringComparison.Ordinal)))
            return folder;
        return folder == top ? "(pasta principal)" : folder[(top.Length + 3)..];
    }

    private void Recheck()
    {
        if (_loading || _content is null)
            return;
        var options = new CheckOptions((double)_join.Value, _spans.Checked ? (double)_maxSpan.Value : null);
        var skipped = _skipped;
        _found = MapChecker.Check(_content, options, f => !skipped.Contains(f.Folder));

        _issues.BeginUpdate();
        _issues.Items.Clear();
        foreach (var issue in _found)
        {
            _issues.Items.Add(new ListViewItem([issue.Problem, issue.Item, issue.Detail, Shown(issue.Folder)])
            {
                ImageKey = issue.Level == IssueLevel.Error ? "erro" : "aviso",
                ToolTipText = issue.Detail,
                Tag = issue,
            });
        }
        _issues.EndUpdate();
        _feedback.Text = string.Empty;
        ShowSummary();
    }

    private void ShowSummary()
    {
        if (_content is null)
        {
            _summary.Text = "Escolha um KMZ ou KML para começar.";
            _summary.ForeColor = Ui.Muted;
            _save.Enabled = _copy.Enabled = false;
            return;
        }
        var errors = _found.Count(i => i.Level == IssueLevel.Error);
        var warnings = _found.Count - errors;
        if (_found.Count == 0)
        {
            _summary.Text = _skipped.Count == _folderNames.Length
                ? "Nenhuma pasta marcada para conferir."
                : "Nada encontrado. O mapa parece em ordem.";
            _summary.ForeColor = Ui.Good;
        }
        else
        {
            var parts = new List<string>();
            if (errors > 0)
                parts.Add(errors == 1 ? "1 erro" : $"{errors:N0} erros");
            if (warnings > 0)
                parts.Add(warnings == 1 ? "1 aviso" : $"{warnings:N0} avisos");
            _summary.Text = string.Join(" e ", parts) + ". " +
                            (errors > 0 ? "Os erros (vermelho) quase sempre precisam de correção; os avisos (amarelo) valem uma olhada."
                                : "Avisos (amarelo) valem uma olhada; podem estar certos.");
            _summary.ForeColor = errors > 0 ? Ui.Bad : Ui.Muted;
        }
        _save.Enabled = !_busy && _found.Count > 0;
        _copy.Enabled = _found.Count > 0;
    }

    private void SetBusy(bool busy, string text = "Salvando...")
    {
        _busy = busy;
        _save.Text = busy ? text : "Salvar KMZ com os problemas marcados";
        _save.Enabled = !busy && _found.Count > 0;
        if (FindForm() is { } window)
            window.UseWaitCursor = busy;
    }

    private static string Coordinates(GeoPoint p) =>
        $"{p.Latitude.ToString("0.000000", CultureInfo.InvariantCulture)}, {p.Longitude.ToString("0.000000", CultureInfo.InvariantCulture)}";

    private void CopyCoordinates()
    {
        if (_issues.SelectedItems.Count == 0 || _issues.SelectedItems[0].Tag is not MapIssue issue)
            return;
        var text = Coordinates(issue.Where);
        if (TryCopy(text))
            _feedback.Text = $"Coordenadas copiadas: {text}";
    }

    private void CopyList()
    {
        var text = new StringBuilder("Tipo\tProblema\tItem\tDetalhe\tPasta\tLatitude\tLongitude\r\n");
        foreach (var issue in _found)
        {
            text.Append(issue.Level == IssueLevel.Error ? "Erro" : "Aviso").Append('\t')
                .Append(Cell(issue.Problem)).Append('\t').Append(Cell(issue.Item)).Append('\t').Append(Cell(issue.Detail)).Append('\t')
                .Append(Cell(issue.Folder)).Append('\t')
                .Append(issue.Where.Latitude.ToString("0.000000", CultureInfo.GetCultureInfo("pt-BR"))).Append('\t')
                .Append(issue.Where.Longitude.ToString("0.000000", CultureInfo.GetCultureInfo("pt-BR"))).Append("\r\n");
        }
        if (TryCopy(text.ToString()))
            _feedback.Text = $"{_found.Count:N0} linhas copiadas. Cole no Excel.";
    }

    private static string Cell(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException e)
        {
            Dialogs.Warn(this, "Não foi possível copiar", "Outro programa está usando a área de transferência. Tente de novo.", e.Message);
            return false;
        }
    }

    private async void Save()
    {
        if (_busy || _found.Count == 0 || _path is null)
            return;
        using var dialog = new SaveFileDialog
        {
            Title = "Salvar os problemas como KMZ",
            InitialDirectory = Path.GetDirectoryName(_path),
            FileName = Path.GetFileNameWithoutExtension(_path) + " - conferência.kmz",
            DefaultExt = "kmz",
            Filter = "Google Earth (*.kmz)|*.kmz",
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        var output = dialog.FileName;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            Dialogs.Warn(this, "Escolha outro nome", "A conferência não pode substituir o mapa original: salve com outro nome.");
            return;
        }
        var issues = _found.ToList();
        var title = Path.GetFileNameWithoutExtension(output);
        SetBusy(true);
        try
        {
            await Task.Run(() => IssueKmzWriter.Write(output, title, issues));
            _lastOutput = output;
            _openOutput.Enabled = true;
            SetBusy(false);
            var answer = Dialogs.Choose(this, TaskDialogIcon.ShieldSuccessGreenBar, "Conferência salva",
                $"{issues.Count:N0} marcadores. Abra este arquivo junto com o mapa original no Google Earth: " +
                $"cada marcador fica em cima do que corrigir.\n\n{output}", "Abrir os dois", "Mostrar na pasta", "Fechar");
            if (answer == 0)
                OpenOutput();
            else if (answer == 1)
                Ui.ShowInFolder(output);
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao salvar a conferência: {0}", e.InnerException ?? e);
            Dialogs.Warn(this, "Não foi possível salvar", e.Message);
        }
        catch (Exception e)
        {
            Trace.TraceError("Falha ao salvar a conferência: {0}", e);
            Dialogs.Error(this, "Ocorreu um erro inesperado ao salvar a conferência", "Se continuar acontecendo, avise quem mantém o programa.", e.ToString());
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Opens the original map and the markers, so they show together in Google Earth.</summary>
    private void OpenOutput()
    {
        if (_lastOutput is null)
            return;
        var error = (_path is not null && File.Exists(_path) ? Ui.Open(_path) : null) ?? Ui.Open(_lastOutput);
        if (error is not null)
            Dialogs.Warn(this, "Não foi possível abrir o KMZ",
                "Nenhum programa está associado a arquivos .kmz. Instale o Google Earth, ou importe os arquivos no " +
                "Google Maps (Meus mapas > Importar).", error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _icons.Dispose();
        base.Dispose(disposing);
    }
}
