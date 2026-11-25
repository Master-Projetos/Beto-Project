namespace ListaMateriais;

/// <summary>File access that tolerates files kept open by Word or Excel.</summary>
internal static class Files
{
    /// <summary>
    /// Reads the whole file while other programs keep it open (as Explorer does when copying).
    /// A document open in Word yields its last saved version.
    /// </summary>
    /// <summary>A title made usable as a file name: no characters Windows refuses, not too long.</summary>
    public static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. title.Select(c => invalid.Contains(c) ? ' ' : c)]).Trim().TrimEnd('.');
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 ? "apresentacao" : name.Length > 80 ? name[..80].TrimEnd() : name;
    }

    public static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream((int)Math.Min(stream.Length, int.MaxValue));
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>True when another program holds the file so it cannot be replaced (e.g. open in Excel).</summary>
    public static bool IsLocked(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only file or folder: saving reports that with its own message.
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>
    /// Writes to a temporary file and swaps it in at the end, so a half-written file is never left behind.
    /// Failures become messages for the user.
    /// </summary>
    public static void SaveSafely(string output, Action<Stream> write)
    {
        var fullOutput = Path.GetFullPath(output);
        var folder = Path.GetDirectoryName(fullOutput)!;
        var temp = Path.Combine(folder, $".~{Guid.NewGuid():N}.tmp");
        var name = Path.GetFileName(fullOutput);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                write(stream);
            File.Move(temp, fullOutput, overwrite: true);
        }
        catch (Exception e) when (File.Exists(fullOutput)
                                  && (e is UnauthorizedAccessException || e.HResult is SharingViolation or LockViolation))
        {
            throw new AppException($"Não foi possível substituir \"{name}\".\n\n" +
                                   "Se essa planilha estiver aberta no Excel, feche-a e tente de novo. " +
                                   "Ou salve com outro nome.", e);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            throw new AppException($"Não foi possível salvar na pasta:\n{folder}\n\n" +
                                   "A pasta não existe ou você não tem permissão para gravar nela. " +
                                   "Escolha outra pasta.", e);
        }
        catch (IOException e)
        {
            throw new AppException($"Não foi possível salvar \"{name}\".\n\n" +
                                   "Verifique se há espaço em disco e se a pasta está acessível.\n\n" +
                                   $"Detalhe: {e.Message}", e);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Não foi possível apagar o arquivo temporário {0}: {1}", temp, e.Message);
            }
        }
    }

    /// <summary>"name (2).xlsx", "name (3).xlsx"... the first one that is free.</summary>
    public static string FreeName(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// True when Word has the document open: it keeps an owner file named "~$" plus the
    /// name (long names lose their first two characters) next to it.
    /// </summary>
    public static bool IsOpenInWord(string docxPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(docxPath))!;
        var name = Path.GetFileName(docxPath);
        return File.Exists(Path.Combine(folder, "~$" + name))
               || (name.Length > 2 && File.Exists(Path.Combine(folder, "~$" + name[2..])));
    }
}
