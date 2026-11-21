using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemotePlay;

/// <summary>Prepares the bundled, versioned dependencies beside the portable executable.</summary>
internal static class PortableFiles
{
    internal const string ResourceName = "RemotePlay.Portable.Dependencies.zip";
    internal static bool IsBundled { get; } = typeof(PortableFiles).Assembly.GetManifestResourceInfo(ResourceName) is not null;
    private static readonly string[] AllowedFiles =
        ["msquic.dll", "SDL3.dll", "SDL3-LICENSE.txt", "Wired-Pro2-LICENSE.txt", "ffmpeg/ffmpeg.exe", "ffmpeg/LICENSE", "ffmpeg/README.txt"];
    private sealed record Asset(string Name, long Size, string Sha256);

    internal static void Prepare()
    {
        if (!IsBundled) return;
        string identity = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant())))[..24];
        using var gate = new Mutex(false, @"Local\RemotePlayCino-Portable-" + identity);
        bool entered = false;
        try
        {
            try { entered = gate.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new IOException("Otra copia está preparando esta carpeta. Vuelve a abrirla en un momento.");
            Ensure();
            AppStorage.Prepare();
        }
        finally { if (entered) gate.ReleaseMutex(); }
    }

    internal static void Ensure()
    {
        if (!IsBundled) return;
        using Stream payload = typeof(PortableFiles).Assembly.GetManifestResourceStream(ResourceName)!;
        Extract(payload, AppContext.BaseDirectory);
    }

    internal static void Extract(Stream payload, string destination)
    {
        string root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry manifest = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Falta el manifiesto portátil.");
        if (manifest.Length > 32_768) throw new InvalidDataException("El manifiesto portátil no es válido.");
        using Stream metadata = manifest.Open();
        Asset[] assets = JsonSerializer.Deserialize<Asset[]>(metadata) ?? throw new InvalidDataException("El manifiesto está vacío.");
        if (assets.Length != AllowedFiles.Length || archive.Entries.Count != assets.Length + 1 ||
            assets.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() != assets.Length)
            throw new InvalidDataException("La lista de archivos portátiles no es válida.");

        foreach (Asset asset in assets)
        {
            if (!AllowedFiles.Contains(asset.Name, StringComparer.Ordinal) || asset.Size is < 0 or > 400_000_000 ||
                asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Un archivo portátil no es válido.");
            ZipArchiveEntry entry = archive.GetEntry(asset.Name) ?? throw new InvalidDataException("Falta un archivo portátil.");
            if (entry.Length != asset.Size) throw new InvalidDataException("El tamaño de un archivo portátil no coincide.");
            string target = Path.GetFullPath(Path.Combine(root, asset.Name));
            if (!target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Un archivo apunta fuera de la carpeta portátil.");
            string parent = Path.GetDirectoryName(target)!;
            RejectLink(parent);
            RejectLink(target);
            Directory.CreateDirectory(parent);
            if (Matches(target, asset)) continue;
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (Stream input = entry.Open())
                using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                if (!Matches(temporary, asset)) throw new InvalidDataException("La huella de un archivo portátil no coincide.");
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("La carpeta portátil contiene un enlace de archivos. Usa una carpeta normal.");
    }

    private static bool Matches(string path, Asset asset)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size) return false;
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
