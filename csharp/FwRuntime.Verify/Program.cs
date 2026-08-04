using Fw.Rt.AI.Behavior;
using Fw.Rt.AI.Core;
using Fw.Rt.AI.Environment;
using Fw.Rt.AI.Evaluation;
using Fw.Rt.AI.Model;
using Fw.Rt.AI.Nav;
using Fw.Rt.AI.Plan;
using Fw.Rt.AI.Policy;
using Fw.Rt.AI.Search;
using Fw.Rt.AI.Training;
using Fw.Rt.AI.Utility;
using Fw.Rt.Events;
using Fw.Rt.Logging;
using Fw.Rt.Randomness;
using Fw.Rt.State;
using Fw.Rt.Systems;

VerifySystemRuntime();
VerifyEventBus();
VerifyStateMachine();
VerifyDeterministicRandom();
VerifyLogBuffer();
VerifyDecisionCore();
VerifyUtility();
VerifyBehavior();
VerifyPlan();
VerifyNavigation();
VerifyPolicy();
VerifyGameEnvironment();
VerifyBeamSearch();
VerifyPuctSearch();
VerifyTrainingAndEvaluation();

Console.WriteLine("Verified FwRuntime systems, events, state, random, logging, and AI modules.");
return;

static void VerifySystemRuntime()
{
    var parentTrace = new List<string>();
    var parent = new SystemRuntime();
    var rootContext = new TestContext("root", parentTrace);
    parent.Add("root", new TestSystem(), rootContext, "app");
    parent.InitAll();

    var trace = new List<string>();
    var child = new SystemRuntime(parent);
    child.SetPhaseOrder(["input", "present"]);
    child.AddWithDependencies(
        "present",
        new TestSystem(),
        new TestContext("present", trace),
        "present",
        ["input", "root"]
    );
    child.Add("input", new TestSystem(), new TestContext("input", trace), "input");
    child.InitAll();
    child.Tick(0.25f);

    Equal("init:input,init:present,tick:input,tick:present", string.Join(',', trace), "system order");
    True(child.GetContext<TestContext>("root") == rootContext, "parent context lookup");
    var snapshots = child.GetSnapshots();
    True(snapshots.Any(item => item.Scope.StartsWith("parent/", StringComparison.Ordinal)), "parent snapshot");

    Throws<InvalidOperationException>(
        () => child.Remove("input"),
        "dependency-safe removal"
    );
    child.Remove("input", SystemRemoveMode.Cascade);
    Equal(
        "init:input,init:present,tick:input,tick:present,shutdown:present,shutdown:input",
        string.Join(',', trace),
        "cascade shutdown order"
    );

    var rollbackTrace = new List<string>();
    var rollback = new SystemRuntime();
    rollback.Add("good", new TestSystem(), new TestContext("good", rollbackTrace));
    rollback.Add("bad", new TestSystem(failInit: true), new TestContext("bad", rollbackTrace));
    Throws<AggregateException>(() => rollback.InitAll(), "initialization failure");
    Equal(
        "init:good,init:bad,shutdown:bad,shutdown:good",
        string.Join(',', rollbackTrace),
        "initialization rollback"
    );

    var inactiveParent = new SystemRuntime();
    inactiveParent.Add("inactive", new TestSystem(), new TestContext("inactive", []));
    var dependentChild = new SystemRuntime(inactiveParent);
    dependentChild.AddWithDependencies(
        "dependent",
        new TestSystem(),
        new TestContext("dependent", []),
        "",
        dependencies: ["inactive"]
    );
    Throws<AggregateException>(
        () => dependentChild.InitAll(),
        "uninitialized parent dependency"
    );

    parent.ShutdownAll();
    Equal("init:root,shutdown:root", string.Join(',', parentTrace), "parent shutdown");
}

static void VerifyEventBus()
{
    var bus = new EventBus<string>(StringComparer.Ordinal);
    var trace = new List<string>();
    using var low = bus.Subscribe<int>("value", value => trace.Add($"low:{value}"), priority: 0);
    using var high = bus.Subscribe<int>("value", value => trace.Add($"high:{value}"), once: true, priority: 10);
    Equal(2, bus.Publish("value", 7), "first publish count");
    Equal(1, bus.Publish("value", 8), "once publish count");
    Equal("high:7,low:7,low:8", string.Join(',', trace), "event priority and once");
    low.Dispose();
    Equal(0, bus.ListenerCount("value"), "event disposal");

    var duplicateBus = new EventBus<string>();
    Action<int> duplicateCallback = _ => { };
    using var firstHandle = duplicateBus.Subscribe("duplicate", duplicateCallback);
    using var secondHandle = duplicateBus.Subscribe("duplicate", duplicateCallback);
    Equal(firstHandle.Id, secondHandle.Id, "duplicate event subscription id");
    True(firstHandle.IsActive && secondHandle.IsActive, "duplicate event subscription activity");
    firstHandle.Dispose();
    True(!secondHandle.IsActive, "duplicate event handle observes shared removal");

    var typedBus = new EventBus<string>();
    var typedTrace = new List<string>();
    using var typed = typedBus.Subscribe<int>("typed", value => typedTrace.Add(value.ToString()));
    Throws<InvalidOperationException>(
        () => typedBus.Subscribe<string>("typed", _ => { }),
        "mixed event subscription payload"
    );
    Throws<InvalidOperationException>(
        () => typedBus.Publish("typed", "wrong"),
        "event payload preflight"
    );
    Equal(0, typedTrace.Count, "event payload mismatch invokes no listeners");
}

static void VerifyStateMachine()
{
    var trace = new List<string>();
    var machine = new StateMachine<string, List<string>>(trace, StringComparer.Ordinal);
    machine.RegisterState(
        "idle",
        (ctx, payload) => ctx.Add($"enter:idle:{payload}"),
        (ctx, _) => ctx.Add("tick:idle"),
        ctx => ctx.Add("exit:idle")
    );
    machine.RegisterState(
        "run",
        (ctx, payload) => ctx.Add($"enter:run:{payload}"),
        null,
        ctx => ctx.Add("exit:run")
    );
    machine.RegisterTransition(
        "idle",
        "run",
        (ctx, from, to, _) => ctx.Add($"transition:{from}:{to}"),
        (_, _, _, payload) => Equals(payload, "go")
    );

    True(machine.Start("idle", "start"), "state start");
    machine.Tick(0.1f);
    True(!machine.TryTransition("run", "blocked"), "state guard");
    True(machine.TryTransition("run", "go"), "state transition");
    machine.Stop();
    Equal(
        "enter:idle:start,tick:idle,exit:idle,transition:idle:run,enter:run:go,exit:run",
        string.Join(',', trace),
        "state lifecycle"
    );

    var comparerTrace = new List<string>();
    var comparerMachine = new StateMachine<string, List<string>>(
        comparerTrace,
        StringComparer.OrdinalIgnoreCase
    );
    comparerMachine.RegisterState("IDLE");
    comparerMachine.RegisterState("RUN");
    comparerMachine.RegisterTransition(
        "IDLE",
        "RUN",
        (ctx, _, _, _) => ctx.Add("case-insensitive-transition")
    );
    True(comparerMachine.Start("idle"), "state comparer start");
    True(comparerMachine.TryTransition("run"), "state comparer transition");
    Equal(
        "case-insensitive-transition",
        string.Join(',', comparerTrace),
        "state transition comparer"
    );
    comparerMachine.Stop();

    StateMachine<string, List<string>>? stopMachine = null;
    stopMachine = new StateMachine<string, List<string>>([]);
    stopMachine.RegisterState("idle", onExit: _ => stopMachine!.TryTransition("run"));
    stopMachine.RegisterState("run");
    True(stopMachine.Start("idle"), "stop transition fixture start");
    stopMachine.Stop();
    True(stopMachine.Start("idle"), "stop transition fixture restart");
    Equal("idle", stopMachine.Current, "stop clears callback-queued transition");

    var startFailure = new StateMachine<string, object>(new object());
    startFailure.RegisterState("broken", onEnter: (_, _) => throw new InvalidOperationException("enter"));
    Throws<InvalidOperationException>(() => startFailure.Start("broken"), "state enter failure");
    True(!startFailure.IsActive && startFailure.Current == null, "state enter failure resets machine");
    startFailure.RegisterState("broken");
    True(startFailure.Start("broken"), "state machine restarts after enter failure");
    startFailure.Stop();

    var startedEventFailure = new StateMachine<string, object>(new object());
    startedEventFailure.RegisterState("idle");
    Action<string, object?> startedFailureHandler = (_, _) =>
        throw new InvalidOperationException("started event");
    startedEventFailure.Started += startedFailureHandler;
    Throws<InvalidOperationException>(
        () => startedEventFailure.Start("idle"),
        "state started event failure"
    );
    True(
        !startedEventFailure.IsActive && startedEventFailure.Current == null,
        "state started event failure resets machine"
    );
    startedEventFailure.Started -= startedFailureHandler;
    True(startedEventFailure.Start("idle"), "state machine restarts after started event failure");
    startedEventFailure.Stop();

    var transitionFailure = new StateMachine<string, object>(new object());
    transitionFailure.RegisterState("idle");
    transitionFailure.RegisterState("broken", onEnter: (_, _) => throw new InvalidOperationException("transition"));
    True(transitionFailure.Start("idle"), "transition failure fixture start");
    Throws<InvalidOperationException>(
        () => transitionFailure.TryTransition("broken"),
        "state transition callback failure"
    );
    True(
        !transitionFailure.IsActive && transitionFailure.Current == null,
        "state transition failure resets machine"
    );

    var transitionedEventFailure = new StateMachine<string, object>(new object());
    transitionedEventFailure.RegisterState("idle");
    transitionedEventFailure.RegisterState("run");
    True(transitionedEventFailure.Start("idle"), "transitioned event failure fixture start");
    transitionedEventFailure.Transitioned += (_, _, _) =>
        throw new InvalidOperationException("transitioned event");
    Throws<InvalidOperationException>(
        () => transitionedEventFailure.TryTransition("run"),
        "state transitioned event failure"
    );
    True(
        !transitionedEventFailure.IsActive && transitionedEventFailure.Current == null,
        "state transitioned event failure resets machine"
    );

    var stopFailure = new StateMachine<string, object>(new object());
    stopFailure.RegisterState("broken", onExit: _ => throw new InvalidOperationException("exit"));
    True(stopFailure.Start("broken"), "stop failure fixture start");
    Throws<InvalidOperationException>(stopFailure.Stop, "state stop callback failure");
    True(!stopFailure.IsActive && stopFailure.Current == null, "state stop failure resets machine");

    var clearFailure = new StateMachine<string, object>(new object());
    clearFailure.RegisterState("broken", onExit: _ => throw new InvalidOperationException("clear"));
    True(clearFailure.Start("broken"), "clear failure fixture start");
    Throws<InvalidOperationException>(clearFailure.Clear, "state clear callback failure");
    True(!clearFailure.IsActive && clearFailure.Current == null, "state clear failure resets machine");
    True(!clearFailure.Start("broken"), "state clear failure still removes registrations");
}

static void VerifyDeterministicRandom()
{
    var first = new DeterministicRandomStream(12345);
    var sequence = Enumerable.Range(0, 8).Select(_ => first.Next(1000)).ToArray();
    var restored = new DeterministicRandomStream(12345);
    var replay = Enumerable.Range(0, 8).Select(_ => restored.Next(1000)).ToArray();
    True(sequence.SequenceEqual(replay), "deterministic sequence");

    var state = first.State;
    var resumed = new DeterministicRandomStream(state.Seed, state.Step);
    Equal(first.Next(1000), resumed.Next(1000), "random state resume");

    var forkSource = new DeterministicRandomStream(321, 4);
    Equal(
        LegacyForkSeed(forkSource.Seed, forkSource.Step, "combat"),
        forkSource.Fork("combat").Seed,
        "legacy channel fork compatibility"
    );

    var left = Enumerable.Range(0, 12).ToList();
    var right = Enumerable.Range(0, 12).ToList();
    RandomPicker.Shuffle(left, new DeterministicRandomStream(77));
    RandomPicker.Shuffle(right, new DeterministicRandomStream(77));
    True(left.SequenceEqual(right), "deterministic shuffle");

    const long largeBound = (long)int.MaxValue + 4_294_967_296L;
    var largeLeft = new DeterministicRandomStream(991);
    var largeRight = new DeterministicRandomStream(991);
    for (var index = 0; index < 32; index++)
    {
        long leftValue = largeLeft.NextInt64(largeBound);
        long rightValue = largeRight.NextInt64(largeBound);
        True(leftValue >= 0 && leftValue < largeBound, "64-bit random bound");
        Equal(leftValue, rightValue, "64-bit random determinism");
    }
    Throws<InvalidOperationException>(
        () => new DeterministicRandomStream(1, long.MaxValue).Next(2),
        "random step exhaustion"
    );
}

static int LegacyForkSeed(int seed, long step, string channel)
{
    const ulong goldenGamma = 0x9E3779B97F4A7C15UL;
    const ulong offset = 14695981039346656037UL;
    const ulong prime = 1099511628211UL;
    var hash = offset;
    foreach (var character in channel)
    {
        hash ^= character;
        hash *= prime;
    }
    var value = unchecked((uint)seed) ^ hash ^ unchecked((ulong)step * goldenGamma);
    value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
    value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
    value ^= value >> 31;
    var normalized = (int)(value & 0x7FFFFFFF);
    return normalized == 0 ? 1 : normalized;
}

static void VerifyLogBuffer()
{
    var log = new LogBuffer { Capacity = 2, MinimumLevel = LogLevel.Info };
    True(log.Add(LogLevel.Debug, "test", "hidden") == null, "log threshold");
    log.Add(LogLevel.Info, "test", "one");
    log.Add(LogLevel.Warning, "test", "two");
    log.Add(LogLevel.Error, "test", "three");
    var entries = log.Recent();
    Equal(2, entries.Count, "log capacity");
    Equal("two,three", string.Join(',', entries.Select(item => item.Message)), "log order");

    var data = new Dictionary<string, object?> { ["value"] = 1 };
    log.Add(LogLevel.Error, "test", "snapshot", data);
    data["value"] = 2;
    Equal(1, log.Recent(1)[0].Data["value"], "log data snapshot");
    log.Capacity = 0;
    Equal(0, log.Recent().Count, "log capacity trims immediately");
    Throws<ArgumentException>(() => log.ForwardTo = log, "log self forwarding");
    var forwarded = new LogBuffer();
    log.ForwardTo = forwarded;
    Throws<ArgumentException>(() => forwarded.ForwardTo = log, "log forwarding cycle");
    log.ForwardTo = null;

    log.Capacity = 128;
    Parallel.For(0, 512, index => log.Add(LogLevel.Info, "parallel", index.ToString()));
    Equal(128, log.Recent().Count, "concurrent log capacity");
}

static void VerifyDecisionCore()
{
    var budget = new DecisionBudget(2);
    True(budget.TrySpend(), "decision budget first unit");
    True(budget.TrySpend(), "decision budget second unit");
    True(!budget.TrySpend(), "decision budget exhaustion");
    budget.Reset();
    Equal(2, budget.Remaining, "decision budget reset");

    var trace = new DecisionTrace(1);
    var data = new Dictionary<string, object?> { ["value"] = 1 };
    trace.Write(1, "test", "first", data);
    data["value"] = 2;
    trace.Write(2, "test", "second");
    var entries = trace.Recent();
    Equal(1, entries.Count, "decision trace capacity");
    Equal("second", entries[0].Event, "decision trace order");
}

static void VerifyUtility()
{
    var selector = new UtilitySelector<int, string>();
    selector.Add(new UtilityOption<int, string>("low", "wait", value => value));
    selector.Add(new UtilityOption<int, string>("high", "attack", value => value + 2));
    var scope = Scope(10);
    var result = selector.Select(3, scope);
    True(result.HasChoice, "utility has choice");
    Equal("high", result.Id, "utility selection");

    scope = Scope(10);
    result = selector.Select(3, scope, "low", 3.0);
    Equal("low", result.Id, "utility switching threshold");
}

static void VerifyBehavior()
{
    var runs = 0;
    var tree = new BehaviorTree<int>(
        new BehaviorSequence<int>(
            new BehaviorCondition<int>(value => value > 0),
            new BehaviorAction<int>(_ => ++runs == 1 ? BehaviorStatus.Running : BehaviorStatus.Success)
        )
    );
    var session = new BehaviorSession();
    Equal(BehaviorStatus.Running, tree.Tick(1, session, Scope(10)), "behavior running");
    Equal(BehaviorStatus.Success, tree.Tick(1, session, Scope(10)), "behavior resumes");
    Equal(2, runs, "behavior session cursor");

    var suspended = new BehaviorTree<int>(new BehaviorAction<int>(_ => BehaviorStatus.Success));
    Equal(BehaviorStatus.Suspended, suspended.Tick(1, new BehaviorSession(), Scope(0)), "behavior budget");
}

static void VerifyPlan()
{
    var planner = new GoalPlanner<string>();
    planner.Add(new PlanAction<string>("find_key", [], ["key"], []));
    planner.Add(new PlanAction<string>("open_door", ["key"], ["open"], []));
    var search = planner.Begin([], new PlanGoal<string>("escape", ["open"]));
    PlanResult<string> result;
    do
    {
        result = search.Step(Scope(1));
    }
    while (result.Status == PlanStatus.Searching);
    Equal(PlanStatus.Complete, result.Status, "plan completes");
    Equal("find_key,open_door", string.Join(',', result.Actions.Select(item => item.Id)), "plan order");
}

static void VerifyNavigation()
{
    var graph = new LineGraph(4);
    var search = new PathSearch<int>(graph, 0, 3);
    PathResult<int> result;
    do
    {
        result = search.Step(Scope(1));
    }
    while (result.Status == PathStatus.Searching);
    Equal(PathStatus.Complete, result.Status, "path completes");
    Equal("0,1,2,3", string.Join(',', result.Nodes), "path nodes");

    var cache = new PathCache<string, int>(1);
    cache.Set("path", result.Nodes);
    True(cache.TryGet("path", out var cached), "path cache hit");
    Equal(4, cached.Count, "path cache content");

    var flow = new FlowField<int>(graph, 3);
    while (!flow.Step(Scope(1))) { }
    True(flow.TryNext(0, out var next), "flow next");
    Equal(1, next, "flow direction");

    var steer = Steering.Arrive(
        new System.Numerics.Vector2(0, 0),
        new System.Numerics.Vector2(1, 0),
        2.0f,
        2.0f
    );
    True(steer.Linear.X > 0.0f && steer.Linear.X <= 2.0f, "steering arrive");
}

static void VerifyPolicy()
{
    var primary = new LocalPolicy<int, int>(_ => throw new InvalidOperationException("offline"));
    var fallback = new LocalPolicy<int, int>(value => value + 1);
    var policy = new FallbackPolicy<int, int>(primary, fallback, value => value > 0);
    var result = policy.EvaluateAsync(4).AsTask().GetAwaiter().GetResult();
    Equal(PolicyStatus.Success, result.Status, "policy fallback status");
    Equal(5, result.Output, "policy fallback output");
}

static void VerifyGameEnvironment()
{
    var spec = new GameEnvironmentSpec("toy", 1);
    Equal("toy", spec.Id, "game environment id");
    Equal(GamePayoffMode.SinglePlayer, spec.PayoffMode, "game payoff mode");
    True(!GameEpisodeResult.Running(1).IsFinished, "running episode state");
    Throws<ArgumentException>(
        () => new GameEnvironmentSpec("bad", 2),
        "single-player count validation"
    );
    Throws<ArgumentOutOfRangeException>(
        () => new ChanceOutcome<string>("bad", 0.0),
        "chance probability validation"
    );
}

static void VerifyBeamSearch()
{
    var environment = new ToyGameEnvironment();
    var root = environment.Reset(1);
    var search = new BeamSearch<ToyGameState, int, string>(
        environment,
        root,
        (state, _) => state.Position / 3.0,
        new BeamSearchOptions(width: 2, maxDepth: 4)
    );

    BeamSearchResult<string> result;
    do
    {
        result = search.Step(Scope(1));
    }
    while (!result.IsFinished);

    Equal(BeamSearchStatus.Success, result.Status, "beam search succeeds");
    Equal("advance,advance,advance", string.Join(',', result.Actions), "beam search path");
    Equal(0, root.Position, "beam search preserves root state");
    True(result.ExpandedNodes >= 3, "beam search incremental expansion");
    Throws<NotSupportedException>(
        () => new BeamSearch<ToyGameState, int, string>(
            new ToyGameEnvironment(new GameEnvironmentSpec(
                "toy_adversarial",
                1,
                payoffMode: GamePayoffMode.ZeroSum
            )),
            new ToyGameState(),
            (state, _) => state.Position
        ),
        "beam search rejects adversarial payoff mode"
    );
    var duplicateSearch = new BeamSearch<ToyGameState, int, string>(
        new ToyGameEnvironment(duplicateActions: true),
        new ToyGameState(),
        (state, _) => state.Position
    );
    Throws<InvalidOperationException>(
        () => duplicateSearch.Step(Scope(1)),
        "beam search rejects duplicate legal actions"
    );
}

static void VerifyPuctSearch()
{
    var environment = new ToyGameEnvironment();
    var root = environment.Reset(1);
    var model = new DelegatePolicyValueModel<int, string>((observation, _, actions) =>
        new PolicyValuePrediction<string>(
            actions.Select(action => new ActionPrior<string>(action, 1.0)),
            [observation / 3.0]
        )
    );
    var search = new PuctSearch<ToyGameState, int, string>(
        environment,
        root,
        model,
        new PuctSearchOptions(simulationLimit: 96, maxDepth: 8)
    );
    var paused = search.Step(Scope(1));
    Equal(PuctSearchStatus.Searching, paused.Status, "puct search pauses on budget");
    Equal(1, paused.Simulations, "puct search spends one simulation");
    var result = search.Step(Scope(95));
    Equal(PuctSearchStatus.Complete, result.Status, "puct search completes");
    True(result.HasAction, "puct search has action");
    Equal(ToyGameEnvironment.Advance, result.Action, "puct search selects winning action");
    Equal(0, root.Position, "puct search preserves root state");
    True(
        result.Actions.Single(item => item.Action == ToyGameEnvironment.Advance).Visits
            > result.Actions.Single(item => item.Action == ToyGameEnvironment.Lose).Visits,
        "puct winning action receives more visits"
    );

    var stochastic = new ChanceGameEnvironment();
    var chanceModel = new UniformPolicyValueModel<int, string>(1);
    var first = new PuctSearch<ChanceGameState, int, string>(
        stochastic,
        stochastic.Reset(4),
        chanceModel,
        new PuctSearchOptions(simulationLimit: 64, maxDepth: 6)
    ).Step(Scope(64));
    var second = new PuctSearch<ChanceGameState, int, string>(
        stochastic,
        stochastic.Reset(4),
        chanceModel,
        new PuctSearchOptions(simulationLimit: 64, maxDepth: 6)
    ).Step(Scope(64));
    Near(first.RootMeanValues[0], second.RootMeanValues[0], 1e-12, "chance search replay");
    Throws<NotSupportedException>(
        () => new PuctSearch<ToyGameState, int, string>(
            new ToyGameEnvironment(new GameEnvironmentSpec(
                "toy_hidden",
                1,
                information: GameInformation.Imperfect
            )),
            new ToyGameState(),
            model
        ),
        "puct rejects hidden-information state"
    );
}

static void VerifyTrainingAndEvaluation()
{
    var sample = new PolicyValueSample<int, string>(
        2,
        0,
        ["left", "right"],
        "right",
        [new PolicyTarget<string>("left", 0.25), new PolicyTarget<string>("right", 0.75)],
        [0.0],
        [1.0]
    );
    var terminal = new GameEpisodeResult(GameResultStatus.Terminated, [1.0], "win");
    var trajectory = new TrainingTrajectory<int, string>("toy", "episode-1", 7, [sample], terminal);
    Equal(1, trajectory.Samples.Count, "training trajectory sample");

    var buffer = new ReplayBuffer<int>(2);
    buffer.AddRange([1, 2, 3]);
    Equal("2,3", string.Join(',', buffer.Snapshot()), "replay buffer eviction");
    buffer.Add(4);
    Equal("3,4", string.Join(',', buffer.Snapshot()), "replay buffer ring wrap");
    var first = buffer.Sample(2, new DeterministicRandomStream(9));
    var second = buffer.Sample(2, new DeterministicRandomStream(9));
    Equal(string.Join(',', first), string.Join(',', second), "replay buffer deterministic sample");

    var evaluation = new EvaluationAccumulator();
    evaluation.Add(terminal, 3);
    evaluation.Add(new GameEpisodeResult(GameResultStatus.Terminated, [-1.0], "loss"), 5);
    evaluation.Add(new GameEpisodeResult(GameResultStatus.Truncated, [0.0], reason: "limit"), 7);
    var summary = evaluation.Snapshot();
    Equal(3, summary.Episodes, "evaluation episodes");
    Equal(1, summary.Successes, "evaluation successes");
    Equal(1, summary.Failures, "evaluation failures");
    Equal(1, summary.Truncated, "evaluation truncation");
    Near(0.5, summary.SuccessRate, 1e-12, "evaluation success rate");
    True(summary.SuccessRateLower < summary.SuccessRate, "evaluation confidence lower bound");
    True(summary.SuccessRateUpper > summary.SuccessRate, "evaluation confidence upper bound");

    var encoder = new DelegatePolicyValueFeatureEncoder<int, string>(
        policyFeatureCount: 2,
        valueFeatureCount: 2,
        (observation, _, action) =>
            [1.0, action == "right" ? observation : -observation],
        (observation, _, _) => [1.0, observation]
    );
    var linear = new LinearPolicyValueModel<int, string>(encoder, playerCount: 1);
    var trainingSamples = new[]
    {
        new PolicyValueSample<int, string>(
            -1,
            0,
            ["left", "right"],
            "left",
            [new PolicyTarget<string>("left", 1.0)],
            [0.0],
            [-1.0]
        ),
        new PolicyValueSample<int, string>(
            1,
            0,
            ["left", "right"],
            "right",
            [new PolicyTarget<string>("right", 1.0)],
            [0.0],
            [1.0]
        ),
    };
    LinearPolicyValueTrainingResult training = LinearPolicyValueTrainer.Train(
        linear,
        trainingSamples,
        new LinearPolicyValueTrainingOptions(
            epochs: 100,
            batchSize: 2,
            learningRate: 0.1,
            l2: 0.0
        )
    );
    Equal(200, training.Samples, "linear trainer sample count");
    Equal(100, training.Updates, "linear trainer update count");
    PolicyValuePrediction<string> positive = linear.Predict(1, 0, ["left", "right"]);
    PolicyValuePrediction<string> negative = linear.Predict(-1, 0, ["left", "right"]);
    True(positive.Priors.Single(item => item.Action == "right").Prior > 0.95, "linear policy positive");
    True(negative.Priors.Single(item => item.Action == "left").Prior > 0.95, "linear policy negative");
    True(positive.Values[0] > 0.9 && negative.Values[0] < -0.9, "linear value fit");

    LinearPolicyValueCheckpoint checkpoint = linear.ExportCheckpoint();
    Equal(linear.ParameterCount, checkpoint.PolicyWeights.Count
        + checkpoint.ValueWeights.Sum(weights => weights.Count), "linear checkpoint parameters");
    var restoredLinear = new LinearPolicyValueModel<int, string>(encoder, checkpoint);
    PolicyValuePrediction<string> restoredPrediction = restoredLinear.Predict(1, 0, ["left", "right"]);
    Near(positive.Priors[1].Prior, restoredPrediction.Priors[1].Prior, 1e-12, "linear checkpoint policy");
    Near(positive.Values[0], restoredPrediction.Values[0], 1e-12, "linear checkpoint value");
    LinearPolicyValueCheckpoint serializedCheckpoint = LinearPolicyValueCheckpoint.FromJson(
        checkpoint.ToJson(writeIndented: true)
    );
    var serializedLinear = new LinearPolicyValueModel<int, string>(encoder, serializedCheckpoint);
    PolicyValuePrediction<string> serializedPrediction = serializedLinear.Predict(1, 0, ["left", "right"]);
    Near(positive.Priors[1].Prior, serializedPrediction.Priors[1].Prior, 1e-12,
        "linear serialized checkpoint policy");
    Near(positive.Values[0], serializedPrediction.Values[0], 1e-12,
        "linear serialized checkpoint value");
}

static DecisionScope Scope(int units)
{
    return new DecisionScope(1, new DecisionBudget(units), new DeterministicRandomStream(7));
}

static void True(bool value, string label)
{
    if (!value)
    {
        throw new InvalidOperationException($"Verification failed: {label}");
    }
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"Verification failed: {label}. Expected '{expected}', got '{actual}'."
        );
    }
}

static void Near(double expected, double actual, double tolerance, string label)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException(
            $"Verification failed: {label}. Expected '{expected}', got '{actual}'."
        );
    }
}

static void Throws<TException>(Action action, string label)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Verification failed: {label} did not throw {typeof(TException).Name}.");
}

sealed class TestContext(string name, List<string> trace)
{
    public string Name { get; } = name;
    public List<string> Trace { get; } = trace;
}

sealed class TestSystem(bool failInit = false) : ISystem<TestContext>
{
    private TestContext? _context;

    public void Init(TestContext context)
    {
        _context = context;
        context.Trace.Add($"init:{context.Name}");
        if (failInit)
        {
            throw new InvalidOperationException("expected init failure");
        }
    }

    public void Tick(float dt)
    {
        _context?.Trace.Add($"tick:{_context.Name}");
    }

    public void Shutdown()
    {
        _context?.Trace.Add($"shutdown:{_context.Name}");
        _context = null;
    }
}

sealed class LineGraph(int count) : IPathGraph<int>, IFlowGraph<int>
{
    public IEnumerable<int> Neighbors(int node)
    {
        if (node > 0) yield return node - 1;
        if (node + 1 < count) yield return node + 1;
    }

    public double Cost(int from, int to) => 1.0;
    public double Estimate(int from, int goal) => Math.Abs(goal - from);
    public IEnumerable<int> Incoming(int node) => Neighbors(node);
}

sealed class ToyGameState
{
    public int Position { get; set; }
}

sealed class ToyGameEnvironment : IGameEnvironment<ToyGameState, int, string>
{
    public const string Advance = "advance";
    public const string Lose = "lose";

    private readonly bool _duplicateActions;

    public ToyGameEnvironment(GameEnvironmentSpec? spec = null, bool duplicateActions = false)
    {
        Spec = spec ?? new GameEnvironmentSpec("toy_line", 1);
        _duplicateActions = duplicateActions;
    }

    public GameEnvironmentSpec Spec { get; }

    public ToyGameState Reset(int seed) => new();
    public ToyGameState Clone(ToyGameState state) => new() { Position = state.Position };
    public string StateKey(ToyGameState state) => state.Position.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public int CurrentActor(ToyGameState state) => Result(state).IsFinished ? GameActors.Terminal : 0;
    public int Observe(ToyGameState state, int actor) => state.Position;
    public IReadOnlyList<string> LegalActions(ToyGameState state) => _duplicateActions
        ? [Advance, Advance]
        : [Advance, Lose];
    public IReadOnlyList<ChanceOutcome<string>> ChanceOutcomes(ToyGameState state) => [];

    public GameEpisodeResult Result(ToyGameState state)
    {
        if (state.Position >= 3)
        {
            return new GameEpisodeResult(GameResultStatus.Terminated, [1.0], "win");
        }
        if (state.Position < 0)
        {
            return new GameEpisodeResult(GameResultStatus.Terminated, [-1.0], "loss");
        }
        return GameEpisodeResult.Running(1);
    }

    public GameTransition<ToyGameState> Step(ToyGameState state, string action)
    {
        state.Position = action switch
        {
            Advance => state.Position + 1,
            Lose => -1,
            _ => throw new ArgumentException("Unknown action.", nameof(action)),
        };
        return new GameTransition<ToyGameState>(state, [0.0], Result(state));
    }
}

sealed class ChanceGameState
{
    public int Phase { get; set; }
    public double Payoff { get; set; }
}

sealed class ChanceGameEnvironment : IGameEnvironment<ChanceGameState, int, string>
{
    public GameEnvironmentSpec Spec { get; } = new(
        "toy_chance",
        1,
        dynamics: GameDynamics.Stochastic
    );

    public ChanceGameState Reset(int seed) => new();
    public ChanceGameState Clone(ChanceGameState state) => new() { Phase = state.Phase, Payoff = state.Payoff };
    public string StateKey(ChanceGameState state) => $"{state.Phase}:{state.Payoff}";
    public int CurrentActor(ChanceGameState state) => state.Phase switch
    {
        0 => 0,
        1 => GameActors.Chance,
        _ => GameActors.Terminal,
    };
    public int Observe(ChanceGameState state, int actor) => state.Phase;
    public IReadOnlyList<string> LegalActions(ChanceGameState state) => state.Phase == 0 ? ["roll"] : [];
    public IReadOnlyList<ChanceOutcome<string>> ChanceOutcomes(ChanceGameState state) => state.Phase == 1
        ? [new ChanceOutcome<string>("good", 0.75), new ChanceOutcome<string>("bad", 0.25)]
        : [];

    public GameEpisodeResult Result(ChanceGameState state)
    {
        return state.Phase < 2
            ? GameEpisodeResult.Running(1)
            : new GameEpisodeResult(GameResultStatus.Terminated, [state.Payoff]);
    }

    public GameTransition<ChanceGameState> Step(ChanceGameState state, string action)
    {
        if (state.Phase == 0 && action == "roll")
        {
            state.Phase = 1;
        }
        else if (state.Phase == 1 && action is "good" or "bad")
        {
            state.Phase = 2;
            state.Payoff = action == "good" ? 1.0 : -1.0;
        }
        else
        {
            throw new ArgumentException("Unknown action.", nameof(action));
        }
        return new GameTransition<ChanceGameState>(state, [0.0], Result(state));
    }
}
