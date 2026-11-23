using System.Diagnostics;
using ListaMateriais.Deck;

namespace ListaMateriais.Pages;

/// <summary>
/// Makes a PowerPoint out of a spreadsheet with no other input: the app works out what the numbers are,
/// finds what is worth saying and draws it in the look of the example deck. A sentence ("Vendas de Lavras
/// no 1º trimestre") narrows it down, but is optional.
/// </summary>
internal sealed class PresentationPage : Page
{
    private readonly DropZone _drop = new() { Prompt = "Arraste a planilha aqui (Excel ou CSV)" };
    private readonly WrapLabel _understood = new() { ForeColor = Ui.Muted };
    private readonly ComboBox _sheet = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly Label _sheetLabel = new() { Text = "Tabela:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
    private readonly TextBox _description = new()
    {
        Dock = DockStyle.Top, PlaceholderText = "Opcional. Ex.: Vendas de Lavras no 1º trimestre de 2026 · Ocupação em Divinópolis e Varginha · último trimestre",
    };
    private readonly ListView _slides = new()
    {
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MinimumSize = new Size(0, 160),
        ShowItemToolTips = true,
    };
    private readonly WrapLabel _notes = new() { ForeColor = Ui.Muted };
    private readonly Button _generate = Ui.Primary(new Button { Text = "Gerar apresentação", Font = Ui.BigButtonFont, Height = 44, Enabled = false });
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Height = 4, Visible = false };
    private readonly Button _openOutput = Ui.Secondary(new Button { Text = "Abrir arquivo", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly Button _showOutput = Ui.Secondary(new Button { Text = "Mostrar na pasta", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly System.Windows.Forms.Timer _typing = new() { Interval = 400 };

    private string? _path;
    private string? _lastOutput;
    private IReadOnlyList<DataSet> _sets = [];
    private DataProfile? _profile;
    private DeckModel? _deck;
    private bool _busy;
    private bool _loading;

    public override string Title => "&Apresentação";
    public override string Glyph => "";

    /// <summary>The button Enter triggers while this page is showing.</summary>
    public Button MainButton => _generate;

    public PresentationPage()
    {
        _slides.Columns.Add("Slide", 230);
        _slides.Columns.Add("O que mostra", 520);
        _slides.ClientSizeChanged += (_, _) => _slides.Columns[1].Width = Math.Max(200, _slides.ClientSize.Width - _slides.Columns[0].Width - 4);

        SetContent(new Stack()
            .Title("Apresentação (PowerPoint)", "Jogue uma planilha e receba a apresentação pronta: o programa descobre o que são os " +
                                                "números, calcula totais, líderes, concentração, evolução e andamento, e monta os slides no " +
                                                "padrão da empresa. Nada é enviado para fora do computador.")
            .Add(_drop, bottom: 8)
            .Add(_understood, bottom: 6)
            .Add(Stack.Flow(_sheetLabel, _sheet), bottom: 8)
            .Caption("Sobre o que é (opcional)")
            .Add(_description, bottom: 2)
            .Add(Ui.Hint("Um período (\"2026\", \"jan a mar\", \"último trimestre\") ou nomes da planilha (uma cidade, um vendedor) " +
                         "filtram os dados; a frase vira o título."), bottom: 10)
            .Caption("Slides que serão gerados")
            .Add(_slides, fill: true)
            .Add(_notes, top: 4)
            .Add(_generate, top: 10)
            .Add(_progress, top: 4)
            .Add(Stack.Flow(_openOutput, _showOutput), top: 8), 640);

        _drop.Activated += (_, _) => Browse();
        _sheet.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading)
                Analyze();
        };
        _description.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
        };
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            Preview();
        };
        _generate.Click += (_, _) => Generate();
        _openOutput.Click += (_, _) => OpenOutput();
        _showOutput.Click += (_, _) =>
        {
            if (_lastOutput is not null)
                Ui.ShowInFolder(_lastOutput);
        };
        _sheetLabel.Visible = _sheet.Visible = false;
        Show(null);
    }

    public override bool Accepts(string path) => HasExtension(path, ".xlsx") || HasExtension(path, ".csv");

    public override void Browse()
    {
        if (_busy)
            return;
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione a planilha com os dados",
            Filter = "Planilhas (*.xlsx;*.csv)|*.xlsx;*.csv",
            InitialDirectory = _path is null ? "" : Path.GetDirectoryName(_path),
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            Open(dialog.FileName);
    }

    public override async void Open(string path)
    {
        if (_busy)
            return;
        SetBusy(true, "Lendo a planilha...");
        IReadOnlyList<DataSet> sets;
        try
        {
            sets = await Task.Run(() => DataSource.Read(path));
        }
        catch (AppException e)
        {
            SetBusy(false);
            Dialogs.Warn(this, "Não foi possível ler a planilha", e.Message);
            return;
        }
        SetBusy(false);
        if (sets.Count == 0)
        {
            Dialogs.Warn(this, "A planilha está vazia", $"\"{Path.GetFileName(path)}\" não tem nenhuma tabela com dados.");
            return;
        }

        _path = path;
        _sets = sets;
        _openOutput.Enabled = _showOutput.Enabled = false;
        _drop.SetFile(Path.GetFileName(path), sets.Count == 1 ? $"{sets[0].Rows.Count:N0} linhas" : $"{sets.Count} tabelas");
        _loading = true;
        _sheet.Items.Clear();
        foreach (var set in sets)
            _sheet.Items.Add($"{set.Name} ({set.Rows.Count:N0} linhas)");
        var best = DataSource.Best(sets);
        _sheet.SelectedIndex = best is null ? 0 : sets.ToList().IndexOf(best);
        _sheetLabel.Visible = _sheet.Visible = sets.Count > 1;
        _loading = false;
        Analyze();
    }

    private async void Analyze()
    {
        if (_sheet.SelectedIndex < 0 || _sheet.SelectedIndex >= _sets.Count)
            return;
        var set = _sets[_sheet.SelectedIndex];
        SetBusy(true, "Analisando...");
        try
        {
            _profile = await Task.Run(() => DataProfile.Analyze(set));
        }
        finally
        {
            SetBusy(false);
        }
        _understood.Text = Understood(_profile);
        Preview();
    }

    /// <summary>One sentence on how the table was read, so a wrong guess is seen before the deck is made.</summary>
    private static string Understood(DataProfile p)
    {
        var parts = new List<string>
        {
            p.Measure is null ? "conta os registros (não achei uma coluna de valores)" : $"soma \"{p.Measure.Name}\"" + (p.Format.Money ? " (em R$)" : string.Empty),
        };
        if (p.Part is { } part)
            parts.Add($"compara com \"{part.Name}\"");
        if (p.Dimensions.Count > 0)
            parts.Add("agrupa por " + string.Join(", ", p.Dimensions.Take(3).Select(d => $"\"{p.NameOf(d)}\"")));
        if (p.Date is { } date)
            parts.Add($"datas em \"{date.Name}\"");
        if (p.Status is { } status)
            parts.Add($"andamento em \"{p.NameOf(status)}\"");
        return $"Entendi: {string.Join("; ", parts)}. {p.Rows.Count:N0} registros.";
    }

    private void Preview()
    {
        if (_profile is null || _path is null)
            return;
        try
        {
            var brief = DeckBuilder.Read(_description.Text, _profile);
            _deck = DeckBuilder.Build(_profile, brief, Path.GetFileNameWithoutExtension(_path));
            Show(_deck);
        }
        catch (AppException e)
        {
            _deck = null;
            Show(null, e.Message);
        }
    }

    private void Show(DeckModel? deck, string? error = null)
    {
        _slides.BeginUpdate();
        _slides.Items.Clear();
        if (deck is not null)
        {
            for (var i = 0; i < deck.Slides.Count; i++)
                _slides.Items.Add(new ListViewItem([$"{i + 1}. {deck.Slides[i].Title}", deck.Slides[i].Finding]) { ToolTipText = deck.Slides[i].Finding });
        }
        _slides.EndUpdate();
        _notes.Text = error ?? (deck is null ? "Escolha uma planilha para começar." : string.Join(" ", deck.Notes));
        _notes.ForeColor = error is null ? Ui.Muted : Ui.Bad;
        _generate.Enabled = !_busy && deck is not null;
    }

    private void SetBusy(bool busy, string text = "Gerando...")
    {
        _busy = busy;
        _generate.Text = busy ? text : "Gerar apresentação";
        _generate.Enabled = !busy && _deck is not null;
        _progress.Visible = busy;
        if (FindForm() is { } window)
            window.UseWaitCursor = busy;
    }

    private async void Generate()
    {
        if (_busy || _deck is not { } deck || _path is null)
            return;
        using var dialog = new SaveFileDialog
        {
            Title = "Salvar apresentação",
            InitialDirectory = Path.GetDirectoryName(_path),
            FileName = Files.SafeName(deck.Title) + ".pptx",
            DefaultExt = "pptx",
            Filter = "Apresentação do PowerPoint (*.pptx)|*.pptx",
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        var output = dialog.FileName;
        SetBusy(true);
        _openOutput.Enabled = _showOutput.Enabled = false;
        try
        {
            var template = Config.LoadPresentationTemplate();
            await Task.Run(() => PptxWriter.Write(output, deck, template));
            _lastOutput = output;
            _openOutput.Enabled = _showOutput.Enabled = true;
            SetBusy(false);
            var answer = Dialogs.Choose(this, TaskDialogIcon.ShieldSuccessGreenBar, "Apresentação gerada",
                $"{deck.Slides.Count} slides{(template is null ? " (visual padrão: nenhum modelo da empresa encontrado)" : string.Empty)}.\n\n{output}",
                "Abrir", "Mostrar na pasta", "Fechar");
            if (answer == 0)
                OpenOutput();
            else if (answer == 1)
                Ui.ShowInFolder(output);
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao gerar a apresentação: {0}", e.InnerException ?? e);
            Dialogs.Warn(this, "Não foi possível gerar a apresentação", e.Message);
        }
        catch (Exception e)
        {
            Trace.TraceError("Falha ao gerar a apresentação: {0}", e);
            Dialogs.Error(this, "Ocorreu um erro inesperado ao gerar a apresentação", "Se continuar acontecendo, avise quem mantém o programa.", e.ToString());
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenOutput()
    {
        if (_lastOutput is not null && Ui.Open(_lastOutput) is { } error)
            Dialogs.Warn(this, "Não foi possível abrir a apresentação", "Verifique se o PowerPoint está instalado.", error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _typing.Dispose();
        base.Dispose(disposing);
    }
}
