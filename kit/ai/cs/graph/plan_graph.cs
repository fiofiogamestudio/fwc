using Fw.Rt.AI.Core;
using Fw.Rt.AI.Plan;
using static Fw.Rt.AI.Graph.DecisionProgramData;

namespace Fw.Rt.AI.Graph;

public sealed class PlanGraph
{
    private readonly IReadOnlyDictionary<string, PlanGoal<string>> _goals;
    private readonly GoalPlanner<string> _planner = new();

    public PlanGraph(DecisionGraph graph, string rootName = "")
    {
        ArgumentNullException.ThrowIfNull(graph);
        DecisionGraphNode root = FindRoot(graph, "GoapRoot", rootName);
        var goals = new Dictionary<string, PlanGoal<string>>(StringComparer.Ordinal);
        foreach (DecisionGraphNode node in graph.Children(root.Id, "goal"))
        {
            RequireType(node, "GoapGoal", graph.Id);
            string name = RequiredText(graph, node, "name");
            if (!goals.TryAdd(name, new PlanGoal<string>(
                name,
                DecisionGraph.TextList(node.Values, "requires")
            )))
            {
                throw new DecisionGraphException(
                    $"Decision graph '{graph.Id}' has duplicate GOAP goal '{name}'."
                );
            }
        }
        foreach (DecisionGraphNode node in graph.Children(root.Id, "action"))
        {
            RequireType(node, "GoapAction", graph.Id);
            _planner.Add(new PlanAction<string>(
                RequiredText(graph, node, "name"),
                DecisionGraph.TextList(node.Values, "requires"),
                DecisionGraph.TextList(node.Values, "adds"),
                DecisionGraph.TextList(node.Values, "removes"),
                DecisionGraph.Number(node.Values, "cost", 1.0)
            ));
        }
        if (goals.Count == 0 || _planner.Actions.Count == 0)
        {
            throw new DecisionGraphException(
                $"Decision graph '{graph.Id}' GOAP root needs goals and actions."
            );
        }
        _goals = goals;
    }

    public IReadOnlyCollection<string> Goals => _goals.Keys.ToArray();
    public IReadOnlyCollection<string> Actions => _planner.Actions.Select(action => action.Id).ToArray();

    public PlanSearch<string> Begin(IEnumerable<string> facts, string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        if (!_goals.TryGetValue(goal.Trim(), out PlanGoal<string>? value))
        {
            throw new DecisionGraphException($"Unknown GOAP goal '{goal}'.");
        }
        return _planner.Begin(facts, value);
    }

    public PlanResult<string> Complete(
        IEnumerable<string> facts,
        string goal,
        DecisionScope scope
    )
    {
        ArgumentNullException.ThrowIfNull(scope);
        PlanSearch<string> search = Begin(facts, goal);
        PlanResult<string> result;
        do
        {
            result = search.Step(scope);
        }
        while (result.Status == PlanStatus.Searching && !scope.Budget.IsExhausted);
        return result;
    }
}
