using System.Text.Json;

namespace Fw.Rt.AI.Graph;

public sealed class DecisionAssets
{
    private const int MaxJsonLength = 4_194_304;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly IReadOnlyDictionary<string, ConditionSpec> _conditions;
    private readonly IReadOnlyDictionary<string, DecisionGraph> _trees;

    private DecisionAssets(
        IReadOnlyDictionary<string, ConditionSpec> conditions,
        IReadOnlyDictionary<string, DecisionGraph> trees
    )
    {
        _conditions = conditions;
        _trees = trees;
    }

    public IReadOnlyCollection<string> Conditions => _conditions.Keys.Order().ToArray();
    public IReadOnlyCollection<string> Trees => _trees.Keys.Order().ToArray();

    public static DecisionAssets Parse(
        string conditionsJson,
        IReadOnlyDictionary<string, string> treeJson,
        string source = "condition.json"
    )
    {
        ArgumentNullException.ThrowIfNull(treeJson);
        IReadOnlyDictionary<string, ConditionSpec> conditions = ParseConditions(
            conditionsJson,
            source
        );
        var trees = new Dictionary<string, DecisionGraph>(StringComparer.Ordinal);
        foreach ((string key, string json) in treeJson.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string id = RequiredId(key, "tree dictionary key");
            DecisionGraph graph = DecisionGraph.Parse(json, $"tree:{id}");
            if (!string.Equals(graph.Id, id, StringComparison.Ordinal))
            {
                throw new DecisionGraphException(
                    $"Tree dictionary key '{id}' does not match graph id '{graph.Id}'."
                );
            }
            ValidateTree(graph);
            trees.Add(id, graph);
        }
        return new DecisionAssets(conditions, trees);
    }

    public DecisionGraph BuildUtility(string json, string source = "goal.json")
    {
        using JsonDocument document = ParseDocument(json, source);
        JsonElement root = RequireObject(document.RootElement, source);
        string id = RequiredString(root, "id", source);
        JsonElement goals = RequiredArray(root, "goals", source);
        var builder = new GraphBuilder(id);
        int rootId = builder.Add("UtilityRoot", Values(
            ("name", id),
            ("switch_threshold", OptionalNumber(root, "switch_threshold")),
            ("switch_threshold_key", OptionalString(root, "switch_threshold_key"))
        ));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement goal in goals.EnumerateArray())
        {
            JsonElement item = RequireObject(goal, source);
            string name = RequiredString(item, "id", source);
            if (!names.Add(name))
            {
                throw new DecisionGraphException($"Duplicate utility goal '{name}': {source}");
            }
            string tree = RequiredString(item, "tree", source);
            int goalId = builder.Add("UtilityGoal", Values(
                ("name", name),
                ("weight", OptionalNumber(item, "weight", 1.0)),
                ("noise", OptionalNumber(item, "noise")),
                ("order", OptionalInt(item, "order"))
            ));
            builder.Link(rootId, "goal", goalId, "goal");
            int score = builder.Add("Number", Values(("value", OptionalNumber(item, "base"))));
            if (item.TryGetProperty("scores", out JsonElement scores))
            {
                RequireArray(scores, source);
                foreach (JsonElement scoreStep in scores.EnumerateArray())
                {
                    score = CompileScore(builder, score, RequireObject(scoreStep, source), source);
                }
            }
            string condition = OptionalString(item, "condition");
            if (condition.Length > 0)
            {
                int gate = CompileCondition(builder, condition, source);
                builder.Link(gate, "value", goalId, "condition");
            }
            builder.Link(score, "value", goalId, "score");
            int behavior = ImportTree(builder, tree, source);
            builder.Link(goalId, "behavior", behavior, "child");
        }
        if (names.Count == 0)
        {
            throw new DecisionGraphException($"Utility asset needs at least one goal: {source}");
        }
        return builder.Build(source);
    }

    public UtilityProgram Utility(string json, string source = "goal.json")
    {
        DecisionGraph graph = BuildUtility(json, source);
        return new UtilityProgram(graph, graph.Id);
    }

    public DecisionGraph BuildState(string json, string source = "state.json")
    {
        using JsonDocument document = ParseDocument(json, source);
        JsonElement root = RequireObject(document.RootElement, source);
        string id = RequiredString(root, "id", source);
        string initial = RequiredString(root, "initial", source);
        JsonElement authoredStates = RequiredArray(root, "states", source);
        var builder = new GraphBuilder(id);
        int rootId = builder.Add("StateTreeRoot", Values(("name", id)));
        var states = new List<(string Name, JsonElement Data, int Compiled)>();
        var stateIds = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (JsonElement authoredState in authoredStates.EnumerateArray())
        {
            JsonElement state = RequireObject(authoredState, source);
            string name = RequiredString(state, "name", source);
            if (stateIds.ContainsKey(name))
            {
                throw new DecisionGraphException($"Duplicate state '{name}': {source}");
            }
            string tree = RequiredString(state, "tree", source);
            int stateId = builder.Add("State", Values(
                ("name", name),
                ("view_state", OptionalString(state, "view_state", name)),
                ("initial", string.Equals(name, initial, StringComparison.Ordinal)),
                ("order", OptionalInt(state, "order", states.Count))
            ));
            stateIds.Add(name, stateId);
            states.Add((name, state, stateId));
            int behavior = ImportTree(builder, tree, source);
            builder.Link(stateId, "task", behavior, "child");
        }
        if (states.Count == 0)
        {
            throw new DecisionGraphException($"State asset needs at least one state: {source}");
        }
        if (!stateIds.ContainsKey(initial))
        {
            throw new DecisionGraphException(
                $"State asset references unknown initial state '{initial}': {source}"
            );
        }

        var stateData = states.ToDictionary(
            state => state.Name,
            state => state.Data,
            StringComparer.Ordinal
        );
        foreach ((string name, _, _) in states)
        {
            var chain = new HashSet<string>(StringComparer.Ordinal);
            string current = name;
            while (current.Length > 0)
            {
                if (!chain.Add(current))
                {
                    throw new DecisionGraphException(
                        $"State '{name}' has a cyclic parent chain: {source}"
                    );
                }
                string parent = OptionalString(stateData[current], "parent");
                if (parent.Length == 0)
                {
                    break;
                }
                parent = RequiredId(parent, $"{source}:{current}:parent");
                if (!stateData.ContainsKey(parent))
                {
                    throw new DecisionGraphException(
                        $"State '{current}' references unknown parent '{parent}': {source}"
                    );
                }
                current = parent;
            }
        }

        foreach ((string name, JsonElement state, int stateId) in states)
        {
            string parent = OptionalString(state, "parent");
            if (parent.Length == 0)
            {
                builder.Link(rootId, "state", stateId, "state");
                continue;
            }
            parent = RequiredId(parent, $"{source}:{name}:parent");
            if (!stateIds.TryGetValue(parent, out int parentId))
            {
                throw new DecisionGraphException(
                    $"State '{name}' references unknown parent '{parent}': {source}"
                );
            }
            builder.Link(parentId, "state", stateId, "state");
        }

        foreach ((string name, JsonElement state, int stateId) in states)
        {
            if (!state.TryGetProperty("transitions", out JsonElement authoredTransitions))
            {
                continue;
            }
            foreach (JsonElement authoredTransition in RequireArray(authoredTransitions, source).EnumerateArray())
            {
                JsonElement transition = RequireObject(authoredTransition, source);
                string target = RequiredString(transition, "target", source);
                if (!stateIds.ContainsKey(target))
                {
                    throw new DecisionGraphException(
                        $"State '{name}' references unknown transition target '{target}': {source}"
                    );
                }
                string trigger = OptionalString(transition, "trigger", "tick");
                if (trigger is not ("tick" or "success" or "failure"))
                {
                    throw new DecisionGraphException(
                        $"State '{name}' has invalid transition trigger '{trigger}': {source}"
                    );
                }
                int compiled = builder.Add("Transition", Values(
                    ("target", target),
                    ("trigger", trigger),
                    ("priority", OptionalInt(transition, "priority"))
                ));
                builder.Link(stateId, "transition", compiled, "transition");
                string condition = OptionalString(transition, "condition");
                if (condition.Length > 0)
                {
                    int guard = CompileCondition(builder, condition, source);
                    builder.Link(guard, "value", compiled, "condition");
                }
            }
        }
        return builder.Build(source);
    }

    public StateProgram State(string json, string source = "state.json")
    {
        DecisionGraph graph = BuildState(json, source);
        return new StateProgram(graph, graph.Id);
    }

    public DecisionGraph BuildPlan(string json, string source = "plan.json")
    {
        using JsonDocument document = ParseDocument(json, source);
        JsonElement root = RequireObject(document.RootElement, source);
        string id = RequiredString(root, "id", source);
        var builder = new GraphBuilder(id);
        int rootId = builder.Add("GoapRoot", Values(("name", id)));
        JsonElement goals = RequiredArray(root, "goals", source);
        JsonElement actions = RequiredArray(root, "actions", source);
        foreach (JsonElement goal in goals.EnumerateArray())
        {
            JsonElement item = RequireObject(goal, source);
            int node = builder.Add("GoapGoal", Values(
                ("name", RequiredString(item, "id", source)),
                ("requires", StringArray(item, "requires", source))
            ));
            builder.Link(rootId, "goal", node, "goal");
        }
        foreach (JsonElement action in actions.EnumerateArray())
        {
            JsonElement item = RequireObject(action, source);
            int node = builder.Add("GoapAction", Values(
                ("name", RequiredString(item, "id", source)),
                ("requires", StringArray(item, "requires", source)),
                ("adds", StringArray(item, "adds", source)),
                ("removes", StringArray(item, "removes", source)),
                ("cost", OptionalNumber(item, "cost", 1.0))
            ));
            builder.Link(rootId, "action", node, "action");
        }
        return builder.Build(source);
    }

    public PlanGraph Plan(string json, string source = "plan.json")
    {
        DecisionGraph graph = BuildPlan(json, source);
        return new PlanGraph(graph, graph.Id);
    }

    private static IReadOnlyDictionary<string, ConditionSpec> ParseConditions(
        string json,
        string source
    )
    {
        using JsonDocument document = ParseDocument(json, source);
        JsonElement root = RequireObject(document.RootElement, source);
        JsonElement conditions = RequiredArray(root, "conditions", source);
        var result = new Dictionary<string, ConditionSpec>(StringComparer.Ordinal);
        foreach (JsonElement condition in conditions.EnumerateArray())
        {
            JsonElement item = RequireObject(condition, source);
            string id = RequiredString(item, "id", source);
            JsonElement groups = RequiredArray(item, "groups", source);
            var parsedGroups = new List<IReadOnlyList<ClauseSpec>>();
            foreach (JsonElement group in groups.EnumerateArray())
            {
                JsonElement clauses = RequiredArray(RequireObject(group, source), "clauses", source);
                var parsedClauses = new List<ClauseSpec>();
                foreach (JsonElement clause in clauses.EnumerateArray())
                {
                    JsonElement value = RequireObject(clause, source);
                    parsedClauses.Add(new ClauseSpec(
                        RequiredString(value, "fact", source),
                        RequiredString(value, "op", source),
                        OptionalNumber(value, "value"),
                        OptionalString(value, "other"),
                        OptionalNumber(value, "scale", 1.0),
                        OptionalNumber(value, "offset")
                    ));
                }
                if (parsedClauses.Count == 0)
                {
                    throw new DecisionGraphException($"Condition '{id}' has an empty group: {source}");
                }
                parsedGroups.Add(parsedClauses);
            }
            if (parsedGroups.Count == 0 || !result.TryAdd(id, new ConditionSpec(parsedGroups)))
            {
                throw new DecisionGraphException($"Invalid or duplicate condition '{id}': {source}");
            }
        }
        return result;
    }

    private static int CompileScore(
        GraphBuilder builder,
        int current,
        JsonElement step,
        string source
    )
    {
        string fact = RequiredString(step, "fact", source);
        int value = builder.Add("FactNumber", Values(("key", fact), ("default", 0.0)));
        string curve = OptionalString(step, "curve", "linear");
        if (curve == "one_minus")
        {
            int node = builder.Add("OneMinus");
            builder.Link(value, "value", node, "value");
            value = node;
        }
        else if (curve == "inverse")
        {
            double divisor = OptionalNumber(step, "divisor", 1.0);
            if (divisor <= 0.0)
            {
                throw new DecisionGraphException($"Inverse score divisor must be positive: {source}");
            }
            int divisorNode = builder.Add("Number", Values(("value", divisor)));
            int scaled = builder.Add("Divide");
            builder.Link(value, "value", scaled, "a");
            builder.Link(divisorNode, "value", scaled, "b");
            int one = builder.Add("Number", Values(("value", 1.0)));
            int denominator = builder.Add("Add");
            builder.Link(one, "value", denominator, "a");
            builder.Link(scaled, "value", denominator, "b");
            int numerator = builder.Add("Number", Values(("value", 1.0)));
            int inverse = builder.Add("Divide");
            builder.Link(numerator, "value", inverse, "a");
            builder.Link(denominator, "value", inverse, "b");
            value = inverse;
        }
        else if (curve != "linear")
        {
            throw new DecisionGraphException($"Unknown utility curve '{curve}': {source}");
        }
        value = ApplyScale(builder, value, OptionalNumber(step, "scale", 1.0), OptionalNumber(step, "offset"));
        string combine = OptionalString(step, "combine", "multiply");
        string nodeType = combine switch
        {
            "add" => "Add",
            "multiply" => "Multiply",
            "min" => "Min",
            "max" => "Max",
            _ => throw new DecisionGraphException($"Unknown utility combine '{combine}': {source}"),
        };
        int combined = builder.Add(nodeType);
        builder.Link(current, "value", combined, "a");
        builder.Link(value, "value", combined, "b");
        return combined;
    }

    private int CompileCondition(GraphBuilder builder, string id, string source)
    {
        if (!_conditions.TryGetValue(id, out ConditionSpec? condition))
        {
            throw new DecisionGraphException($"Unknown condition '{id}': {source}");
        }
        int? result = null;
        foreach (IReadOnlyList<ClauseSpec> group in condition.Groups)
        {
            int? groupResult = null;
            foreach (ClauseSpec clause in group)
            {
                int compiled = CompileClause(builder, clause, source);
                groupResult = groupResult == null
                    ? compiled
                    : CombineBoolean(builder, "And", groupResult.Value, compiled);
            }
            result = result == null
                ? groupResult
                : CombineBoolean(builder, "Or", result.Value, groupResult!.Value);
        }
        return result ?? throw new DecisionGraphException($"Condition '{id}' is empty: {source}");
    }

    private static int CompileClause(GraphBuilder builder, ClauseSpec clause, string source)
    {
        if (clause.Op is "true" or "false")
        {
            int fact = builder.Add("FactBool", Values(("key", clause.Fact), ("default", false)));
            return clause.Op == "true" ? fact : Negate(builder, fact);
        }
        if (clause.Op is "exists" or "missing")
        {
            int exists = builder.Add("FactExists", Values(("key", clause.Fact)));
            return clause.Op == "exists" ? exists : Negate(builder, exists);
        }
        if (clause.Op is not ("eq" or "ne" or "lt" or "lte" or "gt" or "gte"))
        {
            throw new DecisionGraphException($"Unknown condition operator '{clause.Op}': {source}");
        }
        int left = builder.Add("FactNumber", Values(("key", clause.Fact), ("default", 0.0)));
        int right = clause.Other.Length == 0
            ? builder.Add("Number", Values(("value", clause.Value)))
            : builder.Add("FactNumber", Values(("key", clause.Other), ("default", 0.0)));
        if (clause.Other.Length > 0)
        {
            right = ApplyScale(builder, right, clause.Scale, clause.Offset);
        }
        int compare = builder.Add("Compare", Values(("op", clause.Op)));
        builder.Link(left, "value", compare, "left");
        builder.Link(right, "value", compare, "right");
        return compare;
    }

    private static int ApplyScale(GraphBuilder builder, int value, double scale, double offset)
    {
        int result = value;
        if (scale != 1.0)
        {
            int factor = builder.Add("Number", Values(("value", scale)));
            int multiply = builder.Add("Multiply");
            builder.Link(result, "value", multiply, "a");
            builder.Link(factor, "value", multiply, "b");
            result = multiply;
        }
        if (offset != 0.0)
        {
            int delta = builder.Add("Number", Values(("value", offset)));
            int add = builder.Add("Add");
            builder.Link(result, "value", add, "a");
            builder.Link(delta, "value", add, "b");
            result = add;
        }
        return result;
    }

    private int ImportTree(GraphBuilder builder, string id, string source)
    {
        if (!_trees.TryGetValue(id, out DecisionGraph? tree))
        {
            throw new DecisionGraphException($"Unknown behavior tree '{id}': {source}");
        }
        DecisionGraphNode root = tree.NodesOfType("TreeRoot").Single();
        DecisionGraphEdge rootEdge = tree.Edges.Single(edge => edge.FromNode == root.Id);
        var mapped = new Dictionary<int, int>();
        foreach (DecisionGraphNode node in tree.Nodes.Where(node => node.Id != root.Id))
        {
            mapped[node.Id] = CompileTreeNode(builder, node, source);
        }
        foreach (DecisionGraphEdge edge in tree.Edges.Where(edge => edge.FromNode != root.Id))
        {
            builder.Link(mapped[edge.FromNode], "child", mapped[edge.ToNode], "child");
        }
        return mapped[rootEdge.ToNode];
    }

    private int CompileTreeNode(GraphBuilder builder, DecisionGraphNode node, string source)
    {
        string task = node.Type switch
        {
            "MoveTo" => "move_to",
            "AimAt" => "aim_at",
            "Attack" or "AttackTarget" => "attack",
            "Interact" => "interact",
            "IdleScan" => "idle_scan",
            "RollAway" => "roll_away",
            "PolicyTask" => "policy",
            "FaceTarget" => "face_target",
            "MoveTarget" => "move_target",
            "SetCooldown" => "set_cooldown",
            "SetActionTicks" => "set_action_ticks",
            "KillSelf" => "kill_self",
            _ => "",
        };
        if (task.Length > 0)
        {
            var values = node.Values.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal
            );
            values["name"] = task;
            return builder.Add("Task", values);
        }
        if (node.Type == "Guard")
        {
            int condition = CompileCondition(builder, RequiredValue(node, "condition", source), source);
            int guard = builder.Add("Condition", CopyValues(node.Values, "condition"));
            builder.Link(condition, "value", guard, "condition");
            return guard;
        }
        if (node.Type == "CheckBool")
        {
            int fact = builder.Add("FactBool", Values(
                ("key", RequiredValue(node, "key", source)),
                ("default", false)
            ));
            int value = DecisionGraph.Boolean(node.Values, "expected", true)
                ? fact
                : Negate(builder, fact);
            int guard = builder.Add("Condition", CopyValues(node.Values, "key", "expected"));
            builder.Link(value, "value", guard, "condition");
            return guard;
        }
        if (node.Type == "CheckNumber")
        {
            var clause = new ClauseSpec(
                RequiredValue(node, "left", source),
                DecisionGraph.Text(node.Values, "op", "eq"),
                DecisionGraph.Number(node.Values, "value"),
                DecisionGraph.Text(node.Values, "right"),
                DecisionGraph.Number(node.Values, "scale", 1.0),
                DecisionGraph.Number(node.Values, "offset")
            );
            int value = CompileClause(builder, clause, source);
            int guard = builder.Add("Condition", CopyValues(
                node.Values,
                "left",
                "op",
                "value",
                "right",
                "scale",
                "offset"
            ));
            builder.Link(value, "value", guard, "condition");
            return guard;
        }
        if (node.Type == "ChanceGuard")
        {
            int chance = builder.Add("Chance", Values(
                ("probability", DecisionGraph.Number(node.Values, "probability"))
            ));
            int guard = builder.Add("Condition", CopyValues(node.Values, "probability"));
            builder.Link(chance, "value", guard, "condition");
            return guard;
        }
        if (node.Type is not (
            "Sequence" or "Selector" or "ReactiveSequence" or "ReactiveSelector"
            or "Invert" or "Task" or "Wait" or "Succeed" or "Fail"
        ))
        {
            throw new DecisionGraphException($"Tree node '{node.Type}' is unsupported: {source}");
        }
        return builder.Add(node.Type, node.Values);
    }

    private static void ValidateTree(DecisionGraph tree)
    {
        DecisionGraphNode[] roots = tree.NodesOfType("TreeRoot").ToArray();
        if (roots.Length != 1)
        {
            throw new DecisionGraphException($"Tree '{tree.Id}' needs exactly one TreeRoot.");
        }
        if (tree.Edges.Any(edge => edge.FromPort != "child" || edge.ToPort != "child"))
        {
            throw new DecisionGraphException($"Tree '{tree.Id}' only allows child edges.");
        }
        if (tree.Edges.Count(edge => edge.FromNode == roots[0].Id) != 1
            || tree.Edges.Any(edge => edge.ToNode == roots[0].Id))
        {
            throw new DecisionGraphException($"Tree '{tree.Id}' root needs exactly one child.");
        }
        foreach (DecisionGraphNode node in tree.Nodes.Where(node => node.Id != roots[0].Id))
        {
            if (tree.Edges.Count(edge => edge.ToNode == node.Id) != 1)
            {
                throw new DecisionGraphException(
                    $"Tree '{tree.Id}' node {node.Id} needs exactly one parent."
                );
            }
        }
        var reached = new HashSet<int> { roots[0].Id };
        var pending = new Stack<int>();
        pending.Push(roots[0].Id);
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            foreach (DecisionGraphEdge edge in tree.Edges.Where(edge => edge.FromNode == current))
            {
                if (reached.Add(edge.ToNode))
                {
                    pending.Push(edge.ToNode);
                }
            }
        }
        if (reached.Count != tree.Nodes.Count)
        {
            throw new DecisionGraphException(
                $"Tree '{tree.Id}' contains nodes disconnected from its root."
            );
        }
    }

    private static int Negate(GraphBuilder builder, int value)
    {
        int result = builder.Add("Not");
        builder.Link(value, "value", result, "value");
        return result;
    }

    private static int CombineBoolean(GraphBuilder builder, string type, int left, int right)
    {
        int result = builder.Add(type);
        builder.Link(left, "value", result, "a");
        builder.Link(right, "value", result, "b");
        return result;
    }

    private static IReadOnlyDictionary<string, object?> CopyValues(
        IReadOnlyDictionary<string, object?> source,
        params string[] excluded
    )
    {
        var skip = excluded.ToHashSet(StringComparer.Ordinal);
        return source.Where(pair => !skip.Contains(pair.Key)).ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal
        );
    }

    private static IReadOnlyDictionary<string, object?> Values(
        params (string Name, object? Value)[] values
    )
    {
        return values.Where(pair => pair.Value switch
        {
            null => false,
            string text => text.Length > 0,
            _ => true,
        }).ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
    }

    private static string RequiredValue(DecisionGraphNode node, string name, string source)
    {
        string value = DecisionGraph.Text(node.Values, name);
        return value.Length > 0
            ? value
            : throw new DecisionGraphException(
                $"Node {node.Id} ({node.Type}) requires '{name}': {source}"
            );
    }

    private static JsonDocument ParseDocument(string json, string source)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
        {
            throw new DecisionGraphException($"Decision asset is empty or too large: {source}");
        }
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        }
        catch (JsonException exception)
        {
            throw new DecisionGraphException(
                $"Invalid decision asset JSON '{source}': {exception.Message}",
                exception
            );
        }
    }

    private static JsonElement RequireObject(JsonElement value, string source)
    {
        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw new DecisionGraphException($"Decision asset value must be an object: {source}");
    }

    private static JsonElement RequiredArray(JsonElement owner, string name, string source)
    {
        return owner.TryGetProperty(name, out JsonElement value)
            ? RequireArray(value, source)
            : throw new DecisionGraphException($"Decision asset requires array '{name}': {source}");
    }

    private static JsonElement RequireArray(JsonElement value, string source)
    {
        return value.ValueKind == JsonValueKind.Array
            ? value
            : throw new DecisionGraphException($"Decision asset value must be an array: {source}");
    }

    private static string RequiredString(JsonElement owner, string name, string source)
    {
        string value = OptionalString(owner, name);
        return value.Length > 0
            ? RequiredId(value, $"{source}:{name}")
            : throw new DecisionGraphException($"Decision asset requires string '{name}': {source}");
    }

    private static string RequiredId(string value, string source)
    {
        string result = value.Trim();
        if (result.Length == 0 || result.Length > 128)
        {
            throw new DecisionGraphException($"Decision asset id is invalid: {source}");
        }
        return result;
    }

    private static string OptionalString(JsonElement owner, string name, string fallback = "")
    {
        return owner.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? fallback
            : fallback;
    }

    private static double OptionalNumber(JsonElement owner, string name, double fallback = 0.0)
    {
        if (!owner.TryGetProperty(name, out JsonElement value)
            || !value.TryGetDouble(out double result))
        {
            return fallback;
        }
        if (!double.IsFinite(result))
        {
            throw new DecisionGraphException($"Decision asset number '{name}' must be finite.");
        }
        return result;
    }

    private static int OptionalInt(JsonElement owner, string name, int fallback = 0)
    {
        return owner.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : fallback;
    }

    private static IReadOnlyList<string> StringArray(
        JsonElement owner,
        string name,
        string source
    )
    {
        JsonElement values = RequiredArray(owner, name, source);
        return values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? RequiredId(value.GetString() ?? "", $"{source}:{name}")
                : throw new DecisionGraphException($"'{name}' values must be strings: {source}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record ClauseSpec(
        string Fact,
        string Op,
        double Value,
        string Other,
        double Scale,
        double Offset
    );

    private sealed record ConditionSpec(IReadOnlyList<IReadOnlyList<ClauseSpec>> Groups);

    private sealed class GraphBuilder
    {
        private readonly string _id;
        private readonly List<NodeData> _nodes = [];
        private readonly List<EdgeData> _edges = [];
        private int _nextNode = 1;
        private int _nextEdge = 1;

        public GraphBuilder(string id)
        {
            _id = id;
        }

        public int Add(
            string type,
            IReadOnlyDictionary<string, object?>? values = null
        )
        {
            int id = _nextNode++;
            _nodes.Add(new NodeData(id, type, values ?? new Dictionary<string, object?>()));
            return id;
        }

        public void Link(int from, string fromPort, int to, string toPort)
        {
            _edges.Add(new EdgeData(
                $"e_{_nextEdge++}",
                new EndpointData(from, fromPort),
                new EndpointData(to, toPort)
            ));
        }

        public DecisionGraph Build(string source)
        {
            string json = JsonSerializer.Serialize(
                new GraphData(_id, _nodes, _edges),
                SerializerOptions
            );
            return DecisionGraph.Parse(json, source);
        }
    }

    private sealed record GraphData(
        string Id,
        IReadOnlyList<NodeData> Nodes,
        IReadOnlyList<EdgeData> Edges
    );

    private sealed record NodeData(
        int Id,
        string Type,
        IReadOnlyDictionary<string, object?> Values
    );

    private sealed record EdgeData(string Id, EndpointData From, EndpointData To);
    private sealed record EndpointData(int Node, string Port);
}
