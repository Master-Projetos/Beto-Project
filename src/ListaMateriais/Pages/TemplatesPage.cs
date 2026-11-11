using System.IO.Compression;

namespace ListaMateriais.Pages;

/// <summary>The files the app fills in: the spreadsheet template and the Word templates of the TCR and the TCE.</summary>
internal sealed class TemplatesPage : Page
{
    private string? _path;
    private readonly Label _name = new() { AutoSize = true, Font = Ui.BoldFont };
    private readonly WrapLabel _full = Ui.Hint(string.Empty);
    private readonly Button _open = Ui.Secondary(new Button { Text = "Abrir no Excel", Height = 30 });
    private readonly Button _builtIn = Ui.Secondary(new Button { Text = "Usar modelo padrão", Height = 30 });
    private readonly Dictionary<ReportKind, WrapLabel> _word = new()
    {
        [ReportKind.Tcr] = Ui.Hint(string.Empty),
        [ReportKind.Tce] = Ui.Hint(string.Empty),
    };

    public override string Title => "M&odelos";
    public override string Glyph => "\uE8B7";

    /// <summary>The spreadsheet template in use; null when none is set.</summary>
    public string? Spreadsheet => _path;

    public TemplatesPage()
    {
        var change = Ui.Secondary(new Button { Text = "Trocar modelo...", Height = 30 });
        change.Click += (_, _) => ChooseSpreadsheet();
        _open.Click += (_, _) =>
        {
            if (_path is not null && Ui.Open(_path) is { } error)
                Dialogs.Warn(this, "Não foi possível abrir o modelo",
                    "Verifique se o Excel (ou outro programa para .xlsx) está instalado.", error);
        };
        _builtIn.Enabled = Config.HasBuiltInTemplate;
        _builtIn.Click += (_, _) => UseBuiltIn();

        var stack = new Stack()
            .Title("Modelos", "Os arquivos que o programa preenche. Linhas e códigos novos podem ser adicionados " +
                              "direto no modelo, mantendo o layout.")
            .Caption("Planilha de materiais (Excel)")
            .Add(_name)
            .Add(_full, bottom: 8)
            .Add(Stack.Row(30, change, _open, _builtIn))
            .Caption("TCR (Word)", top: 24)
            .Add(_word[ReportKind.Tcr], bottom: 8)
            .Add(WordButtons(ReportKind.Tcr))
            .Caption("TCE (Word)", top: 24)
            .Add(_word[ReportKind.Tce], bottom: 8)
            .Add(WordButtons(ReportKind.Tce))
            .Spring();
        SetContent(stack, 440);

        SetSpreadsheet(Config.LoadTemplatePath(), save: false);
        ShowWord(ReportKind.Tcr);
        ShowWord(ReportKind.Tce);
    }

    /// <summary>
    /// The spreadsheet template to generate with, asking for one when none is set or the file is gone.
    /// Null when the user gave up.
    /// </summary>
    public string? RequireSpreadsheet()
    {
        if (_path is null || !File.Exists(_path))
            SetSpreadsheet(Config.LoadTemplatePath(), save: false);
        if (_path is not null && File.Exists(_path))
            return _path;

        Dialogs.Info(this, "Selecione a planilha modelo", _path is null
            ? "Nenhum modelo de planilha foi configurado."
            : $"O modelo \"{Path.GetFileName(_path)}\" não foi encontrado em:\n{Path.GetDirectoryName(_path)}");
        return ChooseSpreadsheet() ? _path : null;
    }

    /// <summary>The Word template of a report, asking for the blank document when there is none. Null when the user gave up.</summary>
    public byte[]? RequireWord(ReportKind kind)
    {
        if (Config.LoadWordTemplate(kind) is { } template)
            return template;
        var name = KindName(kind);
        Dialogs.Info(this, $"Selecione o modelo do {name}",
            $"Nenhum modelo do Word para o {name} foi configurado. Escolha o documento {name} em branco.");
        return ChooseWord(kind) ? Config.LoadWordTemplate(kind) : null;
    }

    private static string KindName(ReportKind kind) => kind.ToString().ToUpperInvariant();

    private void SetSpreadsheet(string? path, bool save = true)
    {
        if (path is null)
        {
            _path = null;
            _name.Text = "Nenhum modelo selecionado";
            _full.Text = "Clique em \"Trocar modelo...\" para escolher a planilha base.";
            _open.Enabled = false;
            return;
        }
        _path = Path.GetFullPath(path);
        _name.Text = Config.IsBuiltInTemplate(_path) ? "Modelo padrão do programa" : Path.GetFileName(_path);
        _full.Text = _path;
        _open.Enabled = true;
        if (save)
            Config.TrySave(() => Config.SaveTemplatePath(_path));
    }

    private bool ChooseSpreadsheet()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione o modelo",
            Filter = "Planilha Excel (*.xlsx)|*.xlsx",
            InitialDirectory = _path is null ? "" : Path.GetDirectoryName(_path),
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return false;
        try
        {
            XlsxWriter.CheckTemplate(dialog.FileName);
        }
        catch (AppException e)
        {
            Dialogs.Warn(this, "Esse arquivo não serve como modelo", e.Message);
            return false;
        }
        SetSpreadsheet(dialog.FileName);
        return true;
    }

    /// <summary>Switches to the template built into the executable, unpacking it when needed.</summary>
    private void UseBuiltIn()
    {
        try
        {
            var unpack = !File.Exists(Config.BuiltInTemplateFile);
            if (!unpack && Config.BuiltInTemplateWasEdited())
            {
                var answer = Dialogs.Choose(this, TaskDialogIcon.Warning, "O modelo padrão foi alterado",
                    "Ele foi editado depois de instalado. Voltar ao original apaga essas alterações.",
                    "Manter minhas alterações", "Voltar ao original", "Cancelar");
                if (answer is not (0 or 1))
                    return;
                unpack = answer == 1;
            }
            SetSpreadsheet(unpack ? Config.UnpackBuiltInTemplate() : Config.BuiltInTemplateFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Não foi possível extrair o modelo padrão: {0}", e.Message);
            Dialogs.Warn(this, "Não foi possível preparar o modelo padrão",
                $"Local: {Config.BuiltInTemplateFile}\n\nSe ele estiver aberto no Excel, feche-o e tente de novo.", e.Message);
        }
    }

    private TableLayoutPanel WordButtons(ReportKind kind)
    {
        var change = Ui.Secondary(new Button { Text = "Trocar modelo...", Height = 30 });
        var reset = Ui.Secondary(new Button { Text = "Usar modelo padrão", Height = 30 });
        change.Click += (_, _) => ChooseWord(kind);
        reset.Click += (_, _) =>
        {
            Config.TrySave(() => Config.SaveWordTemplatePath(kind, null));
            ShowWord(kind);
        };
        // Keeps the two buttons the same width as the first two of the spreadsheet row.
        return Stack.Row(30, change, reset, new Panel());
    }

    private void ShowWord(ReportKind kind) => _word[kind].Text =
        Config.WordTemplatePath(kind) ?? (Config.HasBuiltInWordTemplate(kind)
            ? "Modelo padrão do programa"
            : "Nenhum modelo. O programa pede o documento em branco na primeira vez que gerar.");

    private bool ChooseWord(ReportKind kind)
    {
        using var dialog = new OpenFileDialog
        {
            Title = $"Selecione o modelo {KindName(kind)} (Word)",
            Filter = "Documento Word (*.docx)|*.docx",
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return false;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(Files.ReadAllBytesShared(dialog.FileName)), ZipArchiveMode.Read);
            if (zip.GetEntry("word/document.xml") is null)
                throw new InvalidDataException();
        }
        catch (InvalidDataException)
        {
            Dialogs.Warn(this, "Esse arquivo não serve como modelo",
                $"\"{Path.GetFileName(dialog.FileName)}\" não é um documento Word (.docx) válido.");
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialogs.Warn(this, "Não foi possível abrir o modelo", "Confira se o arquivo ainda existe e se você pode lê-lo.", e.Message);
            return false;
        }
        Config.TrySave(() => Config.SaveWordTemplatePath(kind, dialog.FileName));
        ShowWord(kind);
        return true;
    }
}
