using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemotePlay;

/// <summary>Looks for a newer release on GitHub, downloads it, checks its SHA-256 and swaps the program folder after exit.</summary>
internal static class Updater
{
    internal const string Repository = "Bernardo-Petrovsky/RemotePlayCino";
    private const long MaxPackageBytes = 700L * 1024 * 1024;
    private static readonly string[] TrustedHosts =
        ["github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    private static readonly HttpClient Http = CreateClient();

    internal sealed record Release(string Version, Uri Package, string Sha256, long Size)
    {
        internal bool IsExecutable => Package.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static Version Current { get; } =
        Version.TryParse(Application.ProductVersion.Split('+')[0], out Version? parsed) ? parsed : new Version(0, 0);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
            { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RemotePlayCino-Updater", "1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static bool IsTrusted(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && TrustedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the newest release when it is newer than this program, or null when this one is current.</summary>
    internal static async Task<Release?> CheckAsync(CancellationToken stop = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using HttpResponseMessage response = await Http.GetAsync(
            $"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Todavía no hay versiones publicadas.");
        response.EnsureSuccessStatusCode();
        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        return await ReadReleaseAsync(json.RootElement, timeout.Token);
    }

    internal static async Task<Release?> ReadReleaseAsync(JsonElement root, CancellationToken stop)
    {
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest))
            throw new InvalidOperationException($"La versión «{tag}» no tiene un número válido.");
        if (latest <= Current)
            return null;

        JsonElement? executable = null, archive = null;
        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("RemotePlayCino", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                archive = asset;
            else if (name.Equals("RemotePlayCino.exe", StringComparison.OrdinalIgnoreCase))
                executable = asset;
        }
        JsonElement? package = PortableFiles.IsBundled ? executable ?? archive : archive ?? executable;
        if (package is not { } zip)
            throw new InvalidOperationException("La versión publicada no incluye el EXE portátil ni el paquete ZIP.");
        var url = new Uri(zip.GetProperty("browser_download_url").GetString() ?? "");
        if (!IsTrusted(url))
            throw new InvalidOperationException("La descarga no viene de GitHub.");
        long size = zip.GetProperty("size").GetInt64();
        if (size is <= 0 or > MaxPackageBytes)
            throw new InvalidOperationException("El tamaño del paquete no es válido.");

        string? hash = null;
        if (zip.TryGetProperty("digest", out JsonElement digest) && digest.GetString() is { } text && text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            hash = text[7..];
        else
        {
            string checksumName = zip.GetProperty("name").GetString() + ".sha256";
            foreach (JsonElement sum in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(sum.GetProperty("name").GetString(), checksumName, StringComparison.OrdinalIgnoreCase)) continue;
                var sumUrl = new Uri(sum.GetProperty("browser_download_url").GetString() ?? "");
                if (IsTrusted(sumUrl))
                    hash = (await Http.GetStringAsync(sumUrl, stop)).Split(' ', '\t', '\r', '\n')[0];
                break;
            }
        }
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidOperationException("La versión no trae una huella SHA-256 válida; por seguridad no se instala.");
        return new Release(latest.ToString(), url, hash.ToLowerInvariant(), size);
    }

    /// <summary>Downloads and verifies the package, unpacks it and returns the script that swaps the files once this program exits.</summary>
    internal static async Task<string> PrepareAsync(Release release, Action<string> status, CancellationToken stop = default)
    {
        string target = AppContext.BaseDirectory.TrimEnd('\\');
        if (target.IndexOfAny(['%', '!', '^', '&', '"']) >= 0)
            throw new InvalidOperationException("La carpeta del programa tiene caracteres que impiden actualizar solo. Descarga la versión nueva a mano.");

        CleanupOld();
        string work = Path.Combine(Path.GetTempPath(), "RemotePlayCino-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        string zipPath = Path.Combine(work, release.IsExecutable ? "paquete.exe" : "paquete.zip");

        status("Descargando… 0 %");
        using (HttpResponseMessage response = await Http.GetAsync(release.Package, HttpCompletionOption.ResponseHeadersRead, stop))
        {
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } finalUrl && !IsTrusted(finalUrl))
                throw new InvalidOperationException("La descarga se redirigió fuera de GitHub.");
            await using Stream source = await response.Content.ReadAsStreamAsync(stop);
            await using FileStream file = File.Create(zipPath);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read, lastPercent = -1;
            while ((read = await source.ReadAsync(buffer, stop)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes)
                    throw new InvalidOperationException("El paquete es más grande de lo esperado.");
                await file.WriteAsync(buffer.AsMemory(0, read), stop);
                int percent = (int)(total * 100 / release.Size);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    status($"Descargando… {Math.Min(percent, 100)} %");
                }
            }
        }

        status("Comprobando la huella…");
        string actual;
        await using (FileStream check = File.OpenRead(zipPath))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, stop));
        if (!actual.Equals(release.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("La huella del paquete no coincide; se descartó la descarga.");

        status("Descomprimiendo…");
        string unpacked = Path.Combine(work, "nuevo");
        if (release.IsExecutable)
        {
            Directory.CreateDirectory(unpacked);
            File.Move(zipPath, Path.Combine(unpacked, "RemotePlayCino.exe"));
        }
        else ZipFile.ExtractToDirectory(zipPath, unpacked);
        string source2 = unpacked;
        if (!File.Exists(Path.Combine(source2, "RemotePlayCino.exe")))
        {
            string[] inner = Directory.GetDirectories(unpacked);
            if (inner.Length == 1 && File.Exists(Path.Combine(inner[0], "RemotePlayCino.exe")))
                source2 = inner[0];
            else
                throw new InvalidOperationException("El paquete no contiene RemotePlayCino.exe.");
        }

        ValidateExecutable(Path.Combine(source2, "RemotePlayCino.exe"), release.Version);
        string script = Path.Combine(work, "actualizar.cmd");
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            $"set \"SRC={source2}\"",
            $"set \"DEST={target}\"",
            $"set \"WORK={work}\"",
            $"set \"PID={Environment.ProcessId}\"",
            ":wait",
            "tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul",
            "if not errorlevel 1 (ping -n 2 127.0.0.1 >nul & goto wait)",
            "xcopy \"%SRC%\\*\" \"%DEST%\" /E /Y /Q >nul",
            "start \"\" \"%DEST%\\RemotePlayCino.exe\"",
            "cd /d \"%TEMP%\"",
            "rd /s /q \"%WORK%\" 2>nul",
            ""));
        return script;
    }

    internal static void Launch(string script) =>
        Process.Start(new ProcessStartInfo(script)
        {
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetTempPath()
        });

    internal static void ValidateExecutable(string path, string expectedVersion)
    {
        string actual = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0] ?? "";
        if (!Version.TryParse(actual, out Version? parsed) || !Version.TryParse(expectedVersion, out Version? expected) || parsed != expected)
            throw new InvalidDataException("El ejecutable descargado no corresponde a la versión publicada. No se instaló.");
    }

    private static void CleanupOld()
    {
        try
        {
            foreach (string folder in Directory.GetDirectories(Path.GetTempPath(), "RemotePlayCino-update-*"))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.Note($"No se pudo limpiar una actualización anterior: {ex.Message}");
        }
    }
}
