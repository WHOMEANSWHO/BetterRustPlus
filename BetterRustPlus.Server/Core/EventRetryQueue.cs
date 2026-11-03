namespace BetterRustPlus.Alerts;

/// <summary>Bounded, in-memory retry scheduling. Call only from its owning consumer.</summary>
public sealed class EventRetryQueue(int capacity = 1024)
{
    private readonly List<(GameEvent Event, DateTimeOffset Due)> pending = [];
    public int Count => pending.Count;

    public bool Add(GameEvent observation, DateTimeOffset due)
    {
        if (capacity <= 0 || pending.Count >= capacity) return false;
        pending.Add((observation, due));
        return true;
    }

    public bool TryTake(DateTimeOffset now, out GameEvent? observation)
    {
        pending.RemoveAll(entry => entry.Event.OccurredAt < now.AddMinutes(-5));
        int index = pending.FindIndex(entry => entry.Due <= now);
        if (index < 0) { observation = null; return false; }
        observation = pending[index].Event;
        pending.RemoveAt(index);
        return true;
    }
}
