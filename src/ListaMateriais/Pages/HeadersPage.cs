namespace ListaMateriais.Pages;

/// <summary>The table titles that start checked when a document is read; Word and the PDF report each have a list.</summary>
internal sealed class HeadersPage : Page
{
    private bool _pdf;
    private readonly ListBox _list = new() { IntegralHeight = false, Font = Ui.ListFont };
    private readonly WrapLabel _feedback = new();

    public override string Title => "&Cabeçalhos";
    public override string Glyph => "\uE8FD";

    public HeadersPage()
    {
        var input = new TextBox { Font = Ui.ListFont, PlaceholderText = "Ex.: LISTA DE MATERIAIS", Margin = new Padding(0, 0, 8, 0) };
        var add = Ui.Secondary(new Button { Text = "Adicionar", Margin = Padding.Empty });
        var remove = Ui.Secondary(new Button { Text = "Remover selecionado", Enabled = false });
        var restore = Ui.Secondary(new Button { Text = "Restaurar padrão" });

        var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Ui.ListFont };
        kind.Items.AddRange(["Documento Word (.docx) e TCE/TCR em PDF", "Relatório do Route Map (.pdf)"]);
        kind.SelectedIndex = 0;
        kind.SelectedIndexChanged += (_, _) =>
        {
            _pdf = kind.SelectedIndex == 1;
            ShowList();
            Say(string.Empty, Ui.Muted);
        };

        var inputRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 32 };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        input.Dock = add.Dock = DockStyle.Fill;
        inputRow.Controls.Add(input, 0, 0);
        inputRow.Controls.Add(add, 1, 0);

        void Add()
        {
            var title = Config.Normalize(input.Text);
            if (title.Length == 0)
            {
                Say("Digite o título da tabela antes de adicionar.", Ui.Bad);
                input.Focus();
                return;
            }
            if (_list.Items.Contains(title))
                Say($"\"{title}\" já está na lista.", Ui.Bad);
            else if (Save([.. _list.Items.Cast<string>(), title], _pdf))
                Say($"\"{title}\" adicionado.", Ui.Good);
            input.Clear();
            input.Focus();
        }

        void Remove()
        {
            if (_list.SelectedItem is string title && Save([.. _list.Items.Cast<string>().Where(t => t != title)], _pdf))
                Say($"\"{title}\" removido.", Ui.Muted);
        }

        add.Click += (_, _) => Add();
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            Add();
        };
        _list.SelectedIndexChanged += (_, _) => remove.Enabled = _list.SelectedIndex >= 0;
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
                Remove();
        };
        remove.Click += (_, _) => Remove();
        restore.Click += (_, _) =>
        {
            if (Dialogs.Confirm(this, "Restaurar a lista padrão?",
                    "A lista atual será substituída pelos cabeçalhos que vêm com o programa:\n\n" +
                    string.Join("\n", Config.DefaultTitles(_pdf).Select(t => "• " + t)), "Restaurar")
                && Save(Config.DefaultTitles(_pdf), _pdf))
                Say("Lista padrão restaurada.", Ui.Good);
        };

        SetContent(new Stack()
            .Title("Cabeçalhos procurados", "Ao gerar, o programa mostra todas as tabelas do documento. As que têm o " +
                                            "título nesta lista já vêm marcadas; as outras você marca na hora.")
            .Caption("Tipo de documento")
            .Add(kind, bottom: 12)
            .Caption("Novo cabeçalho")
            .Add(inputRow)
            .Add(_feedback, top: 4, bottom: 12)
            .Caption("Cabeçalhos cadastrados")
            .Add(_list, bottom: 8, fill: true)
            .Add(Stack.Row(30, remove, restore)), 460);
        ShowList();
    }

    /// <summary>Adds titles the user checked while generating, so they start checked next time.</summary>
    public void Remember(IReadOnlyList<string> titles, bool pdf)
    {
        if (titles.Count > 0 && Save([.. Config.LoadTableTitles(pdf), .. titles], pdf))
            Say($"{titles.Count} cabeçalho(s) adicionado(s) a partir do documento.", Ui.Good);
    }

    private void ShowList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var title in Config.LoadTableTitles(_pdf))
            _list.Items.Add(title);
        _list.EndUpdate();
    }

    private void Say(string text, Color color)
    {
        _feedback.Text = text;
        _feedback.ForeColor = color;
    }

    /// <summary>Saves and shows the list; on failure keeps it as it was.</summary>
    private bool Save(IReadOnlyList<string> titles, bool pdf)
    {
        if (Config.TrySave(() => Config.SaveTableTitles(titles, pdf)) is { } error)
        {
            Dialogs.Error(this, "Não foi possível salvar a lista de cabeçalhos", $"Arquivo: {Config.SettingsFile}", error);
            return false;
        }
        if (pdf == _pdf)
            ShowList();
        return true;
    }
}
