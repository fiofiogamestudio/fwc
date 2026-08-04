using Fw.Rt.AI.Core;
using Fw.Rt.AI.Environment;

namespace Fw.Rt.AI.Search;

public enum BeamSearchStatus
{
    Searching,
    Success,
    Exhausted,
    DepthLimit,
}

public sealed class BeamSearchOptions
{
    public BeamSearchOptions(
        int width = 16,
        int maxDepth = 8,
        int actor = 0,
        double successPayoff = 1.0
    )
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (maxDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }
        if (actor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actor));
        }
        if (!double.IsFinite(successPayoff))
        {
            throw new ArgumentOutOfRangeException(nameof(successPayoff));
        }

        Width = width;
        MaxDepth = maxDepth;
        Actor = actor;
        SuccessPayoff = successPayoff;
    }

    public int Width { get; }
    public int MaxDepth { get; }
    public int Actor { get; }
    public double SuccessPayoff { get; }
}

public sealed class BeamSearchResult<TAction>
    where TAction : notnull
{
    public BeamSearchResult(
        BeamSearchStatus status,
        IEnumerable<TAction> actions,
        double score,
        int depth,
        int expandedNodes,
        int generatedNodes
    )
    {
        if (!double.IsFinite(score) && !double.IsNegativeInfinity(score))
        {
            throw new ArgumentOutOfRangeException(nameof(score));
        }
        Status = status;
        Actions = Array.AsReadOnly((actions ?? throw new ArgumentNullException(nameof(actions))).ToArray());
        Score = score;
        Depth = depth;
        ExpandedNodes = expandedNodes;
        GeneratedNodes = generatedNodes;
    }

    public BeamSearchStatus Status { get; }
    public IReadOnlyList<TAction> Actions { get; }
    public double Score { get; }
    public int Depth { get; }
    public int ExpandedNodes { get; }
    public int GeneratedNodes { get; }
    public bool IsFinished => Status != BeamSearchStatus.Searching;
}

public sealed class BeamSearch<TState, TObservation, TAction>
    where TState : notnull
    where TObservation : notnull
    where TAction : notnull
{
    private readonly IGameEnvironment<TState, TObservation, TAction> _environment;
    private readonly Func<TState, int, double> _evaluate;
    private readonly BeamSearchOptions _options;
    private readonly Dictionary<string, double> _visited = new(StringComparer.Ordinal);
    private readonly List<Node> _next = [];
    private IReadOnlyList<Node> _frontier;
    private Node _best;
    private BeamSearchResult<TAction>? _result;
    private int _frontierIndex;
    private int _depth;
    private int _expandedNodes;
    private int _generatedNodes;
    private long _order;

    public BeamSearch(
        IGameEnvironment<TState, TObservation, TAction> environment,
        TState rootState,
        Func<TState, int, double> evaluate,
        BeamSearchOptions? options = null
    )
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
        _options = options ?? new BeamSearchOptions();
        if (_options.Actor >= _environment.Spec.PlayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Search actor is outside the environment player range.");
        }
        if (_environment.Spec.Dynamics != GameDynamics.Deterministic)
        {
            throw new NotSupportedException("BeamSearch requires deterministic environment dynamics.");
        }
        if (_environment.Spec.MoveMode != GameMoveMode.Sequential)
        {
            throw new NotSupportedException("BeamSearch requires sequential moves.");
        }
        if (_environment.Spec.PayoffMode is not (GamePayoffMode.SinglePlayer or GamePayoffMode.Cooperative))
        {
            throw new NotSupportedException(
                "BeamSearch only supports single-player or cooperative payoff modes."
            );
        }

        var state = _environment.Clone(rootState ?? throw new ArgumentNullException(nameof(rootState)));
        var episode = _environment.Result(state);
        GameEnvironmentGuard.ValidateResult(episode, _environment.Spec);
        var score = episode.IsFinished ? episode.Payoffs[_options.Actor] : Evaluate(state);
        _best = new Node(state, [], score, 0);
        _frontier = [_best];
        _visited[StateKey(state)] = score;

        if (episode.IsFinished)
        {
            var status = episode.Status == GameResultStatus.Terminated
                && score >= _options.SuccessPayoff
                ? BeamSearchStatus.Success
                : BeamSearchStatus.Exhausted;
            _result = Snapshot(status, _best);
        }
    }

    public bool IsFinished => _result?.IsFinished == true;

    public BeamSearchResult<TAction> Step(DecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (_result != null)
        {
            return _result;
        }

        while (scope.Budget.TrySpend())
        {
            if (_frontierIndex >= _frontier.Count && !AdvanceLayer(scope))
            {
                return _result!;
            }

            var node = _frontier[_frontierIndex++];
            _expandedNodes += 1;
            var episode = _environment.Result(node.State);
            GameEnvironmentGuard.ValidateResult(episode, _environment.Spec);
            if (episode.IsFinished)
            {
                ConsiderBest(node);
                continue;
            }

            var actor = _environment.CurrentActor(node.State);
            GameEnvironmentGuard.ValidateActor(actor, _environment.Spec);
            if (actor < 0)
            {
                throw new NotSupportedException($"BeamSearch cannot expand special actor {actor}.");
            }

            var actions = _environment.LegalActions(node.State)
                ?? throw new InvalidOperationException("Environment returned null legal actions.");
            EnsureUniqueActions(actions);
            foreach (var action in actions)
            {
                var state = _environment.Clone(node.State);
                var transition = _environment.Step(state, action);
                GameEnvironmentGuard.ValidateTransition(transition, _environment.Spec);
                _generatedNodes += 1;

                var childEpisode = transition.Result;
                var score = childEpisode.IsFinished
                    ? childEpisode.Payoffs[_options.Actor]
                    : Evaluate(transition.State);
                var path = new List<TAction>(node.Actions.Count + 1);
                path.AddRange(node.Actions);
                path.Add(action);
                var child = new Node(transition.State, path, score, _order++);

                var key = StateKey(child.State);
                if (_visited.TryGetValue(key, out var previousScore) && previousScore >= score)
                {
                    continue;
                }
                _visited[key] = score;
                ConsiderBest(child);

                if (childEpisode.Status == GameResultStatus.Terminated
                    && score >= _options.SuccessPayoff)
                {
                    _result = Snapshot(BeamSearchStatus.Success, child);
                    WriteTrace(scope, "success", child);
                    return _result;
                }
                if (!childEpisode.IsFinished)
                {
                    _next.Add(child);
                }
            }
        }

        if (_frontierIndex >= _frontier.Count && _next.Count == 0)
        {
            _result = Snapshot(BeamSearchStatus.Exhausted, _best);
            WriteTrace(scope, "exhausted", _best);
            return _result;
        }
        return Snapshot(BeamSearchStatus.Searching, _best);
    }

    private bool AdvanceLayer(DecisionScope scope)
    {
        if (_next.Count == 0)
        {
            _result = Snapshot(BeamSearchStatus.Exhausted, _best);
            WriteTrace(scope, "exhausted", _best);
            return false;
        }

        _depth += 1;
        _frontier = _next
            .OrderByDescending(node => node.Score)
            .ThenBy(node => node.Order)
            .Take(_options.Width)
            .ToArray();
        _next.Clear();
        _frontierIndex = 0;
        ConsiderBest(_frontier[0]);
        if (_depth >= _options.MaxDepth)
        {
            _result = Snapshot(BeamSearchStatus.DepthLimit, _best);
            WriteTrace(scope, "depth_limit", _best);
            return false;
        }
        return true;
    }

    private double Evaluate(TState state)
    {
        var score = _evaluate(state, _options.Actor);
        if (!double.IsFinite(score))
        {
            throw new InvalidOperationException("BeamSearch evaluator returned a non-finite score.");
        }
        return score;
    }

    private string StateKey(TState state)
    {
        var key = _environment.StateKey(state);
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException("Environment returned an empty state key.");
        }
        return key;
    }

    private static void EnsureUniqueActions(IReadOnlyList<TAction> actions)
    {
        var seen = new HashSet<TAction>();
        foreach (var action in actions)
        {
            if (!seen.Add(action))
            {
                throw new InvalidOperationException("Environment returned duplicate legal actions.");
            }
        }
    }

    private void ConsiderBest(Node candidate)
    {
        if (candidate.Score > _best.Score)
        {
            _best = candidate;
        }
    }

    private BeamSearchResult<TAction> Snapshot(BeamSearchStatus status, Node node)
    {
        return new BeamSearchResult<TAction>(
            status,
            node.Actions,
            node.Score,
            node.Actions.Count,
            _expandedNodes,
            _generatedNodes
        );
    }

    private void WriteTrace(DecisionScope scope, string eventName, Node node)
    {
        scope.Trace?.Write(
            scope.Tick,
            "beam_search",
            eventName,
            new Dictionary<string, object?>
            {
                ["depth"] = node.Actions.Count,
                ["score"] = node.Score,
                ["expanded_nodes"] = _expandedNodes,
                ["generated_nodes"] = _generatedNodes,
            }
        );
    }

    private sealed record Node(
        TState State,
        IReadOnlyList<TAction> Actions,
        double Score,
        long Order
    );
}
