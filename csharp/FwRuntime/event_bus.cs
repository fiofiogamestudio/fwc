namespace Fw.Rt.Events;

public sealed class EventSubscription : IDisposable
{
    private Action<long>? _unsubscribe;
    private Func<long, bool>? _isActive;

    internal EventSubscription(long id, Action<long> unsubscribe, Func<long, bool> isActive)
    {
        Id = id;
        _unsubscribe = unsubscribe;
        _isActive = isActive;
    }

    public long Id { get; }
    public bool IsActive => _unsubscribe != null && (_isActive?.Invoke(Id) ?? false);

    public void Dispose()
    {
        Interlocked.Exchange(ref _unsubscribe, null)?.Invoke(Id);
        Interlocked.Exchange(ref _isActive, null);
    }
}

public sealed class EventBus<TKey>
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, List<Listener>> _listeners;
    private readonly Dictionary<long, TKey> _keysById = [];
    private long _nextId;

    public EventBus(IEqualityComparer<TKey>? comparer = null)
    {
        _listeners = new Dictionary<TKey, List<Listener>>(comparer);
    }

    public EventSubscription Subscribe<TPayload>(
        TKey key,
        Action<TPayload> callback,
        bool once = false,
        int priority = 0
    )
    {
        ArgumentNullException.ThrowIfNull(callback);
        return SubscribeCore(
            key,
            typeof(TPayload),
            callback,
            payload => callback((TPayload)payload!),
            once,
            priority
        );
    }

    public EventSubscription Subscribe(
        TKey key,
        Action callback,
        bool once = false,
        int priority = 0
    )
    {
        ArgumentNullException.ThrowIfNull(callback);
        return SubscribeCore(key, typeof(void), callback, _ => callback(), once, priority);
    }

    public int Publish<TPayload>(TKey key, TPayload payload)
    {
        return PublishCore(key, payload, typeof(TPayload));
    }

    public int Publish(TKey key)
    {
        return PublishCore(key, null, typeof(void));
    }

    public bool Unsubscribe(long subscriptionId)
    {
        lock (_gate)
        {
            if (!_keysById.Remove(subscriptionId, out var key))
            {
                return false;
            }
            if (!_listeners.TryGetValue(key, out var bucket))
            {
                return false;
            }
            bucket.RemoveAll(listener => listener.Id == subscriptionId);
            if (bucket.Count == 0)
            {
                _listeners.Remove(key);
            }
            return true;
        }
    }

    public int ListenerCount(TKey key)
    {
        lock (_gate)
        {
            return _listeners.TryGetValue(key, out var bucket) ? bucket.Count : 0;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _listeners.Clear();
            _keysById.Clear();
        }
    }

    private EventSubscription SubscribeCore(
        TKey key,
        Type payloadType,
        Delegate original,
        Action<object?> callback,
        bool once,
        int priority
    )
    {
        lock (_gate)
        {
            if (!_listeners.TryGetValue(key, out var bucket))
            {
                bucket = [];
                _listeners[key] = bucket;
            }
            var conflicting = bucket.FirstOrDefault(listener => listener.PayloadType != payloadType);
            if (conflicting != null)
            {
                throw new InvalidOperationException(
                    $"Event '{key}' is already bound to payload type {conflicting.PayloadType.Name}; " +
                    $"cannot also bind {payloadType.Name}."
                );
            }
            var existing = bucket.FirstOrDefault(
                listener => listener.PayloadType == payloadType && listener.Original.Equals(original)
            );
            if (existing != null)
            {
                return new EventSubscription(
                    existing.Id,
                    subscriptionId => Unsubscribe(subscriptionId),
                    ContainsSubscription
                );
            }

            var id = ++_nextId;
            bucket.Add(new Listener(id, payloadType, original, callback, once, priority));
            bucket.Sort(static (left, right) =>
            {
                var priorityOrder = right.Priority.CompareTo(left.Priority);
                return priorityOrder != 0 ? priorityOrder : left.Id.CompareTo(right.Id);
            });
            _keysById[id] = key;
            return new EventSubscription(id, subscriptionId => Unsubscribe(subscriptionId), ContainsSubscription);
        }
    }

    private bool ContainsSubscription(long subscriptionId)
    {
        lock (_gate)
        {
            return _keysById.ContainsKey(subscriptionId);
        }
    }

    private int PublishCore(TKey key, object? payload, Type payloadType)
    {
        Listener[] snapshot;
        lock (_gate)
        {
            if (!_listeners.TryGetValue(key, out var bucket))
            {
                return 0;
            }
            snapshot = [.. bucket];
            var conflicting = snapshot.FirstOrDefault(listener =>
                _keysById.ContainsKey(listener.Id) && listener.PayloadType != payloadType
            );
            if (conflicting != null)
            {
                throw new InvalidOperationException(
                    $"Event '{key}' expected payload {conflicting.PayloadType.Name} " +
                    $"but received {payloadType.Name}."
                );
            }
        }

        var invoked = 0;
        foreach (var listener in snapshot)
        {
            lock (_gate)
            {
                if (!_keysById.ContainsKey(listener.Id))
                {
                    continue;
                }
                if (listener.Once)
                {
                    Unsubscribe(listener.Id);
                }
            }
            listener.Callback(payload);
            invoked++;
        }
        return invoked;
    }

    private sealed record Listener(
        long Id,
        Type PayloadType,
        Delegate Original,
        Action<object?> Callback,
        bool Once,
        int Priority
    );
}
