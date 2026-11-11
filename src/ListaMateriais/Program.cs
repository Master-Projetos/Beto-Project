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
        if (e is AppException)
            Dialogs.Warn(null, "Não foi possível concluir", e.Message);
        else
            Dialogs.Error(null, "Ocorreu um erro inesperado", "Se continuar acontecendo, feche e abra o programa novamente.",
                e?.ToString());
    }
}
