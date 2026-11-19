namespace ListaMateriais;

/// <summary>
/// The app's message boxes, as Windows task dialogs: a heading that says what happened, the detail
/// below it, and buttons named after what they do.
/// </summary>
internal static class Dialogs
{
    public const string AppTitle = "Preguiça App";

    public static void Info(Control? owner, string heading, string text) =>
        Show(owner, TaskDialogIcon.Information, heading, text, null, null, TaskDialogButton.OK);

    public static void Warn(Control? owner, string heading, string text, string? details = null) =>
        Show(owner, TaskDialogIcon.Warning, heading, text, details, null, TaskDialogButton.OK);

    /// <summary>An error, with a quote about laziness below it to soften the blow.</summary>
    public static void Error(Control? owner, string heading, string text, string? details = null) =>
        Show(owner, TaskDialogIcon.Error, heading, text, details, RandomQuote(), TaskDialogButton.OK);

    private static readonly string[] Quotes =
    [
        "“A preguiça é a mãe do progresso. Se o homem não tivesse preguiça de caminhar, não teria inventado a roda.” ~ Mario Quintana, 1973",
        "“Deixem-me dizer-vos neste momento que não fazer nada é a coisa mais difícil do mundo, a mais difícil e a mais intelectual.” ~ Oscar Wilde, 1891",
        "“A ambição é uma desculpa esfarrapada por não se ser suficientemente preguiçoso.” ~ Milan Kundera, s.d.",
        "“Se você faz o trabalho ruim o bastante, às vezes não lhe pedem para fazê-lo novamente.” ~ Bill Watterson, 1990",
        "“A minha vontade é forte, porém minha disposição de espírito é fraca.” ~ Carlos Drummond de Andrade, 1930",
        "“Não gosto de trabalho, nem quando é outro a fazê-lo.” ~ Mark Twain, s.d.",
        "“Que nada é impossível não é verdade; todo o mundo faz nada com facilidade.” ~ Millôr Fernandes, 1964",
        "“O trabalho é o refúgio daqueles que não têm nada melhor para fazer.” ~ Oscar Wilde, 1894",
        "“A preguiça é o hábito de descansar antes de estar cansado.” ~ Jules Renard, 1925",
        "“Nunca deixe para amanhã o que você pode fazer depois de amanhã igualmente bem.” ~ Mark Twain, 1870",
    ];

    internal static string RandomQuote() => Quotes[Random.Shared.Next(Quotes.Length)];

    /// <summary>A question with one action and a way out. True when the user chose the action.</summary>
    public static bool Confirm(Control? owner, string heading, string text, string action, string cancel = "Cancelar",
        bool warning = false)
    {
        var yes = new TaskDialogButton(action);
        var no = new TaskDialogButton(cancel);
        return Show(owner, warning ? TaskDialogIcon.Warning : TaskDialogIcon.Information, heading, text, null, null, yes, no) == yes;
    }

    /// <summary>Shows the buttons given and returns the index of the one clicked; -1 when the dialog was closed.</summary>
    public static int Choose(Control? owner, TaskDialogIcon icon, string heading, string text, params string[] buttons)
    {
        var options = buttons.Select(b => new TaskDialogButton(b)).ToArray();
        return Array.IndexOf(options, Show(owner, icon, heading, text, null, null, options));
    }

    private static TaskDialogButton Show(Control? owner, TaskDialogIcon icon, string heading, string text, string? details,
        string? footnote, params TaskDialogButton[] buttons)
    {
        var page = new TaskDialogPage
        {
            Caption = AppTitle,
            Heading = heading,
            Text = text,
            Icon = icon,
            AllowCancel = true,
            SizeToContent = true,
            DefaultButton = buttons[0],
        };
        foreach (var button in buttons)
            page.Buttons.Add(button);
        if (!string.IsNullOrWhiteSpace(footnote))
            page.Footnote = new TaskDialogFootnote(footnote);
        if (!string.IsNullOrWhiteSpace(details))
            page.Expander = new TaskDialogExpander(details)
            {
                CollapsedButtonText = "Mostrar detalhes", ExpandedButtonText = "Ocultar detalhes",
                Position = TaskDialogExpanderPosition.AfterFootnote,
            };

        var window = owner?.FindForm();
        return window is { IsHandleCreated: true, IsDisposed: false }
            ? TaskDialog.ShowDialog(window, page)
            : TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
    }
}
