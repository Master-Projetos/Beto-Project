namespace ListaMateriais.Pages;

/// <summary>Version, author and the keyboard shortcuts.</summary>
internal sealed class AboutPage : Page
{
    private const string AuthorUrl = "https://github.com/23Edu4rd0";

    public override string Title => "&Sobre";
    public override string Glyph => "\uE946";

    public AboutPage()
    {
        // The informational version may carry a "+commit" suffix after the number.
        var version = Application.ProductVersion.Split('+')[0];
        var author = new LinkLabel { Text = "Eduardo Viana", Font = Ui.BoldFont, AutoSize = true, LinkColor = Ui.Accent };
        author.LinkClicked += (_, _) =>
        {
            if (Ui.Open(AuthorUrl) is not null)
                Dialogs.Warn(this, "Não foi possível abrir o link", AuthorUrl);
        };

        SetContent(new Stack()
            .Title(Dialogs.AppTitle, $"Versão {version}")
            .Add(Ui.Hint("Gera a lista de materiais (planilha), o TCR/TCE (Word) e mapas de pontos (KMZ) a partir dos " +
                         "documentos do projeto, para você não ter que preencher tudo à mão."), bottom: 20)
            .Caption("Criado por")
            .Add(author, bottom: 20)
            .Caption("Atalhos do teclado")
            .Add(Ui.Hint("Ctrl+O — escolher o arquivo da página atual\n" +
                         "Ctrl+1 a Ctrl+6 — trocar de página\n" +
                         "Enter — gerar\n" +
                         "Arrastar um arquivo para a janela — abrir na página certa (PDF e Word em Gerar, Excel em Mapa)"))
            .Spring(), 380);
    }
}
