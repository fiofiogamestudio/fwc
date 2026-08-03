using Fw.Rt.AI.Core;

namespace Fw.Rt.AI.Plan;

public enum PlanStatus
{
    Searching,
    Complete,
    Unreachable,
}

public sealed class PlanAction<TFact>
    where TFact : notnull
{
    public PlanAction(
        string id,
        IEnumerable<TFact> requires,
        IEnumerable<TFact> adds,
        IEnumerable<TFact> removes,
        double cost = 1.0
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!double.IsFinite(cost) || cost < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(cost));
        }
        Id = id;
        Requires = new HashSet<TFact>(requires ?? throw new ArgumentNullException(nameof(requires)));
        Adds = new HashSet<TFact>(adds ?? throw new ArgumentNullException(nameof(adds)));
        Removes = new HashSet<TFact>(removes ?? throw new ArgumentNullException(nameof(removes)));
        Cost = cost;
    }

    public string Id { get; }
    public IReadOnlySet<TFact> Requires { get; }
    public IReadOnlySet<TFact> Adds { get; }
    public IReadOnlySet<TFact> Removes { get; }
    public double Cost { get; }
}

public sealed class PlanGoal<TFact>
    where TFact : notnull
{
    public PlanGoal(string id, IEnumerable<TFact> requires)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Requires = new HashSet<TFact>(requires ?? throw new ArgumentNullException(nameof(requires)));
    }

    public string Id { get; }
    public IReadOnlySet<TFact> Requires { get; }
}

public sealed record PlanResult<TFact>(
    PlanStatus Status,
    IReadOnlyList<PlanAction<TFact>> Actions,
    double Cost
) where TFact : notnull;

public sealed class GoalPlanner<TFact>
    where TFact : notnull
{
    private readonly List<PlanAction<TFact>> _actions = [];

    public IReadOnlyList<PlanAction<TFact>> Actions => _actions;

    public void Add(PlanAction<TFact> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_actions.Any(item => string.Equals(item.Id, action.Id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Duplicate plan action id: {action.Id}", nameof(action));
        }
        _actions.Add(action);
    }

    public bool Remove(string id)
    {
        var index = _actions.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }
        _actions.RemoveAt(index);
        return true;
    }

    public PlanSearch<TFact> Begin(IEnumerable<TFact> facts, PlanGoal<TFact> goal)
    {
        return new PlanSearch<TFact>(facts, goal, _actions);
    }
}

public sealed class PlanSearch<TFact>
    where TFact : notnull
{
    private readonly PlanGoal<TFact> _goal;
    private readonly IReadOnlyList<PlanAction<TFact>> _actions;
    private readonly PriorityQueue<Node, (double Cost, long Order)> _open = new();
    private readonly List<Node> _visited = [];
    private long _order;
    private PlanResult<TFact>? _result;

    internal PlanSearch(
        IEnumerable<TFact> facts,
        PlanGoal<TFact> goal,
        IReadOnlyList<PlanAction<TFact>> actions
    )
    {
        _goal = goal ?? throw new ArgumentNullException(nameof(goal));
        _actions = actions.ToArray();
        Enqueue(new Node(new HashSet<TFact>(facts ?? throw new ArgumentNullException(nameof(facts))), [], 0.0));
    }

    public bool IsFinished => _result != null;

    public PlanResult<TFact> Step(DecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (_result != null)
        {
            return _result;
        }

        while (_open.Count > 0 && scope.Budget.TrySpend())
        {
            var node = _open.Dequeue();
            if (_visited.Any(item => item.Facts.SetEquals(node.Facts) && item.Cost <= node.Cost))
            {
                continue;
            }
            _visited.Add(node);
            if (_goal.Requires.All(node.Facts.Contains))
            {
                _result = new PlanResult<TFact>(PlanStatus.Complete, node.Actions, node.Cost);
                scope.Trace?.Write(
                    scope.Tick,
                    "plan",
                    "complete",
                    new Dictionary<string, object?>
                    {
                        ["goal"] = _goal.Id,
                        ["steps"] = node.Actions.Count,
                        ["cost"] = node.Cost,
                    }
                );
                return _result;
            }

            foreach (var action in _actions)
            {
                if (!action.Requires.All(node.Facts.Contains))
                {
                    continue;
                }
                var nextFacts = new HashSet<TFact>(node.Facts);
                nextFacts.ExceptWith(action.Removes);
                nextFacts.UnionWith(action.Adds);
                if (nextFacts.SetEquals(node.Facts))
                {
                    continue;
                }
                var nextActions = new List<PlanAction<TFact>>(node.Actions) { action };
                Enqueue(new Node(nextFacts, nextActions, node.Cost + action.Cost));
            }
        }

        if (_open.Count == 0)
        {
            _result = new PlanResult<TFact>(PlanStatus.Unreachable, [], 0.0);
            return _result;
        }
        return new PlanResult<TFact>(PlanStatus.Searching, [], 0.0);
    }

    private void Enqueue(Node node)
    {
        _open.Enqueue(node, (node.Cost, _order++));
    }

    private sealed record Node(
        HashSet<TFact> Facts,
        IReadOnlyList<PlanAction<TFact>> Actions,
        double Cost
    );
}
