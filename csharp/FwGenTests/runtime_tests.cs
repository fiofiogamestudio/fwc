using Fw.Rt.Bridge;
using Fw.Rt.Rooms;
using Fw.Rt.Systems;
using static TestKit;

static class RuntimeTests
{
    internal static TestCase[] Cases =>
    [
        new("generation lock excludes writers", TestGenerationLock),
        new("path containment follows platform case rules", TestPathContainmentCaseRules),
        new("wire frame round trip", TestWireFrameRoundTrip),
        new("wire frame rejects tampering", TestWireFrameTampering),
        new("wire frame rejects malformed headers", TestWireFrameHeaders),
        new("wire frame rejects every single-byte mutation", TestWireFrameMutationSweep),
        new("room ticket authenticates claims", TestRoomTicket),
        new("room directory lifecycle", TestRoomDirectory),
        new("system phase ordering", TestSystemPhaseOrdering),
        new("system init rollback", TestSystemInitRollback),
        new("system tick fault cleanup", TestSystemTickFaultCleanup),
        new("system shutdown continues", TestSystemShutdownContinues),
    ];

    private static void TestGenerationLock()
    {
        WithTempDir(root =>
        {
            using var first = GenerationLock.Acquire(root, TimeSpan.FromSeconds(1));
            Throws(() =>
            {
                using var second = GenerationLock.Acquire(root, TimeSpan.FromMilliseconds(25));
            }, "timed out waiting");
            if (OperatingSystem.IsWindows())
            {
                Throws(() =>
                {
                    using var alias = GenerationLock.Acquire(root.ToUpperInvariant(), TimeSpan.FromMilliseconds(25));
                }, "timed out waiting");
            }
        });
    }

    private static void TestPathContainmentCaseRules()
    {
        string parent = Path.Combine(Path.GetTempPath(), "fw-case-root");
        string child = Path.Combine(parent, "child");
        True(FwCheck.IsUnderPath(child, parent), "direct child path");
        True(FwCheck.IsUnderPath(child, Path.GetPathRoot(parent)!), "filesystem root path");

        string caseAlias = parent.ToUpperInvariant();
        Equal(
            OperatingSystem.IsWindows(),
            FwCheck.IsUnderPath(child, caseAlias),
            "platform path casing"
        );
    }

    private static void TestWireFrameRoundTrip()
    {
        byte[] value = System.Text.Encoding.UTF8.GetBytes(new string('a', 4096));
        byte[] frame = WireFrame.Encode(value, new WireFrameOptions(64, 8192, 8192));
        True(WireFrame.HasHeader(frame), "wire header");
        True(WireFrame.Decode(frame, new WireFrameOptions(64, 8192, 8192)).SequenceEqual(value), "wire round trip");
    }

    private static void TestWireFrameTampering()
    {
        byte[] frame = WireFrame.Encode("payload"u8);
        frame[^1] ^= 0xff;
        Throws(() => WireFrame.Decode(frame), "checksum mismatch");
        Throws(() => WireFrame.Encode(new byte[32], new WireFrameOptions(0, 16, 64)), "decoded limit");
        Throws(() => WireFrame.Decode(frame, new WireFrameOptions(0, 64, int.MaxValue)), "limits are invalid");
    }

    private static void TestWireFrameHeaders()
    {
        var options = new WireFrameOptions(1024, 1024, 1024);
        byte[] frame = WireFrame.Encode("payload"u8, options);

        Throws(() => WireFrame.Decode(frame.AsSpan(0, 47), options), "magic");
        Throws(() => WireFrame.Decode(Changed(frame, 0, (byte)'X'), options), "magic");
        Throws(() => WireFrame.Decode(Changed(frame, 4, 2), options), "unsupported");
        Throws(() => WireFrame.Decode(Changed(frame, 5, 2), options), "flags");
        Throws(() => WireFrame.Decode(Changed(frame, 6, 1), options), "flags");

        byte[] invalidDecodedLength = [.. frame];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(invalidDecodedLength.AsSpan(8, 4), -1);
        Throws(() => WireFrame.Decode(invalidDecodedLength, options), "decoded length");

        byte[] invalidEncodedLength = [.. frame];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(invalidEncodedLength.AsSpan(12, 4), 1);
        Throws(() => WireFrame.Decode(invalidEncodedLength, options), "encoded length");

        byte[] mismatchedDecodedLength = [.. frame];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(mismatchedDecodedLength.AsSpan(8, 4), 1);
        Throws(() => WireFrame.Decode(mismatchedDecodedLength, options), "decoded length mismatch");
    }

    private static void TestWireFrameMutationSweep()
    {
        byte[] payload = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var options = new WireFrameOptions(1024, 1024, 1024);
        byte[] frame = WireFrame.Encode(payload, options);

        for (var index = 0; index < frame.Length; index++)
        {
            byte[] changed = [.. frame];
            changed[index] ^= 1;
            int position = index;
            Throws<InvalidDataException>(
                () => WireFrame.Decode(changed, options),
                $"wire frame mutation at byte {position}"
            );
        }
    }

    private static void TestRoomTicket()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        string ticket = RoomTicket.Create("test-secret", "game", "room_1", now.AddSeconds(30), "nonce");
        True(
            RoomTicket.TryValidate(ticket, "test-secret", "game", "room_1", now, out RoomTicketClaims claims),
            "room ticket validates"
        );
        Equal("nonce", claims.Nonce, "room ticket nonce");
        Equal(now.AddSeconds(30).ToUnixTimeSeconds(), claims.ExpiresAtUnixSeconds, "room ticket expiry");
        True(!RoomTicket.TryValidate(ticket, "wrong", "game", "room_1", now, out _), "wrong secret");
        True(!RoomTicket.TryValidate(ticket, "test-secret", "game", "other", now, out _), "wrong room");
        True(!RoomTicket.TryValidate(ticket, "test-secret", "game", "room_1", now.AddSeconds(30), out _), "expired ticket");
        string tampered = ticket[..^1] + (ticket[^1] == 'A' ? "B" : "A");
        True(!RoomTicket.TryValidate(tampered, "test-secret", "game", "room_1", now, out _), "tampered ticket");
    }

    private static void TestRoomDirectory()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var store = new RoomDirectoryStore("test-secret", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));
        var registration = new RoomRegistration
        {
            RoomId = "room_1",
            GameId = "game",
            Name = "Test Room",
            Port = 7777,
            MapKey = "default",
            Capacity = 2,
            ProtocolVersion = 7,
            Tags = null!,
        };
        Throws<ArgumentNullException>(
            () => store.Register(null!, "test-secret", "127.0.0.1", now),
            "registration"
        );
        Throws<UnauthorizedAccessException>(
            () => store.Register(registration, "wrong", "127.0.0.1", now),
            "registration secret"
        );

        RoomRegistrationResult registered = store.Register(registration, "test-secret", "127.0.0.1", now);
        Equal("127.0.0.1", registered.Room.Host, "remote host fallback");
        Equal(0, registered.Room.Tags.Count, "null tags normalize to empty");
        True(registered.AdmissionSecret.Length >= 32, "room admission secret");
        Equal(5000, registered.HeartbeatIntervalMilliseconds, "heartbeat interval follows lease");
        Throws<InvalidOperationException>(
            () => store.Register(registration, "test-secret", "127.0.0.1", now),
            "active room"
        );
        Equal(1, store.List("game", 7, now).Count, "listed room");
        True(!store.Heartbeat("room_1", "wrong", new RoomHeartbeat(), now), "heartbeat token");
        Throws<ArgumentNullException>(
            () => store.Heartbeat("room_1", registered.HeartbeatToken, null!, now),
            "heartbeat"
        );
        True(
            store.Heartbeat(
                "room_1",
                registered.HeartbeatToken,
                new RoomHeartbeat { Players = 2 },
                now.AddSeconds(1)
            ),
            "room heartbeat"
        );
        Equal(RoomStatus.Full, store.List("game", 7, now.AddSeconds(1))[0].Status, "full status");
        True(store.Join("room_1", now.AddSeconds(1)) == null, "full room rejects join");

        True(
            store.Heartbeat(
                "room_1",
                registered.HeartbeatToken,
                new RoomHeartbeat { Players = 1 },
                now.AddSeconds(2)
            ),
            "room reopens"
        );
        RoomJoin join = store.Join("room_1", now.AddSeconds(2))
            ?? throw new InvalidOperationException("Open room did not issue a join ticket.");
        True(
            RoomTicket.TryValidate(
                join.Ticket,
                registered.AdmissionSecret,
                "game",
                "room_1",
                now.AddSeconds(2),
                out _
            ),
            "directory join ticket"
        );
        True(
            !RoomTicket.TryValidate(
                join.Ticket,
                "test-secret",
                "game",
                "room_1",
                now.AddSeconds(2),
                out _
            ),
            "directory registration secret cannot validate room tickets"
        );
        Equal(1, store.Sweep(now.AddSeconds(18)), "stale room swept");
        Equal(0, store.List("game", 7, now.AddSeconds(18)).Count, "stale room hidden");
    }

    private static void TestSystemPhaseOrdering()
    {
        var calls = new List<string>();
        var runtime = new SystemRuntime();
        runtime.SetPhaseOrder(["input", "present"]);
        runtime.Add("present", new ProbeSystem(
            () => calls.Add("init:present"),
            () => calls.Add("shutdown:present"),
            _ => calls.Add("tick:present")
        ), new object(), "present");
        runtime.Add("other", new ProbeSystem(
            () => calls.Add("init:other"),
            () => calls.Add("shutdown:other"),
            _ => calls.Add("tick:other")
        ), new object(), "other");
        var inputContext = new object();
        runtime.Add("input", new ProbeSystem(
            () => calls.Add("init:input"),
            () => calls.Add("shutdown:input"),
            _ => calls.Add("tick:input")
        ), inputContext, "input");

        True(runtime.Has("input"), "registered system");
        True(ReferenceEquals(inputContext, runtime.GetContext<object>("input")), "registered context");
        runtime.InitAll();
        runtime.Tick(0.25f);
        runtime.ShutdownAll();

        Equal(
            "init:input,init:present,init:other,tick:input,tick:present,tick:other,shutdown:other,shutdown:present,shutdown:input",
            string.Join(',', calls),
            "phase lifecycle order"
        );
    }

    private static void TestSystemInitRollback()
    {
        var calls = new List<string>();
        var runtime = new SystemRuntime();
        runtime.SetPhaseOrder(["first", "second"]);
        runtime.Add("first", new ProbeSystem(
            () => calls.Add("init:first"),
            () => calls.Add("shutdown:first")
        ), new object(), "first");
        runtime.Add("second", new ProbeSystem(
            () =>
            {
                calls.Add("init:second");
                throw new InvalidOperationException("init failed");
            },
            () => calls.Add("shutdown:second")
        ), new object(), "second");

        Throws(runtime.InitAll, "initialization failed");
        Equal(SystemRuntimeState.Stopped, runtime.State, "runtime state after rollback");
        Equal(
            "init:first,init:second,shutdown:second,shutdown:first",
            string.Join(',', calls),
            "rollback order"
        );
    }

    private static void TestSystemTickFaultCleanup()
    {
        var calls = new List<string>();
        var runtime = new SystemRuntime();
        runtime.Add("fault", new ProbeSystem(
            () => calls.Add("init"),
            () => calls.Add("shutdown"),
            _ => throw new InvalidOperationException("tick failed")
        ), new object());

        Throws(() => runtime.Tick(0.1f), "cannot tick");
        runtime.InitAll();
        Throws(() => runtime.Tick(0.1f), "tick failed");
        Equal(SystemRuntimeState.Faulted, runtime.State, "runtime state after tick failure");
        runtime.ShutdownAll();
        Equal(SystemRuntimeState.Stopped, runtime.State, "runtime state after fault cleanup");
        Equal("init,shutdown", string.Join(',', calls), "fault cleanup calls");
    }

    private static void TestSystemShutdownContinues()
    {
        var calls = new List<string>();
        var runtime = new SystemRuntime();
        runtime.Add("first", new ProbeSystem(
            () => calls.Add("init:first"),
            () => calls.Add("shutdown:first")
        ), new object());
        runtime.Add("second", new ProbeSystem(
            () => calls.Add("init:second"),
            () =>
            {
                calls.Add("shutdown:second");
                throw new InvalidOperationException("shutdown failed");
            }
        ), new object());

        runtime.InitAll();
        Throws(runtime.ShutdownAll, "failed to shut down");
        Equal(SystemRuntimeState.Stopped, runtime.State, "runtime state after shutdown error");
        Equal("init:first,init:second,shutdown:second,shutdown:first", string.Join(',', calls), "shutdown order");
        runtime.ShutdownAll();
    }

    private sealed class ProbeSystem(Action init, Action shutdown, Action<float>? tick = null) : ISystem<object>
    {
        public void Init(object context)
        {
            _ = context;
            init();
        }

        public void Tick(float dt)
        {
            tick?.Invoke(dt);
        }

        public void Shutdown()
        {
            shutdown();
        }
    }
}
