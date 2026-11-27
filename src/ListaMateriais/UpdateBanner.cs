using System.Diagnostics;

namespace ListaMateriais;

/// <summary>
/// The strip at the top of the window that offers a new version and installs it. It stays hidden
/// until there is one; a check that fails in the background says nothing, as there is nothing the
/// user needs to do about it.
/// </summary>
internal sealed class UpdateBanner : Panel
{
    private readonly Label _text = new() { AutoSize = true, Margin = new Padding(0, 8, 14, 0), Font = Ui.BoldFont };
    private readonly Button _install = Ui.Primary(new Button { Text = "Atualizar agora", AutoSize = true, Padding = new Padding(10, 2, 10, 2) });
    private readonly Button _later = Ui.Secondary(new Button { Text = "Agora não", AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(8, 3, 0, 3) });
    private UpdateInfo? _update;
    private bool _checking;

    public UpdateBanner()
    {
        Dock = DockStyle.Top;
        Height = 46;
        BackColor = Ui.AccentSoft;
        Padding = new Padding(16, 6, 16, 6);
        Visible = false;
        var row = Stack.Flow(_text, _install, _later);
        row.WrapContents = false;
        row.Dock = DockStyle.Fill;
        Controls.Add(row);
        _install.Click += (_, _) => Install();
        _later.Click += (_, _) => Visible = false;
    }

    /// <summary>True while a version is being downloaded: closing the window then would leave it half done.</summary>
    public bool Installing { get; private set; }

    /// <summary>Looks for a new version and shows the strip when there is one.</summary>
    /// <param name="userAsked">False for the check on start: failures are only logged.</param>
    /// <returns>What happened, in a sentence for the "Sobre" page.</returns>
    public async Task<string> CheckAsync(bool userAsked)
    {
        if (_checking || Installing)
            return "Já estou verificando...";
        _checking = true;
        try
        {
            var update = await Updater.CheckAsync();
            if (update is null)
                return $"Você já está na versão mais nova ({Updater.Current.ToString(3)}).";
            Offer(update);
            return $"Versão {update.Version.ToString(3)} disponível.";
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao procurar atualização: {0}", e.InnerException?.Message ?? e.Message);
            if (userAsked)
                Dialogs.Warn(this, "Não foi possível procurar atualizações",
                    "Confira a conexão com a internet e tente de novo.", e.InnerException?.Message);
            return "Não foi possível verificar agora.";
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>The new version is already in place here: failing to open it is not a failed update.</summary>
    private void Restart()
    {
        try
        {
            Updater.Restart();
        }
#pragma warning disable CA1031 // Whatever the reason, the user only has to open the program again.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Trace.TraceError("Falha ao abrir a versão nova: {0}", e);
            _text.Text = "Atualização instalada. Feche e abra o programa.";
            _install.Visible = _later.Visible = false;
        }
    }

    private void Offer(UpdateInfo update)
    {
        _update = update;
        _text.Text = $"Versão nova disponível: {update.Version.ToString(3)} (você tem a {Updater.Current.ToString(3)}).";
        _install.Text = "Atualizar agora";
        _install.Enabled = _later.Enabled = true;
        _install.Visible = Updater.CanUpdateItself;
        if (!Updater.CanUpdateItself)
            _text.Text += " Esta cópia não se atualiza sozinha: baixe pelo GitHub.";
        Visible = true;
    }

    private async void Install()
    {
        if (_update is not { } update || Installing)
            return;
        Installing = true;
        _install.Enabled = _later.Enabled = false;
        var progress = new Progress<double>(done => _text.Text = $"Baixando a versão {update.Version.ToString(3)}... {done:P0}");
        try
        {
            await Updater.InstallAsync(update, progress);
            _text.Text = "Pronto. Abrindo a versão nova...";
            Installing = false;
            Restart();
        }
        catch (AppException e)
        {
            Trace.TraceWarning("Falha ao atualizar: {0}", e.InnerException ?? e);
            _text.Text = "A atualização não foi concluída.";
            _install.Enabled = _later.Enabled = true;
            _install.Text = "Tentar de novo";
            var answer = Dialogs.Choose(this, TaskDialogIcon.Warning, "Não foi possível atualizar", e.Message,
                "Abrir página de download", "Fechar");
            if (answer == 0 && Ui.Open(update.Page.ToString()) is not null)
                Dialogs.Warn(this, "Não foi possível abrir o link", update.Page.ToString());
        }
        finally
        {
            Installing = false;
        }
    }
}
