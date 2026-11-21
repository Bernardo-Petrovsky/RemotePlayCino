namespace RemotePlay;

internal sealed class LiveHostSettings
{
    internal sealed record State(HostOptions Options, Task VideoChanged, Task AudioChanged);
    private readonly Lock gate = new();
    private readonly HashSet<GuestSession> guests = [];
    private TaskCompletionSource video = NewSignal();
    private TaskCompletionSource audio = NewSignal();
    private State current;

    internal LiveHostSettings(HostOptions options) => current = new(options with { Live = this }, video.Task, audio.Task);
    internal State Current => Volatile.Read(ref current);

    internal void Apply(HostOptions options)
    {
        lock (gate)
        {
            State previous = current;
            bool changeVideo = !SameVideo(previous.Options, options);
            bool changeAudio = !SameAudio(previous.Options, options);
            TaskCompletionSource oldVideo = video, oldAudio = audio;
            if (changeVideo) video = NewSignal();
            if (changeAudio) audio = NewSignal();
            Volatile.Write(ref current, new(options with { Live = this }, video.Task, audio.Task));
            if (previous.Options.Input != options.Input)
                foreach (GuestSession guest in guests)
                    guest.SetPermission(GuestDevices.Desktop, options.Input);
            if (changeVideo) oldVideo.TrySetResult();
            if (changeAudio) oldAudio.TrySetResult();
        }
    }

    internal void Register(GuestSession guest)
    {
        lock (gate)
        {
            guests.Add(guest);
            guest.SetPermission(GuestDevices.Desktop, current.Options.Input);
        }
    }

    internal void Unregister(GuestSession guest) { lock (gate) guests.Remove(guest); }

    internal static bool SameVideo(HostOptions a, HostOptions b) =>
        (a.BandwidthMbps, a.MaxFps, a.Encoder, a.Display, a.LimitLocal1440) ==
        (b.BandwidthMbps, b.MaxFps, b.Encoder, b.Display, b.LimitLocal1440);

    private static bool SameAudio(HostOptions a, HostOptions b) =>
        (a.Audio, a.AudioDevice, a.AudioMode, a.AudioApplication?.ProcessId, a.AudioApplication?.Started) ==
        (b.Audio, b.AudioDevice, b.AudioMode, b.AudioApplication?.ProcessId, b.AudioApplication?.Started);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
