using System.Diagnostics;
using System.Text;

namespace RemotePlay;

// Optional latency log. Create an empty file named "trace.on" beside RemotePlayCino.exe to enable it.
// Host and client run in the same process on one PC, so every timestamp comes from the same clock.
// Output: remote-play-trace.csv (t_us = microseconds since start, stage, value).
internal static class Trace
{
    internal static readonly bool Enabled = File.Exists(Path.Combine(AppContext.BaseDirectory, "trace.on"));
    private static readonly long Start = Stopwatch.GetTimestamp();
    private static readonly Lock Gate = new();
    private static readonly StringBuilder Pending = new();
    private static System.Threading.Timer? flush;
    private static long lastHostChunk;
    private static long lastFrameReady;
    private static long lastFrameDone;

    internal static long Now => Stopwatch.GetTimestamp();

    // Free-text diagnostics (ffmpeg commands and errors) go to remote-play-ffmpeg.log while tracing is on.
    internal static void Note(string text)
    {
        if (!Enabled)
            return;
        try
        {
            lock (Gate)
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "remote-play-ffmpeg.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
        }
        catch (IOException) { /* A diagnostic log must not interrupt streaming. */ }
        catch (UnauthorizedAccessException) { /* A diagnostic log must not interrupt streaming. */ }
    }
    internal static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    internal static void Log(string stage, double value)
    {
        if (!Enabled)
            return;
        long micros = (Now - Start) * 1_000_000 / Stopwatch.Frequency;
        lock (Gate)
        {
            Pending.Append(micros).Append(',').Append(stage).Append(',').AppendLine(value.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture));
            flush ??= new System.Threading.Timer(_ => Flush(), null, 500, 500);
        }
    }

    private static void Flush()
    {
        string text;
        lock (Gate)
        {
            if (Pending.Length == 0)
                return;
            text = Pending.ToString();
            Pending.Clear();
        }
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "remote-play-trace.csv");
            if (!File.Exists(path))
                text = "t_us,stage,value\n" + text;
            File.AppendAllText(path, text);
        }
        catch (IOException) { /* A diagnostic log must not interrupt streaming. */ }
        catch (UnauthorizedAccessException) { /* A diagnostic log must not interrupt streaming. */ }
    }

    // Host: ffmpeg handed over encoded bytes.
    internal static void HostChunk(int bytes)
    {
        if (!Enabled) return;
        Volatile.Write(ref lastHostChunk, Now);
        Log("host_chunk_bytes", bytes);
    }

    // Client: a full decoded frame is in memory. The latest host chunk can belong to a different
    // frame. Its timestamp is useful for inspecting stream activity, not for measuring latency.
    internal static void FrameReady()
    {
        if (!Enabled) return;
        long now = Now;
        long prev = Interlocked.Exchange(ref lastFrameReady, now);
        if (prev != 0) Log("client_frame_interval_ms", Ms(prev, now));
        long host = Volatile.Read(ref lastHostChunk);
        if (host != 0) Log("client_frame_age_since_host_chunk_ms", Ms(host, now));
    }

    // Client: the frame finished painting.
    internal static void FramePainted()
    {
        if (!Enabled) return;
        long now = Now;
        long ready = Volatile.Read(ref lastFrameReady);
        if (ready != 0) Log("client_ready_to_painted_ms", Ms(ready, now));
        long host = Volatile.Read(ref lastHostChunk);
        if (host != 0) Log("client_painted_since_host_chunk_ms", Ms(host, now));
        Volatile.Write(ref lastFrameDone, now);
    }
}
