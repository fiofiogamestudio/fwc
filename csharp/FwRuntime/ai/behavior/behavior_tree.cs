using Fw.Rt.AI.Core;

namespace Fw.Rt.AI.Behavior;

public enum BehaviorStatus
{
    Success,
    Failure,
    Running,
    Suspended,
}

public sealed class BehaviorSession
{
    private readonly Dictionary<object, object?> _state = new(ReferenceEqualityComparer.Instance);

    public BehaviorSession Clone()
    {
        var clone = new BehaviorSession();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(BehaviorSession other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _state.Clear();
        foreach ((object owner, object? value) in other._state)
        {
            _state[owner] = value;
        }
    }

    public T Get<T>(object owner, T fallback = default!)
    {
        return _state.TryGetValue(owner, out var value) && value is T typed ? typed : fallback;
    }

    public void Set<T>(object owner, T value)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _state[owner] = value;
    }

    public void Clear(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _state.Remove(owner);
    }

    public void Reset()
    {
        _state.Clear();
    }
}

public abstract class BehaviorNode<TContext>
{
    public abstract BehaviorStatus Tick(TContext context, BehaviorSession session, DecisionScope scope);

    internal virtual void Reset(BehaviorSession session)
    {
        session.Clear(this);
    }
}

public sealed class BehaviorTree<TContext>
{
    public BehaviorTree(BehaviorNode<TContext> root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
    }

    public BehaviorNode<TContext> Root { get; }

    public BehaviorStatus Tick(TContext context, BehaviorSession session, DecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scope);
        var status = Root.Tick(context, session, scope);
        scope.Trace?.Write(
            scope.Tick,
            "behavior",
            "tick",
            new Dictionary<string, object?> { ["status"] = status.ToString() }
        );
        return status;
    }
}

public sealed class BehaviorAction<TContext> : BehaviorNode<TContext>
{
    private readonly Func<TContext, BehaviorStatus> _action;

    public BehaviorAction(Func<TContext, BehaviorStatus> action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public override BehaviorStatus Tick(TContext context, BehaviorSession session, DecisionScope scope)
    {
        return scope.Budget.TrySpend() ? _action(context) : BehaviorStatus.Suspended;
    }
}

public sealed class BehaviorCondition<TContext> : BehaviorNode<TContext>
{
    private readonly Predicate<TContext> _condition;

    public BehaviorCondition(Predicate<TContext> condition)
    {
        _condition = condition ?? throw new ArgumentNullException(nameof(condition));
    }

    public override BehaviorStatus Tick(TContext context, BehaviorSession session, DecisionScope scope)
    {
        if (!scope.Budget.TrySpend())
        {
            return BehaviorStatus.Suspended;
        }
        return _condition(context) ? BehaviorStatus.Success : BehaviorStatus.Failure;
    }
}

public sealed class BehaviorSequence<TContext> : BehaviorNode<TContext>
{
    private readonly IReadOnlyList<BehaviorNode<TContext>> _children;

    public BehaviorSequence(params BehaviorNode<TContext>[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Any(child => child == null))
        {
            throw new ArgumentException("Behavior children cannot contain null.", nameof(children));
        }
        _children = children.ToArray();
    }

    public override BehaviorStatus Tick(TContext context, BehaviorSession session, DecisionScope scope)
    {
        if (!scope.Budget.TrySpend())
        {
            return BehaviorStatus.Suspended;
        }
        var index = session.Get(this, 0);
        while (index < _children.Count)
        {
            var status = _children[index].Tick(context, session, scope);
            if (status == BehaviorStatus.Success)
            {
                index++;
                session.Set(this, index);
                continue;
            }
            if (status == BehaviorStatus.Failure)
            {
                session.Clear(this);
            }
            return status;
        }
        session.Clear(this);
        return BehaviorStatus.Success;
    }

    internal override void Reset(BehaviorSession session)
    {
        base.Reset(session);
        foreach (BehaviorNode<TContext> child in _children)
        {
            child.Reset(session);
        }
    }
}

public sealed class BehaviorSelector<TContext> : BehaviorNode<TContext>
{
    private readonly IReadOnlyList<BehaviorNode<TContext>> _children;

    public BehaviorSelector(params BehaviorNode<TContext>[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Any(child => child == null))
        {
            throw new ArgumentException("Behavior children cannot contain null.", nameof(children));
        }
        _children = children.ToArray();
    }

    public override BehaviorStatus Tick(TContext context, BehaviorSession session, DecisionScope scope)
    {
        if (!scope.Budget.TrySpend())
        {
            return BehaviorStatus.Suspended;
        }
        var index = session.Get(this, 0);
        while (index < _children.Count)
        {
            var status = _children[index].Tick(context, session, scope);
            if (status == BehaviorStatus.Failure)
            {
                index++;
                session.Set(this, index);
                continue;
            }
            if (status == BehaviorStatus.Success)
            {
                session.Clear(this);
            }
            return status;
        }
        session.Clear(this);
        return BehaviorStatus.Failure;
    }

    internal override void Reset(BehaviorSession session)
    {
        base.Reset(session);
        foreach (BehaviorNode<TContext> child in _children)
        {
            child.Reset(session);
        }
    }
}

public sealed class BehaviorReactiveSequence<TContext> : BehaviorNode<TContext>
{
    private readonly IReadOnlyList<BehaviorNode<TContext>> _children;

    public BehaviorReactiveSequence(params BehaviorNode<TContext>[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Any(child => child == null))
        {
            throw new ArgumentException("Behavior children cannot contain null.", nameof(children));
        }
        _children = children.ToArray();
    }

    public override BehaviorStatus Tick(TContext context, BehaviorSession session, DecisionScope scope)
    {
        if (!scope.Budget.TrySpend())
        {
            return BehaviorStatus.Suspended;
        }
        int previous = session.Get(this, -1);
        for (int index = 0; index < _children.Count; index += 1)
        {
            BehaviorStatus status = _children[index].Tick(context, session, scope);
            if (status == BehaviorStatus.Suspended)
            {
                return status;
            }
            if (status != BehaviorStatus.Success)
            {
                if (previous >= 0 && previous != index)
                {
                    _children[previous].Reset(session);
                }
                if (status == BehaviorStatus.Running)
                {
                    session.Set(this, index);
                }
                else
                {
                    session.Clear(this);
                }
                return status;
            }
        }
        if (previous >= 0)
        {
            _children[previous].Reset(session);
        }
        session.Clear(this);
        return BehaviorStatus.Success;
    }

    internal override void Reset(BehaviorSession session)
    {
        base.Reset(session);
        foreach (BehaviorNode<TContext> child in _children)
        {
            child.Reset(session);
        }
    }
}

public sealed class BehaviorReactiveSelector<TContext> : BehaviorNode<TContext>
{
    private readonly IReadOnlyList<BehaviorNode<TContext>> _children;

    public BehaviorReactiveSelector(params BehaviorNode<TContext>[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Any(child => child == null))
        {
            throw new ArgumentException("Behavior children cannot contain null.", nameof(children));
        }
        _children = children.ToArray();
    }

    public override BehaviorStatus Tick(TContext context, BehaviorSession session, DecisionScope scope)
    {
        if (!scope.Budget.TrySpend())
        {
            return BehaviorStatus.Suspended;
        }
        int previous = session.Get(this, -1);
        for (int index = 0; index < _children.Count; index += 1)
        {
            BehaviorStatus status = _children[index].Tick(context, session, scope);
            if (status == BehaviorStatus.Suspended)
            {
                return status;
            }
            if (status != BehaviorStatus.Failure)
            {
                if (previous >= 0 && previous != index)
                {
                    _children[previous].Reset(session);
                }
                if (status == BehaviorStatus.Running)
                {
                    session.Set(this, index);
                }
                else
                {
                    session.Clear(this);
                }
                return status;
            }
        }
        if (previous >= 0)
        {
            _children[previous].Reset(session);
        }
        session.Clear(this);
        return BehaviorStatus.Failure;
    }

    internal override void Reset(BehaviorSession session)
    {
        base.Reset(session);
        foreach (BehaviorNode<TContext> child in _children)
        {
            child.Reset(session);
        }
    }
}

public sealed class BehaviorWait<TContext> : BehaviorNode<TContext>
{
    private readonly Func<TContext, int> _duration;

    public BehaviorWait(Func<TContext, int> duration)
    {
        _duration = duration ?? throw new ArgumentNullException(nameof(duration));
    }

    public override BehaviorStatus Tick(TContext context, BehaviorSession session, DecisionScope scope)
    {
        if (!scope.Budget.TrySpend())
        {
            return BehaviorStatus.Suspended;
        }
        int duration = Math.Max(_duration(context), 0);
        int elapsed = session.Get(this, 0);
        if (elapsed >= duration)
        {
            session.Clear(this);
            return BehaviorStatus.Success;
        }
        session.Set(this, elapsed + 1);
        return BehaviorStatus.Running;
    }
}

public sealed class BehaviorInvert<TContext> : BehaviorNode<TContext>
{
    private readonly BehaviorNode<TContext> _child;

    public BehaviorInvert(BehaviorNode<TContext> child)
    {
        _child = child ?? throw new ArgumentNullException(nameof(child));
    }

    public override BehaviorStatus Tick(TContext context, BehaviorSession session, DecisionScope scope)
    {
        if (!scope.Budget.TrySpend())
        {
            return BehaviorStatus.Suspended;
        }
        return _child.Tick(context, session, scope) switch
        {
            BehaviorStatus.Success => BehaviorStatus.Failure,
            BehaviorStatus.Failure => BehaviorStatus.Success,
            var status => status,
        };
    }

    internal override void Reset(BehaviorSession session)
    {
        base.Reset(session);
        _child.Reset(session);
    }
}
