using System.Collections.Concurrent;

namespace dotnetTgBot.Services;

public class UserStateStore
{
    private readonly ConcurrentDictionary<long, StateEntry> _states = new();
    private readonly TimeSpan _ttl;
    private DateTime _lastCleanupUtc = DateTime.MinValue;

    public UserStateStore(TimeSpan ttl)
    {
        _ttl = ttl;
    }

    public UserState this[long telegramUserId]
    {
        set
        {
            CleanupExpiredIfNeeded();
            _states[telegramUserId] = new StateEntry(value, DateTime.UtcNow);
        }
    }

    public bool TryGetValue(long telegramUserId, out UserState state)
    {
        CleanupExpiredIfNeeded();

        if (_states.TryGetValue(telegramUserId, out var entry))
        {
            if (!IsExpired(entry))
            {
                entry.Touch();
                state = entry.State;
                return true;
            }

            _states.TryRemove(telegramUserId, out _);
        }

        state = null!;
        return false;
    }

    public UserState? GetValueOrDefault(long telegramUserId)
    {
        return TryGetValue(telegramUserId, out var state) ? state : null;
    }

    public bool TryRemove(long telegramUserId, out UserState state)
    {
        if (_states.TryRemove(telegramUserId, out var entry))
        {
            state = entry.State;
            return true;
        }

        state = null!;
        return false;
    }

    private bool IsExpired(StateEntry entry)
    {
        return DateTime.UtcNow - entry.LastTouchedUtc > _ttl;
    }

    private void CleanupExpiredIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (now - _lastCleanupUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastCleanupUtc = now;
        foreach (var pair in _states)
        {
            if (IsExpired(pair.Value))
            {
                _states.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class StateEntry
    {
        public StateEntry(UserState state, DateTime lastTouchedUtc)
        {
            State = state;
            LastTouchedUtc = lastTouchedUtc;
        }

        public UserState State { get; }
        public DateTime LastTouchedUtc { get; private set; }

        public void Touch()
        {
            LastTouchedUtc = DateTime.UtcNow;
        }
    }
}
