using Fw.Rt.AI.Core;
using Fw.Rt.AI.Environment;
using Fw.Rt.AI.Model;

namespace Fw.Rt.AI.Search;

public enum PuctSearchStatus
{
    Searching,
    Complete,
}

public sealed class PuctSearchOptions
{
    public PuctSearchOptions(
        int simulationLimit = 256,
        int maxDepth = 128,
        double exploration = 1.25
    )
    {
        if (simulationLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(simulationLimit));
        }
        if (maxDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }
        if (!double.IsFinite(exploration) || exploration < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(exploration));
        }

        SimulationLimit = simulationLimit;
        MaxDepth = maxDepth;
        Exploration = exploration;
    }

    public int SimulationLimit { get; }
    public int MaxDepth { get; }
    public double Exploration { get; }
}

public sealed class PuctActionStatistics<TAction>
    where TAction : notnull
{
    public PuctActionStatistics(
        TAction action,
        double prior,
        int visits,
        IEnumerable<double> meanValues
    )
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Prior = prior;
        Visits = visits;
        MeanValues = Array.AsReadOnly(
            (meanValues ?? throw new ArgumentNullException(nameof(meanValues))).ToArray()
        );
    }

    public TAction Action { get; }
    public double Prior { get; }
    public int Visits { get; }
    public IReadOnlyList<double> MeanValues { get; }
}

public sealed class PuctSearchResult<TAction>
    where TAction : notnull
{
    public PuctSearchResult(
        PuctSearchStatus status,
        bool hasAction,
        TAction? action,
        int simulations,
        IEnumerable<double> rootMeanValues,
        IEnumerable<PuctActionStatistics<TAction>> actions
    )
    {
        Status = status;
        HasAction = hasAction;
        Action = action;
        Simulations = simulations;
        RootMeanValues = Array.AsReadOnly(
            (rootMeanValues ?? throw new ArgumentNullException(nameof(rootMeanValues))).ToArray()
        );
        Actions = Array.AsReadOnly(
            (actions ?? throw new ArgumentNullException(nameof(actions))).ToArray()
        );
    }

    public PuctSearchStatus Status { get; }
    public bool HasAction { get; }
    public TAction? Action { get; }
    public int Simulations { get; }
    public IReadOnlyList<double> RootMeanValues { get; }
    public IReadOnlyList<PuctActionStatistics<TAction>> Actions { get; }
    public bool IsFinished => Status == PuctSearchStatus.Complete;
}

public sealed class PuctSearch<TState, TObservation, TAction>
    where TState : notnull
    where TObservation : notnull
    where TAction : notnull
{
    private readonly IGameEnvironment<TState, TObservation, TAction> _environment;
    private readonly IPolicyValueModel<TObservation, TAction> _model;
    private readonly PuctSearchOptions _options;
    private readonly Node _root;
    private int _simulations;

    public PuctSearch(
        IGameEnvironment<TState, TObservation, TAction> environment,
        TState rootState,
        IPolicyValueModel<TObservation, TAction> model,
        PuctSearchOptions? options = null
    )
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? new PuctSearchOptions();
        if (_environment.Spec.MoveMode != GameMoveMode.Sequential)
        {
            throw new NotSupportedException("PuctSearch requires sequential moves.");
        }
        if (_environment.Spec.Information != GameInformation.Perfect)
        {
            throw new NotSupportedException(
                "PuctSearch requires a perfect-information state. Determinize hidden information in the host adapter first."
            );
        }

        var state = _environment.Clone(rootState ?? throw new ArgumentNullException(nameof(rootState)));
        var result = _environment.Result(state);
        GameEnvironmentGuard.ValidateResult(result, _environment.Spec);
        _root = CreateNode(state, result);
    }

    public bool IsFinished => _root.Result.IsFinished || _simulations >= _options.SimulationLimit;

    public PuctSearchResult<TAction> Step(DecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        while (!IsFinished && scope.Budget.TrySpend())
        {
            Simulate(scope);
            _simulations += 1;
        }

        var status = IsFinished ? PuctSearchStatus.Complete : PuctSearchStatus.Searching;
        var result = Snapshot(status);
        if (status == PuctSearchStatus.Complete)
        {
            scope.Trace?.Write(
                scope.Tick,
                "puct_search",
                "complete",
                new Dictionary<string, object?>
                {
                    ["simulations"] = _simulations,
                    ["has_action"] = result.HasAction,
                }
            );
        }
        return result;
    }

    private void Simulate(DecisionScope scope)
    {
        var node = _root;
        var visitedNodes = new List<Node> { node };
        var visitedEdges = new List<Edge>();
        IReadOnlyList<double> values;

        for (var depth = 0; ; depth += 1)
        {
            if (node.Result.IsFinished)
            {
                values = node.Result.Payoffs;
                break;
            }
            if (depth >= _options.MaxDepth)
            {
                values = EvaluateLeaf(node);
                break;
            }
            if (!node.IsExpanded)
            {
                values = Expand(node);
                if (node.Actor != GameActors.Chance)
                {
                    break;
                }
            }
            if (node.Edges.Count == 0)
            {
                throw new InvalidOperationException("A running game state has no legal actions.");
            }

            var edge = node.Actor == GameActors.Chance
                ? SampleChance(node.Edges, scope)
                : SelectPlayerEdge(node);
            visitedEdges.Add(edge);
            if (edge.Child == null)
            {
                var state = _environment.Clone(node.State);
                var transition = _environment.Step(state, edge.Action);
                GameEnvironmentGuard.ValidateTransition(transition, _environment.Spec);
                edge.Child = CreateNode(transition.State, transition.Result);
            }
            node = edge.Child;
            visitedNodes.Add(node);
        }

        ValidateValues(values);
        foreach (var visited in visitedNodes)
        {
            visited.Visits += 1;
            AddValues(visited.ValueSums, values);
        }
        foreach (var edge in visitedEdges)
        {
            edge.Visits += 1;
            AddValues(edge.ValueSums, values);
        }
    }

    private IReadOnlyList<double> Expand(Node node)
    {
        if (node.Actor == GameActors.Simultaneous)
        {
            throw new NotSupportedException("PuctSearch does not support simultaneous actions.");
        }
        if (node.Actor == GameActors.Terminal)
        {
            throw new InvalidOperationException("A running state cannot report the terminal actor.");
        }
        if (node.Actor == GameActors.Chance)
        {
            var outcomes = _environment.ChanceOutcomes(node.State)
                ?? throw new InvalidOperationException("Environment returned null chance outcomes.");
            if (outcomes.Count == 0)
            {
                throw new InvalidOperationException("A chance state has no outcomes.");
            }
            var chanceTotal = outcomes.Sum(outcome => outcome.Probability);
            if (!double.IsFinite(chanceTotal) || chanceTotal <= 0.0)
            {
                throw new InvalidOperationException("Chance outcome probabilities have no finite positive mass.");
            }
            foreach (var outcome in outcomes)
            {
                node.Edges.Add(new Edge(outcome.Action, outcome.Probability / chanceTotal, _environment.Spec.PlayerCount));
            }
            node.IsExpanded = true;
            return new double[_environment.Spec.PlayerCount];
        }
        if (node.Actor < 0 || node.Actor >= _environment.Spec.PlayerCount)
        {
            throw new InvalidOperationException($"Environment returned invalid actor {node.Actor}.");
        }

        var legalActions = _environment.LegalActions(node.State)
            ?? throw new InvalidOperationException("Environment returned null legal actions.");
        if (legalActions.Count == 0)
        {
            throw new InvalidOperationException("A running player state has no legal actions.");
        }
        EnsureUniqueActions(legalActions);
        var observation = _environment.Observe(node.State, node.Actor);
        var prediction = _model.Predict(observation, node.Actor, legalActions)
            ?? throw new InvalidOperationException("Policy-value model returned null.");
        ValidateValues(prediction.Values);

        var weights = new double[legalActions.Count];
        var assigned = new bool[legalActions.Count];
        foreach (var item in prediction.Priors)
        {
            var index = FindAction(legalActions, item.Action);
            if (index < 0)
            {
                throw new InvalidOperationException("Policy-value model returned a prior for an illegal action.");
            }
            if (assigned[index])
            {
                throw new InvalidOperationException("Policy-value model returned duplicate action priors.");
            }
            assigned[index] = true;
            weights[index] = item.Prior;
        }

        var policyTotal = weights.Sum();
        if (policyTotal <= 0.0)
        {
            Array.Fill(weights, 1.0 / legalActions.Count);
        }
        else
        {
            for (var index = 0; index < weights.Length; index += 1)
            {
                weights[index] /= policyTotal;
            }
        }
        for (var index = 0; index < legalActions.Count; index += 1)
        {
            node.Edges.Add(new Edge(legalActions[index], weights[index], _environment.Spec.PlayerCount));
        }
        node.IsExpanded = true;
        return prediction.Values;
    }

    private IReadOnlyList<double> EvaluateLeaf(Node node)
    {
        if (node.Result.IsFinished)
        {
            return node.Result.Payoffs;
        }
        if (node.Actor == GameActors.Chance)
        {
            return new double[_environment.Spec.PlayerCount];
        }
        if (node.Actor < 0 || node.Actor >= _environment.Spec.PlayerCount)
        {
            throw new NotSupportedException($"PuctSearch cannot evaluate special actor {node.Actor}.");
        }
        var legalActions = _environment.LegalActions(node.State)
            ?? throw new InvalidOperationException("Environment returned null legal actions.");
        var prediction = _model.Predict(
            _environment.Observe(node.State, node.Actor),
            node.Actor,
            legalActions
        ) ?? throw new InvalidOperationException("Policy-value model returned null.");
        ValidateValues(prediction.Values);
        return prediction.Values;
    }

    private Edge SelectPlayerEdge(Node node)
    {
        Edge? best = null;
        var bestScore = double.NegativeInfinity;
        var parentScale = Math.Sqrt(Math.Max(1, node.Visits));
        foreach (var edge in node.Edges)
        {
            var exploitation = edge.Visits == 0
                ? 0.0
                : edge.ValueSums[node.Actor] / edge.Visits;
            var exploration = _options.Exploration * edge.Prior * parentScale / (1 + edge.Visits);
            var score = exploitation + exploration;
            if (score > bestScore)
            {
                best = edge;
                bestScore = score;
            }
        }
        return best ?? throw new InvalidOperationException("PuctSearch could not select an action.");
    }

    private static Edge SampleChance(IReadOnlyList<Edge> edges, DecisionScope scope)
    {
        var sample = scope.Random.NextDouble();
        var cumulative = 0.0;
        foreach (var edge in edges)
        {
            cumulative += edge.Prior;
            if (sample < cumulative)
            {
                return edge;
            }
        }
        return edges[^1];
    }

    private Node CreateNode(TState state, GameEpisodeResult result)
    {
        var actor = result.IsFinished ? GameActors.Terminal : _environment.CurrentActor(state);
        GameEnvironmentGuard.ValidateActor(actor, _environment.Spec);
        return new Node(state, result, actor, _environment.Spec.PlayerCount);
    }

    private PuctSearchResult<TAction> Snapshot(PuctSearchStatus status)
    {
        var statistics = _root.Edges.Select(edge => new PuctActionStatistics<TAction>(
            edge.Action,
            edge.Prior,
            edge.Visits,
            MeanValues(edge.ValueSums, edge.Visits)
        )).ToArray();
        var best = _root.Edges
            .Select((edge, index) => (Edge: edge, Index: index))
            .OrderByDescending(item => item.Edge.Visits)
            .ThenByDescending(item => item.Edge.Prior)
            .ThenBy(item => item.Index)
            .Select(item => item.Edge)
            .FirstOrDefault();
        return new PuctSearchResult<TAction>(
            status,
            best != null,
            best == null ? default : best.Action,
            _simulations,
            MeanValues(_root.ValueSums, _root.Visits),
            statistics
        );
    }

    private void ValidateValues(IReadOnlyList<double> values)
    {
        if (values.Count != _environment.Spec.PlayerCount)
        {
            throw new InvalidOperationException(
                $"Policy-value model returned {values.Count} values for {_environment.Spec.PlayerCount} players."
            );
        }
        if (values.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidOperationException("Policy-value model returned a non-finite value.");
        }
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

    private static int FindAction(IReadOnlyList<TAction> actions, TAction action)
    {
        var comparer = EqualityComparer<TAction>.Default;
        for (var index = 0; index < actions.Count; index += 1)
        {
            if (comparer.Equals(actions[index], action))
            {
                return index;
            }
        }
        return -1;
    }

    private static void AddValues(double[] totals, IReadOnlyList<double> values)
    {
        for (var index = 0; index < totals.Length; index += 1)
        {
            totals[index] += values[index];
        }
    }

    private static IReadOnlyList<double> MeanValues(double[] totals, int visits)
    {
        if (visits == 0)
        {
            return Array.AsReadOnly(new double[totals.Length]);
        }
        return Array.AsReadOnly(totals.Select(value => value / visits).ToArray());
    }

    private sealed class Node
    {
        public Node(TState state, GameEpisodeResult result, int actor, int playerCount)
        {
            State = state;
            Result = result;
            Actor = actor;
            ValueSums = new double[playerCount];
        }

        public TState State { get; }
        public GameEpisodeResult Result { get; }
        public int Actor { get; }
        public List<Edge> Edges { get; } = [];
        public double[] ValueSums { get; }
        public int Visits { get; set; }
        public bool IsExpanded { get; set; }
    }

    private sealed class Edge
    {
        public Edge(TAction action, double prior, int playerCount)
        {
            Action = action;
            Prior = prior;
            ValueSums = new double[playerCount];
        }

        public TAction Action { get; }
        public double Prior { get; }
        public double[] ValueSums { get; }
        public int Visits { get; set; }
        public Node? Child { get; set; }
    }
}
