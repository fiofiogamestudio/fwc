using Fw.Rt.AI.Behavior;
using Fw.Rt.AI.Core;
using Fw.Rt.AI.State;
using Fw.Rt.AI.Utility;
using static Fw.Rt.AI.Graph.DecisionProgramData;

namespace Fw.Rt.AI.Graph;

public interface ITaskHost
{
    BehaviorStatus Tick(
        string task,
        IReadOnlyDictionary<string, object?> parameters,
        Blackboard blackboard,
        DecisionScope scope
    );
}

public sealed class DecisionSession
{
    public string ChoiceId { get; set; } = "";
    public string BehaviorOwner { get; set; } = "";
    public BehaviorSession Behavior { get; } = new();
    public StateTreeSession State { get; } = new();

    public DecisionSession Clone()
    {
        var clone = new DecisionSession
        {
            ChoiceId = ChoiceId,
            BehaviorOwner = BehaviorOwner,
        };
        clone.Behavior.CopyFrom(Behavior);
        clone.State.CopyFrom(State);
        return clone;
    }

    public void CopyFrom(DecisionSession other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ChoiceId = other.ChoiceId;
        BehaviorOwner = other.BehaviorOwner;
        Behavior.CopyFrom(other.Behavior);
        State.CopyFrom(other.State);
    }

    public void Reset()
    {
        ChoiceId = "";
        BehaviorOwner = "";
        Behavior.Reset();
        State.Reset();
    }

    public void ResetBehavior(string owner)
    {
        if (string.Equals(BehaviorOwner, owner, StringComparison.Ordinal))
        {
            return;
        }
        Behavior.Reset();
        BehaviorOwner = owner;
    }
}

public sealed record DecisionResult(
    string Active,
    BehaviorStatus Status
);

public sealed class UtilityProgram
{
    private readonly DecisionGraph _graph;
    private readonly DecisionExpression _expression;
    private readonly UtilitySelector<DecisionFrame, GoalSpec> _selector = new();
    private readonly IReadOnlyDictionary<string, GoalSpec> _goals;
    private readonly double _switchThreshold;
    private readonly string _switchThresholdKey;

    public UtilityProgram(DecisionGraph graph, string rootName = "")
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _expression = new DecisionExpression(graph);
        DecisionGraphNode root = FindRoot(graph, "UtilityRoot", rootName);
        _switchThreshold = DecisionGraph.Number(root.Values, "switch_threshold");
        _switchThresholdKey = DecisionGraph.Text(root.Values, "switch_threshold_key");
        var compiler = new BehaviorCompiler(graph, _expression);
        var goals = new Dictionary<string, GoalSpec>(StringComparer.Ordinal);
        foreach (DecisionGraphNode node in graph.Children(root.Id, "goal"))
        {
            RequireType(node, "UtilityGoal", graph.Id);
            string name = RequiredText(graph, node, "name");
            if (goals.ContainsKey(name))
            {
                throw new DecisionGraphException(
                    $"Decision graph '{graph.Id}' has duplicate utility goal '{name}'."
                );
            }
            DecisionGraphNode score = graph.Input(node.Id, "score")!;
            DecisionGraphNode? condition = graph.Input(node.Id, "condition", false);
            DecisionGraphNode[] behaviors = graph.Children(node.Id, "behavior").ToArray();
            if (behaviors.Length != 1)
            {
                throw new DecisionGraphException(
                    $"Decision graph '{graph.Id}' utility goal '{name}' needs one behavior."
                );
            }
            var goal = new GoalSpec(
                name,
                node,
                score.Id,
                condition?.Id,
                compiler.Compile(behaviors[0].Id)
            );
            goals.Add(name, goal);
            _selector.Add(new UtilityOption<DecisionFrame, GoalSpec>(
                name,
                goal,
                frame => IsAvailable(goal, frame.Blackboard, frame.Scope)
                    ? _expression.Number(goal.ScoreNode, frame.Blackboard, frame.Scope)
                    : double.NegativeInfinity,
                DecisionGraph.Number(node.Values, "weight", 1.0),
                DecisionGraph.Number(node.Values, "noise")
            ));
        }
        if (goals.Count == 0)
        {
            throw new DecisionGraphException(
                $"Decision graph '{graph.Id}' utility root needs at least one goal."
            );
        }
        _goals = goals;
    }

    public IReadOnlyCollection<string> Goals => _goals.Keys.ToArray();

    public bool IsAvailable(
        string goal,
        Blackboard blackboard,
        DecisionScope scope
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(blackboard);
        ArgumentNullException.ThrowIfNull(scope);
        if (!_goals.TryGetValue(goal.Trim(), out GoalSpec? value))
        {
            throw new DecisionGraphException($"Unknown utility goal '{goal}'.");
        }
        return IsAvailable(value, blackboard, scope);
    }

    public DecisionResult Tick(
        Blackboard blackboard,
        DecisionSession session,
        ITaskHost host,
        DecisionScope scope,
        bool reselect = true
    )
    {
        ArgumentNullException.ThrowIfNull(blackboard);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(scope);
        var frame = new DecisionFrame(blackboard, session, host, scope);
        GoalSpec? goal = null;
        if (!reselect && session.ChoiceId.Length > 0)
        {
            _goals.TryGetValue(session.ChoiceId, out goal);
            if (goal != null && !IsAvailable(goal, blackboard, scope))
            {
                goal = null;
            }
        }
        if (goal == null)
        {
            double threshold = _switchThresholdKey.Length == 0
                ? _switchThreshold
                : blackboard.Number(_switchThresholdKey, _switchThreshold);
            UtilityResult<GoalSpec> selected = _selector.Select(
                frame,
                scope,
                session.ChoiceId,
                Math.Max(threshold, 0.0)
            );
            if (!selected.Complete)
            {
                return new DecisionResult("", BehaviorStatus.Suspended);
            }
            if (!selected.HasChoice || selected.Choice == null)
            {
                session.Reset();
                return new DecisionResult("", BehaviorStatus.Failure);
            }
            goal = selected.Choice;
        }

        return TickSelected(goal, frame, session, blackboard, scope);
    }

    private bool IsAvailable(
        GoalSpec goal,
        Blackboard blackboard,
        DecisionScope scope
    )
    {
        return !goal.ConditionNode.HasValue
            || _expression.Boolean(goal.ConditionNode.Value, blackboard, scope);
    }

    public DecisionResult TickGoal(
        string goal,
        Blackboard blackboard,
        DecisionSession session,
        ITaskHost host,
        DecisionScope scope
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(blackboard);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(scope);
        if (!_goals.TryGetValue(goal.Trim(), out GoalSpec? selected))
        {
            throw new DecisionGraphException($"Unknown utility goal '{goal}'.");
        }
        var frame = new DecisionFrame(blackboard, session, host, scope);
        return TickSelected(selected, frame, session, blackboard, scope);
    }

    private DecisionResult TickSelected(
        GoalSpec goal,
        DecisionFrame frame,
        DecisionSession session,
        Blackboard blackboard,
        DecisionScope scope
    )
    {
        if (!string.Equals(session.ChoiceId, goal.Name, StringComparison.Ordinal))
        {
            session.ChoiceId = goal.Name;
            session.ResetBehavior($"goal:{goal.Name}");
        }
        blackboard.Set("decision.goal", goal.Name);
        foreach ((string key, object? value) in goal.Node.Values)
        {
            blackboard.Set($"decision.{key}", value);
        }
        BehaviorStatus status = goal.Behavior.Tick(frame, session.Behavior, scope);
        scope.Trace?.Write(
            scope.Tick,
            "graph",
            "goal",
            new Dictionary<string, object?>
            {
                ["graph"] = _graph.Id,
                ["goal"] = goal.Name,
                ["status"] = status.ToString(),
            }
        );
        return new DecisionResult(goal.Name, status);
    }

    private sealed record GoalSpec(
        string Name,
        DecisionGraphNode Node,
        int ScoreNode,
        int? ConditionNode,
        BehaviorTree<DecisionFrame> Behavior
    );
}

public sealed class StateProgram
{
    private readonly DecisionGraph _graph;
    private readonly StateTree<DecisionFrame> _tree;
    private readonly IReadOnlyDictionary<string, string> _views;

    public StateProgram(DecisionGraph graph, string rootName = "")
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        var expression = new DecisionExpression(graph);
        var behaviorCompiler = new BehaviorCompiler(graph, expression);
        DecisionGraphNode root = FindRoot(graph, "StateTreeRoot", rootName);
        var states = new List<StateSpec>();
        CollectStates(root, "", states, new HashSet<int>());
        if (states.Count == 0)
        {
            throw new DecisionGraphException(
                $"Decision graph '{graph.Id}' state root needs at least one state."
            );
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (StateSpec state in states)
        {
            if (!names.Add(state.Name))
            {
                throw new DecisionGraphException(
                    $"Decision graph '{graph.Id}' has duplicate state '{state.Name}'."
                );
            }
        }

        var nodes = new List<StateNode<DecisionFrame>>();
        var views = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (StateSpec state in states)
        {
            DecisionGraphNode? enterNode = graph.Input(state.Node.Id, "enter", false);
            DecisionGraphNode[] taskNodes = graph.Children(state.Node.Id, "task").ToArray();
            if (taskNodes.Length > 1)
            {
                throw new DecisionGraphException(
                    $"Decision graph '{graph.Id}' state '{state.Name}' has multiple tasks."
                );
            }
            BehaviorTree<DecisionFrame>? task = taskNodes.Length == 0
                ? null
                : behaviorCompiler.Compile(taskNodes[0].Id);
            var transitions = new List<StateTransition<DecisionFrame>>();
            foreach (DecisionGraphNode transition in graph.Children(state.Node.Id, "transition"))
            {
                RequireType(transition, "Transition", graph.Id);
                string target = RequiredText(graph, transition, "target");
                if (!names.Contains(target))
                {
                    throw new DecisionGraphException(
                        $"Decision graph '{graph.Id}' transition targets unknown state '{target}'."
                    );
                }
                DecisionGraphNode? conditionNode = graph.Input(transition.Id, "condition", false);
                transitions.Add(new StateTransition<DecisionFrame>(
                    target,
                    ParseTrigger(graph, transition),
                    conditionNode == null ? null : (frame, session) =>
                    {
                        SetStateFacts(frame.Blackboard, state.Name, session.StateTicks);
                        return expression.Boolean(conditionNode.Id, frame.Blackboard, frame.Scope);
                    },
                    (int)DecisionGraph.Number(transition.Values, "priority")
                ));
            }
            nodes.Add(new StateNode<DecisionFrame>(
                state.Name,
                state.Parent,
                DecisionGraph.Boolean(state.Node.Values, "initial"),
                (int)DecisionGraph.Number(state.Node.Values, "order", state.Node.Id),
                enterNode == null ? null : (frame, session) =>
                {
                    SetStateFacts(frame.Blackboard, state.Name, session.StateTicks);
                    return expression.Boolean(enterNode.Id, frame.Blackboard, frame.Scope);
                },
                task == null ? null : (frame, session, scope) =>
                {
                    SetStateFacts(frame.Blackboard, state.Name, session.StateTicks);
                    frame.Session.ResetBehavior($"state:{frame.Session.State.ActiveState}");
                    return task.Tick(frame, frame.Session.Behavior, scope);
                },
                transitions
            ));
            views[state.Name] = DecisionGraph.Text(state.Node.Values, "view_state", state.Name);
        }
        _tree = new StateTree<DecisionFrame>(nodes);
        _views = views;
    }

    public IReadOnlyCollection<string> States => _views.Keys.ToArray();

    public DecisionResult Tick(
        Blackboard blackboard,
        DecisionSession session,
        ITaskHost host,
        DecisionScope scope
    )
    {
        ArgumentNullException.ThrowIfNull(blackboard);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(scope);
        var frame = new DecisionFrame(blackboard, session, host, scope);
        StateTreeResult result = _tree.Tick(frame, session.State, scope);
        SetStateFacts(blackboard, result.ActiveState, session.State.StateTicks);
        string view = _views[result.ActiveState];
        blackboard.Set("decision.view_state", view);
        scope.Trace?.Write(
            scope.Tick,
            "graph",
            "state",
            new Dictionary<string, object?>
            {
                ["graph"] = _graph.Id,
                ["state"] = result.ActiveState,
                ["view"] = view,
                ["status"] = result.Status.ToString(),
            }
        );
        return new DecisionResult(view, result.Status);
    }

    private void CollectStates(
        DecisionGraphNode owner,
        string parent,
        ICollection<StateSpec> result,
        ISet<int> path
    )
    {
        if (!path.Add(owner.Id))
        {
            throw new DecisionGraphException(
                $"Decision graph '{_graph.Id}' state hierarchy contains a cycle."
            );
        }
        foreach (DecisionGraphNode child in _graph.Children(owner.Id, "state"))
        {
            RequireType(child, "State", _graph.Id);
            string name = RequiredText(_graph, child, "name");
            result.Add(new StateSpec(name, parent, child));
            CollectStates(child, name, result, path);
        }
        path.Remove(owner.Id);
    }

    private static void SetStateFacts(
        Blackboard blackboard,
        string state,
        int ticks
    )
    {
        blackboard.Set("decision.state", state);
        blackboard.Set("decision.state_ticks", ticks);
    }

    private static StateTransitionTrigger ParseTrigger(
        DecisionGraph graph,
        DecisionGraphNode node
    )
    {
        return DecisionGraph.Text(node.Values, "trigger", "tick") switch
        {
            "tick" => StateTransitionTrigger.Tick,
            "success" => StateTransitionTrigger.Success,
            "failure" => StateTransitionTrigger.Failure,
            string value => throw new DecisionGraphException(
                $"Decision graph '{graph.Id}' transition {node.Id} has unknown trigger '{value}'."
            ),
        };
    }

    private sealed record StateSpec(string Name, string Parent, DecisionGraphNode Node);
}

internal sealed record DecisionFrame(
    Blackboard Blackboard,
    DecisionSession Session,
    ITaskHost Host,
    DecisionScope Scope
);

internal sealed class BehaviorCompiler
{
    private readonly DecisionGraph _graph;
    private readonly DecisionExpression _expression;
    private readonly Dictionary<int, BehaviorNode<DecisionFrame>> _compiled = [];

    public BehaviorCompiler(DecisionGraph graph, DecisionExpression expression)
    {
        _graph = graph;
        _expression = expression;
    }

    public BehaviorTree<DecisionFrame> Compile(int nodeId)
    {
        return new BehaviorTree<DecisionFrame>(CompileNode(nodeId, new HashSet<int>()));
    }

    private BehaviorNode<DecisionFrame> CompileNode(int nodeId, HashSet<int> path)
    {
        if (_compiled.TryGetValue(nodeId, out BehaviorNode<DecisionFrame>? existing))
        {
            return existing;
        }
        if (!path.Add(nodeId))
        {
            throw new DecisionGraphException(
                $"Decision graph '{_graph.Id}' behavior contains a cycle at node {nodeId}."
            );
        }
        try
        {
            DecisionGraphNode node = _graph.Node(nodeId);
            BehaviorNode<DecisionFrame> result = node.Type switch
            {
                "Sequence" => new BehaviorSequence<DecisionFrame>(Children(node, path)),
                "Selector" => new BehaviorSelector<DecisionFrame>(Children(node, path)),
                "ReactiveSequence" => new BehaviorReactiveSequence<DecisionFrame>(Children(node, path)),
                "ReactiveSelector" => new BehaviorReactiveSelector<DecisionFrame>(Children(node, path)),
                "Invert" => new BehaviorInvert<DecisionFrame>(SingleChild(node, path)),
                "Condition" => CompileCondition(node),
                "Task" => CompileTask(node),
                "Wait" => CompileWait(node),
                "Succeed" => new BehaviorAction<DecisionFrame>(_ => BehaviorStatus.Success),
                "Fail" => new BehaviorAction<DecisionFrame>(_ => BehaviorStatus.Failure),
                _ => throw new DecisionGraphException(
                    $"Decision graph '{_graph.Id}' has unsupported behavior node '{node.Type}'."
                ),
            };
            _compiled[nodeId] = result;
            return result;
        }
        finally
        {
            path.Remove(nodeId);
        }
    }

    private BehaviorNode<DecisionFrame>[] Children(
        DecisionGraphNode node,
        HashSet<int> path
    )
    {
        DecisionGraphNode[] children = _graph.Children(node.Id, "child").ToArray();
        if (children.Length == 0)
        {
            throw new DecisionGraphException(
                $"Decision graph '{_graph.Id}' behavior node {node.Id} needs children."
            );
        }
        return children.Select(child => CompileNode(child.Id, path)).ToArray();
    }

    private BehaviorNode<DecisionFrame> SingleChild(
        DecisionGraphNode node,
        HashSet<int> path
    )
    {
        DecisionGraphNode[] children = _graph.Children(node.Id, "child").ToArray();
        if (children.Length != 1)
        {
            throw new DecisionGraphException(
                $"Decision graph '{_graph.Id}' behavior node {node.Id} needs one child."
            );
        }
        return CompileNode(children[0].Id, path);
    }

    private BehaviorNode<DecisionFrame> CompileCondition(DecisionGraphNode node)
    {
        DecisionGraphNode condition = _graph.Input(node.Id, "condition")!;
        return new BehaviorCondition<DecisionFrame>(frame =>
            _expression.Boolean(condition.Id, frame.Blackboard, frame.Scope));
    }

    private BehaviorNode<DecisionFrame> CompileTask(DecisionGraphNode node)
    {
        string name = RequiredText(_graph, node, "name");
        return new BehaviorAction<DecisionFrame>(frame => frame.Host.Tick(
            name,
            node.Values,
            frame.Blackboard,
            frame.Scope
        ));
    }

    private BehaviorNode<DecisionFrame> CompileWait(DecisionGraphNode node)
    {
        string key = DecisionGraph.Text(node.Values, "ticks_key");
        int ticks = Math.Max((int)DecisionGraph.Number(node.Values, "ticks"), 0);
        return new BehaviorWait<DecisionFrame>(frame => key.Length == 0
            ? ticks
            : Math.Max((int)frame.Blackboard.Number(key, ticks), 0));
    }
}

internal static class DecisionProgramData
{
    public static DecisionGraphNode FindRoot(
        DecisionGraph graph,
        string type,
        string name
    )
    {
        DecisionGraphNode[] roots = graph.NodesOfType(type)
            .Where(node => name.Length == 0
                || string.Equals(DecisionGraph.Text(node.Values, "name"), name, StringComparison.Ordinal))
            .ToArray();
        if (roots.Length != 1)
        {
            throw new DecisionGraphException(
                $"Decision graph '{graph.Id}' needs exactly one '{type}' root for '{name}'."
            );
        }
        return roots[0];
    }

    public static string RequiredText(
        DecisionGraph graph,
        DecisionGraphNode node,
        string name
    )
    {
        string value = DecisionGraph.Text(node.Values, name);
        if (value.Length == 0)
        {
            throw new DecisionGraphException(
                $"Decision graph '{graph.Id}' node {node.Id} requires '{name}'."
            );
        }
        return value;
    }

    public static void RequireType(DecisionGraphNode node, string type, string graphId)
    {
        if (!string.Equals(node.Type, type, StringComparison.Ordinal))
        {
            throw new DecisionGraphException(
                $"Decision graph '{graphId}' expected '{type}', found '{node.Type}'."
            );
        }
    }
}
