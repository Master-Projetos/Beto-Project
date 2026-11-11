namespace ListaMateriais.Pages;

/// <summary>How the spreadsheet is laid out and what happens to the fields the PDF does not fill.</summary>
internal sealed class SettingsPage : Page
{
    public override string Title => "Con&figurações";
    public override string Glyph => "\uE713";

    public SettingsPage()
    {
        var feedback = new WrapLabel();
        var layout = Config.LoadOutputLayout();
        var hideEmpty = new CheckBox
        {
            Text = "Esconder também título e total das seções sem material", AutoSize = true,
            Checked = Config.LoadHideEmptySections(), Enabled = layout == OutputLayout.Compact,
        };
        hideEmpty.CheckedChanged += (_, _) =>
            Ui.Say(feedback, Config.TrySave(() => Config.SaveHideEmptySections(hideEmpty.Checked)), "Salvo. Vale para as próximas planilhas.");

        RadioButton Layout(string text, OutputLayout value)
        {
            var option = Option(text, layout == value);
            option.CheckedChanged += (_, _) =>
            {
                if (value == OutputLayout.Compact)
                    hideEmpty.Enabled = option.Checked;
                if (option.Checked)
                    Ui.Say(feedback, Config.TrySave(() => Config.SaveOutputLayout(value)), "Salvo. Vale para as próximas planilhas.");
            };
            return option;
        }

        var ask = Config.LoadAskPlaceholders();
        var askNow = Option("Perguntar na hora de gerar", ask);
        var leaveInWord = Option("Deixar em amarelo para preencher no Word", !ask);
        askNow.CheckedChanged += (_, _) =>
            Ui.Say(feedback, Config.TrySave(() => Config.SaveAskPlaceholders(askNow.Checked)), "Salvo. Vale para os próximos documentos.");

        // Each group of options sits in its own panel: radio buttons exclude one another per container.
        var spreadsheet = new Stack { Dock = DockStyle.Top, AutoSize = true }
            .Add(Layout("Simplificada", OutputLayout.Simplified))
            .Add(Ui.Hint("Só os materiais do documento, em uma lista única com o total no fim."), indent: 18, bottom: 10)
            .Add(Layout("Completa", OutputLayout.Complete))
            .Add(Ui.Hint("A lista inteira do modelo, com as seções e o total de cada uma. Os materiais que não estão " +
                         "no documento ficam com quantidade 0."), indent: 18, bottom: 10)
            .Add(Layout("Compacta", OutputLayout.Compact))
            .Add(Ui.Hint("Mantém as seções e o total de cada uma, mas oculta os materiais que não estão no documento. " +
                         "Dá para reexibir as linhas no Excel."), indent: 18, bottom: 4)
            .Add(hideEmpty, indent: 18);
        var word = new Stack { Dock = DockStyle.Top, AutoSize = true }
            .Add(askNow)
            .Add(Ui.Hint("Antes de salvar, o programa lista os campos em amarelo que não vieram do PDF para você digitar."),
                indent: 18, bottom: 10)
            .Add(leaveInWord)
            .Add(Ui.Hint("O documento sai com esses campos em amarelo e você preenche depois, no Word."), indent: 18);

        SetContent(new Stack()
            .Title("Configurações", "As escolhas são salvas na hora e valem para os próximos arquivos gerados.")
            .Caption("Tipo de planilha")
            .Add(spreadsheet)
            .Caption("Campos em amarelo do Word (TCR/TCE)", top: 20)
            .Add(word)
            .Add(feedback, top: 14)
            .Spring(), 520);
    }

    private static RadioButton Option(string text, bool selected) =>
        new() { Text = text, Font = Ui.BoldFont, AutoSize = true, Checked = selected };
}
