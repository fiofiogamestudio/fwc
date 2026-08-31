using Fw.Rt.AI.Behavior;
using Fw.Rt.AI.Core;

namespace Fw.Rt.AI.State;

public enum StateTransitionTrigger
{
    Tick,
    Success,
    Failure,
}

public sealed record StateTransition<TContext>(
    string Target,
    StateTransitionTrigger Trigger,
    Func<TContext, StateTreeSession, bool>? Condition = null,
    int Priority = 0
);

public sealed class StateNode<TContext>
{
    public StateNode(
        string id,
        string parent = "",
        bool initial = false,
        int order = 0,
        Func<TContext, StateTreeSession, bool>? canEnter = null,
        Func<TContext, StateTreeSession, DecisionScope, BehaviorStatus>? task = null,
        IEnumerable<StateTransition<TContext>>? transitions = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id.Trim();
        Parent = parent.Trim();
        Initial = initial;
        Order = order;
        CanEnter = canEnter;
        Task = task;
        Transitions = (transitions ?? []).OrderByDescending(item => item.Priority).ToArray();
    }

    public string Id { get; }
    public string Parent { get; }
    public bool Initial { get; }
    public int Order { get; }
    public Func<TContext, StateTreeSession, bool>? CanEnter { get; }
    public Func<TContext, StateTreeSession, DecisionScope, BehaviorStatus>? Task { get; }
    public IReadOnlyList<StateTransition<TContext>> Transitions { get; }
}

public sealed class StateTreeSession
{
    public string ActiveState { get; private set; } = "";
    public int StateTicks { get; private set; }

    public StateTreeSession Clone()
    {
        return new StateTreeSession
        {
            ActiveState = ActiveState,
            StateTicks = StateTicks,
        };
    }

    public void CopyFrom(StateTreeSession other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ActiveState = other.ActiveState;
        StateTicks = other.StateTicks;
    }

    public void Reset()
    {
        ActiveState = "";
        StateTicks = 0;
    }

    internal void Activate(string state)
    {
        ActiveState = state;
        StateTicks = 0;
    }

    internal void Advance()
    {
        StateTicks += 1;
    }
}

public sealed record StateTreeResult(
    string ActiveState,
    BehaviorStatus Status,
    bool Changed
);

public sealed class StateTree<TContext>
{
    private readonly IReadOnlyDictionary<string, StateNode<TContext>> _states;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<StateNode<TContext>>> _children;

    public StateTree(IEnumerable<StateNode<TContext>> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        StateNode<TContext>[] values = states.ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException("State tree requires at least one state.", nameof(states));
        }
        var byId = new Dictionary<string, StateNode<TContext>>(StringComparer.Ordinal);
        foreach (StateNode<TContext> state in values)
        {
            if (!byId.TryAdd(state.Id, state))
            {
                throw new ArgumentException($"Duplicate state tree id: {state.Id}", nameof(states));
            }
        }
        foreach (StateNode<TContext> state in values)
        {
            if (state.Parent.Length > 0 && !byId.ContainsKey(state.Parent))
            {
                throw new ArgumentException(
                    $"State '{state.Id}' references missing parent '{state.Parent}'.",
                    nameof(states)
                );
            }
            foreach (StateTransition<TContext> transition in state.Transitions)
            {
                if (!byId.ContainsKey(transition.Target))
                {
                    throw new ArgumentException(
                        $"State '{state.Id}' references missing target '{transition.Target}'.",
                        nameof(states)
                    );
                }
            }
            ValidateParentCycle(state, byId);
        }
        _states = byId;
        _children = values
            .GroupBy(state => state.Parent, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<StateNode<TContext>>)group
                    .OrderByDescending(state => state.Initial)
                    .ThenBy(state => state.Order)
                    .ThenBy(state => state.Id, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal
            );
    }

    public StateTreeResult Tick(TContext context, StateTreeSession session, DecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scope);
        bool changed = false;
        if (session.ActiveState.Length == 0)
        {
            StateNode<TContext> initial = SelectLeaf("", context, session)
                ?? throw new InvalidOperationException("State tree has no selectable root state.");
            session.Activate(initial.Id);
            changed = true;
            Trace(scope, "enter", initial.Id);
        }

        TransitionSearch tickTransition = FindTransition(
            context,
            session,
            scope,
            StateTransitionTrigger.Tick,
            out string tickTarget
        );
        if (tickTransition == TransitionSearch.Suspended)
        {
            return new StateTreeResult(session.ActiveState, BehaviorStatus.Suspended, changed);
        }
        if (tickTransition == TransitionSearch.Found)
        {
            ActivateLeaf(tickTarget, context, session, scope);
            changed = true;
        }

        BehaviorStatus status = TickPath(context, session, scope);
        if (status == BehaviorStatus.Suspended)
        {
            return new StateTreeResult(session.ActiveState, status, changed);
        }
        StateTransitionTrigger? trigger = status switch
        {
            BehaviorStatus.Success => StateTransitionTrigger.Success,
            BehaviorStatus.Failure => StateTransitionTrigger.Failure,
            _ => null,
        };
        bool transitionedAfterTask = false;
        if (trigger.HasValue)
        {
            TransitionSearch resultTransition = FindTransition(
                context,
                session,
                scope,
                trigger.Value,
                out string resultTarget
            );
            if (resultTransition == TransitionSearch.Suspended)
            {
                return new StateTreeResult(
                    session.ActiveState,
                    BehaviorStatus.Suspended,
                    changed
                );
            }
            if (resultTransition == TransitionSearch.Found)
            {
                ActivateLeaf(resultTarget, context, session, scope);
                changed = true;
                transitionedAfterTask = true;
            }
        }

        // A state entered before TickPath has already run once; only a post-task
        // transition leaves its new state at tick zero for the next update.
        if (!transitionedAfterTask)
        {
            session.Advance();
        }
        return new StateTreeResult(session.ActiveState, status, changed);
    }

    private BehaviorStatus TickPath(TContext context, StateTreeSession session, DecisionScope scope)
    {
        BehaviorStatus result = BehaviorStatus.Success;
        foreach (StateNode<TContext> state in ActivePath(session.ActiveState))
        {
            if (!scope.Budget.TrySpend())
            {
                return BehaviorStatus.Suspended;
            }
            if (state.Task == null)
            {
                continue;
            }
            BehaviorStatus status = state.Task(context, session, scope);
            if (status == BehaviorStatus.Failure || status == BehaviorStatus.Suspended)
            {
                return status;
            }
            if (status == BehaviorStatus.Running)
            {
                result = BehaviorStatus.Running;
            }
        }
        return result;
    }

    private TransitionSearch FindTransition(
        TContext context,
        StateTreeSession session,
        DecisionScope scope,
        StateTransitionTrigger trigger,
        out string target
    )
    {
        foreach (StateNode<TContext> state in ActivePath(session.ActiveState).Reverse())
        {
            foreach (StateTransition<TContext> transition in state.Transitions)
            {
                if (transition.Trigger != trigger)
                {
                    continue;
                }
                if (!scope.Budget.TrySpend())
                {
                    target = "";
                    return TransitionSearch.Suspended;
                }
                if (transition.Condition == null || transition.Condition(context, session))
                {
                    target = transition.Target;
                    return TransitionSearch.Found;
                }
            }
        }
        target = "";
        return TransitionSearch.None;
    }

    private void ActivateLeaf(
        string target,
        TContext context,
        StateTreeSession session,
        DecisionScope scope
    )
    {
        StateNode<TContext> leaf = SelectLeaf(target, context, session)
            ?? throw new InvalidOperationException($"State tree target '{target}' is not selectable.");
        if (string.Equals(session.ActiveState, leaf.Id, StringComparison.Ordinal))
        {
            session.Activate(leaf.Id);
            Trace(scope, "restart", leaf.Id);
            return;
        }
        string previous = session.ActiveState;
        session.Activate(leaf.Id);
        scope.Trace?.Write(
            scope.Tick,
            "state",
            "transition",
            new Dictionary<string, object?>
            {
                ["from"] = previous,
                ["to"] = leaf.Id,
            }
        );
    }

    private StateNode<TContext>? SelectLeaf(
        string parentOrState,
        TContext context,
        StateTreeSession session
    )
    {
        StateNode<TContext>? selected;
        if (parentOrState.Length == 0)
        {
            selected = SelectChild("", context, session);
        }
        else
        {
            selected = _states[parentOrState];
            if (selected.CanEnter != null && !selected.CanEnter(context, session))
            {
                return null;
            }
        }
        if (selected == null)
        {
            return null;
        }
        while (_children.TryGetValue(selected.Id, out IReadOnlyList<StateNode<TContext>>? children)
            && children.Count > 0)
        {
            selected = SelectChild(selected.Id, context, session);
            if (selected == null)
            {
                return null;
            }
        }
        return selected;
    }

    private StateNode<TContext>? SelectChild(
        string parent,
        TContext context,
        StateTreeSession session
    )
    {
        if (!_children.TryGetValue(parent, out IReadOnlyList<StateNode<TContext>>? children))
        {
            return null;
        }
        foreach (StateNode<TContext> child in children)
        {
            if (child.CanEnter == null || child.CanEnter(context, session))
            {
                return child;
            }
        }
        return null;
    }

    private IReadOnlyList<StateNode<TContext>> ActivePath(string leaf)
    {
        var path = new List<StateNode<TContext>>();
        StateNode<TContext> current = _states[leaf];
        while (true)
        {
            path.Add(current);
            if (current.Parent.Length == 0)
            {
                break;
            }
            current = _states[current.Parent];
        }
        path.Reverse();
        return path;
    }

    private static void ValidateParentCycle(
        StateNode<TContext> state,
        IReadOnlyDictionary<string, StateNode<TContext>> states
    )
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { state.Id };
        string parent = state.Parent;
        while (parent.Length > 0)
        {
            if (!seen.Add(parent))
            {
                throw new ArgumentException($"State tree contains a parent cycle at '{state.Id}'.");
            }
            parent = states[parent].Parent;
        }
    }

    private static void Trace(DecisionScope scope, string @event, string state)
    {
        scope.Trace?.Write(
            scope.Tick,
            "state",
            @event,
            new Dictionary<string, object?> { ["state"] = state }
        );
    }

    private enum TransitionSearch
    {
        None,
        Found,
        Suspended,
    }
}
