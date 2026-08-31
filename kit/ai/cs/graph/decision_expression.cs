using Fw.Rt.AI.Core;

namespace Fw.Rt.AI.Graph;

public sealed class DecisionExpression
{
    private const int MaxDepth = 64;
    private readonly DecisionGraph _graph;

    public DecisionExpression(DecisionGraph graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    public double Number(int nodeId, Blackboard blackboard, DecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(blackboard);
        ArgumentNullException.ThrowIfNull(scope);
        HashSet<int> path = scope.ExpressionPath;
        path.Clear();
        try
        {
            return EvaluateNumber(nodeId, blackboard, scope, path, 0);
        }
        finally
        {
            path.Clear();
        }
    }

    public bool Boolean(int nodeId, Blackboard blackboard, DecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(blackboard);
        ArgumentNullException.ThrowIfNull(scope);
        HashSet<int> path = scope.ExpressionPath;
        path.Clear();
        try
        {
            return EvaluateBoolean(nodeId, blackboard, scope, path, 0);
        }
        finally
        {
            path.Clear();
        }
    }

    private double EvaluateNumber(
        int nodeId,
        Blackboard blackboard,
        DecisionScope scope,
        HashSet<int> path,
        int depth
    )
    {
        Enter(nodeId, scope, path, depth);
        try
        {
            DecisionGraphNode node = _graph.Node(nodeId);
            double value = node.Type switch
            {
                "Number" => DecisionGraph.Number(node.Values, "value"),
                "FactNumber" => blackboard.Number(
                    RequiredText(node, "key"),
                    DecisionGraph.Number(node.Values, "default")
                ),
                "Add" => InputNumber(node, "a", blackboard, scope, path, depth)
                    + InputNumber(node, "b", blackboard, scope, path, depth),
                "Subtract" => InputNumber(node, "a", blackboard, scope, path, depth)
                    - InputNumber(node, "b", blackboard, scope, path, depth),
                "Multiply" => InputNumber(node, "a", blackboard, scope, path, depth)
                    * InputNumber(node, "b", blackboard, scope, path, depth),
                "Divide" => Divide(
                    InputNumber(node, "a", blackboard, scope, path, depth),
                    InputNumber(node, "b", blackboard, scope, path, depth)
                ),
                "Min" => Math.Min(
                    InputNumber(node, "a", blackboard, scope, path, depth),
                    InputNumber(node, "b", blackboard, scope, path, depth)
                ),
                "Max" => Math.Max(
                    InputNumber(node, "a", blackboard, scope, path, depth),
                    InputNumber(node, "b", blackboard, scope, path, depth)
                ),
                "Clamp" => Math.Clamp(
                    InputNumber(node, "value", blackboard, scope, path, depth),
                    OptionalInputNumber(
                        node,
                        "min",
                        DecisionGraph.Number(node.Values, "min"),
                        blackboard,
                        scope,
                        path,
                        depth
                    ),
                    OptionalInputNumber(
                        node,
                        "max",
                        DecisionGraph.Number(node.Values, "max", 1.0),
                        blackboard,
                        scope,
                        path,
                        depth
                    )
                ),
                "OneMinus" => 1.0 - InputNumber(
                    node,
                    "value",
                    blackboard,
                    scope,
                    path,
                    depth
                ),
                "If" => InputBoolean(node, "condition", blackboard, scope, path, depth)
                    ? InputNumber(node, "true", blackboard, scope, path, depth)
                    : InputNumber(node, "false", blackboard, scope, path, depth),
                "Random" => scope.Random.NextDouble(),
                _ => throw new DecisionGraphException(
                    $"Decision graph '{_graph.Id}' node {node.Id} ({node.Type}) is not numeric."
                ),
            };
            return double.IsFinite(value) ? value : 0.0;
        }
        finally
        {
            path.Remove(nodeId);
        }
    }

    private bool EvaluateBoolean(
        int nodeId,
        Blackboard blackboard,
        DecisionScope scope,
        HashSet<int> path,
        int depth
    )
    {
        Enter(nodeId, scope, path, depth);
        try
        {
            DecisionGraphNode node = _graph.Node(nodeId);
            return node.Type switch
            {
                "Bool" => DecisionGraph.Boolean(node.Values, "value"),
                "FactBool" => blackboard.Boolean(
                    RequiredText(node, "key"),
                    DecisionGraph.Boolean(node.Values, "default")
                ),
                "FactExists" => blackboard.Contains(RequiredText(node, "key"))
                    && blackboard.Value(RequiredText(node, "key")) != null,
                "Compare" => Compare(node, blackboard, scope, path, depth),
                "And" => InputBoolean(node, "a", blackboard, scope, path, depth)
                    && InputBoolean(node, "b", blackboard, scope, path, depth),
                "Or" => InputBoolean(node, "a", blackboard, scope, path, depth)
                    || InputBoolean(node, "b", blackboard, scope, path, depth),
                "Not" => !InputBoolean(node, "value", blackboard, scope, path, depth),
                "Chance" => scope.Random.NextDouble()
                    < Math.Clamp(
                        OptionalInputNumber(
                            node,
                            "probability",
                            DecisionGraph.Number(node.Values, "probability"),
                            blackboard,
                            scope,
                            path,
                            depth
                        ),
                        0.0,
                        1.0
                    ),
                _ => throw new DecisionGraphException(
                    $"Decision graph '{_graph.Id}' node {node.Id} ({node.Type}) is not boolean."
                ),
            };
        }
        finally
        {
            path.Remove(nodeId);
        }
    }

    private bool Compare(
        DecisionGraphNode node,
        Blackboard blackboard,
        DecisionScope scope,
        HashSet<int> path,
        int depth
    )
    {
        double left = InputNumber(node, "left", blackboard, scope, path, depth);
        double right = InputNumber(node, "right", blackboard, scope, path, depth);
        return DecisionGraph.Text(node.Values, "op", "eq") switch
        {
            "eq" => Math.Abs(left - right) <= double.Epsilon,
            "ne" => Math.Abs(left - right) > double.Epsilon,
            "lt" => left < right,
            "lte" => left <= right,
            "gt" => left > right,
            "gte" => left >= right,
            string value => throw new DecisionGraphException(
                $"Decision graph '{_graph.Id}' node {node.Id} has unknown compare op '{value}'."
            ),
        };
    }

    private double InputNumber(
        DecisionGraphNode node,
        string port,
        Blackboard blackboard,
        DecisionScope scope,
        HashSet<int> path,
        int depth
    )
    {
        return EvaluateNumber(
            _graph.Input(node.Id, port)!.Id,
            blackboard,
            scope,
            path,
            depth + 1
        );
    }

    private double OptionalInputNumber(
        DecisionGraphNode node,
        string port,
        double fallback,
        Blackboard blackboard,
        DecisionScope scope,
        HashSet<int> path,
        int depth
    )
    {
        DecisionGraphNode? input = _graph.Input(node.Id, port, false);
        return input == null
            ? fallback
            : EvaluateNumber(input.Id, blackboard, scope, path, depth + 1);
    }

    private bool InputBoolean(
        DecisionGraphNode node,
        string port,
        Blackboard blackboard,
        DecisionScope scope,
        HashSet<int> path,
        int depth
    )
    {
        return EvaluateBoolean(
            _graph.Input(node.Id, port)!.Id,
            blackboard,
            scope,
            path,
            depth + 1
        );
    }

    private static double Divide(double left, double right)
    {
        return Math.Abs(right) <= double.Epsilon ? 0.0 : left / right;
    }

    private void Enter(int nodeId, DecisionScope scope, HashSet<int> path, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new DecisionGraphException(
                $"Decision graph '{_graph.Id}' expression exceeds depth {MaxDepth}."
            );
        }
        if (!path.Add(nodeId))
        {
            throw new DecisionGraphException(
                $"Decision graph '{_graph.Id}' expression contains a cycle at node {nodeId}."
            );
        }
        if (!scope.Budget.TrySpend())
        {
            throw new DecisionGraphBudgetException();
        }
    }

    private string RequiredText(DecisionGraphNode node, string name)
    {
        string value = DecisionGraph.Text(node.Values, name);
        if (value.Length == 0)
        {
            throw new DecisionGraphException(
                $"Decision graph '{_graph.Id}' node {node.Id} requires '{name}'."
            );
        }
        return value;
    }
}
