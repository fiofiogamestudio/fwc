using Fw.Rt.AI.Core;

namespace Fw.Rt.AI.Nav;

public interface IFlowGraph<TNode>
    where TNode : notnull
{
    IEnumerable<TNode> Incoming(TNode node);
    double Cost(TNode from, TNode to);
}

public sealed class FlowField<TNode>
    where TNode : notnull
{
    private readonly IFlowGraph<TNode> _graph;
    private readonly PriorityQueue<TNode, (double Cost, long Order)> _open = new();
    private readonly Dictionary<TNode, double> _costs;
    private readonly HashSet<TNode> _closed;
    private long _order;

    public FlowField(
        IFlowGraph<TNode> graph,
        TNode goal,
        IEqualityComparer<TNode>? comparer = null
    )
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        var equality = comparer ?? EqualityComparer<TNode>.Default;
        _costs = new Dictionary<TNode, double>(equality) { [goal] = 0.0 };
        _closed = new HashSet<TNode>(equality);
        _open.Enqueue(goal, (0.0, _order++));
    }

    public bool IsComplete => _open.Count == 0;
    public IReadOnlyDictionary<TNode, double> Costs => _costs;

    public bool Step(DecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        while (_open.Count > 0 && scope.Budget.TrySpend())
        {
            var current = _open.Dequeue();
            if (!_closed.Add(current))
            {
                continue;
            }
            foreach (var neighbor in _graph.Incoming(current))
            {
                var edgeCost = _graph.Cost(neighbor, current);
                if (!double.IsFinite(edgeCost) || edgeCost < 0.0)
                {
                    throw new InvalidOperationException("Flow edge costs must be finite and non-negative.");
                }
                var nextCost = _costs[current] + edgeCost;
                if (_costs.TryGetValue(neighbor, out var known) && known <= nextCost)
                {
                    continue;
                }
                _costs[neighbor] = nextCost;
                _open.Enqueue(neighbor, (nextCost, _order++));
            }
        }
        return IsComplete;
    }

    public bool TryNext(TNode from, out TNode next)
    {
        next = default!;
        var found = false;
        var best = double.PositiveInfinity;
        foreach (var neighbor in _graph.Incoming(from))
        {
            if (!_costs.TryGetValue(neighbor, out var cost) || cost >= best)
            {
                continue;
            }
            best = cost;
            next = neighbor;
            found = true;
        }
        return found;
    }
}
