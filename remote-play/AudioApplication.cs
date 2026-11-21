using System.ComponentModel;
using System.Diagnostics;

namespace RemotePlay;

internal sealed record AudioApplication(int ProcessId, long Started, string Name, string Title)
{
    internal static AudioApplication FromProcess(Process process) => new(process.Id,
        process.StartTime.ToUniversalTime().Ticks, process.ProcessName, process.MainWindowTitle);

    internal Process Open()
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != Started)
                throw new InvalidOperationException("La aplicación de audio se cerró. Actualiza la lista y vuelve a elegirla.");
            return process;
        }
        catch
        {
            process?.Dispose();
            throw;
        }
    }

    internal static AudioApplication[] List()
    {
        var result = new List<AudioApplication>();
        using Process self = Process.GetCurrentProcess();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != self.Id && process.SessionId == self.SessionId &&
                        process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                        result.Add(FromProcess(process));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                { /* A protected or exited process is not a selectable audio source. */ }
            }
        }
        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public override string ToString() => $"{Name} · {Title}";
}
