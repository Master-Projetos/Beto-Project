using System.Diagnostics;

namespace ListaMateriais;

internal sealed class MainForm : Form
{
    private const int FormWidth = 420;
    private const int FormHeight = 540;
    private const int Pad = 16;
    private const int WrapWidth = FormWidth - 60;

    private static readonly Font TitleFont = new("Segoe UI", 14, FontStyle.Bold);
    private static readonly Font BoldFont = new("Segoe UI", 10, FontStyle.Bold);
    private static readonly Font BigButtonFont = new("Segoe UI", 11, FontStyle.Bold);
    private static readonly Color Muted = Color.FromArgb(0x66, 0x66, 0x66);

    private string? _docxPath;
    private string? _templatePath;

    private readonly Label _docxName = MutedLabel("Nenhum documento selecionado");
    private readonly Label _templateName = new() { AutoSize = true, Font = BoldFont };
    private readonly Label _templateFull = MutedLabel(string.Empty);
    private readonly Button _generateButton;
    private readonly TextBox _result;

    public MainForm()
    {
        Text = "Lista de Materiais";
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(FormWidth, FormHeight);
        MinimumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;

        _generateButton = new Button
        {
            Text = "Gerar planilha",
            Font = BigButtonFont,
            Dock = DockStyle.Fill,
            Height = 44,
            Enabled = false,
        };
        _generateButton.Click += (_, _) => Generate();

        _result = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
        };

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGenerateTab());
        tabs.TabPages.Add(BuildTemplateTab());
        Controls.Add(tabs);
        Padding = new Padding(8);

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
            if (control is Button)
                control.Dock = DockStyle.Fill;
            panel.Controls.Add(control);
        }
        return panel;
    }

    private TabPage BuildGenerateTab()
    {
        var chooseDocx = new Button { Text = "Selecionar documento...", Height = 30 };
        chooseDocx.Click += (_, _) => ChooseDocx();

        var tab = new TabPage("Gerar planilha") { Padding = new Padding(Pad) };
        tab.Controls.Add(Column(
            (new Label { Text = "Gerar lista de materiais", Font = TitleFont, AutoSize = true }, new Padding(0), false),
            (MutedLabel("Selecione o documento Word com a tabela de materiais."), new Padding(0, 2, 0, 16), false),
            (chooseDocx, new Padding(0), false),
            (_docxName, new Padding(0, 6, 0, 16), false),
            (_generateButton, new Padding(0), false),
            (new Label { Text = "Resultado", AutoSize = true }, new Padding(0, 20, 0, 4), false),
            (_result, new Padding(0), true)));
        return tab;
    }

    private TabPage BuildTemplateTab()
    {
        var change = new Button { Text = "Trocar modelo...", Height = 30 };
        change.Click += (_, _) => ChooseTemplate();
        var open = new Button { Text = "Abrir modelo no Excel", Height = 30 };
        open.Click += (_, _) =>
        {
            if (_templatePath is not null)
                OpenFile(_templatePath);
        };

        var tab = new TabPage("Modelo") { Padding = new Padding(Pad) };
        tab.Controls.Add(Column(
            (new Label { Text = "Modelo da planilha", Font = TitleFont, AutoSize = true }, new Padding(0), false),
            (MutedLabel("Planilha usada como base. Novas linhas e códigos podem ser adicionados " +
                        "direto no modelo, mantendo o layout."), new Padding(0, 2, 0, 16), false),
            (new Label { Text = "Modelo atual", AutoSize = true }, new Padding(0), false),
            (_templateName, new Padding(0, 2, 0, 0), false),
            (_templateFull, new Padding(0, 0, 0, 16), false),
            (change, new Padding(0, 0, 0, 8), false),
            (open, new Padding(0), false),
            (new Panel(), new Padding(0), true)));
        return tab;
    }

    private void SetTemplate(string? path, bool save = true)
    {
        if (path is null)
        {
            _templatePath = null;
            _templateName.Text = "Nenhum modelo selecionado";
            _templateFull.Text = "Clique em \"Trocar modelo...\" para escolher.";
            return;
        }
        _templatePath = Path.GetFullPath(path);
        _templateName.Text = Path.GetFileName(_templatePath);
        _templateFull.Text = _templatePath;
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
        SetTemplate(dialog.FileName);
        return true;
    }

    private void ChooseDocx()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione o documento",
            Filter = "Documento Word (*.docx)|*.docx",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _docxPath = dialog.FileName;
        _docxName.Text = Path.GetFileName(_docxPath);
        _generateButton.Enabled = true;
        _result.Text = string.Empty;
    }

    private async void Generate()
    {
        if (_docxPath is null)
            return;
        if (_templatePath is null || !File.Exists(_templatePath))
        {
            MessageBox.Show(this, "Nenhum modelo encontrado. Selecione a planilha modelo.", "Modelo",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!ChooseTemplate())
                return;
        }

        string output;
        using (var dialog = new SaveFileDialog
               {
                   Title = "Salvar planilha",
                   InitialDirectory = Path.GetDirectoryName(_docxPath),
                   FileName = $"{Path.GetFileNameWithoutExtension(_docxPath)}_materiais.xlsx",
                   DefaultExt = "xlsx",
                   Filter = "Planilha Excel (*.xlsx)|*.xlsx",
               })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            output = dialog.FileName;
        }

        var docx = _docxPath;
        var template = _templatePath!;
        _generateButton.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var (materials, added) = await Task.Run(() =>
            {
                var read = DocxReader.ReadMaterials(docx);
                return read.Count == 0 ? (read, (IReadOnlyList<string>)[]) : (read, XlsxWriter.Fill(read, output, template));
            });

            if (materials.Count == 0)
            {
                MessageBox.Show(this, "Nenhuma tabela de MATERIAL foi encontrada no documento.", "Nada encontrado",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var message = $"{materials.Count} materiais processados.\r\n" +
                          $"{materials.Count - added.Count} encontrados no modelo.\r\n\r\n" +
                          $"Salvo em:\r\n{output}";
            if (added.Count > 0)
                message += "\r\n\r\nAdicionados como novas linhas (sem código Protheus):\r\n- " +
                           string.Join("\r\n- ", added);
            _result.Text = message;

            if (MessageBox.Show(this, "Planilha gerada. Abrir agora?", "Concluído",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                OpenFile(output);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException and not FileNotFoundException
                                      and not DirectoryNotFoundException)
        {
            MessageBox.Show(this, "Não foi possível salvar. Feche a planilha se ela estiver aberta no Excel.",
                "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception e)
        {
            Trace.TraceError("Falha ao gerar planilha: {0}", e);
            MessageBox.Show(this, $"Falha ao gerar planilha:\n{e.Message}", "Erro",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _generateButton.Enabled = true;
        }
    }

    private static void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
}
