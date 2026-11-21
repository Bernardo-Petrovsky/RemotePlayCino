using System.Net.Quic;

namespace RemotePlay;

internal static class ConnectionLog
{
    private static readonly Lock Gate = new();
    internal static string FilePath => Path.Combine(AppStorage.ProfileDirectory, "conexion.log");

    // Short diagnostic lines (stalls, reconnects) that matter even when full tracing is off.
    internal static void Note(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"[{DateTimeOffset.Now:O}] PID={Environment.ProcessId} {text}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* Diagnostics must not interrupt the session. */ }
        catch (UnauthorizedAccessException) { /* Diagnostics must not interrupt the session. */ }
    }

    internal static void Write(string role, string stage, Exception error)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 256 * 1024)
                    File.Move(FilePath, FilePath + ".anterior", overwrite: true);
                string message = error.ToString();
                if (message.Length > 16_384)
                    message = message[..16_384];
                string network = error is QuicException quic ? $" QUIC={quic.QuicError} código={quic.ApplicationErrorCode}" : "";
                File.AppendAllText(FilePath,
                    $"[{DateTimeOffset.Now:O}] PID={Environment.ProcessId} {role} · {stage}{network}{Environment.NewLine}{message}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* Diagnostics must not interrupt the session. */ }
        catch (UnauthorizedAccessException) { /* Diagnostics must not interrupt the session. */ }
    }
}
