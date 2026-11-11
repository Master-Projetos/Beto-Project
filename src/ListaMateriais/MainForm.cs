using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ListaMateriais;

internal sealed class MainForm : Form
{
    private const int FormWidth = 640;
    private const int FormHeight = 800;
    private const int Pad = 16;
    private const int ResultHeight = 170;
    private const int WrapWidth = FormWidth - 60;
    private const string AppTitle = "Preguiça App";

    private static readonly Font TitleFont = new("Segoe UI", 14, FontStyle.Bold);
    private static readonly Font BoldFont = new("Segoe UI", 10, FontStyle.Bold);
    private static readonly Font BigButtonFont = new("Segoe UI", 11, FontStyle.Bold);
    private static readonly Font ListFont = new("Segoe UI", 10);
    private static readonly Color Muted = Color.FromArgb(0x66, 0x66, 0x66);

    private string? _docxPath;
    private string? _templatePath;
    private bool _busy;
    private bool _headersPdf;

    private readonly DropZone _drop = new();
    private readonly ProgressBar _progress = new()
    {
        Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Height = 5, Dock = DockStyle.Fill, Visible = false,
    };
    private readonly Button _openOutput = new() { Text = "Abrir arquivo", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _showOutput = new() { Text = "Mostrar na pasta", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) };
    private string? _lastOutput;
    private readonly Label _templateName = new() { AutoSize = true, Font = BoldFont };
    private readonly Label _templateFull = MutedLabel(string.Empty);
    private readonly Button _generateButton;
    private FlowLayoutPanel? _pdfChoice;
    private readonly RadioButton _pdfWord = new() { Text = "Word (TCR)", AutoSize = true, Font = BoldFont };
    private readonly RadioButton _pdfTce = new() { Text = "Word (TCE)", AutoSize = true, Font = BoldFont };
    private readonly Button _openTemplateButton = new() { Text = "Abrir modelo no Excel", Height = 30 };
    private readonly Button _builtInTemplateButton = new() { Text = "Usar modelo padrão", Height = 30 };
    private readonly TextBox _result;
    private readonly ListBox _headers = new() { Dock = DockStyle.Fill, IntegralHeight = false, Font = ListFont };
    private readonly Label _headersFeedback = new() { AutoSize = true, MaximumSize = new Size(WrapWidth, 0) };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _generateTab;
    private readonly TabPage _templateTab;
    private readonly TabPage _headersTab;

    public MainForm()
    {
        Text = AppTitle;
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
        {
            if (iconStream is not null) Icon = new Icon(iconStream);
        }
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(FormWidth, FormHeight);
        KeyPreview = true;
        MinimumSize = SizeFromClientSize(new Size(420, 540));
        StartPosition = FormStartPosition.CenterScreen;

        _generateButton = new Button
        {
            Text = "Gerar planilha",
            Font = BigButtonFont,
            Dock = DockStyle.Fill,
            Height = 46,
            Enabled = false,
        };
        Ui.Primary(_generateButton);
        _generateButton.Click += (_, _) => Generate();
        AcceptButton = _generateButton;
        Ui.Secondary(_openOutput);
        Ui.Secondary(_showOutput);
        _openOutput.Click += (_, _) => { if (_lastOutput is not null) OpenFile(_lastOutput); };
        _showOutput.Click += (_, _) => ShowInFolder();
        _drop.Activated += (_, _) => ChooseDocx();

        _result = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            // Fixed height: the box never grows or shrinks with the text or the window.
            MinimumSize = new Size(0, ResultHeight),
            MaximumSize = new Size(0, ResultHeight),
        };

        _generateTab = BuildGenerateTab();
        _templateTab = BuildTemplateTab();
        _headersTab = BuildHeadersTab();
        _tabs.TabPages.AddRange([_generateTab, _templateTab, _headersTab, BuildSettingsTab(), BuildCreditsTab()]);
        Controls.Add(_tabs);
        Padding = new Padding(8);

        // OLE drag and drop only works on an STA thread.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            EnableDocxDrop(_generateTab);
        SetTemplate(Config.LoadTemplatePath(), save: false);
    }

    private static Label MutedLabel(string text) => new()
    {
        Text = text,
        ForeColor = Muted,
        AutoSize = true,
        MaximumSize = new Size(WrapWidth, 0),
    };

    private static TableLayoutPanel Column(params (Control Control, Padding Margin, bool Fill)[] rows)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (control, margin, fill) in rows)
        {
            panel.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            control.Margin = margin;
            if (control is Button or DropZone)
                control.Dock = DockStyle.Fill;
            panel.Controls.Add(control);
        }
        return panel;
    }

    /// <summary>Controls side by side in equal columns.</summary>
    private static TableLayoutPanel Row(int height, params Control[] controls)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = controls.Length, RowCount = 1, Height = height,
        };
        for (var i = 0; i < controls.Length; i++)
        {
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / controls.Length));
            controls[i].Dock = DockStyle.Fill;
            controls[i].Margin = new Padding(i == 0 ? 0 : 4, 0, i == controls.Length - 1 ? 0 : 4, 0);
            panel.Controls.Add(controls[i], i, 0);
        }
        return panel;
    }

    private TabPage BuildGenerateTab()
    {
        // Everything fits at the default size; on a smaller window the tab scrolls instead of cutting the buttons.
        var tab = new TabPage("Gerar") { Padding = new Padding(Pad), AutoScroll = true, AutoScrollMinSize = new Size(0, 700) };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        actions.Controls.AddRange([_openOutput, _showOutput]);
        tab.Controls.Add(Column(
            (new Label { Text = "Gerar documento ou planilha", Font = TitleFont, AutoSize = true }, new Padding(0), false),
            (MutedLabel("Escolha o relatório do projeto (PDF) para gerar o TCR/TCE em Word, ou um Word com " +
                        "as tabelas de materiais para gerar a planilha."), new Padding(0, 2, 0, 12), false),
            (_drop, new Padding(0), false),
            (_pdfChoice = PdfOutputChoice(), new Padding(0, 12, 0, 0), false),
            (_generateButton, new Padding(0, 12, 0, 0), false),
            (_progress, new Padding(0, 4, 0, 0), false),
            (new Label { Text = "Resultado", Font = BoldFont, AutoSize = true }, new Padding(0, 14, 0, 4), false),
            (_result, new Padding(0), false),
            (actions, new Padding(0), false),
            (new Panel(), new Padding(0), true)));
        return tab;
    }

    /// <summary>What to generate when the input is a PDF report; only enabled once a PDF is selected.</summary>
    private FlowLayoutPanel PdfOutputChoice()
    {
        var current = Config.LoadPdfOutput();
        _pdfWord.Checked = current != PdfOutput.Tce;
        _pdfTce.Checked = current == PdfOutput.Tce;

        void Save(PdfOutput output)
        {
            try
            {
                Config.SavePdfOutput(output);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Não foi possível salvar as configurações: {0}", e.Message);
            }
        }

        _pdfWord.CheckedChanged += (_, _) =>
        {
            if (_pdfWord.Checked)
                Save(PdfOutput.Word);
        };
        _pdfTce.CheckedChanged += (_, _) =>
        {
            if (_pdfTce.Checked)
                Save(PdfOutput.Tce);
        };

        var caption = new Label { Text = "Gerar:", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
        // Only makes sense for a PDF: a Word document in has no "generate Word" to choose.
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Visible = false };
        panel.Controls.Add(caption);
        panel.Controls.AddRange([_pdfWord, _pdfTce]);
        return panel;
    }

    private TabPage BuildTemplateTab()
    {
        var change = new Button { Text = "Trocar modelo...", Height = 30 };
        change.Click += (_, _) => ChooseTemplate();
        _openTemplateButton.Click += (_, _) =>
        {
            if (_templatePath is not null)
                OpenFile(_templatePath);
        };
        _builtInTemplateButton.Enabled = Config.HasBuiltInTemplate;
        _builtInTemplateButton.Click += (_, _) => UseBuiltInTemplate();

        var tab = new TabPage("Modelo") { Padding = new Padding(Pad) };
        tab.Controls.Add(Column(
            (new Label { Text = "Modelo da planilha", Font = TitleFont, AutoSize = true }, new Padding(0), false),
            (MutedLabel("Planilha usada como base. Novas linhas e códigos podem ser adicionados " +
                        "direto no modelo, mantendo o layout."), new Padding(0, 2, 0, 16), false),
            (new Label { Text = "Modelo atual", AutoSize = true }, new Padding(0), false),
            (_templateName, new Padding(0, 2, 0, 0), false),
            (_templateFull, new Padding(0, 0, 0, 16), false),
            (change, new Padding(0, 0, 0, 8), false),
            (_openTemplateButton, new Padding(0, 0, 0, 8), false),
            (_builtInTemplateButton, new Padding(0), false),
            (new Panel(), new Padding(0), true)));
        return tab;
    }

    private TabPage BuildSettingsTab()
    {
        var current = Config.LoadOutputLayout();
        var simplified = new RadioButton
        {
            Text = "Simplificada", Font = BoldFont, AutoSize = true, Checked = current == OutputLayout.Simplified,
        };
        var complete = new RadioButton
        {
            Text = "Completa", Font = BoldFont, AutoSize = true, Checked = current == OutputLayout.Complete,
        };
        var compact = new RadioButton
        {
            Text = "Compacta", Font = BoldFont, AutoSize = true, Checked = current == OutputLayout.Compact,
        };
        var hideEmpty = new CheckBox
        {
            Text = "Esconder também título e total das seções sem material", AutoSize = true,
            Checked = Config.LoadHideEmptySections(), Enabled = current == OutputLayout.Compact,
        };
        var feedback = new Label { AutoSize = true, MaximumSize = new Size(WrapWidth, 0) };
        var askNow = new RadioButton
        {
            Text = "Perguntar na hora de gerar", Font = BoldFont, AutoSize = true, Checked = Config.LoadAskPlaceholders(),
        };
        var leaveInWord = new RadioButton
        {
            Text = "Deixar em amarelo para preencher no Word", Font = BoldFont, AutoSize = true,
            Checked = !Config.LoadAskPlaceholders(),
        };
        askNow.CheckedChanged += (_, _) =>
        {
            try
            {
                Config.SaveAskPlaceholders(askNow.Checked);
                feedback.Text = "Salvo. Vale para os próximos documentos.";
                feedback.ForeColor = Color.SeaGreen;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Não foi possível salvar as configurações: {0}", e.Message);
                feedback.Text = $"Não foi possível salvar a configuração: {e.Message}";
                feedback.ForeColor = Color.Firebrick;
            }
        };

        void Save(OutputLayout layout)
        {
            try
            {
                Config.SaveOutputLayout(layout);
                feedback.Text = "Salvo. Vale para as próximas planilhas.";
                feedback.ForeColor = Color.SeaGreen;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Não foi possível salvar as configurações: {0}", e.Message);
                feedback.Text = $"Não foi possível salvar a configuração: {e.Message}";
                feedback.ForeColor = Color.Firebrick;
            }
        }

        simplified.CheckedChanged += (_, _) =>
        {
            if (simplified.Checked)
                Save(OutputLayout.Simplified);
        };
        complete.CheckedChanged += (_, _) =>
        {
            if (complete.Checked)
                Save(OutputLayout.Complete);
        };
        compact.CheckedChanged += (_, _) =>
        {
            hideEmpty.Enabled = compact.Checked;
            if (compact.Checked)
                Save(OutputLayout.Compact);
        };
        hideEmpty.CheckedChanged += (_, _) =>
        {
            try
            {
                Config.SaveHideEmptySections(hideEmpty.Checked);
                feedback.Text = "Salvo. Vale para as próximas planilhas.";
                feedback.ForeColor = Color.SeaGreen;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Não foi possível salvar as configurações: {0}", e.Message);
                feedback.Text = $"Não foi possível salvar a configuração: {e.Message}";
                feedback.ForeColor = Color.Firebrick;
            }
        };

        var tab = new TabPage("Configurações") { Padding = new Padding(Pad) };
        tab.Controls.Add(Column(
            (new Label { Text = "Configurações", Font = TitleFont, AutoSize = true }, new Padding(0), false),
            (MutedLabel("Escolha como a planilha é gerada. Nas duas, os valores ficam centralizados " +
                        "(menos a descrição dos materiais)."), new Padding(0, 2, 0, 16), false),
            (new Label { Text = "Tipo de planilha", AutoSize = true }, new Padding(0, 0, 0, 6), false),
            (simplified, new Padding(0), false),
            (MutedLabel("Só os materiais do documento, em uma lista única com o total no fim."),
                new Padding(18, 0, 0, 12), false),
            (complete, new Padding(0), false),
            (MutedLabel("A lista inteira do modelo, com as seções e o total de cada uma. Os materiais que " +
                        "não estão no documento ficam com quantidade 0."), new Padding(18, 0, 0, 12), false),
            (compact, new Padding(0), false),
            (MutedLabel("Mantém as seções e o total de cada uma, mas oculta os materiais que não estão " +
                        "no documento. Dá para reexibir as linhas no Excel."), new Padding(18, 0, 0, 4), false),
            (hideEmpty, new Padding(18, 0, 0, 12), false),
            (new Label { Text = "Campos em amarelo do Word (TCR/TCE)", AutoSize = true }, new Padding(0, 0, 0, 6), false),
            (askNow, new Padding(0), false),
            (MutedLabel("Antes de salvar, o app lista os campos em amarelo que não vieram do PDF para você digitar."),
                new Padding(18, 0, 0, 8), false),
            (leaveInWord, new Padding(0), false),
            (MutedLabel("O documento sai com esses campos em amarelo e você preenche depois, no Word."),
                new Padding(18, 0, 0, 12), false),
            (feedback, new Padding(0), false),
            (new Panel(), new Padding(0), true)));
        return tab;
    }

    private const string AuthorUrl = "https://github.com/23Edu4rd0";

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("Não foi possível abrir {0}: {1}", url, e.Message);
            MessageBox.Show($"Não foi possível abrir o link:\n{url}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static TabPage BuildCreditsTab()
    {
        // The informational version may carry a "+commit" suffix after the number.
        var version = Application.ProductVersion.Split('+')[0];
        var author = new LinkLabel { Text = "Eduardo Viana", Font = BoldFont, AutoSize = true };
        author.LinkClicked += (_, _) => OpenLink(AuthorUrl);
        var tab = new TabPage("Créditos") { Padding = new Padding(Pad) };
        tab.Controls.Add(Column(
            (new Label { Text = AppTitle, Font = TitleFont, AutoSize = true }, new Padding(0), false),
            (MutedLabel($"Versão {version}"), new Padding(0, 2, 0, 16), false),
            (new Label { Text = "Criado por", AutoSize = true }, new Padding(0), false),
            (author, new Padding(0, 2, 0, 16), false),
            (MutedLabel("Gera a lista de materiais (planilha) e o TCR/TCE (Word) a partir dos documentos " +
                        "do projeto, para você não ter que preencher tudo à mão."), new Padding(0), false),
            (new Panel(), new Padding(0), true)));
        return tab;
    }

    private TabPage BuildHeadersTab()
    {
        ShowHeaders(Config.LoadTableTitles());

        var input = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = ListFont,
            PlaceholderText = "Ex.: LISTA DE MATERIAIS",
            Margin = new Padding(0, 0, 8, 0),
        };
        var add = new Button { Text = "+ Adicionar", Dock = DockStyle.Fill, Margin = new Padding(0) };
        var remove = new Button { Text = "Remover selecionado", Enabled = false };
        var restore = new Button { Text = "Restaurar padrão" };

        var kind = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Font = ListFont,
        };
        kind.Items.AddRange(["Documento Word (.docx)", "Relatório PDF (.pdf)"]);
        kind.SelectedIndex = 0;
        kind.SelectedIndexChanged += (_, _) =>
        {
            _headersPdf = kind.SelectedIndex == 1;
            ShowHeaders(Config.LoadTableTitles(_headersPdf));
            SayHeaders(string.Empty, Muted);
        };

        var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Height = 32 };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        inputRow.Controls.Add(input, 0, 0);
        inputRow.Controls.Add(add, 1, 0);

        void AddHeader()
        {
            var title = Config.Normalize(input.Text);
            if (title.Length == 0)
            {
                SayHeaders("Digite o título da tabela antes de adicionar.", Color.Firebrick);
                input.Focus();
                return;
            }
            if (_headers.Items.Contains(title))
                SayHeaders($"\"{title}\" já está na lista.", Color.Firebrick);
            else if (SaveHeaders([.. _headers.Items.Cast<string>(), title], _headersPdf))
                SayHeaders($"\"{title}\" adicionado.", Color.SeaGreen);
            input.Clear();
            input.Focus();
        }

        add.Click += (_, _) => AddHeader();
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            AddHeader();
        };
        _headers.SelectedIndexChanged += (_, _) => remove.Enabled = _headers.SelectedIndex >= 0;
        remove.Click += (_, _) =>
        {
            if (_headers.SelectedItem is not string title)
                return;
            if (SaveHeaders([.. _headers.Items.Cast<string>().Where(t => t != title)], _headersPdf))
                SayHeaders($"\"{title}\" removido.", Muted);
        };
        restore.Click += (_, _) =>
        {
            if (Ask("Substituir a lista pelos cabeçalhos que vêm com o programa?\n\n" +
                    string.Join("\n", Config.DefaultTitles(_headersPdf).Select(t => "• " + t)),
                    "Restaurar padrão", MessageBoxIcon.Question) && SaveHeaders(Config.DefaultTitles(_headersPdf), _headersPdf))
                SayHeaders("Lista padrão restaurada.", Color.SeaGreen);
        };

        var tab = new TabPage("Cabeçalhos") { Padding = new Padding(Pad) };
        tab.Controls.Add(Column(
            (new Label { Text = "Cabeçalhos procurados", Font = TitleFont, AutoSize = true }, new Padding(0), false),
            (MutedLabel("Ao gerar, o programa mostra todas as tabelas do documento. As que têm o título " +
                        "nesta lista já vêm marcadas; as outras você marca na hora. Cada tipo de documento " +
                        "(Word ou PDF) tem a sua lista."),
                new Padding(0, 2, 0, 16), false),
            (new Label { Text = "Tipo de documento", Font = BoldFont, AutoSize = true }, new Padding(0, 0, 0, 4), false),
            (kind, new Padding(0, 0, 0, 10), false),
            (new Label { Text = "Novo cabeçalho", Font = BoldFont, AutoSize = true }, new Padding(0, 0, 0, 4), false),
            (inputRow, new Padding(0), false),
            (_headersFeedback, new Padding(0, 4, 0, 12), false),
            (new Label { Text = "Cabeçalhos cadastrados", Font = BoldFont, AutoSize = true },
                new Padding(0, 0, 0, 4), false),
            (_headers, new Padding(0, 0, 0, 8), true),
            (Row(30, remove, restore), new Padding(0), false)));
        return tab;
    }

    private void ShowHeaders(IEnumerable<string> titles)
    {
        _headers.BeginUpdate();
        _headers.Items.Clear();
        foreach (var title in titles)
            _headers.Items.Add(title);
        _headers.EndUpdate();
    }

    private void SayHeaders(string text, Color color)
    {
        _headersFeedback.Text = text;
        _headersFeedback.ForeColor = color;
    }

    /// <summary>Saves and shows the header list; on failure keeps the list as it was.</summary>
    private bool SaveHeaders(IReadOnlyList<string> titles, bool pdf)
    {
        try
        {
            Config.SaveTableTitles(titles, pdf);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Não foi possível salvar as configurações: {0}", e.Message);
            ShowError($"Não foi possível salvar a lista de cabeçalhos em:\n{Config.SettingsFile}\n\n" +
                      $"Detalhe: {e.Message}");
            return false;
        }
        if (pdf == _headersPdf)
            ShowHeaders(Config.LoadTableTitles(pdf));
        return true;
    }

    private void SetTemplate(string? path, bool save = true)
    {
        if (path is null)
        {
            _templatePath = null;
            _templateName.Text = "Nenhum modelo selecionado";
            _templateFull.Text = "Clique em \"Trocar modelo...\" para escolher a planilha base.";
            _openTemplateButton.Enabled = false;
            return;
        }
        _templatePath = Path.GetFullPath(path);
        _templateName.Text = Config.IsBuiltInTemplate(_templatePath)
            ? "Modelo padrão do programa"
            : Path.GetFileName(_templatePath);
        _templateFull.Text = _templatePath;
        _openTemplateButton.Enabled = true;
        if (save)
        {
            try
            {
                Config.SaveTemplatePath(_templatePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Não foi possível salvar as configurações: {0}", e.Message);
            }
        }
    }

    /// <summary>Switches to the template built into the executable, unpacking it when needed.</summary>
    private void UseBuiltInTemplate()
    {
        try
        {
            var unpack = !File.Exists(Config.BuiltInTemplateFile);
            if (!unpack && Config.BuiltInTemplateWasEdited())
            {
                var answer = MessageBox.Show(this,
                    "O modelo padrão foi alterado depois de instalado.\n\n" +
                    "Sim: voltar ao modelo original do programa (as alterações serão perdidas).\n" +
                    "Não: usar o modelo padrão com as suas alterações.",
                    "Modelo padrão", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel)
                    return;
                unpack = answer == DialogResult.Yes;
            }
            SetTemplate(unpack ? Config.UnpackBuiltInTemplate() : Config.BuiltInTemplateFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Não foi possível extrair o modelo padrão: {0}", e.Message);
            ShowWarning($"Não foi possível preparar o modelo padrão em:\n{Config.BuiltInTemplateFile}\n\n" +
                        "Se ele estiver aberto no Excel, feche-o e tente de novo.\n\n" +
                        $"Detalhe: {e.Message}", "Modelo padrão");
        }
    }

    private bool ChooseTemplate()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione o modelo",
            Filter = "Planilha Excel (*.xlsx)|*.xlsx",
            InitialDirectory = _templatePath is null ? "" : Path.GetDirectoryName(_templatePath),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return false;
        try
        {
            XlsxWriter.CheckTemplate(dialog.FileName);
        }
        catch (AppException e)
        {
            ShowWarning(e.Message, "Modelo inválido");
            return false;
        }
        SetTemplate(dialog.FileName);
        return true;
    }

    private void ChooseDocx()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione o documento",
            Filter = "Word ou PDF (*.docx;*.pdf)|*.docx;*.pdf|Documento Word (*.docx)|*.docx|Relatório do projeto (*.pdf)|*.pdf",
            InitialDirectory = _docxPath is null ? "" : Path.GetDirectoryName(_docxPath),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SetDocx(dialog.FileName);
    }

    private void SetDocx(string path)
    {
        if (Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal))
        {
            ShowWarning("Esse é um arquivo temporário que o Word cria enquanto o documento está aberto.\n\n" +
                        "Selecione o documento original (sem \"~$\" no início do nome).", "Documento");
            return;
        }
        if (!IsDocx(path) && !IsPdf(path))
        {
            ShowWarning($"\"{Path.GetFileName(path)}\" não é um documento .docx nem um PDF.\n\n" +
                        "Se for um arquivo .doc antigo, abra no Word e use Salvar como > Documento do Word (*.docx).",
                "Documento");
            return;
        }
        _docxPath = path;
        _drop.SetFile(Path.GetFileName(_docxPath),
            IsPdf(path) ? "PDF · relatório do projeto" : "Word · documento com planilhas de materiais");
        _generateButton.Enabled = true;
        _pdfChoice!.Visible = IsPdf(path);
        _generateButton.Text = IsPdf(path) ? "Gerar documento" : "Gerar planilha";
        _result.Text = string.Empty;
    }

    private static bool IsDocx(string path) => path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);

    private static bool IsPdf(string path) => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Accepts a .docx or .pdf dropped anywhere on the control or its children.</summary>
    private void EnableDocxDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
            e.Effect = e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } ? DragDropEffects.Copy
                : DragDropEffects.None;
        control.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && !_busy)
                SetDocx(files[0]);
        };
        foreach (Control child in control.Controls)
            EnableDocxDrop(child);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _generateButton.Enabled = !busy;
        _generateButton.Text = busy ? "Gerando..." : _docxPath is not null && IsPdf(_docxPath) ? "Gerar documento" : "Gerar planilha";
        _progress.Visible = busy;
        _drop.Enabled = !busy;
        UseWaitCursor = busy;
    }

    /// <summary>Remembers the generated file so it can be opened or shown in its folder from the result area.</summary>
    private void SetOutput(string path)
    {
        _lastOutput = path;
        _openOutput.Enabled = _showOutput.Enabled = File.Exists(path);
    }

    private void ShowInFolder()
    {
        if (_lastOutput is null || !File.Exists(_lastOutput))
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("Não foi possível abrir a pasta: {0}", e.Message);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.O) && !_busy)
        {
            ChooseDocx();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async void Generate()
    {
        if (_docxPath is null)
            return;
        var word = IsPdf(_docxPath);
        var kind = _pdfTce.Checked ? ReportKind.Tce : ReportKind.Tcr;
        if (!word && (_templatePath is null || !File.Exists(_templatePath)))
            SetTemplate(Config.LoadTemplatePath(), save: false);
        if (!word && (_templatePath is null || !File.Exists(_templatePath)))
        {
            ShowInfo(_templatePath is null
                    ? "Nenhum modelo de planilha foi configurado. Selecione a planilha modelo."
                    : $"O modelo \"{Path.GetFileName(_templatePath)}\" não foi encontrado em:\n" +
                      $"{Path.GetDirectoryName(_templatePath)}\n\nSelecione a planilha modelo.",
                "Modelo");
            if (!ChooseTemplate())
                return;
        }

        var docx = _docxPath;
        var template = _templatePath;
        SetBusy(true);
        _openOutput.Enabled = _showOutput.Enabled = false;
        _result.Text = "Lendo o documento...";
        try
        {
            var content = await Task.Run(() => IsPdf(docx) ? PdfReader.Read(docx) : DocxReader.Read(docx));
            if (content.Tables.Count == 0)
            {
                _result.Text = NothingFoundMessage(content, IsPdf(docx));
                ShowWarning(_result.Text.Replace("\r\n", "\n"), "Nada encontrado");
                return;
            }

            _result.Text = "Escolha as tabelas...";
            if (ChooseTables(content.Tables, IsPdf(docx)) is not var (tables, sum))
            {
                _result.Text = string.Empty;
                return;
            }
            if (word)
            {
                await GenerateWord(docx, content, tables, kind);
                return;
            }

            var groups = MaterialGroups.Group(tables);
            var materials = MaterialGroups.Combine(groups, sum);

            if (AskOutput(docx, word: false) is not { } chosen)
            {
                _result.Text = string.Empty;
                return;
            }
            // A file open in Excel cannot be replaced, so the spreadsheet gets the next free name instead.
            var output = Files.IsLocked(chosen) ? Files.FreeName(chosen) : chosen;
            var renamed = output != chosen
                ? $"\"{Path.GetFileName(chosen)}\" está aberta no Excel, então a planilha foi salva como " +
                  $"\"{Path.GetFileName(output)}\"."
                : null;

            _result.Text = "Gerando a planilha...";
            var layout = Config.LoadOutputLayout();
            var hideEmpty = Config.LoadHideEmptySections();
            var added = await Task.Run(() =>
                XlsxWriter.Fill(materials, content.Info, output, template!, layout, hideEmpty));

            SetOutput(output);
            _result.Text = SummaryMessage(content, tables, groups, materials, sum, added, output, layout);
            if (renamed is not null)
                _result.Text = renamed + "\r\n\r\n" + _result.Text;
            if (Ask(renamed is null ? "Planilha gerada. Abrir agora?" : $"Planilha gerada.\n\n{renamed}\n\nAbrir agora?",
                    "Concluído", MessageBoxIcon.Information))
                OpenFile(output);
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao gerar planilha: {0}", e.InnerException ?? e);
            _result.Text = "Não foi possível gerar a planilha.\r\n\r\n" + e.Message.Replace("\n", "\r\n");
            ShowWarning(e.Message, "Não foi possível gerar");
        }
        catch (Exception e)
        {
            Trace.TraceError("Falha ao gerar planilha: {0}", e);
            _result.Text = $"Erro inesperado: {e.Message}";
            ShowError("Ocorreu um erro inesperado ao gerar a planilha.\n\n" +
                      $"Detalhe: {e.Message}\n\n" +
                      "Se continuar acontecendo, envie este documento e o modelo para quem mantém o programa.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Shows the tables found so the user picks the ones to use. Remembers newly checked
    /// titles when asked. Null when cancelled.
    /// </summary>
    private (IReadOnlyList<MaterialTable> Tables, bool Sum)? ChooseTables(
        IReadOnlyList<MaterialTable> tables, bool fromPdf)
    {
        var registered = Config.LoadTableTitles(fromPdf);
        using var picker = new TableSelectionForm(tables, registered);
        if (picker.ShowDialog(this) != DialogResult.OK)
            return null;

        var remember = picker.TitlesToRemember;
        if (remember.Count > 0 && SaveHeaders([.. registered, .. remember], fromPdf))
            SayHeaders($"{remember.Count} cabeçalho(s) adicionado(s) a partir do documento.", Color.SeaGreen);
        return (picker.Selected, picker.Sum);
    }

    private static string NothingFoundMessage(DocumentContent content, bool fromPdf)
    {
        if (fromPdf)
            return AppendWarnings("Não encontrei a lista de materiais no PDF.\r\n\r\n" +
                                  "O programa espera o relatório do projeto exportado pelo Route Map, com as tabelas " +
                                  "\"Material / Qtd. / Un. / Valor unit. / Total\".", content.Warnings);
        var message = content.EmbeddedWorkbooks == 0
            ? "O documento não tem nenhuma planilha do Excel incorporada.\r\n\r\n" +
              "O programa lê apenas tabelas inseridas como objeto do Excel (Inserir > Tabela > Planilha do Excel), " +
              "não tabelas comuns do Word."
            : $"O documento tem {content.EmbeddedWorkbooks} planilha(s) do Excel, mas nenhuma parece uma lista " +
              "de materiais.\r\n\r\n" +
              "O programa espera o nome do material na primeira coluna e a quantidade (um número) na terceira.";
        return AppendWarnings(message, content.Warnings);
    }

    private static string Quantity(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static string SummaryMessage(DocumentContent content, IReadOnlyList<MaterialTable> tables,
        IReadOnlyList<IReadOnlyList<Occurrence>> groups, OrderedDictionary<string, Material> materials,
        bool summed, IReadOnlyList<string> added, string output, OutputLayout layout)
    {
        var kind = layout switch
        {
            OutputLayout.Complete => "completa (lista inteira do modelo)",
            OutputLayout.Compact => "compacta (seções mantidas, materiais não usados ocultos)",
            _ => "simplificada",
        };
        var message = $"Planilha {kind}.\r\n" +
                      $"{materials.Count} materiais processados, de {tables.Count} tabela(s).\r\n" +
                      $"{materials.Count - added.Count} encontrados no modelo.\r\n\r\n" +
                      $"Salvo em:\r\n{output}";
        if (added.Count > 0)
            message += "\r\n\r\nAdicionados como novas linhas (sem código Protheus):\r\n- " +
                       string.Join("\r\n- ", added);

        var info = content.Info;
        var missing = new List<string>();
        if (info.Code is null)
            missing.Add("código do projeto");
        if (info.Date is null)
            missing.Add("data");
        if (info.City is null)
            missing.Add("cidade");
        if (missing.Count > 0)
            message += $"\r\n\r\nNão encontrado no documento (preencha à mão): {string.Join(", ", missing)}.";

        var repeated = groups.Where(g => g.Count > 1).ToList();
        if (repeated.Count > 0)
            message += $"\r\n\r\n{repeated.Count} material(is) repetido(s), " +
                       (summed ? "com as quantidades somadas:" : "contado(s) só na primeira ocorrência:") +
                       "\r\n- " + string.Join("\r\n- ", repeated.Select(g =>
                           $"{g[0].Name}: {Quantity(summed ? g.Sum(o => o.Material.Quantity) : g[0].Material.Quantity)}"));

        var unused = content.Tables.Except(tables).Select(t => t.Title).Distinct().ToList();
        if (unused.Count > 0)
            message += "\r\n\r\nTabelas não usadas:\r\n- " + string.Join("\r\n- ", unused);
        return AppendWarnings(message, content.Warnings);
    }

    private static string AppendWarnings(string message, IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? message : message + "\r\n\r\nAvisos:\r\n- " + string.Join("\r\n- ", warnings);

    private string? AskOutput(string docx, bool word, ReportKind kind = ReportKind.Tcr)
    {
        using var dialog = new SaveFileDialog
        {
            Title = word ? "Salvar documento" : "Salvar planilha",
            InitialDirectory = Path.GetDirectoryName(docx),
            FileName = word ? $"{ReportName(docx, kind)}.docx" : $"{OutputName(docx)}.xlsx",
            DefaultExt = word ? "docx" : "xlsx",
            Filter = word ? "Documento Word (*.docx)|*.docx" : "Planilha Excel (*.xlsx)|*.xlsx",
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>"Relatorio_Rede_X" becomes "TCR_Rede_X" (or TCE); any other name just gets the prefix.</summary>
    private static string ReportName(string pdfPath, ReportKind kind)
    {
        var prefix = kind == ReportKind.Tce ? "TCE" : "TCR";
        var name = Path.GetFileNameWithoutExtension(pdfPath);
        var renamed = Regex.Replace(name, "^Relat[óo]rio", prefix, RegexOptions.IgnoreCase);
        return renamed != name ? renamed : prefix + "_" + name;
    }

    /// <summary>Fills the TCR Word template with the PDF report and offers to open it.</summary>
    private async Task GenerateWord(string pdf, DocumentContent content, IReadOnlyList<MaterialTable> tables,
        ReportKind kind)
    {
        if (content.Report is not { } report)
            throw new AppException("Não foi possível ler os dados do projeto no PDF.");

        var name = kind.ToString().ToUpperInvariant();
        var template = Config.LoadWordTemplate(kind);
        if (template is null)
        {
            ShowInfo($"Nenhum modelo do Word ({name}) foi configurado. Selecione o documento {name} em branco.", "Modelo do Word");
            template = ChooseWordTemplate(kind);
            if (template is null)
            {
                _result.Text = string.Empty;
                return;
            }
        }

        using (var form = new ReportDataForm(report, kind))
        {
            if (form.ShowDialog(this) != DialogResult.OK)
            {
                _result.Text = string.Empty;
                return;
            }
            report = form.Result;
        }

        if (AskOutput(pdf, word: true, kind) is not { } chosen)
        {
            _result.Text = string.Empty;
            return;
        }
        // A file open in Word cannot be replaced, so the document gets the next free name instead.
        var output = Files.IsLocked(chosen) ? Files.FreeName(chosen) : chosen;
        var renamed = output != chosen
            ? $"\"{Path.GetFileName(chosen)}\" está aberto no Word, então o documento foi salvo como " +
              $"\"{Path.GetFileName(output)}\"."
            : null;

        _result.Text = "Gerando o documento...";
        var (warnings, objects) = await Task.Run(() => TcrWriter.Fill(template, report, tables, output, kind, AskPlaceholders));
        _result.Text = "Atualizando as tabelas pelo Word...";
        var refresh = await Task.Run(() => WordRefresh.Run(output, objects));
        if (refresh is not null)
            warnings = [.. warnings, refresh];

        var message = $"Documento {name} gerado.\r\n" +
                      $"{tables.Sum(t => t.Items.Count)} materiais em {tables.Count} tabela(s).\r\n\r\n" +
                      $"Salvo em:\r\n{output}\r\n\r\n" +
                      $"Confira no Word: o código do {name} (capa) e o mapa não vêm do PDF, e a mão de obra " +
                      "ficou como no modelo.";
        SetOutput(output);
        _result.Text = AppendWarnings(renamed is null ? message : renamed + "\r\n\r\n" + message, warnings);
        if (Ask(renamed is null ? "Documento gerado. Abrir agora?" : $"Documento gerado.\n\n{renamed}\n\nAbrir agora?",
                "Concluído", MessageBoxIcon.Information))
            OpenFile(output);
    }

    /// <summary>
    /// Called from the generating thread: asks the user for the highlighted spots, unless the setting
    /// says to leave them for Word. Null leaves them all highlighted.
    /// </summary>
    private Dictionary<int, string>? AskPlaceholders(IReadOnlyList<Placeholder> items)
    {
        if (!Config.LoadAskPlaceholders())
            return null;
        return (Dictionary<int, string>?)Invoke(() =>
        {
            using var form = new PlaceholderForm(items);
            return form.ShowDialog(this) == DialogResult.OK ? form.Values : null;
        });
    }

    /// <summary>Lets the user pick the blank TCR when none is built in; remembers the choice.</summary>
    private byte[]? ChooseWordTemplate(ReportKind kind)
    {
        using var dialog = new OpenFileDialog
        {
            Title = $"Selecione o modelo {kind.ToString().ToUpperInvariant()} (Word)",
            Filter = "Documento Word (*.docx)|*.docx",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;
        try
        {
            var bytes = Files.ReadAllBytesShared(dialog.FileName);
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes),
                System.IO.Compression.ZipArchiveMode.Read);
            if (zip.GetEntry("word/document.xml") is null)
                throw new InvalidDataException();
            Config.SaveWordTemplatePath(kind, dialog.FileName);
            return bytes;
        }
        catch (InvalidDataException)
        {
            ShowWarning($"\"{Path.GetFileName(dialog.FileName)}\" não é um documento Word (.docx) válido.", "Modelo inválido");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowWarning($"Não foi possível abrir o modelo.\n\nDetalhe: {e.Message}", "Modelo do Word");
            return null;
        }
    }

    /// <summary>Same name as the TCE document, with the leading TCE swapped for TCX.</summary>
    private static string OutputName(string docxPath) =>
        Regex.Replace(Path.GetFileNameWithoutExtension(docxPath), "^tce", m => m.Value == "tce" ? "tcx" : "TCX",
            RegexOptions.IgnoreCase);

    private void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Trace.TraceWarning("Não foi possível abrir {0}: {1}", path, e.Message);
            ShowWarning($"Não foi possível abrir \"{Path.GetFileName(path)}\".\n\n" +
                        "Verifique se o Excel (ou outro programa para .xlsx) está instalado.\n\n" +
                        $"Detalhe: {e.Message}", "Abrir arquivo");
        }
    }

    private bool Ask(string text, string caption, MessageBoxIcon icon) =>
        MessageBox.Show(this, text, caption, MessageBoxButtons.YesNo, icon) == DialogResult.Yes;

    private void ShowInfo(string text, string caption) =>
        MessageBox.Show(this, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowWarning(string text, string caption) =>
        MessageBox.Show(this, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void ShowError(string text) =>
        MessageBox.Show(this, text, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
