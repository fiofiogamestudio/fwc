using Fw.Rt.AI.Behavior;
using Fw.Rt.AI.Core;
using Fw.Rt.AI.Environment;
using Fw.Rt.AI.Evaluation;
using Fw.Rt.AI.Model;
using Fw.Rt.AI.Graph;
using Fw.Rt.AI.Nav;
using Fw.Rt.AI.Plan;
using Fw.Rt.AI.Policy;
using Fw.Rt.AI.Search;
using Fw.Rt.AI.Training;
using Fw.Rt.AI.Utility;
using Fw.Rt.Animation;
using Fw.Rt.Events;
using Fw.Rt.Logging;
using Fw.Rt.Localization;
using Fw.Rt.Net;
using Fw.Rt.Randomness;
using Fw.Rt.Script;
using Fw.Rt.State;
using System.Numerics;
using Fw.Rt.Systems;

VerifySystemRuntime();
VerifyEventBus();
VerifyStateMachine();
VerifyDeterministicRandom();
VerifyLogBuffer();
VerifyProceduralAction();
VerifyLocalizationContracts();
VerifyDecisionCore();
VerifyUtility();
VerifyBehavior();
VerifyPlan();
VerifyDecisionGraph();
VerifyDecisionAssets();
VerifyNavigation();
VerifyPolicy();
VerifyGameEnvironment();
VerifyBeamSearch();
VerifyPuctSearch();
VerifyTrainingAndEvaluation();
VerifyScript();
VerifyNet();

Console.WriteLine("Verified FwRuntime systems, events, state, random, logging, animation, localization, AI, script, and network modules.");
return;

static void VerifyProceduralAction()
{
    var keys = new[]
    {
        new ProceduralPoseKey(
            0.0f,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.One,
            ProceduralEasing.Linear
        ),
        new ProceduralPoseKey(
            4.0f,
            new Vector3(0.0f, 0.0f, 0.48f),
            new Vector3(0.0f, 40.0f, 0.0f),
            Vector3.One,
            ProceduralEasing.EaseInCubic
        ),
    };
    ProceduralPose midpoint = ProceduralActionSampler.Sample(keys, 2.0f);
    Near(0.06, midpoint.Position.Z, 0.000001, "procedural action position easing");
    Vector3 midpointForward = ProceduralActionSampler.RotateLocal(
        -Vector3.UnitZ,
        midpoint.Rotation
    );
    Near(-0.087156, midpointForward.X, 0.000001, "procedural action rotation easing x");
    Near(-0.996195, midpointForward.Z, 0.000001, "procedural action rotation easing z");
    Equal(
        4,
        ProceduralActionSampler.RequiredSubsamples(
            ProceduralActionSampler.Sample(keys, 0.0f),
            ProceduralActionSampler.Sample(keys, 4.0f)
        ),
        "procedural action bounded adaptive sampling"
    );
    Equal(
        ProceduralPose.Identity,
        ProceduralActionSampler.Sample([], 3.0f),
        "procedural action empty track identity"
    );
    Vector3 rotatedForward = ProceduralActionSampler.RotateLocalYxz(
        -Vector3.UnitZ,
        new Vector3(30.0f, 40.0f, 20.0f)
    );
    Near(-0.556670, rotatedForward.X, 0.000001, "procedural YXZ forward x");
    Near(0.5, rotatedForward.Y, 0.000001, "procedural YXZ forward y");
    Near(-0.663414, rotatedForward.Z, 0.000001, "procedural YXZ forward z");

    var wrappedKeys = new[]
    {
        new ProceduralPoseKey(0.0f, Vector3.Zero, new Vector3(0.0f, 170.0f, 0.0f), Vector3.One),
        new ProceduralPoseKey(2.0f, Vector3.Zero, new Vector3(0.0f, -170.0f, 0.0f), Vector3.One),
    };
    Vector3 wrappedForward = ProceduralActionSampler.RotateLocal(
        -Vector3.UnitZ,
        ProceduralActionSampler.Sample(wrappedKeys, 1.0f).Rotation
    );
    Near(0.0, wrappedForward.X, 0.000001, "procedural quaternion shortest path x");
    Near(1.0, wrappedForward.Z, 0.000001, "procedural quaternion shortest path z");
}

static void VerifyNet()
{
    var journal = new NetCommandJournal(3);
    True(journal.TryAppend(1, [1], 10, 1000, out NetCommand? first), "network command append first");
    True(journal.TryAppend(2, [2], 20, 1000, out NetCommand? second), "network command append second");
    True(journal.TryAppend(3, [3], 30, 1000, out NetCommand? third), "network command append third");
    True(first != null && second != null && third != null, "network command values");
    True(journal.IsStalled, "network command backpressure");
    True(!journal.TryAppend(4, [4], 40, 1000, out _), "network command rejects overflow");
    Equal(2, journal.CompleteThrough(second!.Id), "network command cumulative completion");
    True(journal.TryPeek(out NetCommand? pending) && pending?.Id == third!.Id, "network command ordered pending");

    var ledger = new NetCommandLedger(2);
    NetCommandReceipt receipt = ledger.Commit(first!.Id, NetCommandStatus.Applied, 7);
    True(ReferenceEquals(receipt, ledger.Commit(first.Id, NetCommandStatus.Rejected, 8)), "network command dedupe");
    Equal(NetCommandStatus.Applied, receipt.Status, "network command first terminal result wins");

    var history = new NetInputHistory<string>(3);
    history.Capture(uint.MaxValue, "before-wrap");
    history.Capture(0, "after-wrap");
    history.Capture(0, "replacement");
    history.Capture(uint.MaxValue, "stale");
    IReadOnlyList<NetInputFrame<string>> frames = history.NewestFirst(3);
    Equal(2, frames.Count, "network input history count");
    Equal("replacement", frames[0].State, "network input history latest");

    var duplicated = new NetFaultSimulator<int>(new NetFaultOptions
    {
        Duplicate = 1.0,
        Seed = 7,
    });
    duplicated.Enqueue(42, 0);
    Equal(2, duplicated.Receive(0).Count, "network fault duplication");
    var dropped = new NetFaultSimulator<int>(new NetFaultOptions
    {
        Loss = 1.0,
        Seed = 7,
    });
    dropped.Enqueue(42, 0);
    Equal(0, dropped.Receive(0).Count, "network fault loss");

    VerifyReliableCommandsUnderFaults();

    int port = ReserveUdpPort();
    var options = new NetTransportOptions
    {
        ConnectionKey = "fw-net-verify",
        MaxQueuedMessages = 8,
        PollIntervalMilliseconds = 1,
        DisconnectTimeoutMilliseconds = 2000,
    };
    using var server = new LiteNetTransport();
    using var client = new LiteNetTransport();
    True(server.StartServer(port, options), "network loopback server start");
    True(client.StartClient("127.0.0.1", port, options), "network loopback client start");
    WaitUntil(
        () => client.IsConnected && server.Snapshot().Peers == 1,
        "network loopback connect"
    );

    byte[] payload = [1, 2, 3, 4];
    True(client.SendToServer(payload, 1, NetDelivery.ReliableOrdered), "network reliable send");
    NetReceivedMessage connected = WaitMessage(server, message => message.Connected);
    True(payload.SequenceEqual(connected.Payload), "network reliable payload");
    True(server.Send(connected.RemoteEndPoint, [9], 2, NetDelivery.ReliableSequenced), "network server reply");
    NetReceivedMessage reply = WaitMessage(client, message => message.Connected);
    Equal((byte)9, reply.Payload[0], "network reliable reply");

    byte[] fragmentedPayload = Enumerable.Range(0, 64 * 1024)
        .Select(index => (byte)(index % 251))
        .ToArray();
    True(
        client.SendToServer(fragmentedPayload, 5, NetDelivery.ReliableOrdered),
        "network fragmented reliable send"
    );
    NetReceivedMessage fragmented = WaitMessage(server, message => message.Connected);
    True(fragmentedPayload.SequenceEqual(fragmented.Payload), "network fragmented reliable payload");
    fragmentedPayload[0] = 77;
    True(
        client.SendToServer(fragmentedPayload, 6, NetDelivery.ReliableUnordered),
        "network fragmented unordered send"
    );
    NetReceivedMessage unordered = WaitMessage(server, message => message.Connected);
    True(fragmentedPayload.SequenceEqual(unordered.Payload), "network fragmented unordered payload");

    True(server.Disconnect(connected.RemoteEndPoint), "network authority disconnect");
    WaitUntil(
        () => server.Snapshot().Peers == 0 && !client.IsConnected,
        "network authority disconnect completes"
    );
    True(!server.Disconnect(connected.RemoteEndPoint), "network authority disconnect is idempotent");

    using var discovery = new LiteNetTransport();
    True(discovery.StartUnconnected(options), "network discovery start");
    True(
        discovery.SendUnconnected(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), [8, 7]),
        "network discovery send"
    );
    NetReceivedMessage unconnected = WaitMessage(server, message => !message.Connected);
    Equal((byte)8, unconnected.Payload[0], "network discovery payload");
    for (int index = 0; index < 64; index++)
    {
        True(
            discovery.SendUnconnected(
                new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port),
                [8, 7]
            ),
            "network discovery flood send"
        );
    }
    WaitUntil(() => server.Snapshot().QueueDrops > 0, "network bounded receive queue");

    int tcpPort = ReserveTcpPort();
    using var tcpServer = new TcpNetTransport();
    using var tcpClient = new TcpNetTransport();
    True(tcpServer.StartServer(tcpPort, options), "tcp loopback server start");
    True(tcpClient.StartClient("127.0.0.1", tcpPort, options), "tcp loopback client start");
    WaitUntil(
        () => tcpClient.IsConnected && tcpServer.Snapshot().Peers == 1,
        "tcp loopback connect"
    );
    True(
        tcpClient.SendToServer(fragmentedPayload, 5, NetDelivery.ReliableOrdered),
        "tcp framed send"
    );
    NetReceivedMessage tcpMessage = WaitMessage(tcpServer, message => message.Connected);
    True(fragmentedPayload.SequenceEqual(tcpMessage.Payload), "tcp framed payload");
    True(
        tcpServer.Send(tcpMessage.RemoteEndPoint, [4, 3, 2, 1], 2, NetDelivery.ReliableSequenced),
        "tcp server reply"
    );
    NetReceivedMessage tcpReply = WaitMessage(tcpClient, message => message.Connected);
    True(new byte[] { 4, 3, 2, 1 }.SequenceEqual(tcpReply.Payload), "tcp reply payload");
    True(tcpServer.Disconnect(tcpMessage.RemoteEndPoint), "tcp authority disconnect");
    WaitUntil(
        () => tcpServer.Snapshot().Peers == 0 && !tcpClient.IsConnected,
        "tcp authority disconnect completes"
    );
}

static void VerifyReliableCommandsUnderFaults()
{
    var journal = new NetCommandJournal(128);
    for (uint tick = 1; tick <= 100; tick++)
    {
        True(
            journal.TryAppend(tick, BitConverter.GetBytes(tick), 0, 60_000, out _),
            "faulted command journal append"
        );
    }
    var ledger = new NetCommandLedger(128);
    var uplink = new NetFaultSimulator<NetCommand>(new NetFaultOptions
    {
        Loss = 0.4,
        Duplicate = 0.25,
        Reorder = 0.35,
        MinimumDelayMilliseconds = 5,
        MaximumDelayMilliseconds = 80,
        Seed = 103,
    });
    var downlink = new NetFaultSimulator<NetCommandReceipt>(new NetFaultOptions
    {
        Loss = 0.4,
        Duplicate = 0.25,
        Reorder = 0.35,
        MinimumDelayMilliseconds = 5,
        MaximumDelayMilliseconds = 80,
        Seed = 211,
    });

    for (long now = 0; now <= 60_000 && journal.Count > 0; now += 10)
    {
        foreach (NetCommand command in journal.Pending(8))
        {
            uplink.Enqueue(command, now);
        }
        foreach (NetCommand command in uplink.Receive(now, 64))
        {
            NetCommandReceipt receipt = ledger.Commit(
                command.Id,
                NetCommandStatus.Applied,
                now / 10
            );
            downlink.Enqueue(receipt, now);
        }
        foreach (NetCommandReceipt receipt in downlink.Receive(now, 64))
        {
            journal.Complete(receipt);
        }
    }

    Equal(0, journal.Count, "faulted command journal eventually drains");
    Equal(100, ledger.Count, "faulted command ledger applies each id once");
}

static int ReserveUdpPort()
{
    using var socket = new System.Net.Sockets.UdpClient(0);
    return ((System.Net.IPEndPoint)socket.Client.LocalEndPoint!).Port;
}

static int ReserveTcpPort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static void WaitUntil(Func<bool> predicate, string label, int timeoutMilliseconds = 10_000)
{
    long deadline = Environment.TickCount64 + timeoutMilliseconds;
    while (Environment.TickCount64 < deadline)
    {
        if (predicate())
        {
            return;
        }
        Thread.Sleep(5);
    }
    throw new InvalidOperationException($"Verification failed: {label} timed out.");
}

static NetReceivedMessage WaitMessage(
    INetTransport transport,
    Func<NetReceivedMessage, bool> predicate,
    int timeoutMilliseconds = 3000
)
{
    NetReceivedMessage? result = null;
    WaitUntil(
        () =>
        {
            foreach (NetReceivedMessage message in transport.Receive(64))
            {
                if (predicate(message))
                {
                    result = message;
                    return true;
                }
            }
            return false;
        },
        "network message receive",
        timeoutMilliseconds
    );
    return result!;
}

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
    var timings = child.GetTimingSnapshots();
    True(timings.Any(item => item.Scope.StartsWith("parent/", StringComparison.Ordinal)), "parent timing snapshot");
    SystemTimingSnapshot inputTiming = timings.Single(item => item.Id == "input");
    Equal(1, inputTiming.SampleCount, "system timing sample count");
    True(inputTiming.LastMilliseconds >= 0.0, "system timing last");
    True(inputTiming.AverageMilliseconds >= 0.0, "system timing average");
    True(inputTiming.P95Milliseconds >= 0.0, "system timing p95");
    True(inputTiming.MaxMilliseconds >= inputTiming.LastMilliseconds, "system timing max");
    True(inputTiming.LastAllocatedBytes >= 0L, "system timing allocated bytes");

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

static void VerifyLocalizationContracts()
{
    var source = new Dictionary<string, object?>
    {
        ["count"] = 2,
    };
    var message = new LocalizedMessage("game.turns", source, "{count} turns");
    source["count"] = 99;
    Equal(2, message.Arguments["count"], "localized message argument snapshot");
    Equal("{count} turns", message.Fallback, "localized message fallback");

    var updated = message.WithArgument("count", 3);
    Equal(2, message.Arguments["count"], "localized message remains immutable");
    Equal(3, updated.Arguments["count"], "localized message argument update");
    var payload = updated.ToPayload();
    Equal("game.turns", payload["id"], "localized message payload id");
    True(payload["args"] is IReadOnlyDictionary<string, object?>, "localized message payload args");

    var asset = new LocalizedAsset("game.logo", "res://logo.png");
    Equal("game.logo", asset.Id, "localized asset id");
    Equal("res://logo.png", asset.Fallback, "localized asset fallback");
    Throws<ArgumentException>(() => new LocalizedMessage(""), "localized message id validation");
    Throws<ArgumentException>(() => new LocalizedAsset(""), "localized asset id validation");
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

    result = selector.Select(3, Scope(1));
    True(!result.Complete && !result.HasChoice, "utility rejects partial scoring");
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

    bool urgent = false;
    var delayed = new BehaviorWait<bool>(_ => 1);
    var reactive = new BehaviorTree<bool>(new BehaviorReactiveSelector<bool>(
        new BehaviorCondition<bool>(value => value),
        delayed
    ));
    var reactiveSession = new BehaviorSession();
    Equal(
        BehaviorStatus.Running,
        reactive.Tick(urgent, reactiveSession, Scope(20)),
        "reactive fallback starts"
    );
    urgent = true;
    Equal(
        BehaviorStatus.Success,
        reactive.Tick(urgent, reactiveSession, Scope(20)),
        "reactive priority branch preempts fallback"
    );
    urgent = false;
    Equal(
        BehaviorStatus.Running,
        reactive.Tick(urgent, reactiveSession, Scope(20)),
        "preempted reactive branch restarts cleanly"
    );

    bool allowed = true;
    var sequenceDelay = new BehaviorWait<bool>(_ => 1);
    var reactiveSequence = new BehaviorTree<bool>(new BehaviorReactiveSequence<bool>(
        new BehaviorCondition<bool>(_ => allowed),
        sequenceDelay
    ));
    var reactiveSequenceSession = new BehaviorSession();
    Equal(
        BehaviorStatus.Running,
        reactiveSequence.Tick(true, reactiveSequenceSession, Scope(20)),
        "reactive sequence starts trailing branch"
    );
    allowed = false;
    Equal(
        BehaviorStatus.Failure,
        reactiveSequence.Tick(true, reactiveSequenceSession, Scope(20)),
        "reactive sequence aborts on leading condition"
    );
    allowed = true;
    Equal(
        BehaviorStatus.Running,
        reactiveSequence.Tick(true, reactiveSequenceSession, Scope(20)),
        "aborted reactive sequence branch restarts cleanly"
    );
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

static void VerifyDecisionGraph()
{
    var blackboard = new Blackboard();
    blackboard.Set("score", 0.75);
    blackboard.Set("ready", true);
    var clone = blackboard.Clone();
    clone.Set("score", 0.5);
    Equal(0.75, blackboard.Number("score"), "blackboard clone isolation");

    const string utilityJson = """
    {
      "id": "utility_test",
      "nodes": [
        { "id": 1, "type": "UtilityRoot", "values": { "name": "main" } },
        { "id": 2, "type": "UtilityGoal", "values": { "name": "act" } },
        { "id": 3, "type": "FactNumber", "values": { "key": "score" } },
        { "id": 4, "type": "ReactiveSequence", "values": {} },
        { "id": 5, "type": "Condition", "values": { "order": 0 } },
        { "id": 6, "type": "FactBool", "values": { "key": "ready" } },
        { "id": 7, "type": "Task", "values": { "name": "act", "order": 1 } }
      ],
      "edges": [
        { "id": "goal", "from": { "node": 1, "port": "goal" }, "to": { "node": 2, "port": "goal" } },
        { "id": "score", "from": { "node": 3, "port": "value" }, "to": { "node": 2, "port": "score" } },
        { "id": "goal_condition", "from": { "node": 6, "port": "value" }, "to": { "node": 2, "port": "condition" } },
        { "id": "behavior", "from": { "node": 2, "port": "behavior" }, "to": { "node": 4, "port": "parent" } },
        { "id": "condition", "from": { "node": 6, "port": "value" }, "to": { "node": 5, "port": "condition" } },
        { "id": "child_condition", "from": { "node": 4, "port": "child" }, "to": { "node": 5, "port": "parent" } },
        { "id": "child_task", "from": { "node": 4, "port": "child" }, "to": { "node": 7, "port": "parent" } }
      ]
    }
    """;
    DecisionGraph utilityGraph = DecisionGraph.Parse(utilityJson);
    var expression = new DecisionExpression(utilityGraph);
    var exhaustedExpressionScope = Scope(0);
    Throws<DecisionGraphBudgetException>(
        () => expression.Number(3, blackboard, exhaustedExpressionScope),
        "expression budget exhaustion"
    );
    exhaustedExpressionScope.Budget.Reset();
    Throws<DecisionGraphBudgetException>(
        () => expression.Number(3, blackboard, exhaustedExpressionScope),
        "expression scope clears path after failure"
    );
    var utility = new UtilityProgram(utilityGraph, "main");
    var utilitySession = new DecisionSession();
    var taskHost = new TestDecisionHost();
    DecisionResult utilityResult = utility.Tick(
        blackboard,
        utilitySession,
        taskHost,
        Scope(32)
    );
    Equal("act", utilityResult.Active, "utility graph choice");
    Equal(BehaviorStatus.Success, utilityResult.Status, "utility graph behavior");
    Equal("act", taskHost.LastTask, "utility graph task host");
    blackboard.Set("ready", false);
    DecisionResult unavailableResult = utility.Tick(
        blackboard,
        utilitySession,
        taskHost,
        Scope(32),
        reselect: false
    );
    Equal("", unavailableResult.Active, "utility drops unavailable pinned goal");
    Equal(BehaviorStatus.Failure, unavailableResult.Status,
        "utility fails safely when every goal is unavailable");
    Equal("", utilitySession.ChoiceId, "utility clears unavailable pinned session");
    blackboard.Set("ready", true);
    var pinnedSession = new DecisionSession();
    DecisionResult pinnedResult = utility.TickGoal(
        "act",
        blackboard,
        pinnedSession,
        taskHost,
        Scope(32)
    );
    Equal("act", pinnedResult.Active, "utility graph pinned goal");
    Equal("act", pinnedSession.ChoiceId, "utility graph pinned session");
    Throws<DecisionGraphException>(
        () => utility.TickGoal("missing", blackboard, pinnedSession, taskHost, Scope(32)),
        "utility graph rejects unknown pinned goal"
    );

    const string utilityIdleFragment = """
    {
      "id": "utility_idle",
      "nodes": [
        { "id": 1, "type": "UtilityRoot", "values": { "name": "main", "switch_threshold": 0.1 } },
        { "id": 2, "type": "UtilityGoal", "values": { "name": "idle", "order": 1 } },
        { "id": 3, "type": "Number", "values": { "value": 0.25 } },
        { "id": 4, "type": "Succeed", "values": {} }
      ],
      "edges": [
        { "id": "goal", "from": { "node": 1, "port": "goal" }, "to": { "node": 2, "port": "goal" } },
        { "id": "score", "from": { "node": 3, "port": "value" }, "to": { "node": 2, "port": "score" } },
        { "id": "behavior", "from": { "node": 2, "port": "behavior" }, "to": { "node": 4, "port": "child" } }
      ]
    }
    """;
    const string utilityFightFragment = """
    {
      "id": "utility_fight",
      "nodes": [
        { "id": 1, "type": "UtilityRoot", "values": { "name": "main", "switch_threshold": 0.1 } },
        { "id": 2, "type": "UtilityGoal", "values": { "name": "fight", "order": 0 } },
        { "id": 3, "type": "Number", "values": { "value": 0.75 } },
        { "id": 4, "type": "Succeed", "values": {} }
      ],
      "edges": [
        { "id": "goal", "from": { "node": 1, "port": "goal" }, "to": { "node": 2, "port": "goal" } },
        { "id": "score", "from": { "node": 3, "port": "value" }, "to": { "node": 2, "port": "score" } },
        { "id": "behavior", "from": { "node": 2, "port": "behavior" }, "to": { "node": 4, "port": "child" } }
      ]
    }
    """;
    DecisionGraph composedUtility = DecisionGraph.Compose(
        "utility_set",
        [
            DecisionGraph.Parse(utilityIdleFragment),
            DecisionGraph.Parse(utilityFightFragment),
        ]
    );
    Equal(1, composedUtility.NodesOfType("UtilityRoot").Count, "composed utility root");
    Equal(2, composedUtility.NodesOfType("UtilityGoal").Count, "composed utility goals");
    DecisionResult composedResult = new UtilityProgram(
        composedUtility,
        "main"
    ).Tick(blackboard, new DecisionSession(), taskHost, Scope(32));
    Equal("fight", composedResult.Active, "composed utility selection");

    const string stateJson = """
    {
      "id": "state_test",
      "nodes": [
        { "id": 10, "type": "StateTreeRoot", "values": { "name": "monster" } },
        { "id": 11, "type": "State", "values": { "name": "idle", "initial": true, "view_state": "idle" } },
        { "id": 12, "type": "State", "values": { "name": "attack", "view_state": "attack" } },
        { "id": 13, "type": "Transition", "values": { "target": "attack", "trigger": "tick" } },
        { "id": 14, "type": "FactBool", "values": { "key": "ready" } },
        { "id": 15, "type": "Succeed", "values": {} }
      ],
      "edges": [
        { "id": "idle", "from": { "node": 10, "port": "state" }, "to": { "node": 11, "port": "state" } },
        { "id": "attack", "from": { "node": 10, "port": "state" }, "to": { "node": 12, "port": "state" } },
        { "id": "transition", "from": { "node": 11, "port": "transition" }, "to": { "node": 13, "port": "transition" } },
        { "id": "transition_condition", "from": { "node": 14, "port": "value" }, "to": { "node": 13, "port": "condition" } },
        { "id": "attack_task", "from": { "node": 12, "port": "task" }, "to": { "node": 15, "port": "parent" } }
      ]
    }
    """;
    var state = new StateProgram(DecisionGraph.Parse(stateJson), "monster");
    DecisionResult stateResult = state.Tick(
        blackboard,
        new DecisionSession(),
        taskHost,
        Scope(32)
    );
    Equal("attack", stateResult.Active, "state graph transition");

    const string nestedStateJson = """
    {
      "id": "nested_state_test",
      "nodes": [
        { "id": 30, "type": "StateTreeRoot", "values": { "name": "nested" } },
        { "id": 31, "type": "State", "values": { "name": "parent", "initial": true } },
        { "id": 32, "type": "State", "values": { "name": "child", "initial": true } },
        { "id": 33, "type": "Wait", "values": { "ticks": 1 } },
        { "id": 34, "type": "Wait", "values": { "ticks": 1 } }
      ],
      "edges": [
        { "id": "parent", "from": { "node": 30, "port": "state" }, "to": { "node": 31, "port": "state" } },
        { "id": "child", "from": { "node": 31, "port": "state" }, "to": { "node": 32, "port": "state" } },
        { "id": "parent_task", "from": { "node": 31, "port": "task" }, "to": { "node": 33, "port": "child" } },
        { "id": "child_task", "from": { "node": 32, "port": "task" }, "to": { "node": 34, "port": "child" } }
      ]
    }
    """;
    var nestedState = new StateProgram(
        DecisionGraph.Parse(nestedStateJson),
        "nested"
    );
    var nestedSession = new DecisionSession();
    Equal(
        BehaviorStatus.Running,
        nestedState.Tick(blackboard, nestedSession, taskHost, Scope(32)).Status,
        "nested state tasks start together"
    );
    Equal(1, nestedSession.State.StateTicks, "entered state advances after its first tick");
    Equal(
        BehaviorStatus.Success,
        nestedState.Tick(blackboard, nestedSession, taskHost, Scope(32)).Status,
        "nested state tasks keep independent cursors"
    );
    Equal(2, nestedSession.State.StateTicks, "stable state advances once per tick");

    var budgetState = new Fw.Rt.AI.State.StateTree<int>(
    [
        new Fw.Rt.AI.State.StateNode<int>(
            "idle",
            initial: true,
            task: (_, _, _) => BehaviorStatus.Success,
            transitions:
            [
                new Fw.Rt.AI.State.StateTransition<int>(
                    "done",
                    Fw.Rt.AI.State.StateTransitionTrigger.Success
                ),
            ]
        ),
        new Fw.Rt.AI.State.StateNode<int>("done"),
    ]);
    var budgetStateSession = new Fw.Rt.AI.State.StateTreeSession();
    Fw.Rt.AI.State.StateTreeResult budgetStateResult = budgetState.Tick(
        0,
        budgetStateSession,
        Scope(1)
    );
    Equal(
        BehaviorStatus.Suspended,
        budgetStateResult.Status,
        "state transition budget exhaustion suspends instead of skipping"
    );
    Equal("idle", budgetStateSession.ActiveState, "suspended transition does not activate target");
    Equal(0, budgetStateSession.StateTicks, "suspended transition does not advance state time");

    const string planJson = """
    {
      "id": "plan_test",
      "nodes": [
        { "id": 20, "type": "GoapRoot", "values": { "name": "main" } },
        { "id": 21, "type": "GoapGoal", "values": { "name": "escape", "requires": ["open"] } },
        { "id": 22, "type": "GoapAction", "values": { "name": "find_key", "adds": ["key"], "cost": 1 } },
        { "id": 23, "type": "GoapAction", "values": { "name": "open_door", "requires": ["key"], "adds": ["open"], "cost": 1 } }
      ],
      "edges": [
        { "id": "plan_goal", "from": { "node": 20, "port": "goal" }, "to": { "node": 21, "port": "goal" } },
        { "id": "plan_action_1", "from": { "node": 20, "port": "action" }, "to": { "node": 22, "port": "action" } },
        { "id": "plan_action_2", "from": { "node": 20, "port": "action" }, "to": { "node": 23, "port": "action" } }
      ]
    }
    """;
    var plan = new PlanGraph(DecisionGraph.Parse(planJson), "main");
    PlanResult<string> planResult = plan.Complete([], "escape", Scope(32));
    Equal(PlanStatus.Complete, planResult.Status, "GOAP graph completes");
    Equal("find_key,open_door", string.Join(',', planResult.Actions.Select(item => item.Id)), "GOAP graph order");
}

static void VerifyDecisionAssets()
{
    const string conditions = """
    {
      "id": "test",
      "conditions": [
        {
          "id": "ready",
          "groups": [
            { "clauses": [{ "fact": "ready", "op": "true" }] }
          ]
        }
      ]
    }
    """;
    var trees = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["act"] = """
        {
          "id": "act",
          "nodes": [
            { "id": 1, "type": "TreeRoot", "values": { "name": "act" } },
            { "id": 2, "type": "Sequence", "values": {} },
            { "id": 3, "type": "Guard", "values": { "condition": "ready", "order": 0 } },
            { "id": 4, "type": "MoveTo", "values": { "target": "threat", "order": 1 } }
          ],
          "edges": [
            { "id": "root", "from": { "node": 1, "port": "child" }, "to": { "node": 2, "port": "child" } },
            { "id": "guard", "from": { "node": 2, "port": "child" }, "to": { "node": 3, "port": "child" } },
            { "id": "task", "from": { "node": 2, "port": "child" }, "to": { "node": 4, "port": "child" } }
          ]
        }
        """,
        ["idle"] = """
        {
          "id": "idle",
          "nodes": [
            { "id": 1, "type": "TreeRoot", "values": { "name": "idle" } },
            { "id": 2, "type": "Succeed", "values": {} }
          ],
          "edges": [
            { "id": "root", "from": { "node": 1, "port": "child" }, "to": { "node": 2, "port": "child" } }
          ]
        }
        """,
    };
    DecisionAssets assets = DecisionAssets.Parse(conditions, trees);
    Equal("act,idle", string.Join(',', assets.Trees), "decision asset tree catalog");

    UtilityProgram utilityProgram = assets.Utility("""
    {
      "id": "utility",
      "goals": [
        {
          "id": "act",
          "tree": "act",
          "condition": "ready",
          "base": 0.2,
          "scores": [
            { "combine": "add", "fact": "urgency", "curve": "linear", "scale": 0.5 }
          ]
        },
        { "id": "idle", "tree": "idle", "base": 0.1, "order": 1, "scores": [] }
      ]
    }
    """);
    var blackboard = new Blackboard();
    blackboard.Set("ready", true);
    blackboard.Set("urgency", 1.0);
    var host = new TestDecisionHost();
    True(utilityProgram.IsAvailable("act", blackboard, Scope(64)),
        "authored utility available condition");
    blackboard.Set("ready", false);
    True(!utilityProgram.IsAvailable("act", blackboard, Scope(64)),
        "authored utility unavailable condition");
    blackboard.Set("ready", true);
    DecisionResult utility = utilityProgram.Tick(
        blackboard,
        new DecisionSession(),
        host,
        Scope(64)
    );
    Equal("act", utility.Active, "authored utility selection");
    Equal("move_to", host.LastTask, "authored semantic task behavior");

    StateProgram stateProgram = assets.State("""
    {
      "id": "state",
      "initial": "idle",
      "states": [
        {
          "name": "idle",
          "tree": "idle",
          "transitions": [
            { "target": "active", "condition": "ready", "trigger": "tick", "priority": 10 }
          ]
        },
        { "name": "active", "tree": "act", "transitions": [] }
      ]
    }
    """);
    DecisionResult state = stateProgram.Tick(
        blackboard,
        new DecisionSession(),
        host,
        Scope(64)
    );
    Equal("active", state.Active, "authored state transition");
    Throws<DecisionGraphException>(
        () => assets.BuildState("""
        {
          "id": "invalid_state",
          "initial": "missing",
          "states": [{ "name": "idle", "tree": "idle", "transitions": [] }]
        }
        """),
        "authored state validates initial state"
    );
    Throws<DecisionGraphException>(
        () => assets.BuildState("""
        {
          "id": "invalid_state",
          "initial": "idle",
          "states": [
            {
              "name": "idle",
              "tree": "idle",
              "transitions": [{ "target": "missing", "trigger": "tick" }]
            }
          ]
        }
        """),
        "authored state validates transition target"
    );
    Throws<DecisionGraphException>(
        () => assets.BuildState("""
        {
          "id": "cyclic_state",
          "initial": "a",
          "states": [
            { "name": "a", "parent": "b", "tree": "idle", "transitions": [] },
            { "name": "b", "parent": "a", "tree": "idle", "transitions": [] }
          ]
        }
        """),
        "authored state rejects cyclic parents"
    );

    PlanGraph planGraph = assets.Plan("""
    {
      "id": "plan",
      "goals": [{ "id": "escape", "requires": ["open"] }],
      "actions": [
        { "id": "key", "requires": [], "adds": ["key"], "removes": [], "cost": 1 },
        { "id": "open", "requires": ["key"], "adds": ["open"], "removes": [], "cost": 1 }
      ]
    }
    """);
    PlanResult<string> plan = planGraph.Complete([], "escape", Scope(64));
    Equal("key,open", string.Join(',', plan.Actions.Select(item => item.Id)), "authored GOAP plan");

    var invalidTrees = new Dictionary<string, string>(trees, StringComparer.Ordinal)
    {
        ["invalid"] = """
        {
          "id": "invalid",
          "nodes": [
            { "id": 1, "type": "TreeRoot", "values": {} },
            { "id": 2, "type": "Sequence", "values": {} },
            { "id": 3, "type": "Task", "values": { "name": "act" } }
          ],
          "edges": [
            { "id": "root", "from": { "node": 1, "port": "child" }, "to": { "node": 2, "port": "child" } },
            { "id": "first", "from": { "node": 2, "port": "child" }, "to": { "node": 3, "port": "child" } },
            { "id": "second", "from": { "node": 1, "port": "child" }, "to": { "node": 3, "port": "child" } }
          ]
        }
        """,
    };
    Throws<DecisionGraphException>(
        () => DecisionAssets.Parse(conditions, invalidTrees),
        "authored tree requires one parent"
    );
    var disconnectedTrees = new Dictionary<string, string>(trees, StringComparer.Ordinal)
    {
        ["disconnected"] = """
        {
          "id": "disconnected",
          "nodes": [
            { "id": 1, "type": "TreeRoot", "values": {} },
            { "id": 2, "type": "Succeed", "values": {} },
            { "id": 3, "type": "Sequence", "values": {} },
            { "id": 4, "type": "Invert", "values": {} }
          ],
          "edges": [
            { "id": "root", "from": { "node": 1, "port": "child" }, "to": { "node": 2, "port": "child" } },
            { "id": "cycle_a", "from": { "node": 3, "port": "child" }, "to": { "node": 4, "port": "child" } },
            { "id": "cycle_b", "from": { "node": 4, "port": "child" }, "to": { "node": 3, "port": "child" } }
          ]
        }
        """,
    };
    Throws<DecisionGraphException>(
        () => DecisionAssets.Parse(conditions, disconnectedTrees),
        "authored tree rejects disconnected cycles"
    );
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
    var parallelSpec = new GameEnvironmentSpec(
        "parallel",
        2,
        information: GameInformation.Imperfect,
        moveMode: GameMoveMode.Simultaneous,
        payoffMode: GamePayoffMode.ZeroSum
    );
    ParallelEnvironmentGuard.ValidateActors([0, 1], parallelSpec);
    ParallelEnvironmentGuard.ValidateActions([0, 1], new Dictionary<int, string>
    {
        [0] = "left",
        [1] = "right",
    });
    var parallelView = new ParallelActorView<int, string>(0, 7, ["left", "right"]);
    Equal(2, parallelView.LegalActions.Count, "parallel actor legal actions");

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

    var dense = new DenseActorCriticModel(
        "verify_dense_v1",
        inputCount: 2,
        hiddenCount: 8,
        actionCount: 2,
        seed: 19
    );
    var denseTrainer = new DensePpoTrainer(
        dense,
        new DensePpoTrainingOptions(
            epochs: 1,
            batchSize: 2,
            learningRate: 0.01,
            entropyCoefficient: 0.0
        )
    );
    var imitation = new[]
    {
        new DenseImitationSample([-1.0, 1.0], [true, true], 0),
        new DenseImitationSample([1.0, 1.0], [true, true], 1),
    };
    DenseTrainingResult imitationResult = denseTrainer.TrainImitation(
        imitation,
        new DeterministicRandomStream(23),
        epochs: 200
    );
    Equal(400, imitationResult.Samples, "dense imitation sample count");
    DenseActorCriticPrediction denseNegative = dense.Predict([-1.0, 1.0], [true, true]);
    DenseActorCriticPrediction densePositive = dense.Predict([1.0, 1.0], [true, true]);
    True(denseNegative.Probabilities[0] > 0.9, "dense imitation negative policy");
    True(densePositive.Probabilities[1] > 0.9, "dense imitation positive policy");

    DensePolicyChoice oldChoice = dense.Choose(
        [1.0, 1.0],
        [true, true],
        new DeterministicRandomStream(29),
        sample: false
    );
    DenseTrainingResult ppoResult = denseTrainer.Train(
        [new DensePpoSample(
            [1.0, 1.0],
            [true, true],
            oldChoice.Action,
            Math.Log(oldChoice.Probability),
            advantage: 1.0,
            valueTarget: 1.0
        )],
        new DeterministicRandomStream(31)
    );
    Equal(1, ppoResult.Updates, "dense PPO call-local update count");

    var guardedDense = new DenseActorCriticModel(
        "verify_dense_kl_v1",
        inputCount: 2,
        hiddenCount: 8,
        actionCount: 2,
        seed: 41
    );
    DensePolicyChoice guardedChoice = guardedDense.Choose(
        [1.0, 1.0],
        [true, true],
        new DeterministicRandomStream(43),
        sample: false
    );
    var guardedTrainer = new DensePpoTrainer(
        guardedDense,
        new DensePpoTrainingOptions(
            epochs: 4,
            batchSize: 1,
            learningRate: 0.01,
            entropyCoefficient: 0.0,
            targetKl: double.Epsilon
        )
    );
    DenseTrainingResult guardedResult = guardedTrainer.Train(
        [new DensePpoSample(
            [1.0, 1.0],
            [true, true],
            guardedChoice.Action,
            Math.Log(guardedChoice.Probability),
            advantage: 1.0,
            valueTarget: 1.0
        )],
        new DeterministicRandomStream(47)
    );
    Equal(2, guardedResult.Samples, "dense PPO target KL stops before all epochs");
    Equal(2, guardedResult.Updates, "dense PPO target KL update count");

    DenseActorCriticCheckpoint denseCheckpoint = DenseActorCriticCheckpoint.FromJson(
        dense.ExportCheckpoint().ToJson()
    );
    var restoredDense = new DenseActorCriticModel(denseCheckpoint);
    DenseActorCriticPrediction restoredDensePrediction = restoredDense.Predict(
        [1.0, 1.0],
        [true, true]
    );
    Near(
        dense.Predict([1.0, 1.0], [true, true]).Probabilities[1],
        restoredDensePrediction.Probabilities[1],
        1e-12,
        "dense serialized checkpoint policy"
    );

    var league = new ZeroSumLeague();
    league.Add("candidate", "champion", 1.0);
    league.Add("candidate", "champion", 0.0);
    LeaguePayoff candidatePayoff = league.Get("candidate", "champion");
    Near(0.5, candidatePayoff.Mean, 1e-12, "league payoff mean");
    Near(-0.5, league.Get("champion", "candidate").Mean, 1e-12, "league reverse payoff");
    IReadOnlyDictionary<string, double> meta = league.MetaStrategy(["candidate", "champion"], 200);
    Near(1.0, meta.Values.Sum(), 1e-12, "league meta strategy mass");
    True(meta.ContainsKey(league.SampleOpponent(meta, new DeterministicRandomStream(37))),
        "league deterministic sample");
}

static void VerifyScript()
{
    using var runtime = new ScriptRuntime(new ScriptRuntimeOptions(
        LoadInstructionLimit: 10_000,
        CallInstructionLimit: 2_000
    ));
    runtime.Load("decision", """
        return {
            decide = function(input, api)
                local bonus = api.query("bonus", input.value)
                api.command("move", input.value + bonus)
                return {
                    total = input.value + bonus,
                    safe = os == nil and io == nil and require == nil
                        and load == nil and dofile == nil and math.random == nil
                }
            end
        }
        """);
    var host = new ScriptHost();
    host.RegisterQuery("bonus", args => Convert.ToDouble(args[0]) * 2.0);
    var result = runtime.Call(
        "decision",
        "decide",
        new Dictionary<string, object?> { ["value"] = 3 },
        host
    );
    var value = (IReadOnlyDictionary<string, object?>)result.Value!;
    Equal(9.0, value["total"], "script result");
    Equal(true, value["safe"], "script sandbox");
    Equal(1, result.Commands.Count, "script command count");
    Equal("move", result.Commands[0].Name, "script command name");
    Equal(9.0, result.Commands[0].Args[0], "script command payload");
    True(result.Instructions > 0, "script instruction accounting");
    True(runtime.HasFunction("decision", "decide"), "script function discovery");
    True(runtime.HasFunction(" decision ", " decide "), "script name normalization");
    True(!runtime.HasFunction("decision", "missing"), "missing script function discovery");
    runtime.RequireFunction("decision", "decide");
    Throws<ScriptRuntimeException>(
        () => runtime.RequireFunction("decision", "missing"),
        "required script function"
    );

    var graph = ScriptGraph.Parse("""
        {
          "id": "test",
          "nodes": [
            { "id": 1, "type": "Start", "values": {} },
            { "id": 2, "type": "Done", "values": { "value": 4 } }
          ],
          "edges": [
            { "id": "next", "from": { "node": 1, "port": "then" }, "to": { "node": 2, "port": "exec" } }
          ]
        }
        """);
    Equal("test", graph.Id, "script graph id");
    Equal(2, graph.Nodes.Count, "script graph nodes");
    Equal(1, graph.Edges.Count, "script graph edges");
    True(graph.Data is not Dictionary<string, object?>, "script graph data is immutable");
    True(graph.Nodes is not List<ScriptGraphNode>, "script graph nodes are immutable");
    True(
        graph.Nodes[0].Values is not Dictionary<string, object?>,
        "script graph node values are immutable"
    );
    Throws<ScriptRuntimeException>(
        () => ScriptGraph.Parse(new string('x', 4_194_305)),
        "script graph source length limit"
    );
    Throws<ScriptRuntimeException>(
        () => ScriptGraph.Parse($$"""
            {
              "id": "test",
              "nodes": [
                { "id": 1, "type": "{{new string('x', 129)}}", "values": {} }
              ],
              "edges": []
            }
            """),
        "script graph identifier length limit"
    );

    using var limited = new ScriptRuntime(new ScriptRuntimeOptions(
        LoadInstructionLimit: 1_000,
        CallInstructionLimit: 100
    ));
    limited.Load("loop", "return { run = function() while true do end end }");
    Throws<ScriptBudgetException>(() => limited.Call("loop", "run"), "script instruction budget");

    using var transactional = new ScriptRuntime();
    transactional.Load(
        "transaction",
        "return { run = function(_, api) api.command('discarded'); error('stop') end }"
    );
    Throws<ScriptRuntimeException>(
        () => transactional.Call("transaction", "run"),
        "script commands are discarded on error"
    );

    using var hardened = new ScriptRuntime(new ScriptRuntimeOptions(
        LoadInstructionLimit: 10_000,
        CallInstructionLimit: 2_000,
        MaxValueDepth: 8,
        MaxCollectionItems: 2,
        MaxStringLength: 8,
        MaxCommands: 1
    ));
    hardened.Load("limits", """
        return {
            echo = function(input) return input end,
            non_finite = function() return 0 / 0 end,
            long_string = function() return string.rep("x", 9) end,
            commands = function(_, api)
                api.command("a")
                api.command("b")
            end
        }
        """);
    Throws<ScriptRuntimeException>(
        () => hardened.Call("limits", "echo", double.PositiveInfinity),
        "script input finite number"
    );
    Throws<ScriptRuntimeException>(
        () => hardened.Call("limits", "non_finite"),
        "script output finite number"
    );
    Throws<ScriptRuntimeException>(
        () => hardened.Call("limits", "long_string"),
        "script total string limit"
    );
    Throws<ScriptRuntimeException>(
        () => hardened.Call("limits", "commands"),
        "script command count limit"
    );
    Throws<ScriptRuntimeException>(
        () => hardened.Call(
            "limits",
            "echo",
            new Dictionary<string, object?>
            {
                ["a"] = 1,
                ["b"] = new Dictionary<string, object?> { ["c"] = 2 },
            }
        ),
        "script total collection limit"
    );
    using var duplicateJson = System.Text.Json.JsonDocument.Parse("{\"a\":1,\"a\":2}");
    Throws<ScriptRuntimeException>(
        () => hardened.Call("limits", "echo", duplicateJson.RootElement),
        "script JSON duplicate keys"
    );

    using var sourceLimited = new ScriptRuntime(new ScriptRuntimeOptions(MaxSourceLength: 8));
    Throws<ScriptRuntimeException>(
        () => sourceLimited.Load("large", "return { run = function() end }"),
        "script source length limit"
    );

    string excessiveGraph = System.Text.Json.JsonSerializer.Serialize(new
    {
        id = "too_large",
        nodes = Enumerable.Range(0, 16_385).Select(id => new
        {
            id,
            type = "Node",
            values = new { },
        }),
        edges = Array.Empty<object>(),
    });
    Throws<ScriptRuntimeException>(
        () => ScriptGraph.Parse(excessiveGraph),
        "script graph node limit"
    );
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

sealed class TestDecisionHost : ITaskHost
{
    public string LastTask { get; private set; } = "";

    public BehaviorStatus Tick(
        string task,
        IReadOnlyDictionary<string, object?> parameters,
        Blackboard blackboard,
        DecisionScope scope
    )
    {
        LastTask = task;
        return BehaviorStatus.Success;
    }
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
