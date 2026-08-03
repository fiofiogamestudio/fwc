using Fw.Rt.AI.Core;

namespace Fw.Rt.AI.Nav;

public interface IPathGraph<TNode>
    where TNode : notnull
{
    IEnumerable<TNode> Neighbors(TNode node);
    double Cost(TNode from, TNode to);
    double Estimate(TNode from, TNode goal);
}

public enum PathStatus
{
    Searching,
    Complete,
    Unreachable,
}

public sealed record PathResult<TNode>(
    PathStatus Status,
    IReadOnlyList<TNode> Nodes,
    double Cost
) where TNode : notnull;

public sealed class PathSearch<TNode>
    where TNode : notnull
{
    private readonly IPathGraph<TNode> _graph;
    private readonly TNode _start;
    private readonly TNode _goal;
    private readonly PriorityQueue<TNode, (double Cost, long Order)> _open = new();
    private readonly Dictionary<TNode, double> _costs;
    private readonly Dictionary<TNode, TNode> _parents;
    private readonly HashSet<TNode> _closed;
    private readonly IEqualityComparer<TNode> _comparer;
    private long _order;
    private PathResult<TNode>? _result;

    public PathSearch(
        IPathGraph<TNode> graph,
        TNode start,
        TNode goal,
        IEqualityComparer<TNode>? comparer = null
    )
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _start = start;
        _goal = goal;
        _comparer = comparer ?? EqualityComparer<TNode>.Default;
        _costs = new Dictionary<TNode, double>(_comparer) { [start] = 0.0 };
        _parents = new Dictionary<TNode, TNode>(_comparer);
        _closed = new HashSet<TNode>(_comparer);
        Enqueue(start, _graph.Estimate(start, goal));
    }

    public bool IsFinished => _result != null;

    public PathResult<TNode> Step(DecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (_result != null)
        {
            return _result;
        }

        while (_open.Count > 0 && scope.Budget.TrySpend())
        {
            var current = _open.Dequeue();
            if (!_closed.Add(current))
            {
                continue;
            }
            if (_comparer.Equals(current, _goal))
            {
                var nodes = Reconstruct(current);
                _result = new PathResult<TNode>(PathStatus.Complete, nodes, _costs[current]);
                scope.Trace?.Write(
                    scope.Tick,
                    "nav",
                    "path_complete",
                    new Dictionary<string, object?>
                    {
                        ["nodes"] = nodes.Count,
                        ["cost"] = _costs[current],
                    }
                );
                return _result;
            }

            foreach (var neighbor in _graph.Neighbors(current))
            {
                if (_closed.Contains(neighbor))
                {
                    continue;
                }
                var edgeCost = _graph.Cost(current, neighbor);
                if (!double.IsFinite(edgeCost) || edgeCost < 0.0)
                {
                    throw new InvalidOperationException("Path edge costs must be finite and non-negative.");
                }
                var nextCost = _costs[current] + edgeCost;
                if (_costs.TryGetValue(neighbor, out var knownCost) && knownCost <= nextCost)
                {
                    continue;
                }
                _costs[neighbor] = nextCost;
                _parents[neighbor] = current;
                Enqueue(neighbor, nextCost + _graph.Estimate(neighbor, _goal));
            }
        }

        if (_open.Count == 0)
        {
            _result = new PathResult<TNode>(PathStatus.Unreachable, [], 0.0);
            return _result;
        }
        return new PathResult<TNode>(PathStatus.Searching, [], 0.0);
    }

    private void Enqueue(TNode node, double priority)
    {
        if (!double.IsFinite(priority))
        {
            throw new InvalidOperationException("Path priorities must be finite.");
        }
        _open.Enqueue(node, (priority, _order++));
    }

    private IReadOnlyList<TNode> Reconstruct(TNode current)
    {
        var nodes = new List<TNode> { current };
        while (!_comparer.Equals(current, _start))
        {
            current = _parents[current];
            nodes.Add(current);
        }
        nodes.Reverse();
        return nodes;
    }
}

public sealed class PathCache<TKey, TNode>
    where TKey : notnull
    where TNode : notnull
{
    private readonly Dictionary<TKey, CacheEntry> _paths = [];
    private readonly LinkedList<TKey> _order = [];

    public PathCache(int capacity = 128)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count => _paths.Count;

    public bool TryGet(TKey key, out IReadOnlyList<TNode> path)
    {
        if (!_paths.TryGetValue(key, out var entry))
        {
            path = [];
            return false;
        }
        _order.Remove(entry.Order);
        _order.AddLast(entry.Order);
        path = entry.Path;
        return true;
    }

    public void Set(TKey key, IEnumerable<TNode> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Capacity == 0)
        {
            return;
        }
        var snapshot = path.ToArray();
        if (_paths.TryGetValue(key, out var existing))
        {
            _order.Remove(existing.Order);
            _order.AddLast(existing.Order);
            _paths[key] = new CacheEntry(snapshot, existing.Order);
            return;
        }
        while (_paths.Count >= Capacity && _order.First != null)
        {
            var oldest = _order.First;
            _order.RemoveFirst();
            _paths.Remove(oldest.Value);
        }
        var order = _order.AddLast(key);
        _paths[key] = new CacheEntry(snapshot, order);
    }

    public bool Remove(TKey key)
    {
        if (!_paths.Remove(key, out var entry))
        {
            return false;
        }
        _order.Remove(entry.Order);
        return true;
    }

    public void Clear()
    {
        _paths.Clear();
        _order.Clear();
    }

    private sealed record CacheEntry(IReadOnlyList<TNode> Path, LinkedListNode<TKey> Order);
}
