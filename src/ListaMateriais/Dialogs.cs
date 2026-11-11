namespace ListaMateriais;

/// <summary>
/// The app's message boxes, as Windows task dialogs: a heading that says what happened, the detail
/// below it, and buttons named after what they do.
/// </summary>
internal static class Dialogs
{
    public const string AppTitle = "Preguiça App";

    public static void Info(Control? owner, string heading, string text) =>
        Show(owner, TaskDialogIcon.Information, heading, text, null, TaskDialogButton.OK);

    public static void Warn(Control? owner, string heading, string text, string? details = null) =>
        Show(owner, TaskDialogIcon.Warning, heading, text, details, TaskDialogButton.OK);

    public static void Error(Control? owner, string heading, string text, string? details = null) =>
        Show(owner, TaskDialogIcon.Error, heading, text, details, TaskDialogButton.OK);

    /// <summary>A question with one action and a way out. True when the user chose the action.</summary>
    public static bool Confirm(Control? owner, string heading, string text, string action, string cancel = "Cancelar",
        bool warning = false)
    {
        var yes = new TaskDialogButton(action);
        var no = new TaskDialogButton(cancel);
        return Show(owner, warning ? TaskDialogIcon.Warning : TaskDialogIcon.Information, heading, text, null, yes, no) == yes;
    }

    /// <summary>Shows the buttons given and returns the index of the one clicked; -1 when the dialog was closed.</summary>
    public static int Choose(Control? owner, TaskDialogIcon icon, string heading, string text, params string[] buttons)
    {
        var options = buttons.Select(b => new TaskDialogButton(b)).ToArray();
        return Array.IndexOf(options, Show(owner, icon, heading, text, null, options));
    }

    private static TaskDialogButton Show(Control? owner, TaskDialogIcon icon, string heading, string text, string? details,
        params TaskDialogButton[] buttons)
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
