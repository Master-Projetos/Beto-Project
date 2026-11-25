namespace ListaMateriais.Pages;

/// <summary>Version, updates, author and the keyboard shortcuts.</summary>
internal sealed class AboutPage : Page
{
    private const string AuthorUrl = "https://github.com/23Edu4rd0";

    public override string Title => "&Sobre";
    public override string Glyph => "";

    public AboutPage(UpdateBanner updates)
    {
        // The informational version may carry a "+commit" suffix after the number.
        var version = Application.ProductVersion.Split('+')[0];
        var author = new LinkLabel { Text = "Eduardo Viana", Font = Ui.BoldFont, AutoSize = true, LinkColor = Ui.Accent };
        author.LinkClicked += (_, _) =>
        {
            if (Ui.Open(AuthorUrl) is not null)
                Dialogs.Warn(this, "Não foi possível abrir o link", AuthorUrl);
        };

        var status = new Label
        {
            AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(10, 7, 0, 0),
            Text = Updater.CanUpdateItself ? string.Empty : "Esta cópia (de desenvolvimento) não se atualiza sozinha.",
        };
        var check = Ui.Secondary(new Button { Text = "Procurar atualizações", AutoSize = true, Padding = new Padding(8, 2, 8, 2) });
        check.Click += async (_, _) =>
        {
            check.Enabled = false;
            status.Text = "Verificando...";
            status.Text = await updates.CheckAsync(userAsked: true);
            check.Enabled = true;
        };
        var automatic = new CheckBox { Text = "Procurar sozinho ao abrir o programa", AutoSize = true, Checked = Config.LoadCheckUpdates() };
        automatic.CheckedChanged += (_, _) => Config.TrySave(() => Config.SaveCheckUpdates(automatic.Checked));

        SetContent(new Stack()
            .Title(Dialogs.AppTitle, $"Versão {version}")
            .Add(Ui.Hint("Gera a lista de materiais (planilha), o TCR/TCE (Word) e mapas KMZ a partir dos " +
                         "documentos do projeto, e simplifica mapas KMZ complicados, para você não ter que fazer tudo à mão."), bottom: 20)
            .Add(Ui.Hint("A preguiça é a mãe do progresso. Se o homem não tivesse preguiça de caminhar, não teria inventado a roda. ~ Mario Quintana, 1973 "), bottom: 20)
            .Caption("Atualizações")
            .Add(Stack.Flow(check, status))
            .Add(automatic, top: 4, bottom: 20)
            .Caption("Criado por")
            .Add(author, bottom: 20)
            .Caption("Agradecimento")
            .Add(Ui.Hint("\"Esse app não seria desenvolvido sem sua colaboração, Rafinha, obrigado por tudo.\" - Rafael Mesquita - 2026"), bottom: 20)
            .Caption("Atalhos do teclado")
            .Add(Ui.Hint("Ctrl+O — escolher o arquivo da página atual\n" +
                         "Ctrl+1 a Ctrl+9 e Ctrl+0 — trocar de página\n" +
                         "Enter — gerar\n" +
                         "Arrastar um arquivo para a janela — abrir na página certa (PDF e Word em Gerar, Excel em Mapa, " +
                         "KMZ/KML em Simplificar ou, com Conferir aberta, em Conferir; com Apresentação aberta, a planilha vai para ela)"))
            .Add(Ui.Hint("Não somos um setor de sistemas, e não sabemos ler uma planilha de Excel. Obrigado por nao fazerem nada por nos, e somente pegar os creditos. ~ Eduardo Viana, 2026"), top: 20)
            .Spring(), 460);
    }
}
