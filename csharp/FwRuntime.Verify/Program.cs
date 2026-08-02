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

Console.WriteLine("Verified FwRuntime systems, events, state, random, and logging.");
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
