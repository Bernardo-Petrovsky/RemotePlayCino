using System.Text.Json;

namespace RemotePlay;

internal sealed record AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string DefaultPath => Path.Combine(AppStorage.ProfileDirectory, "settings.json");

    public string? HostAddress { get; init; }
    public int? HostPort { get; init; }
    public int? HostBandwidth { get; init; }
    public int? HostMaxFps { get; init; }
    public int? HostEncoder { get; init; }
    public string? HostDisplay { get; init; }
    public string? HostAudioDevice { get; init; }
    public int HostAudioMode { get; init; }
    public string? HostAudioApplication { get; init; }
    public int HostIdle { get; init; }
    public bool HostAudio { get; init; }
    public bool HostInput { get; init; }
    public bool HostLock { get; init; }
    public bool HostApprove { get; init; } = true;
    public bool HostLimitLocal1440 { get; init; } = true;
    public bool HostPriority { get; init; }
    public string? ClientAddress { get; init; }
    public string? ClientName { get; init; }
    public int? ClientPort { get; init; }
    public int? ClientWidth { get; init; }
    public int? ClientFps { get; init; }
    public int? ClientDecoder { get; init; }
    public int? ClientPad { get; init; }
    public string? ClientControllerKey { get; init; }
    public string? ClientControllerName { get; init; }
    public bool ClientNintendoLetters { get; init; }
    public bool ClientAudio { get; init; }
    public bool ClientInput { get; init; }

    internal static AppSettings? Load(string path)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
