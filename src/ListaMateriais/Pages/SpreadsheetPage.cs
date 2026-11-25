using System.Diagnostics;
using ListaMateriais.Planilhas;

namespace ListaMateriais.Pages;

/// <summary>
/// Paints cells of chosen columns and filters rows of an .xlsx, on every sheet or only on the ones
/// checked. The result is saved as a new file. Whatever cannot be done on a sheet is reported as a
/// warning and the rest goes on.
/// </summary>
internal sealed class SpreadsheetPage : Page
{
    private const int BaseHeight = 940;
    private const int MaxValueChoices = 1000;

    private readonly DropZone _drop = new() { Prompt = "Arraste a planilha do Excel (.xlsx) aqui" };
    private readonly CheckBox _allSheets = new() { Text = "Todas as abas", AutoSize = true, Margin = new Padding(0, 4, 24, 0) };
    private readonly NumericUpDown _headerRow = new() { Minimum = 0, Maximum = 1_000_000, Width = 80 };
    private readonly ListView _sheets = new()
    {
        View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Height = 130, ShowItemToolTips = true,
    };
    private readonly ListView _paint = new()
    {
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false,
        Height = 110, ShowItemToolTips = true,
    };
    private readonly Button _addPaint = Ui.Secondary(new Button { Text = "+ Adicionar pintura", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly Button _editPaint = Ui.Secondary(new Button { Text = "Editar", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly Button _removePaint = Ui.Secondary(new Button { Text = "Remover", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly ConditionList _filter = new();
    private readonly TextBox _warnings = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, MinimumSize = new Size(0, 70),
    };
    private readonly Button _save = Ui.Primary(new Button { Text = "Salvar planilha tratada...", Font = Ui.BigButtonFont, Height = 44, Enabled = false });
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Height = 4, Visible = false };
    private readonly Button _openOutput = Ui.Secondary(new Button { Text = "Abrir arquivo", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly Button _showOutput = Ui.Secondary(new Button { Text = "Mostrar na pasta", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) });
    private readonly System.Windows.Forms.Timer _typing = new() { Interval = 400 };

    private readonly List<PaintRule> _rules = [];
    private Workbook? _workbook;
    private IReadOnlyList<string> _columns = [];
    private string? _lastOutput;
    private bool _busy;
    private bool _loading;

    public override string Title => "&Planilha";
    public override string Glyph => "";

    /// <summary>The button Enter triggers while this page is showing.</summary>
    public Button MainButton => _save;

    public SpreadsheetPage()
    {
        _sheets.Columns.Add("Aba", 200);
        _sheets.Columns.Add("Cabeçalho", 80);
        _sheets.Columns.Add("Linhas", 70, HorizontalAlignment.Right);
        _sheets.Columns.Add("O que vai acontecer", 330);
        _sheets.ClientSizeChanged += (_, _) => FillLastColumn(_sheets);
        _paint.Columns.Add("Colunas", 260);
        _paint.Columns.Add("Cor", 130);
        _paint.Columns.Add("Quando", 300);
        _paint.ClientSizeChanged += (_, _) => FillLastColumn(_paint);

        SetContent(new Stack()
            .Title("Planilha: pintar e filtrar", "Pinta células das colunas escolhidas e oculta as linhas que não interessam, em " +
                                                 "todas as abas ou só nas marcadas. O resultado é salvo em um arquivo novo; o original não muda.")
            .Add(_drop, bottom: 12)
            .Caption("Abas")
            .Add(Stack.Flow(_allSheets,
                new Label { Text = "Linha do cabeçalho (0 = achar sozinho):", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _headerRow), bottom: 4)
            .Add(_sheets, bottom: 12)
            .Caption("Pintar células (se duas pinturas pegam a mesma célula, vale a de baixo)")
            .Add(_paint, bottom: 4)
            .Add(Stack.Flow(_addPaint, _editPaint, _removePaint), bottom: 12)
            .Caption("Filtrar linhas (as que não atendem ficam ocultas, nada é apagado)")
            .Add(_filter, bottom: 12)
            .Caption("Avisos")
            .Add(_warnings, fill: true)
            .Add(_save, top: 10)
            .Add(_progress, top: 4)
            .Add(Stack.Flow(_openOutput, _showOutput), top: 8), BaseHeight);

        _drop.Activated += (_, _) => Browse();
        _allSheets.CheckedChanged += (_, _) => CheckAll(_allSheets.Checked);
        _sheets.ItemChecked += (_, _) =>
        {
            if (_loading)
                return;
            _loading = true;
            _allSheets.Checked = _sheets.Items.Count > 0 && _sheets.CheckedItems.Count == _sheets.Items.Count;
            _loading = false;
            Later();
        };
        _headerRow.ValueChanged += (_, _) =>
        {
            ReadColumns();
            Later();
        };
        _addPaint.Click += (_, _) => EditPaint(-1);
        _editPaint.Click += (_, _) => EditPaint(Selected);
        _paint.DoubleClick += (_, _) => EditPaint(Selected);
        _paint.SelectedIndexChanged += (_, _) => EnableButtons();
        _paint.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
                RemovePaint();
        };
        _removePaint.Click += (_, _) => RemovePaint();
        _filter.Changed += (_, _) =>
        {
            SetMinHeight(BaseHeight + _filter.Count * 32);
            Later();
        };
        _filter.Typing += (_, _) => Later();
        _typing.Tick += (_, _) => Preview();
        _save.Click += (_, _) => Save();
        _openOutput.Click += (_, _) => OpenOutput();
        _showOutput.Click += (_, _) =>
        {
            if (_lastOutput is not null)
                Ui.ShowInFolder(_lastOutput);
        };
        _warnings.Text = "Escolha uma planilha para começar.";
    }

    private int Selected => _paint.SelectedIndices.Count > 0 ? _paint.SelectedIndices[0] : -1;

    private static void FillLastColumn(ListView list)
    {
        var used = list.Columns.Cast<ColumnHeader>().Take(list.Columns.Count - 1).Sum(c => c.Width);
        list.Columns[^1].Width = Math.Max(160, list.ClientSize.Width - used - 4);
    }

    public override bool Accepts(string path) => HasExtension(path, ".xlsx");

    public override void Browse()
    {
        if (_busy)
            return;
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione a planilha",
            Filter = "Planilha do Excel (*.xlsx)|*.xlsx",
            InitialDirectory = _workbook is null ? "" : Path.GetDirectoryName(_workbook.Path),
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            Open(dialog.FileName);
    }

    public override async void Open(string path)
    {
        if (_busy)
            return;
        if (!Accepts(path))
        {
            Dialogs.Warn(this, "Arquivo não aceito", "Esta página trabalha com planilhas .xlsx.");
            return;
        }
        SetBusy(true, "Lendo a planilha...");
        try
        {
            var workbook = await Task.Run(() => Workbook.Open(path));
            Show(workbook);
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao ler a planilha: {0}", e.InnerException ?? e);
            Dialogs.Warn(this, "Não foi possível ler a planilha", e.Message);
        }
#pragma warning disable CA1031 // An unexpected failure is shown to the user instead of closing the app.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Trace.TraceError("Falha ao ler a planilha: {0}", e);
            Dialogs.Error(this, "Ocorreu um erro inesperado ao ler a planilha", "O programa continua aberto.", e.ToString());
        }
        finally
        {
            SetBusy(false);
        }
        // Only now: the preview waits until the page is no longer busy.
        Preview();
    }

    /// <summary>Shows a newly opened workbook, with every sheet checked. The rules are kept for the next file.</summary>
    private void Show(Workbook workbook)
    {
        _workbook = workbook;
        _lastOutput = null;
        _openOutput.Enabled = _showOutput.Enabled = false;
        var count = workbook.Sheets.Count;
        _drop.SetFile(Path.GetFileName(workbook.Path), count == 1 ? "1 aba" : $"{count} abas");
        _loading = true;
        _headerRow.Value = 0;
        _sheets.Items.Clear();
        foreach (var sheet in workbook.Sheets)
            _sheets.Items.Add(new ListViewItem([sheet.Name, "", "", ""]) { Checked = true });
        _allSheets.Checked = count > 0;
        _loading = false;
        ReadColumns();
    }

    private void CheckAll(bool check)
    {
        if (_loading)
            return;
        _loading = true;
        foreach (ListViewItem item in _sheets.Items)
            item.Checked = check;
        _loading = false;
        Later();
    }

    /// <summary>The column names of every sheet, once each.</summary>
    private void ReadColumns()
    {
        if (_workbook is null)
            return;
        var header = (int)_headerRow.Value;
        _columns = [.. _workbook.Sheets.SelectMany(s => s.Columns(header)).Select(c => c.Name).DistinctBy(SheetData.Key)];
        _filter.SetColumns(_columns, ValuesOf);
    }

    /// <summary>The distinct values of a column in the checked sheets, sorted.</summary>
    private IReadOnlyList<string> ValuesOf(string column)
    {
        if (_workbook is null)
            return [];
        var header = (int)_headerRow.Value;
        var values = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var sheet in _workbook.Sheets.Where((_, i) => i < _sheets.Items.Count && _sheets.Items[i].Checked))
        {
            var first = header > 0 ? header : sheet.FindHeaderRow();
            foreach (var index in SheetData.Find(sheet.Columns(header), column))
            {
                foreach (var (number, row) in sheet.Rows)
                {
                    if (values.Count >= MaxValueChoices)
                        break;
                    if (number > first && index < row.Length && row[index] is { } value)
                        values.Add(value);
                }
            }
        }
        return [.. values.Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    private Job CurrentJob => new(
        [.. _sheets.CheckedItems.Cast<ListViewItem>().Select(i => i.Text)],
        [.. _rules], _filter.Conditions, (int)_headerRow.Value);

    private void Later()
    {
        _typing.Stop();
        _typing.Start();
    }

    /// <summary>Says, sheet by sheet, what saving would do.</summary>
    private void Preview()
    {
        _typing.Stop();
        EnableButtons();
        if (_workbook is null || _busy)
            return;
        try
        {
            var job = CurrentJob;
            var result = _workbook.Plan(job);
            ShowResult(result);
            if (job.Sheets.Count == 0)
                _warnings.Text = "Nenhuma aba marcada.";
            else if (!HasWork(job))
                _warnings.Text = "Adicione uma pintura ou um filtro." + Lines(result.Warnings);
            else
                _warnings.Text = result.Warnings.Count == 0 ? "Nenhum problema encontrado." : string.Join(Environment.NewLine, result.Warnings);
        }
#pragma warning disable CA1031 // The preview must never close the app; the failure is shown in the warnings.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Trace.TraceError("Falha na prévia da planilha: {0}", e);
            _warnings.Text = $"Não foi possível calcular a prévia: {e.Message}";
        }
    }

    private static string Lines(IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, warnings);

    private static bool HasWork(Job job) => job.Paint.Count > 0 || job.Filter.Any(c => c.Active);

    private void ShowResult(JobResult result)
    {
        var bySheet = result.Sheets.ToDictionary(s => s.Name, StringComparer.Ordinal);
        _sheets.BeginUpdate();
        foreach (ListViewItem item in _sheets.Items)
        {
            if (!bySheet.TryGetValue(item.Text, out var sheet))
            {
                item.SubItems[1].Text = item.SubItems[2].Text = string.Empty;
                item.SubItems[3].Text = "fica como está";
                item.ToolTipText = string.Empty;
                continue;
            }
            item.SubItems[1].Text = sheet.HeaderRow > 0 ? $"linha {sheet.HeaderRow}" : "-";
            item.SubItems[2].Text = sheet.DataRows.ToString("N0");
            item.SubItems[3].Text = Describe(sheet);
            item.ToolTipText = string.Join(Environment.NewLine, sheet.Warnings);
        }
        _sheets.EndUpdate();
    }

    private static string Describe(SheetResult sheet)
    {
        var parts = new List<string>();
        if (sheet.PaintedCells > 0)
            parts.Add($"pinta {sheet.PaintedCells:N0} célula(s)");
        if (sheet.Shown is { } shown)
            parts.Add($"mostra {shown:N0} linha(s), oculta {sheet.Hidden:N0}");
        if (parts.Count == 0)
            parts.Add("nada a alterar");
        if (sheet.Warnings.Count > 0)
            parts.Add($"{sheet.Warnings.Count} aviso(s)");
        return string.Join(" · ", parts);
    }

    private void EditPaint(int index)
    {
        if (_busy || _workbook is null || index >= _rules.Count || index < 0 && !_addPaint.Enabled)
            return;
        using var dialog = new PaintRuleForm(_columns, ValuesOf, index < 0 ? null : _rules[index]);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        if (index < 0)
        {
            index = _rules.Count;
            _rules.Add(dialog.Rule);
        }
        else
        {
            _rules[index] = dialog.Rule;
        }
        ShowRules(index);
        Preview();
    }

    private void RemovePaint()
    {
        var index = Selected;
        if (_busy || index < 0)
            return;
        _rules.RemoveAt(index);
        ShowRules(Math.Min(index, _rules.Count - 1));
        Preview();
    }

    private void ShowRules(int select)
    {
        _paint.BeginUpdate();
        _paint.Items.Clear();
        foreach (var rule in _rules)
        {
            var columns = rule.Columns.Count == 0 ? "Linha inteira" : string.Join(", ", rule.Columns);
            var when = Describe(rule.When);
            var item = new ListViewItem([columns, PaintRuleForm.ColorName(rule.Fill), when])
            {
                UseItemStyleForSubItems = false, ToolTipText = $"{columns} · {when}",
            };
            item.SubItems[1].BackColor = rule.Fill ?? Color.White;
            // Dark colors need white text.
            item.SubItems[1].ForeColor = rule.Fill is { } c && c.R * 0.299 + c.G * 0.587 + c.B * 0.114 < 130 ? Color.White : Color.Black;
            _paint.Items.Add(item);
        }
        if (select >= 0 && select < _paint.Items.Count)
            _paint.Items[select].Selected = true;
        _paint.EndUpdate();
    }

    private static string Describe(IReadOnlyList<Condition> conditions)
    {
        var active = conditions.Where(c => c.Active).ToList();
        if (active.Count == 0)
            return "todas as linhas";
        return string.Concat(active.Select((c, i) =>
            (i == 0 ? string.Empty : c.Or ? " OU " : " E ") + $"{c.Column} {c.Operator.ToLowerInvariant()}" +
            (Kmz.SheetFilters.NeedsValue(c.Operator) ? $" \"{c.Value}\"" : string.Empty)));
    }

    private void EnableButtons()
    {
        var ready = !_busy && _workbook is not null;
        _addPaint.Enabled = ready && _columns.Count > 0;
        _editPaint.Enabled = _removePaint.Enabled = ready && Selected >= 0;
        _save.Enabled = ready;
    }

    private void SetBusy(bool busy, string text = "Salvando...")
    {
        _busy = busy;
        _save.Text = busy ? text : "Salvar planilha tratada...";
        _sheets.Enabled = _filter.Enabled = _allSheets.Enabled = _headerRow.Enabled = !busy;
        _progress.Visible = busy;
        EnableButtons();
        if (FindForm() is { } window)
            window.UseWaitCursor = busy;
    }

    private async void Save()
    {
        if (_busy || _workbook is not { } workbook)
            return;
        try
        {
            var job = CurrentJob;
            if (job.Sheets.Count == 0)
            {
                Dialogs.Info(this, "Nenhuma aba marcada", "Marque ao menos uma aba, ou \"Todas as abas\".");
                return;
            }
            if (!HasWork(job))
            {
                Dialogs.Info(this, "Nada a fazer", "Adicione uma pintura ou um filtro antes de salvar.");
                return;
            }
            var planned = workbook.Plan(job);
            if (!planned.Changes)
            {
                ShowNothing(planned);
                return;
            }

            var folder = Path.GetDirectoryName(Path.GetFullPath(workbook.Path))!;
            var suggested = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(workbook.Path)} (tratada).xlsx");
            if (File.Exists(suggested))
                suggested = Files.FreeName(suggested);
            using var dialog = new SaveFileDialog
            {
                Title = "Salvar planilha tratada",
                InitialDirectory = folder,
                FileName = Path.GetFileName(suggested),
                DefaultExt = "xlsx",
                Filter = "Planilha do Excel (*.xlsx)|*.xlsx",
            };
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
                return;
            var output = dialog.FileName;

            _openOutput.Enabled = _showOutput.Enabled = false;
            SetBusy(true);
            JobResult result;
            try
            {
                result = await Task.Run(() => workbook.Save(job, output));
            }
            finally
            {
                SetBusy(false);
            }
            ShowResult(result);
            _warnings.Text = result.Warnings.Count == 0 ? "Nenhum problema encontrado." : string.Join(Environment.NewLine, result.Warnings);
            if (!result.Changes)
            {
                ShowNothing(result);
                return;
            }

            _lastOutput = output;
            _openOutput.Enabled = _showOutput.Enabled = true;
            var changed = result.Sheets.Count(s => s.Changes);
            var summary = $"{changed} aba(s) alterada(s), {result.Sheets.Sum(s => s.PaintedCells):N0} célula(s) pintada(s), " +
                          $"{result.Sheets.Sum(s => s.Hidden):N0} linha(s) oculta(s).\n\n{output}";
            var answer = result.Warnings.Count == 0
                ? Dialogs.Choose(this, TaskDialogIcon.ShieldSuccessGreenBar, "Planilha salva", summary, "Abrir", "Mostrar na pasta", "Fechar")
                : Dialogs.Choose(this, TaskDialogIcon.Warning, "Planilha salva, com avisos",
                    $"{summary}\n\n{Short(result.Warnings)}", "Abrir", "Mostrar na pasta", "Fechar");
            if (answer == 0)
                OpenOutput();
            else if (answer == 1)
                Ui.ShowInFolder(output);
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao salvar a planilha: {0}", e.InnerException ?? e);
            Dialogs.Warn(this, "Não foi possível salvar a planilha", e.Message);
        }
#pragma warning disable CA1031 // An unexpected failure is shown to the user instead of closing the app.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Trace.TraceError("Falha ao salvar a planilha: {0}", e);
            Dialogs.Error(this, "Ocorreu um erro inesperado ao salvar a planilha", "O programa continua aberto.", e.ToString());
        }
    }

    private void ShowNothing(JobResult result) =>
        Dialogs.Info(this, "Nada para alterar",
            "Nenhuma das abas marcadas precisa de mudança; nenhum arquivo foi salvo." +
            (result.Warnings.Count == 0 ? string.Empty : $"\n\n{Short(result.Warnings)}"));

    /// <summary>The first warnings, for a dialog; the page lists all of them.</summary>
    private static string Short(IReadOnlyList<string> warnings) =>
        string.Join("\n", warnings.Take(8)) +
        (warnings.Count > 8 ? $"\n... e mais {warnings.Count - 8} aviso(s), listados na página." : string.Empty);

    private void OpenOutput()
    {
        if (_lastOutput is not null && Ui.Open(_lastOutput) is { } error)
            Dialogs.Warn(this, "Não foi possível abrir a planilha", "Verifique se o Excel está instalado.", error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _typing.Dispose();
        base.Dispose(disposing);
    }
}
