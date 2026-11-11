using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ListaMateriais.Pages;

/// <summary>
/// Takes the project document (the Route Map report in PDF, a TCE/TCR in Word or saved as PDF) and
/// generates the material spreadsheet or the TCR/TCE Word document from it.
/// </summary>
internal sealed class GeneratePage : Page
{
    private readonly TemplatesPage _templates;
    private readonly HeadersPage _headers;

    private string? _path;
    private string? _lastOutput;
    private bool _busy;

    /// <summary>The chosen PDF turned out to be a TCE/TCR: only the spreadsheet can come out of it.</summary>
    private bool _spreadsheetOnly;

    private readonly DropZone _drop = new() { Prompt = "Arraste o PDF ou o Word do projeto aqui" };
    private readonly FlowLayoutPanel _outputChoice;
    private readonly RadioButton _tcr = Choice("TCR (Word)");
    private readonly RadioButton _tce = Choice("TCE (Word)");
    private readonly RadioButton _spreadsheet = Choice("Planilha de materiais (Excel)");
    private readonly Button _generate = Ui.Primary(new Button { Text = "Gerar planilha", Font = Ui.BigButtonFont, Height = 44, Enabled = false });
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Height = 4, Visible = false };
    private readonly TextBox _result = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window,
        MinimumSize = new Size(0, 120),
    };
    private readonly Button _openOutput = OutputButton("Abrir arquivo");
    private readonly Button _showOutput = OutputButton("Mostrar na pasta");

    public override string Title => "&Gerar";
    public override string Glyph => "\uE8A5";

    /// <summary>The button Enter triggers while this page is showing.</summary>
    public Button MainButton => _generate;

    public GeneratePage(TemplatesPage templates, HeadersPage headers)
    {
        _templates = templates;
        _headers = headers;

        _drop.Activated += (_, _) => Browse();
        _generate.Click += (_, _) => Generate();
        _openOutput.Click += (_, _) => OpenOutput();
        _showOutput.Click += (_, _) =>
        {
            if (_lastOutput is not null)
                Ui.ShowInFolder(_lastOutput);
        };

        var saved = Config.LoadPdfOutput();
        _tcr.Checked = saved == PdfOutput.Word;
        _tce.Checked = saved == PdfOutput.Tce;
        _spreadsheet.Checked = saved == PdfOutput.Spreadsheet;
        foreach (var (option, output) in new[] { (_tcr, PdfOutput.Word), (_tce, PdfOutput.Tce), (_spreadsheet, PdfOutput.Spreadsheet) })
        {
            option.CheckedChanged += (_, _) =>
            {
                if (!option.Checked)
                    return;
                Config.TrySave(() => Config.SavePdfOutput(output));
                UpdateButton();
            };
        }
        // Only a PDF has a choice: a Word document always becomes the spreadsheet.
        _outputChoice = Stack.Flow(new Label { Text = "Gerar:", AutoSize = true, Margin = new Padding(0, 5, 10, 0) }, _tcr, _tce, _spreadsheet);
        _outputChoice.Visible = false;

        SetContent(new Stack()
            .Title("Gerar documento ou planilha", "Escolha o relatório do projeto (PDF do Route Map) para gerar o TCR/TCE " +
                                                 "em Word ou a planilha de materiais. Um TCE/TCR em Word ou salvo em PDF " +
                                                 "gera a planilha.")
            .Add(_drop)
            .Add(_outputChoice, top: 12)
            .Add(_generate, top: 12)
            .Add(_progress, top: 4)
            .Caption("Resultado", top: 14)
            .Add(_result, fill: true)
            .Add(Stack.Flow(_openOutput, _showOutput), top: 8), 520);
    }

    private static RadioButton Choice(string text) => new() { Text = text, AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(0, 2, 14, 0) };

    private static Button OutputButton(string text) =>
        Ui.Secondary(new Button { Text = text, AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });

    private static bool IsPdf(string path) => HasExtension(path, ".pdf");

    private PdfOutput Output =>
        _spreadsheetOnly || _spreadsheet.Checked ? PdfOutput.Spreadsheet : _tce.Checked ? PdfOutput.Tce : PdfOutput.Word;

    public override bool Accepts(string path) => IsPdf(path) || HasExtension(path, ".docx") || HasExtension(path, ".doc");

    public override void Browse()
    {
        if (_busy)
            return;
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione o documento",
            Filter = "Word ou PDF (*.docx;*.pdf)|*.docx;*.pdf|Documento Word (*.docx)|*.docx|PDF (*.pdf)|*.pdf",
            InitialDirectory = _path is null ? "" : Path.GetDirectoryName(_path),
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            Open(dialog.FileName);
    }

    public override void Open(string path)
    {
        if (_busy)
            return;
        if (Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal))
        {
            Dialogs.Warn(this, "Esse é um arquivo temporário do Word",
                "O Word cria esse arquivo enquanto o documento está aberto. Selecione o documento original " +
                "(sem \"~$\" no início do nome).");
            return;
        }
        if (!IsPdf(path) && !HasExtension(path, ".docx"))
        {
            Dialogs.Warn(this, "Esse arquivo não é um .docx nem um PDF",
                $"\"{Path.GetFileName(path)}\" não pode ser lido.\n\nSe for um arquivo .doc antigo, abra no Word e use " +
                "Salvar como > Documento do Word (*.docx).");
            return;
        }
        _path = path;
        _spreadsheetOnly = false;
        _drop.SetFile(Path.GetFileName(path), IsPdf(path) ? "PDF · relatório do Route Map ou TCE/TCR" : "Word · documento com planilhas de materiais");
        _outputChoice.Visible = IsPdf(path);
        _generate.Enabled = true;
        _result.Text = string.Empty;
        UpdateButton();
    }

    private void UpdateButton() => _generate.Text =
        _busy ? "Gerando..." : _path is not null && IsPdf(_path) && Output != PdfOutput.Spreadsheet ? "Gerar documento" : "Gerar planilha";

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _generate.Enabled = !busy && _path is not null;
        _progress.Visible = busy;
        _drop.Enabled = _outputChoice.Enabled = !busy;
        if (FindForm() is { } window)
            window.UseWaitCursor = busy;
        UpdateButton();
    }

    private void Say(string text) => _result.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>Remembers the generated file so it can be opened or shown in its folder from the result area.</summary>
    private void SetOutput(string path)
    {
        _lastOutput = path;
        _openOutput.Enabled = _showOutput.Enabled = File.Exists(path);
    }

    private void OpenOutput()
    {
        if (_lastOutput is not null && Ui.Open(_lastOutput) is { } error)
            Dialogs.Warn(this, $"Não foi possível abrir \"{Path.GetFileName(_lastOutput)}\"",
                "Verifique se o programa para esse tipo de arquivo (Excel ou Word) está instalado.", error);
    }

    private async void Generate()
    {
        if (_path is null || _busy)
            return;
        var path = _path;
        var pdf = IsPdf(path);
        var output = pdf ? Output : PdfOutput.Spreadsheet;

        SetBusy(true);
        _openOutput.Enabled = _showOutput.Enabled = false;
        Say("Lendo o documento...");
        try
        {
            var content = await Task.Run(() => pdf ? PdfReader.Read(path) : DocxReader.Read(path));
            if (content.Tables.Count == 0)
            {
                Say(NothingFound(content, pdf));
                Dialogs.Warn(this, "Não encontrei a lista de materiais", NothingFound(content, pdf));
                return;
            }

            // A TCE/TCR saved as PDF has the material tables, but not what a new Word document is written from.
            var routeMap = content.Report is not null;
            if (pdf && !routeMap && output != PdfOutput.Spreadsheet)
            {
                if (!Dialogs.Confirm(this, "Este PDF é um TCE/TCR, não o relatório do Route Map",
                        "O TCR/TCE em Word só pode ser montado a partir do relatório do projeto exportado pelo Route Map.\n\n" +
                        $"As tabelas de materiais deste PDF foram lidas ({Count(content.Tables.Count, "tabela")}), " +
                        "então dá para gerar a planilha de materiais.", "Gerar planilha"))
                {
                    Say(string.Empty);
                    return;
                }
                // Asked once per file: from here on this PDF only offers the spreadsheet.
                output = PdfOutput.Spreadsheet;
                _spreadsheetOnly = true;
                _outputChoice.Visible = false;
                _drop.SetFile(Path.GetFileName(path), "PDF · TCE/TCR com as tabelas de materiais");
            }

            string? template = null;
            if (output == PdfOutput.Spreadsheet && (template = _templates.RequireSpreadsheet()) is null)
            {
                Say(string.Empty);
                return;
            }

            Say("Escolha as tabelas...");
            if (ChooseTables(content.Tables, routeMap) is not var (tables, sum))
            {
                Say(string.Empty);
                return;
            }

            if (output == PdfOutput.Spreadsheet)
                await GenerateSpreadsheet(path, content, tables, sum, template!);
            else
                await GenerateWord(path, content, tables, output == PdfOutput.Tce ? ReportKind.Tce : ReportKind.Tcr);
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao gerar: {0}", e.InnerException ?? e);
            Say("Não foi possível gerar.\n\n" + e.Message);
            Dialogs.Warn(this, "Não foi possível gerar", e.Message);
        }
        catch (Exception e)
        {
            Trace.TraceError("Falha ao gerar: {0}", e);
            Say($"Erro inesperado: {e.Message}");
            Dialogs.Error(this, "Ocorreu um erro inesperado ao gerar",
                "Se continuar acontecendo, envie este documento e o modelo para quem mantém o programa.", e.ToString());
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Shows the tables found so the user picks the ones to use, and remembers the newly checked
    /// titles when asked. Null when cancelled.
    /// </summary>
    private (IReadOnlyList<MaterialTable> Tables, bool Sum)? ChooseTables(IReadOnlyList<MaterialTable> tables, bool routeMap)
    {
        using var picker = new TableSelectionForm(tables, Config.LoadTableTitles(routeMap));
        if (picker.ShowDialog(FindForm()) != DialogResult.OK)
            return null;
        _headers.Remember(picker.TitlesToRemember, routeMap);
        return (picker.Selected, picker.Sum);
    }

    private async Task GenerateSpreadsheet(string source, DocumentContent content, IReadOnlyList<MaterialTable> tables,
        bool sum, string template)
    {
        var groups = MaterialGroups.Group(tables);
        var materials = MaterialGroups.Combine(groups, sum);

        if (AskOutput(source, $"{SpreadsheetName(source)}.xlsx", "Salvar planilha", "Planilha Excel (*.xlsx)|*.xlsx") is not { } chosen)
        {
            Say(string.Empty);
            return;
        }
        var (output, renamed) = FreeOutput(chosen, "a planilha foi salva", "no Excel");

        Say("Gerando a planilha...");
        var layout = Config.LoadOutputLayout();
        var hideEmpty = Config.LoadHideEmptySections();
        var added = await Task.Run(() => XlsxWriter.Fill(materials, content.Info, output, template, layout, hideEmpty));

        SetOutput(output);
        var summary = SpreadsheetSummary(content, tables, groups, materials, sum, added, output, layout);
        Say(renamed is null ? summary : renamed + "\n\n" + summary);
        Done("Planilha gerada", $"{Count(materials.Count, "material", "materiais")} de {Count(tables.Count, "tabela")}.", renamed, output);
    }

    /// <summary>Fills the TCR/TCE Word template with the Route Map report.</summary>
    private async Task GenerateWord(string pdf, DocumentContent content, IReadOnlyList<MaterialTable> tables, ReportKind kind)
    {
        if (content.Report is not { } report)
            throw new AppException("Não foi possível ler os dados do projeto no PDF.");

        var name = kind.ToString().ToUpperInvariant();
        if (_templates.RequireWord(kind) is not { } template)
        {
            Say(string.Empty);
            return;
        }

        using (var form = new ReportDataForm(report, kind))
        {
            if (form.ShowDialog(FindForm()) != DialogResult.OK)
            {
                Say(string.Empty);
                return;
            }
            report = form.Result;
        }

        if (AskOutput(pdf, $"{ReportName(pdf, kind)}.docx", "Salvar documento", "Documento Word (*.docx)|*.docx") is not { } chosen)
        {
            Say(string.Empty);
            return;
        }
        var (output, renamed) = FreeOutput(chosen, "o documento foi salvo", "no Word");

        Say("Gerando o documento...");
        var (warnings, objects) = await Task.Run(() => TcrWriter.Fill(template, report, tables, output, kind, AskPlaceholders));
        Say("Atualizando as tabelas pelo Word...");
        if (await Task.Run(() => WordRefresh.Run(output, objects)) is { } refresh)
            warnings = [.. warnings, refresh];

        var summary = $"Documento {name} gerado.\n" +
                      $"{Count(tables.Sum(t => t.Items.Count), "material", "materiais")} em {Count(tables.Count, "tabela")}.\n\n" +
                      $"Salvo em:\n{output}\n\n" +
                      $"Confira no Word: o código do {name} (capa) e o mapa não vêm do PDF, e a mão de obra ficou como no modelo.";
        SetOutput(output);
        Say(AppendWarnings(renamed is null ? summary : renamed + "\n\n" + summary, warnings));
        Done($"{name} gerado", "Confira no Word o código da capa, o mapa e a mão de obra.", renamed, output);
    }

    /// <summary>Tells the user the file is ready and offers to open it.</summary>
    private void Done(string heading, string text, string? renamed, string output)
    {
        SetBusy(false);
        var answer = Dialogs.Choose(this, TaskDialogIcon.ShieldSuccessGreenBar, heading,
            (renamed is null ? text : renamed + "\n\n" + text) + $"\n\n{output}", "Abrir", "Mostrar na pasta", "Fechar");
        if (answer == 0)
            OpenOutput();
        else if (answer == 1)
            Ui.ShowInFolder(output);
    }

    /// <summary>A file open in Excel or Word cannot be replaced, so the output gets the next free name instead.</summary>
    private static (string Output, string? Renamed) FreeOutput(string chosen, string saved, string where)
    {
        if (!Files.IsLocked(chosen))
            return (chosen, null);
        var output = Files.FreeName(chosen);
        return (output, $"\"{Path.GetFileName(chosen)}\" está aberto {where}, então {saved} como \"{Path.GetFileName(output)}\".");
    }

    private string? AskOutput(string source, string fileName, string title, string filter)
    {
        using var dialog = new SaveFileDialog
        {
            Title = title,
            InitialDirectory = Path.GetDirectoryName(source),
            FileName = fileName,
            DefaultExt = Path.GetExtension(fileName).TrimStart('.'),
            Filter = filter,
        };
        return dialog.ShowDialog(FindForm()) == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>
    /// Called from the generating thread: asks the user for the highlighted spots, unless the setting
    /// says to leave them for Word. Null leaves them all highlighted.
    /// </summary>
    private Dictionary<int, string>? AskPlaceholders(IReadOnlyList<Placeholder> items)
    {
        if (!Config.LoadAskPlaceholders())
            return null;
        return Invoke(() =>
        {
            using var form = new PlaceholderForm(items);
            return form.ShowDialog(FindForm()) == DialogResult.OK ? form.Values : null;
        });
    }

    /// <summary>"Relatorio_Rede_X" becomes "TCR_Rede_X" (or TCE); any other name just gets the prefix.</summary>
    private static string ReportName(string pdfPath, ReportKind kind)
    {
        var prefix = kind == ReportKind.Tce ? "TCE" : "TCR";
        var name = Path.GetFileNameWithoutExtension(pdfPath);
        var renamed = Regex.Replace(name, "^Relat[óo]rio", prefix, RegexOptions.IgnoreCase);
        return renamed != name ? renamed : prefix + "_" + name;
    }

    /// <summary>Same name as the TCE document, with the leading TCE swapped for TCX.</summary>
    private static string SpreadsheetName(string path) =>
        Regex.Replace(Path.GetFileNameWithoutExtension(path), "^tce", m => m.Value == "tce" ? "tcx" : "TCX", RegexOptions.IgnoreCase);

    private static string Count(int count, string one, string? many = null) => $"{count} {(count == 1 ? one : many ?? one + "s")}";

    private static string Quantity(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static string NothingFound(DocumentContent content, bool pdf)
    {
        if (pdf)
            return AppendWarnings(
                "O programa lê dois tipos de PDF:\n" +
                "• o relatório do projeto exportado pelo Route Map, com as tabelas \"Material / Qtd. / Un. / Valor unit. / Total\";\n" +
                "• um TCE/TCR salvo em PDF pelo Word, com as tabelas de materiais (material, unidade, quantidade e valores).\n\n" +
                "Se este é um TCE/TCR, use o arquivo do Word (.docx): ele é lido direto das planilhas, sem depender do texto do PDF.",
                content.Warnings);
        return AppendWarnings(content.EmbeddedWorkbooks == 0
            ? "O documento não tem nenhuma planilha do Excel incorporada.\n\n" +
              "O programa lê apenas tabelas inseridas como objeto do Excel (Inserir > Tabela > Planilha do Excel), " +
              "não tabelas comuns do Word."
            : $"O documento tem {content.EmbeddedWorkbooks} planilha(s) do Excel, mas nenhuma parece uma lista de materiais.\n\n" +
              "O programa espera o nome do material na primeira coluna e a quantidade (um número) na terceira.",
            content.Warnings);
    }

    private static string SpreadsheetSummary(DocumentContent content, IReadOnlyList<MaterialTable> tables,
        IReadOnlyList<IReadOnlyList<Occurrence>> groups, OrderedDictionary<string, Material> materials,
        bool summed, IReadOnlyList<string> added, string output, OutputLayout layout)
    {
        var kind = layout switch
        {
            OutputLayout.Complete => "completa (lista inteira do modelo)",
            OutputLayout.Compact => "compacta (seções mantidas, materiais não usados ocultos)",
            _ => "simplificada",
        };
        var message = $"Planilha {kind}.\n" +
                      $"{materials.Count} materiais processados, de {tables.Count} tabela(s).\n" +
                      $"{materials.Count - added.Count} encontrados no modelo.\n\n" +
                      $"Salvo em:\n{output}";
        if (added.Count > 0)
            message += "\n\nAdicionados como novas linhas (sem código Protheus):\n- " + string.Join("\n- ", added);

        var info = content.Info;
        var missing = new List<string>();
        if (info.Code is null)
            missing.Add("código do projeto");
        if (info.Date is null)
            missing.Add("data");
        if (info.City is null)
            missing.Add("cidade");
        if (missing.Count > 0)
            message += $"\n\nNão encontrado no documento (preencha à mão): {string.Join(", ", missing)}.";

        var repeated = groups.Where(g => g.Count > 1).ToList();
        if (repeated.Count > 0)
            message += $"\n\n{repeated.Count} material(is) repetido(s), " +
                       (summed ? "com as quantidades somadas:" : "contado(s) só na primeira ocorrência:") +
                       "\n- " + string.Join("\n- ", repeated.Select(g =>
                           $"{g[0].Name}: {Quantity(summed ? g.Sum(o => o.Material.Quantity) : g[0].Material.Quantity)}"));

        var unused = content.Tables.Except(tables).Select(t => t.Title).Distinct().ToList();
        if (unused.Count > 0)
            message += "\n\nTabelas não usadas:\n- " + string.Join("\n- ", unused);
        return AppendWarnings(message, content.Warnings);
    }

    private static string AppendWarnings(string message, IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? message : message + "\n\nAvisos:\n- " + string.Join("\n- ", warnings);
}
