using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ListaMateriais;

/// <summary>A newer version published on GitHub, ready to download.</summary>
/// <param name="Sha256">Expected hash of the download, in lowercase hexadecimal.</param>
internal sealed record UpdateInfo(Version Version, Uri Download, long Size, string Sha256, Uri Page);

/// <summary>
/// Keeps the app up to date from the releases of its GitHub repository. The released executable is
/// built without the client's templates (the repository is public); the ones built into the copy
/// handed out before are kept in the settings folder by <see cref="Config.KeepBuiltInTemplates"/>.
/// </summary>
/// <remarks>
/// Windows does not let a running executable be overwritten, but it does let it be renamed: the new
/// file is downloaded next to it, the running one is renamed to ".old", the new one takes its name,
/// and the app restarts. The ".old" file is deleted on the next start.
/// </remarks>
internal static class Updater
{
    private const string Repository = "Master-Projetos/Beto-Project";
    private const string AssetName = "ListaMateriais.exe";
    private static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases/latest");

    /// <summary>This version, as major.minor.build.</summary>
    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    // After Current: static fields are initialized in the order they are written, and the client uses it.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ListaMateriais-Updater", Current.ToString()));
        return client;
    }

    private static string? Executable => Environment.ProcessPath;

    /// <summary>
    /// True for the published single-file executable. Runs from Visual Studio or <c>dotnet run</c> have
    /// the app in a .dll beside it, which an update must not replace.
    /// </summary>
    public static bool CanUpdateItself =>
        Executable is { } exe && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        && !File.Exists(Path.ChangeExtension(exe, ".dll"));

    /// <summary>Deletes what a previous update left behind: the old executable and an unfinished download.</summary>
    public static void CleanUp()
    {
        if (!CanUpdateItself || Executable is not { } exe)
            return;
        foreach (var leftover in new[] { exe + ".old", exe + ".new" })
        {
            try
            {
                if (File.Exists(leftover))
                    File.Delete(leftover);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The old process may still be closing; the next start tries again.
                Trace.TraceWarning("Não foi possível apagar {0}: {1}", leftover, e.Message);
            }
        }
    }

    /// <summary>The newer version published, or null when this one is the latest.</summary>
    /// <exception cref="AppException">GitHub could not be reached or answered something unexpected.</exception>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // Nothing published yet.
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            return await ReadRelease(json.RootElement, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                      or FormatException or UriFormatException
                                      || (e is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            throw new AppException($"Não foi possível consultar se há versão nova.\n\nDetalhe: {e.Message}", e);
        }
    }

    private static async Task<UpdateInfo?> ReadRelease(JsonElement release, CancellationToken cancel)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()
            || release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            return null;
        if (ParseVersion(release.GetProperty("tag_name").GetString() ?? "") is not { } version || !IsNewer(version, Current))
            return null;

        JsonElement? exe = null;
        JsonElement? hashFile = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                exe = asset;
            else if (string.Equals(name, AssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                hashFile = asset;
        }
        if (exe is not { } file)
            return null;

        // GitHub computes the hash of each file; older releases may not have it, so the .sha256 file published along is used.
        var hash = file.TryGetProperty("digest", out var digest) && digest.GetString() is { } d
                                                                && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? d["sha256:".Length..]
            : hashFile is { } h ? await DownloadText(new Uri(h.GetProperty("browser_download_url").GetString()!), cancel).ConfigureAwait(false)
            : null;
        hash = hash?.Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
        if (hash is not { Length: 64 })
        {
            Trace.TraceWarning("A versão {0} não tem o hash do executável; ela não será instalada.", version);
            return null;
        }

        return new UpdateInfo(version, new Uri(file.GetProperty("browser_download_url").GetString()!),
            file.GetProperty("size").GetInt64(), hash, new Uri(release.GetProperty("html_url").GetString()!));
    }

    private static async Task<string> DownloadText(Uri url, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads the new version, checks it, and puts it in place of the running executable. The app
    /// must then <see cref="Restart"/>. Nothing changes when anything fails.
    /// </summary>
    /// <param name="progress">From 0 to 1.</param>
    /// <exception cref="AppException">The download or the replacement failed.</exception>
    public static async Task InstallAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancel = default)
    {
        if (!CanUpdateItself || Executable is not { } exe)
            throw new AppException("Esta cópia do programa não pode se atualizar sozinha.");
        var download = exe + ".new";
        var old = exe + ".old";
        try
        {
            await Download(update, download, progress, cancel).ConfigureAwait(false);
            if (File.Exists(old))
                File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Move(download, exe);
            }
            catch
            {
                File.Move(old, exe);
                throw;
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                      || (e is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            TryDelete(download);
            var folder = Path.GetDirectoryName(exe);
            throw new AppException(e is UnauthorizedAccessException
                ? $"Sem permissão para trocar o programa na pasta:\n{folder}\n\nBaixe a versão nova pela página de downloads e substitua o arquivo à mão."
                : $"Não foi possível baixar ou instalar a versão {update.Version.ToString(3)}.\n\nDetalhe: {e.Message}", e);
        }
        catch
        {
            TryDelete(download);
            throw;
        }
    }

    private static async Task Download(UpdateInfo update, string path, IProgress<double>? progress, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var response = await Http.GetAsync(update.Download, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                received += read;
                if (update.Size > 0)
                    progress?.Report(Math.Min(1, (double)received / update.Size));
            }
        }
        var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (received != update.Size || hash != update.Sha256)
            throw new IOException("o arquivo baixado não confere com o publicado (download incompleto ou alterado).");
    }

    /// <summary>Starts the new version and closes this one.</summary>
    public static void Restart()
    {
        if (Executable is not { } exe)
            return;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
        Application.Exit();
    }

    /// <summary>"v2.3.0", "2.3", "v2.3.0-beta+abc" → 2.3.0; null when it is not a version.</summary>
    internal static Version? ParseVersion(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
            text = text[..end];
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    internal static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Não foi possível apagar {0}: {1}", path, e.Message);
        }
    }
}
