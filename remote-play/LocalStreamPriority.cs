using System.ComponentModel;
using System.Diagnostics;

namespace RemotePlay;

internal static class LocalStreamPriority
{
    private sealed class Entry(int id, long started)
    {
        internal int Id { get; } = id;
        internal long Started { get; } = started;
        internal ProcessPriorityClass? RestoreTo;
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<int, Entry> Children = [];
    private static Entry? self;
    private static bool enabled;

    internal static bool SetEnabled(bool value)
    {
        lock (Gate)
        {
            enabled = value;
            using Process current = Process.GetCurrentProcess();
            self ??= new Entry(current.Id, current.StartTime.ToUniversalTime().Ticks);
            bool success = Change(current, self, value);
            foreach (Entry child in Children.Values.ToArray())
            {
                using Process? process = Find(child);
                if (process is null)
                    Children.Remove(child.Id);
                else
                    success &= Change(process, child, value);
            }
            return success;
        }
    }

    internal static void TrackChild(Process process)
    {
        try
        {
            lock (Gate)
            {
                foreach (Entry previous in Children.Values.ToArray())
                {
                    using Process? active = Find(previous);
                    if (active is null) Children.Remove(previous.Id);
                }
                var entry = new Entry(process.Id, process.StartTime.ToUniversalTime().Ticks);
                Children[process.Id] = entry;
                if (enabled) Change(process, entry, true);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ConnectionLog.Write("Anfitrión", "Prioridad del proceso de video", ex);
        }
    }

    private static Process? Find(Entry entry)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(entry.Id);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == entry.Started)
                return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { /* An exited or reused PID must not be changed. */ }
        process?.Dispose();
        return null;
    }

    private static bool Change(Process process, Entry entry, bool boost)
    {
        try
        {
            process.Refresh();
            ProcessPriorityClass current = process.PriorityClass;
            if (boost && entry.RestoreTo is null && current is (ProcessPriorityClass.Idle or
                ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Normal))
            {
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
                entry.RestoreTo = current;
            }
            else if (!boost && entry.RestoreTo is { } original)
            {
                // Preserve a manual priority change made outside Remote Play.
                if (current == ProcessPriorityClass.AboveNormal)
                    process.PriorityClass = original;
                entry.RestoreTo = null;
            }
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            ConnectionLog.Write("Anfitrión", "Prioridad local", ex);
            return false;
        }
    }
}
