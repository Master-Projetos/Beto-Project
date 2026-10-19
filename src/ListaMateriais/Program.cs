using System.Diagnostics;

namespace ListaMateriais;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowCrash(e.ExceptionObject as Exception);
        Application.Run(new MainForm());
    }

    private static void ShowCrash(Exception? e)
    {
        Trace.TraceError("Erro não tratado: {0}", e);
        MessageBox.Show(
            e is AppException
                ? e.Message
                : "Ocorreu um erro inesperado.\n\n" +
                  $"Detalhe: {e?.Message ?? "desconhecido"}\n\n" +
                  "Se continuar acontecendo, feche e abra o programa novamente.",
            "Preguiça App", MessageBoxButtons.OK, e is AppException ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
    }
}
