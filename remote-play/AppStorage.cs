namespace RemotePlay;

/// <summary>Keeps the single-file portable profile beside the program; folder editions keep their existing profile.</summary>
internal static class AppStorage
{
    internal static string LegacyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePlay");
    internal static string ProfileDirectory => PortableFiles.IsBundled
        ? Path.Combine(AppContext.BaseDirectory, "Datos") : LegacyDirectory;

    internal static void Prepare()
    {
        if (PortableFiles.IsBundled) ImportOnce(ProfileDirectory, LegacyDirectory);
    }

    internal static void ImportOnce(string profileDirectory, string legacyDirectory)
    {
        Directory.CreateDirectory(profileDirectory);
        string marker = Path.Combine(profileDirectory, ".perfil-portatil");
        if (File.Exists(marker)) return;
        foreach (string name in new[] { "settings.json", "estilo.txt" })
        {
            string oldFile = Path.Combine(legacyDirectory, name), newFile = Path.Combine(profileDirectory, name);
            if (File.Exists(newFile) || !File.Exists(oldFile)) continue;
            string temporary = newFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(oldFile, temporary, overwrite: false);
                File.Move(temporary, newFile, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        File.WriteAllText(marker, "RemotePlayCino portable\n");
    }
}
