namespace Fw.Rt.State;

public sealed class StateMachine<TState, TContext>
    where TState : notnull
{
    private const int MaxChainedTransitions = 32;

    private readonly TContext _context;
    private readonly Dictionary<TState, StateCallbacks> _states;
    private readonly Dictionary<(TState From, TState To), TransitionCallbacks> _transitions;
    private bool _transitioning;
    private PendingTransition? _pending;

    public StateMachine(TContext context, IEqualityComparer<TState>? comparer = null)
    {
        _context = context;
        _states = new Dictionary<TState, StateCallbacks>(comparer);
        _transitions = new Dictionary<(TState From, TState To), TransitionCallbacks>(
            new TransitionKeyComparer(_states.Comparer)
        );
    }

    public bool IsActive { get; private set; }
    public TState? Current { get; private set; }

    public event Action<TState, object?>? Started;
    public event Action<TState, TState, object?>? Transitioned;
    public event Action<TState>? Stopped;

    public void RegisterState(
        TState state,
        Action<TContext, object?>? onEnter = null,
        Action<TContext, float>? onTick = null,
        Action<TContext>? onExit = null
    )
    {
        _states[state] = new StateCallbacks(onEnter, onTick, onExit);
    }

    public void RegisterTransition(
        TState from,
        TState to,
        Action<TContext, TState, TState, object?>? action = null,
        Func<TContext, TState, TState, object?, bool>? guard = null
    )
    {
        _transitions[(from, to)] = new TransitionCallbacks(action, guard);
    }

    public bool Start(TState initial, object? payload = null)
    {
        if (IsActive || !_states.TryGetValue(initial, out var callbacks))
        {
            return false;
        }
        IsActive = true;
        Current = initial;
        _transitioning = true;
        try
        {
            callbacks.Enter?.Invoke(_context, payload);
            Started?.Invoke(initial, payload);
        }
        catch
        {
            ResetAfterFailure();
            throw;
        }
        finally
        {
            _transitioning = false;
        }
        try
        {
            DrainPendingTransitions();
        }
        catch
        {
            ResetAfterFailure();
            throw;
        }
        return true;
    }

    public void Tick(float dt)
    {
        if (!IsActive || Current is not TState current || !_states.TryGetValue(current, out var callbacks))
        {
            return;
        }
        callbacks.Tick?.Invoke(_context, dt);
    }

    public bool TryTransition(TState target, object? payload = null)
    {
        if (!IsActive || Current is not TState || !_states.ContainsKey(target))
        {
            return false;
        }
        if (_transitioning)
        {
            _pending = new PendingTransition(target, payload);
            return true;
        }
        return PerformTransition(target, payload);
    }

    public void Stop()
    {
        if (!IsActive || Current is not TState previous)
        {
            return;
        }
        _pending = null;
        _transitioning = true;
        try
        {
            _states[previous].Exit?.Invoke(_context);
        }
        finally
        {
            _pending = null;
            _transitioning = false;
            IsActive = false;
            Current = default;
        }
        Stopped?.Invoke(previous);
    }

    public void Clear()
    {
        try
        {
            Stop();
        }
        finally
        {
            _states.Clear();
            _transitions.Clear();
            ResetAfterFailure();
        }
    }

    private bool PerformTransition(TState target, object? payload)
    {
        var previous = Current!;
        _transitions.TryGetValue((previous, target), out var transition);
        if (transition?.Guard != null && !transition.Guard(_context, previous, target, payload))
        {
            return false;
        }

        _transitioning = true;
        try
        {
            _states[previous].Exit?.Invoke(_context);
            transition?.Action?.Invoke(_context, previous, target, payload);
            Current = target;
            _states[target].Enter?.Invoke(_context, payload);
            Transitioned?.Invoke(previous, target, payload);
        }
        catch
        {
            ResetAfterFailure();
            throw;
        }
        finally
        {
            _transitioning = false;
        }
        try
        {
            DrainPendingTransitions();
        }
        catch
        {
            ResetAfterFailure();
            throw;
        }
        return true;
    }

    private void ResetAfterFailure()
    {
        _pending = null;
        _transitioning = false;
        IsActive = false;
        Current = default;
    }

    private void DrainPendingTransitions()
    {
        var count = 0;
        while (IsActive && !_transitioning && _pending is { } pending)
        {
            if (count++ >= MaxChainedTransitions)
            {
                _pending = null;
                throw new InvalidOperationException("State machine exceeded the chained transition limit.");
            }
            _pending = null;
            PerformTransition(pending.Target, pending.Payload);
        }
    }

    private sealed record StateCallbacks(
        Action<TContext, object?>? Enter,
        Action<TContext, float>? Tick,
        Action<TContext>? Exit
    );

    private sealed record TransitionCallbacks(
        Action<TContext, TState, TState, object?>? Action,
        Func<TContext, TState, TState, object?, bool>? Guard
    );

    private sealed record PendingTransition(TState Target, object? Payload);

    private sealed class TransitionKeyComparer(IEqualityComparer<TState> stateComparer)
        : IEqualityComparer<(TState From, TState To)>
    {
        public bool Equals(
            (TState From, TState To) left,
            (TState From, TState To) right
        )
        {
            return stateComparer.Equals(left.From, right.From)
                && stateComparer.Equals(left.To, right.To);
        }

        public int GetHashCode((TState From, TState To) value)
        {
            return HashCode.Combine(
                stateComparer.GetHashCode(value.From),
                stateComparer.GetHashCode(value.To)
            );
        }
    }
}
