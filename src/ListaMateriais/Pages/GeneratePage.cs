using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ListaMateriais.Pages;

/// <summary>
/// Takes the project document (the Route Map report in PDF, a TCE/TCR in Word or saved as PDF) and
/// generates the material spreadsheet and/or the TCR/TCE Word document from it, all in one go.
/// </summary>
internal sealed class GeneratePage : Page
{
    private readonly TemplatesPage _templates;
    private readonly HeadersPage _headers;

    private string? _path;
    private List<string> _lastOutputs = [];
    private bool _busy;

    /// <summary>The chosen PDF turned out to be a TCE/TCR: only the spreadsheet can come out of it.</summary>
    private bool _spreadsheetOnly;

    private readonly DropZone _drop = new() { Prompt = "Arraste o PDF ou o Word do projeto aqui" };
    private readonly FlowLayoutPanel _outputChoice;
    private readonly CheckBox _tcr = Choice("TCR (Word)");
    private readonly CheckBox _tce = Choice("TCE (Word)");
    private readonly CheckBox _spreadsheet = Choice("Planilha de materiais (Excel)");
    private readonly Button _generate = Ui.Primary(new Button { Text = "Gerar planilha", Font = Ui.BigButtonFont, Height = 44, Enabled = false });
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Height = 4, Visible = false };
    private readonly TextBox _result = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window,
        MinimumSize = new Size(0, 120),
    };
    private readonly Button _openOutput = OutputButton("Abrir");
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
            if (_lastOutputs.Count > 0)
                Ui.ShowInFolder(_lastOutputs[^1]);
        };

        var saved = Config.LoadPdfOutput();
        _tcr.Checked = saved.HasFlag(PdfOutput.Word);
        _tce.Checked = saved.HasFlag(PdfOutput.Tce);
        _spreadsheet.Checked = saved.HasFlag(PdfOutput.Spreadsheet);
        foreach (var option in new[] { _tcr, _tce, _spreadsheet })
        {
            option.CheckedChanged += (_, _) =>
            {
                // At least one file must come out: the last one checked cannot be unchecked.
                if (Chosen == PdfOutput.None)
                {
                    option.Checked = true;
                    return;
                }
                var chosen = Chosen;
                Config.TrySave(() => Config.SavePdfOutput(chosen));
                UpdateButton();
            };
        }
        // Only a PDF has a choice: a Word document always becomes the spreadsheet.
        _outputChoice = Stack.Flow(new Label { Text = "Gerar:", AutoSize = true, Margin = new Padding(0, 5, 10, 0) }, _tcr, _tce, _spreadsheet);
        _outputChoice.Visible = false;

        SetContent(new Stack()
            .Title("Gerar documento e planilha", "Escolha o relatório do projeto (PDF do Route Map) para gerar o TCR/TCE " +
                                                "em Word e a planilha de materiais: marque um ou mais, saem todos de uma vez. " +
                                                "Um TCE/TCR em Word ou salvo em PDF gera a planilha.")
            .Add(_drop)
            .Add(_outputChoice, top: 12)
            .Add(_generate, top: 12)
            .Add(_progress, top: 4)
            .Caption("Resultado", top: 14)
            .Add(_result, fill: true)
            .Add(Stack.Flow(_openOutput, _showOutput), top: 8), 520);
    }

    private static CheckBox Choice(string text) => new() { Text = text, AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(0, 2, 14, 0) };

    private static Button OutputButton(string text) =>
        Ui.Secondary(new Button { Text = text, AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });

    private static bool IsPdf(string path) => HasExtension(path, ".pdf");

    /// <summary>The boxes checked.</summary>
    private PdfOutput Chosen =>
        (_tcr.Checked ? PdfOutput.Word : 0) | (_tce.Checked ? PdfOutput.Tce : 0) | (_spreadsheet.Checked ? PdfOutput.Spreadsheet : 0);

    /// <summary>What comes out of the current file.</summary>
    private PdfOutput Outputs => _path is null || !IsPdf(_path) || _spreadsheetOnly ? PdfOutput.Spreadsheet : Chosen;

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

    private void UpdateButton() => _generate.Text = _busy ? "Gerando..." : "Gerar " + Names(Outputs);

    /// <summary>"TCR e planilha", "TCR, TCE e planilha"...</summary>
    private static string Names(PdfOutput outputs)
    {
        var names = new List<string>();
        if (outputs.HasFlag(PdfOutput.Word))
            names.Add("TCR");
        if (outputs.HasFlag(PdfOutput.Tce))
            names.Add("TCE");
        if (outputs.HasFlag(PdfOutput.Spreadsheet))
            names.Add("planilha");
        return names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " e " + names[^1];
    }

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

    /// <summary>Remembers the generated files so they can be opened or shown in their folder from the result area.</summary>
    private void SetOutputs(List<string> paths)
    {
        _lastOutputs = [.. paths.Where(File.Exists)];
        _openOutput.Enabled = _showOutput.Enabled = _lastOutputs.Count > 0;
        _openOutput.Text = _lastOutputs.Count > 1 ? "Abrir todos" : "Abrir";
    }

    private void OpenOutput()
    {
        foreach (var output in _lastOutputs)
        {
            if (Ui.Open(output) is { } error)
            {
                Dialogs.Warn(this, $"Não foi possível abrir \"{Path.GetFileName(output)}\"",
                    "Verifique se o programa para esse tipo de arquivo (Excel ou Word) está instalado.", error);
                return;
            }
        }
    }

    /// <summary>One file generated: where it went, its line in the final dialog, and what the result area says about it.</summary>
    private sealed record Generated(string Output, string Heading, string Line, string Summary);

    private async void Generate()
    {
        if (_path is null || _busy)
            return;
        var path = _path;
        var pdf = IsPdf(path);
        var outputs = Outputs;

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
            var notes = new List<string>();
            const PdfOutput words = PdfOutput.Word | PdfOutput.Tce;
            if (pdf && !routeMap && (outputs & words) != 0)
            {
                const string why = "O TCR/TCE em Word só pode ser montado a partir do relatório do projeto exportado pelo Route Map.";
                if (outputs.HasFlag(PdfOutput.Spreadsheet))
                {
                    notes.Add($"Este PDF é um TCE/TCR, não o relatório do Route Map: só a planilha foi gerada. {why}");
                }
                else if (!Dialogs.Confirm(this, "Este PDF é um TCE/TCR, não o relatório do Route Map",
                             $"{why}\n\nAs tabelas de materiais deste PDF foram lidas ({Count(content.Tables.Count, "tabela")}), " +
                             "então dá para gerar a planilha de materiais.", "Gerar planilha"))
                {
                    Say(string.Empty);
                    return;
                }
                // Found out once per file: from here on this PDF only offers the spreadsheet.
                outputs = PdfOutput.Spreadsheet;
                _spreadsheetOnly = true;
                _outputChoice.Visible = false;
                _drop.SetFile(Path.GetFileName(path), "PDF · TCE/TCR com as tabelas de materiais");
                UpdateButton();
            }

            // Every template is asked for before anything else, so a missing one does not stop the work halfway.
            string? spreadsheetTemplate = null;
            if (outputs.HasFlag(PdfOutput.Spreadsheet) && (spreadsheetTemplate = _templates.RequireSpreadsheet()) is null)
            {
                Say(string.Empty);
                return;
            }
            var wordTemplates = new List<(ReportKind Kind, byte[] Template)>();
            foreach (var (flag, kind) in new[] { (PdfOutput.Word, ReportKind.Tcr), (PdfOutput.Tce, ReportKind.Tce) })
            {
                if (!outputs.HasFlag(flag))
                    continue;
                if (_templates.RequireWord(kind) is not { } template)
                {
                    Say(string.Empty);
                    return;
                }
                wordTemplates.Add((kind, template));
            }

            Say("Escolha as tabelas...");
            if (ChooseTables(content.Tables, routeMap) is not var (tables, sum))
            {
                Say(string.Empty);
                return;
            }

            // The Word documents first: they ask for the report data; the spreadsheet then needs only where to save.
            var done = new List<Generated>();
            var skipped = new List<string>();
            foreach (var (kind, template) in wordTemplates)
            {
                if (await GenerateWord(path, content, tables, kind, template) is { } word)
                    done.Add(word);
                else
                    skipped.Add(kind.ToString().ToUpperInvariant());
            }
            if (spreadsheetTemplate is not null)
            {
                if (await GenerateSpreadsheet(path, content, tables, sum, spreadsheetTemplate) is { } sheet)
                    done.Add(sheet);
                else
                    skipped.Add("planilha");
            }
            if (skipped.Count > 0 && done.Count > 0)
                notes.Add($"Cancelado, não gerado: {string.Join(", ", skipped)}.");
            Finish(done, notes);
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

    /// <summary>Null when the user cancelled saving it.</summary>
    private async Task<Generated?> GenerateSpreadsheet(string source, DocumentContent content, IReadOnlyList<MaterialTable> tables,
        bool sum, string template)
    {
        var groups = MaterialGroups.Group(tables);
        var materials = MaterialGroups.Combine(groups, sum);

        if (AskOutput(source, $"{SpreadsheetName(source)}.xlsx", "Salvar planilha", "Planilha Excel (*.xlsx)|*.xlsx") is not { } chosen)
            return null;
        var (output, renamed) = FreeOutput(chosen, "a planilha foi salva", "no Excel");

        Say("Gerando a planilha...");
        var layout = Config.LoadOutputLayout();
        var hideEmpty = Config.LoadHideEmptySections();
        var added = await Task.Run(() => XlsxWriter.Fill(materials, content.Info, output, template, layout, hideEmpty));

        var summary = SpreadsheetSummary(content, tables, groups, materials, sum, added, output, layout);
        return new Generated(output, "Planilha gerada",
            (renamed is null ? "" : renamed + "\n") + $"Planilha: {Count(materials.Count, "material", "materiais")} de {Count(tables.Count, "tabela")}.",
            renamed is null ? summary : renamed + "\n\n" + summary);
    }

    /// <summary>Fills the TCR/TCE Word template with the Route Map report. Null when the user cancelled.</summary>
    private async Task<Generated?> GenerateWord(string pdf, DocumentContent content, IReadOnlyList<MaterialTable> tables, ReportKind kind,
        byte[] template)
    {
        if (content.Report is not { } report)
            throw new AppException("Não foi possível ler os dados do projeto no PDF.");

        var name = kind.ToString().ToUpperInvariant();
        using (var form = new ReportDataForm(report, kind))
        {
            if (form.ShowDialog(FindForm()) != DialogResult.OK)
                return null;
            report = form.Result;
        }

        if (AskOutput(pdf, $"{ReportName(pdf, kind)}.docx", $"Salvar {name}", "Documento Word (*.docx)|*.docx") is not { } chosen)
            return null;
        var (output, renamed) = FreeOutput(chosen, "o documento foi salvo", "no Word");

        Say($"Gerando o {name}...");
        var (warnings, objects) = await Task.Run(() => TcrWriter.Fill(template, report, tables, output, kind, AskPlaceholders));
        Say("Atualizando as tabelas pelo Word...");
        if (await Task.Run(() => WordRefresh.Run(output, objects)) is { } refresh)
            warnings = [.. warnings, refresh];

        var summary = $"Documento {name} gerado.\n" +
                      $"{Count(tables.Sum(t => t.Items.Count), "material", "materiais")} em {Count(tables.Count, "tabela")}.\n\n" +
                      $"Salvo em:\n{output}\n\n" +
                      $"Confira no Word: o código do {name} (capa) e o mapa não vêm do PDF, e a mão de obra ficou como no modelo.";
        return new Generated(output, $"{name} gerado",
            (renamed is null ? "" : renamed + "\n") + $"{name}: confira no Word o código da capa, o mapa e a mão de obra.",
            AppendWarnings(renamed is null ? summary : renamed + "\n\n" + summary, warnings));
    }

    /// <summary>Shows what was generated and offers to open it.</summary>
    private void Finish(List<Generated> done, List<string> notes)
    {
        if (done.Count == 0)
        {
            Say(string.Join("\n\n", notes));
            return;
        }
        var outputs = done.Select(d => d.Output).ToList();
        SetOutputs(outputs);
        Say(string.Join("\n\n" + new string('-', 40) + "\n\n", notes.Concat(done.Select(d => d.Summary))));
        SetBusy(false);

        var heading = done.Count == 1 ? done[0].Heading : $"{done.Count} arquivos gerados";
        var text = string.Join("\n", notes.Concat(done.Select(d => d.Line))) + "\n\n" + string.Join("\n", outputs);
        var answer = Dialogs.Choose(this, TaskDialogIcon.ShieldSuccessGreenBar, heading, text,
            done.Count == 1 ? "Abrir" : "Abrir todos", "Mostrar na pasta", "Fechar");
        if (answer == 0)
            OpenOutput();
        else if (answer == 1)
            Ui.ShowInFolder(outputs[^1]);
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
