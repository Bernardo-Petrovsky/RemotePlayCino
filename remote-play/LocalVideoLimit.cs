namespace RemotePlay;

internal sealed class LocalVideoLimit
{
    private readonly Lock gate = new();
    private int guests;
    private TaskCompletionSource changed = NewSignal();

    internal (bool Limited, Task Changed) Snapshot()
    {
        lock (gate)
            return (guests >= GuestSession.Limit, changed.Task);
    }

    internal void Join() => Update(1);
    internal void Leave() => Update(-1);

    private void Update(int step)
    {
        lock (gate)
        {
            bool previous = guests >= GuestSession.Limit;
            guests += step;
            if (previous != (guests >= GuestSession.Limit))
            {
                changed.TrySetResult();
                changed = NewSignal();
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
