using System.Runtime.InteropServices;

namespace ListaMateriais;

/// <summary>
/// An Excel object in Word is drawn from a cached picture that only Word itself regenerates.
/// After the data is written into the embedded workbooks, this asks Word (if installed) to
/// activate each changed object once, so the picture shows the new values right away.
/// </summary>
internal static class WordRefresh
{
    private const int WdInlineShapeEmbeddedOle = 1;
    private const int VerbPrimary = -1;

    /// <returns>Null when done; otherwise why the pictures were left as in the template.</returns>
    public static string? Run(string path, IReadOnlyList<int> objects)
    {
        if (objects.Count == 0)
            return null;
        var type = Type.GetTypeFromProgID("Word.Application");
        if (type is null)
            return "O Word não está instalado: as tabelas têm os dados, mas só aparecem atualizadas depois de " +
                   "abrir cada uma (duplo clique) no Word.";

        dynamic? word = null;
        dynamic? document = null;
        try
        {
            word = Activator.CreateInstance(type)!;
            word.Visible = false;
            word.DisplayAlerts = 0;
            document = word.Documents.Open(path, false, false);

            var position = -1;
            foreach (dynamic shape in document.InlineShapes)
            {
                if ((int)shape.Type != WdInlineShapeEmbeddedOle)
                    continue;
                position++;
                if (!objects.Contains(position))
                    continue;
                shape.OLEFormat.DoVerb(VerbPrimary);
                Thread.Sleep(300);
            }
            document.Save();
            return null;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return "Não foi possível atualizar a aparência das tabelas pelo Word; os dados estão nelas, mas só " +
                   $"aparecem atualizados depois de abrir cada uma (duplo clique). Detalhe: {e.Message}";
        }
        finally
        {
            try
            {
                document?.Close(false);
                word?.Quit();
            }
            catch (COMException)
            {
                // Word is already gone.
            }
        }
    }
}
