using System.Globalization;

namespace ListaMateriais;

/// <summary>
/// Shows what was read from the PDF report so the user can confirm and correct it, and fill in what the
/// PDF does not have (document code, POP, contract term...), before the Word document is generated.
/// </summary>
internal sealed class ReportDataForm : Form
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Color Muted = Color.FromArgb(0x66, 0x66, 0x66);

    private sealed record Field(string Label, bool Multiline, Func<ProjectReport, string?> Get,
        Func<ProjectReport, string?, ProjectReport> Set, bool Number = false, string? Hint = null);

    private readonly ReportKind _kind;
    private readonly ProjectReport _original;
    private readonly List<(Field Field, TextBox Box)> _boxes = [];
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, Anchor = AnchorStyles.Left };

    /// <summary>The report with the user's corrections; valid after the dialog returns OK.</summary>
    public ProjectReport Result { get; private set; }

    private readonly CheckBox _importMap = new()
    {
        Text = "Importar a imagem do mapa do PDF para o Word", AutoSize = true, Checked = true,
        Margin = new Padding(0, 4, 0, 6),
    };

    private static readonly Color Missing = Color.FromArgb(0xFF, 0xF8, 0xC4);
    private static readonly Color Rule = Color.FromArgb(0xD9, 0xD9, 0xD9);
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private readonly Label _subtitle = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 2, 0, 0) };
    private readonly ToolTip _tip = new();

    public ReportDataForm(ProjectReport report, ReportKind kind)
    {
        _original = Result = report;
        _kind = kind;
        var name = kind == ReportKind.Tce ? "TCE" : "TCR";

        Text = $"Dados do {name}";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(820, 700);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = true;

        // Header: what this is and how many fields still need attention.
        var header = new Panel { Dock = DockStyle.Top, Height = 74, Padding = new Padding(20, 14, 20, 10), BackColor = Color.White };
        header.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Rule), 0, header.Height - 1, header.Width, header.Height - 1);
        var title = new Label
        {
            Text = $"Confira os dados do {name}", Font = new Font("Segoe UI", 14, FontStyle.Bold), AutoSize = true,
        };
        var headerText = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
        };
        headerText.Controls.AddRange([title, _subtitle]);
        header.Controls.Add(headerText);

        // Body: one section per group, fields in two columns, label above each box.
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(20, 8, 20, 12),
        };
        foreach (var group in Groups())
            body.Controls.Add(BuildSection(group.Title, group.Fields, report, body));
        body.SizeChanged += (_, _) => ResizeSections(body);
        Shown += (_, _) => ResizeSections(body);

        // Footer: map option, validation message and the buttons side by side.
        var ok = new Button { Text = $"Gerar {name}", Width = 130, Height = 32, UseVisualStyleBackColor = true };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Width = 110, Height = 32 };
        Ui.Primary(ok);
        Ui.Secondary(cancel);
        ok.Click += (_, _) => Confirm();
        AcceptButton = ok;
        CancelButton = cancel;

        _importMap.Enabled = report.Map is not null;
        _importMap.Checked = report.Map is not null;
        if (report.Map is null)
            _importMap.Text += " (não há imagem no PDF)";
        _importMap.Margin = new Padding(0, 6, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            Margin = new Padding(0),
        };
        buttons.Controls.AddRange([cancel, ok]);
        _error.Margin = new Padding(0, 6, 0, 0);

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
        };
        left.Controls.AddRange([_importMap, _error]);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(20, 10, 20, 12), BackColor = Color.FromArgb(0xF6, 0xF6, 0xF6) };
        footer.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Rule), 0, 0, footer.Width, 0);
        footer.Controls.Add(left);
        footer.Controls.Add(buttons);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);
        UpdateSubtitle();
    }

    private Control BuildSection(string title, Field[] fields, ProjectReport report, Control host)
    {
        var section = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(0),
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var heading = new Label
        {
            Text = title.ToUpperInvariant(), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x2F, 0x6B, 0x8A), Margin = new Padding(0, 12, 0, 2),
        };
        section.Controls.Add(heading);
        section.SetColumnSpan(heading, 2);
        var rule = new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Rule, Margin = new Padding(0, 0, 0, 6) };
        section.Controls.Add(rule);
        section.SetColumnSpan(rule, 2);

        foreach (var field in fields)
        {
            var box = new TextBox
            {
                Text = field.Get(report) ?? string.Empty, Dock = DockStyle.Top, Multiline = field.Multiline,
                AcceptsReturn = field.Multiline, Height = field.Multiline ? 76 : 25,
                ScrollBars = field.Multiline ? ScrollBars.Vertical : ScrollBars.None,
                PlaceholderText = field.Hint ?? string.Empty,
            };
            var label = new Label { Text = field.Label, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
            var cell = new TableLayoutPanel
            {
                ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 12, 8),
            };
            cell.Controls.Add(label);
            cell.Controls.Add(box);
            section.Controls.Add(cell);
            if (field.Multiline)
                section.SetColumnSpan(cell, 2);

            box.TextChanged += (_, _) =>
            {
                Tint(box);
                if (field.Number)
                    _errors.SetError(box, Valid(field, box) ? string.Empty : "Digite um número, ex.: 1.234,56");
                UpdateSubtitle();
            };
            Tint(box);
            _boxes.Add((field, box));
        }
        return section;
    }

    /// <summary>Empty fields are pale yellow, like the spots they will leave highlighted in the Word.</summary>
    private void Tint(TextBox box)
    {
        var empty = box.Text.Trim().Length == 0;
        box.BackColor = empty ? Missing : SystemColors.Window;
        _tip.SetToolTip(box, empty ? "Não encontrado no PDF. Em branco, fica como no modelo." : string.Empty);
    }

    private void UpdateSubtitle()
    {
        var empty = _boxes.Count(b => b.Box.Text.Trim().Length == 0);
        _subtitle.Text = empty == 0
            ? "Tudo preenchido. Revise e gere o documento."
            : $"{empty} campo(s) em amarelo não foram encontrados no PDF. Preencha agora ou deixe em branco " +
              "para ficarem como no modelo.";
    }

    private static bool Valid(Field field, TextBox box) =>
        !field.Number || box.Text.Trim().Length == 0 || double.TryParse(box.Text.Trim(), NumberStyles.Number, PtBr, out _);

    /// <summary>Sections follow the width of the scroll area (minus the scroll bar).</summary>
    private static void ResizeSections(FlowLayoutPanel body)
    {
        var width = Math.Max(300, body.ClientSize.Width - body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        foreach (Control section in body.Controls)
            section.Width = width;
    }

    private IEnumerable<(string Title, Field[] Fields)> Groups()
    {
        yield return ("Documento", [
            Line("Código do documento", r => r.Code, (r, v) => r with { Code = v }, hint: $"Ex.: {(_kind == ReportKind.Tce ? "TCE" : "TCR")}-LC-XXX-0000-00"),
            Line("Projetista", r => r.Author, (r, v) => r with { Author = v }),
            Line("Data da solicitação", r => r.Date, (r, v) => r with { Date = v }, hint: "dd/mm/aaaa"),
            Line("Cidade", r => r.City, (r, v) => r with { City = v }),
        ]);

        if (_kind == ReportKind.Tce)
        {
            yield return ("Cliente (B2B)", [
                Client("Empresa", c => c.Name, (c, v) => c with { Name = v }),
                Client("Endereço", c => c.Address, (c, v) => c with { Address = v }),
                Client("Coordenadas", c => c.Coordinates, (c, v) => c with { Coordinates = v }),
                Client("Atendimento (rede)", c => c.Network, (c, v) => c with { Network = v }, hint: "Metro, FTTH..."),
                Client("Produto / serviço", c => c.Service, (c, v) => c with { Service = v }),
                Client("Velocidade / banda", c => c.Bandwidth, (c, v) => c with { Bandwidth = v }),
                Client("Bloco IP", c => c.Ip, (c, v) => c with { Ip = v }),
                Client("Vendedor", c => c.Seller, (c, v) => c with { Seller = v }),
                Line("Prazo contratual", r => r.ContractTerm, (r, v) => r with { ContractTerm = v }, hint: "Ex.: 12, 24 e 36 meses"),
            ]);
            yield return ("Atendimento", [
                Line("POP de atendimento", r => r.Pop, (r, v) => r with { Pop = v }),
                Line("Equipamento", r => r.Equipment, (r, v) => r with { Equipment = v }, hint: "Ex.: OLT FIBERHOME"),
            ]);
        }
        else
        {
            yield return ("Estudo da área", [
                Line("Bairro", r => r.Neighborhood, (r, v) => r with { Neighborhood = v }),
                Num("Casas (HP)", r => r.Houses, (r, v) => r with { Houses = v }),
                Num("Portas existentes (HC)", r => r.ExistingPorts, (r, v) => r with { ExistingPorts = v }),
                Num("Novas portas", r => r.NewPorts, (r, v) => r with { NewPorts = v }),
                Line("Taxa de penetração", r => r.PenetrationRate, (r, v) => r with { PenetrationRate = v }, hint: "Ex.: 35,00%"),
                Multi("Objetivo", r => r.Objective, (r, v) => r with { Objective = v }),
            ]);
        }

        yield return ("Escopo e custo", [
            Multi("Escopo", r => r.Scope, (r, v) => r with { Scope = v }),
            Num("Lançamento (m)", r => r.LaunchMeters, (r, v) => r with { LaunchMeters = v }),
            Num("Postes", r => r.Posts, (r, v) => r with { Posts = v }),
            Num("Prazo da obra (dias)", r => r.Days, (r, v) => r with { Days = v }),
            Num("Mão de obra regional (R$)", r => r.RegionalLabor, (r, v) => r with { RegionalLabor = v }),
            Num("Mão de obra terceirizada (R$)", r => r.OutsourcedLabor, (r, v) => r with { OutsourcedLabor = v }),
            Num("Coef. de segurança (%)", r => r.SafetyPercent, (r, v) => r with { SafetyPercent = v }),
        ]);
    }

    private static Field Line(string label, Func<ProjectReport, string?> get, Func<ProjectReport, string?, ProjectReport> set,
        string? hint = null) => new(label, false, get, set, Hint: hint);

    private static Field Multi(string label, Func<ProjectReport, string?> get, Func<ProjectReport, string?, ProjectReport> set) =>
        new(label, true, get, set);

    private static Field Client(string label, Func<ClientInfo, string?> get, Func<ClientInfo, string?, ClientInfo> set,
        string? hint = null) =>
        new(label, false, r => get(r.Client ?? Empty), (r, v) => r with { Client = set(r.Client ?? Empty, v) }, Hint: hint);

    private static readonly ClientInfo Empty = new(null, null, null, null, null, null, null, null);

    private static Field Num(string label, Func<ProjectReport, double?> get, Func<ProjectReport, double?, ProjectReport> set) =>
        new(label, false, r => get(r) is { } n ? n.ToString("#,##0.##", PtBr) : null,
            (r, v) => set(r, v is null ? null : double.Parse(v, NumberStyles.Number, PtBr)), Number: true);

    private void Confirm()
    {
        var report = _original;
        foreach (var (field, box) in _boxes)
        {
            var text = box.Text.Trim();
            if (!Valid(field, box))
            {
                _error.Text = $"\"{field.Label}\" precisa ser um número (ex.: 1.234,56).";
                box.Focus();
                return;
            }
            report = field.Set(report, text.Length == 0 ? null : text);
        }
        Result = _importMap.Checked ? report : report with { Map = null };
        DialogResult = DialogResult.OK;
    }
}
