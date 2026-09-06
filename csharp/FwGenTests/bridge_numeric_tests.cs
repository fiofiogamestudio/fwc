using System.Diagnostics;
using static TestKit;

static partial class BridgeTests
{
    private static void TestBridgeNumericAliasShapes()
    {
        foreach (string declaration in new[] { "message ItemId {\n  string label = 1;\n}\n", "enum ItemId {\n  ITEM_UNSPECIFIED = 0;\n}\n" })
        {
            WithTempDir(root =>
            {
                WriteProjectConfig(root);
                WriteVersionSchema(root, "message GameView {\n  uint32 count = 1;\n}\n");
                var config = FwConfig.Load(root);
                BridgeGen.Generate(root, config);
                string[] outputs = [ config.BridgeTypesCsPath(root), config.BridgeCodecCsPath(root), config.BridgeIntentCodecCsPath(root), config.BridgeEventCodecCsPath(root), config.BridgePacketCodecCsPath(root), Path.Combine(config.GodotGenDir(root), "_bridge.gd") ];
                var before = outputs.ToDictionary(path => path, File.ReadAllText);
                const string header = "syntax = \"proto3\";\npackage audit.bridge;\n";
                Write(root, "schema/bridge/value.proto", header + declaration);
                Throws(() => BridgeGen.Generate(root, config), "alias");
                foreach (var (path, content) in before)
                    Equal(content, File.ReadAllText(path), "invalid alias preserves generated artifact");
            });
        }
    }

    private const string BridgeNumericFields = """
          double precise = 1;
          float single = 2;
          uint32 count = 3;
          int64 low = 4;
          sint64 high = 5;
          uint64 id = 6;
          int32 small = 7;
          sint32 signed = 8;
          repeated double precisions = 9;
          repeated float singles = 10;
          repeated uint32 counts = 11;
          repeated int64 lows = 12;
          repeated sint64 highs = 13;
          repeated uint64 ids = 14;
          repeated int32 smalls = 15;
          repeated sint32 signeds = 16;
        """;

    private static void TestBridgeNumericRuntime()
    {
        string? godot = Environment.GetEnvironmentVariable("GODOT_BIN");
        if (string.IsNullOrWhiteSpace(godot))
        {
            Console.WriteLine("SKIP real bridge numeric runtime: GODOT_BIN is not set");
            return;
        }
        WithTempDir(root =>
        {
            WriteProjectConfig(root);
            const string header = "syntax = \"proto3\";\npackage audit.bridge;\n";
            Write(root, "schema/bridge/value.proto", header + "message PlayerId {}\nmessage EntityId {}\nmessage ItemId {}\n");
            Write(root, "schema/bridge/intent.proto", header + "message MeasureIntent {\n" + BridgeNumericFields + "\n}\n" + """
                message SignedIntent {
                  int64 mixed = 1;
                  repeated int64 mixed_values = 2;
                }
                message UnsignedIntent {
                  uint64 mixed = 1;
                  repeated uint64 mixed_values = 2;
                }
                message AliasIntent {
                  PlayerId mixed = 1;
                  repeated PlayerId mixed_values = 2;
                  EntityId entity = 3;
                  ItemId item = 4;
                }
                message System {
                  uint64 safe_id = 1;
                }
                message GameAction {
                  oneof kind {
                    MeasureIntent measure = 1;
                    SignedIntent signed_value = 2;
                    UnsignedIntent unsigned_value = 3;
                    System system_value = 4;
                    AliasIntent alias_value = 5;
                  }
                }
                message GameIntent {
                  GameAction action = 1;
                  uint32 client_tick = 2;
                }
                """);
            Write(root, "schema/bridge/view.proto", header + "message GameView {\n" + BridgeNumericFields + "\n}\n" + """
                message BridgeView {
                  uint64 shadow_id = 1;
                }
                message NumericView {
                  uint32 shadow_count = 1;
                }
                """);
            Write(root, "schema/bridge/event.proto", header + "message MeasuredEvent {\n" + BridgeNumericFields + "\n}\n" + """
                message SignedUnionEvent {
                  int64 mixed = 1;
                  repeated int64 mixed_values = 2;
                }
                message UnsignedUnionEvent {
                  uint64 mixed = 1;
                  repeated uint64 mixed_values = 2;
                }
                message AliasUnionEvent {
                  PlayerId mixed = 1;
                  repeated PlayerId mixed_values = 2;
                  EntityId entity = 3;
                  ItemId item = 4;
                }
                message BridgeEvent {
                  uint64 shadow_id = 1;
                }
                message GameEvent {
                  oneof payload {
                    MeasuredEvent measured = 1;
                    SignedUnionEvent signed_value = 2;
                    UnsignedUnionEvent unsigned_value = 3;
                    AliasUnionEvent alias_value = 4;
                    BridgeEvent bridge_value = 5;
                  }
                }
                """);
            Write(root, "schema/bridge/packet.proto", header + "message NumericPacket {\n" + BridgeNumericFields + "\n}\n" + """
                message AliasesPacket {
                  PlayerId player = 1;
                  EntityId entity = 2;
                  ItemId item = 3;
                  repeated PlayerId players = 4;
                }
                enum PacketType {
                  PACKET_TYPE_UNSPECIFIED = 0;
                  PACKET_TYPE_NUMERIC = 1;
                  PACKET_TYPE_ALIASES = 2;
                }
                message Packet {
                  PacketType type = 1;
                  uint32 protocol_version = 2;
                  string session_token = 3;
                  oneof payload {
                    NumericPacket numeric = 4;
                    AliasesPacket aliases = 5;
                  }
                }
                """);
            var config = FwConfig.Load(root);
            BridgeGen.Generate(root, config);
            Write(root, "BridgeNumericProbe.csproj", """
                <Project Sdk="Godot.NET.Sdk/4.6.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>
                """);
            Write(root, "project.godot", """
                config_version=5
                [application]
                config/name="Bridge Numeric Probe"
                [dotnet]
                project/assembly_name="BridgeNumericProbe"
                [rendering]
                renderer/rendering_method="gl_compatibility"
                """);
            Write(root, "Probe.cs", BridgeNumericCsProbe);
            Write(root, "probe.gd", BridgeNumericGdProbe);
            string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            RunBridgeNumericProcess(root, dotnet, ["build", "BridgeNumericProbe.csproj", "--nologo"], 90);
            RunBridgeNumericProcess(root, godot, ["--headless", "--path", root, "--editor", "--import"], 45);
            string output = RunBridgeNumericProcess(root, godot, ["--headless", "--path", root, "--script", "probe.gd", "--quit-after", "5"], 30);
            True(output.Contains("BRIDGE_NUMERIC_CS_OK", StringComparison.Ordinal), "real C# numeric matrix completes: " + output);
            True(output.Contains("BRIDGE_NUMERIC_GD_OK", StringComparison.Ordinal), "Godot/C# numeric round trip completes: " + output);
            True(!output.Contains("ERROR", StringComparison.Ordinal), "positive numeric runtime has no errors: " + output);
            Write(root, "invalid.gd", BridgeNumericInvalidGdProbe);
            output = RunBridgeNumericProcess(root, godot, ["--headless", "--path", root, "--script", "invalid.gd", "--quit-after", "5"], 30);
            True(output.Contains("BRIDGE_NUMERIC_REJECT_OK", StringComparison.Ordinal), "Godot invalid numeric matrix completes: " + output);
            True(!output.Contains("SCRIPT ERROR", StringComparison.Ordinal), "invalid values rejected by codec, not script failure");
        });
    }

    private const string BridgeNumericCsProbe = """
        using Godot;
        using Audit.Core;
        using Audit.Bridge;
        using GdArray = Godot.Collections.Array;
        using GdDictionary = Godot.Collections.Dictionary;

        public partial class Probe : RefCounted
        {
            private static void Check(bool value, string label)
            {
                if (!value) throw new InvalidOperationException(label);
            }
            private static void Reject(Action action)
            {
                try { action(); }
                catch (FormatException) { return; }
                catch (OverflowException) { return; }
                throw new InvalidOperationException("invalid numeric input was accepted");
            }
            public GdDictionary Echo(GdDictionary action)
            {
                var decoded = IntentCodec.DecodeOne(0, new() { ["action"] = action, ["client_tick"] = (long)uint.MaxValue });
                Check(decoded.ClientTick == uint.MaxValue, "root uint32 does not wrap");
                var input = decoded.Action!.As<MeasureIntent>()!;
                var output = new MeasuredEvent();
                foreach (var property in typeof(MeasureIntent).GetProperties())
                    typeof(MeasuredEvent).GetProperty(property.Name)!.SetValue(output, property.GetValue(input));
                return EventCodec.Encode([new CoreEvent { Type = CoreEvent.Measured, Payload = output }])[0].AsGodotDictionary();
            }
            public void RunChecks()
            {
                Check(typeof(MeasureIntent).GetProperty("Precise")!.PropertyType == typeof(double), "double type");
                Check(typeof(MeasureIntent).GetProperty("Count")!.PropertyType == typeof(uint), "uint32 type");
                Check(typeof(MeasureIntent).GetProperty("Low")!.PropertyType == typeof(long), "int64 type");
                Check(typeof(MeasureIntent).GetProperty("Id")!.PropertyType == typeof(ulong), "uint64 type");
                foreach (double number in new[] { double.Epsilon, double.MaxValue, -double.MaxValue, 1.0000000000000002, -0.0 })
                    Check(BitConverter.DoubleToInt64Bits(BridgeCodec.RequireDouble(BridgeCodec.EncodeDouble(number))) == BitConverter.DoubleToInt64Bits(number), "binary64 bits");
                foreach (float number in new[] { float.Epsilon, float.MaxValue, -float.MaxValue, 0.1f, -0.0f })
                    Check(BitConverter.SingleToInt32Bits(BridgeCodec.RequireFloat(BridgeCodec.EncodeFloat(number))) == BitConverter.SingleToInt32Bits(number), "binary32 bits");
                Check(BridgeCodec.ReadULong(new(), "id") == 0 && BridgeCodec.ReadLong(new(), "low") == 0, "missing defaults");
                foreach (Variant invalid in new Variant[] { -1L, 1.5, true, "00", "+1", "-1", "18446744073709551616", " 1", "" })
                    Reject(() => BridgeCodec.RequireULong(invalid));
                foreach (Variant invalid in new Variant[] { -1L, 4294967296L, 1.5, true, "1" })
                    Reject(() => BridgeCodec.RequireUInt(invalid));
                foreach (Variant invalid in new Variant[] { 2147483648L, -2147483649L, 1.5, true })
                    Reject(() => BridgeCodec.RequireInt(invalid));
                foreach (Variant invalid in new Variant[] { 1.5, true, "9223372036854775807" })
                    Reject(() => BridgeCodec.RequireLong(invalid));
                foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    Reject(() => BridgeCodec.RequireDouble(invalid));
                    Reject(() => BridgeCodec.EncodeDouble(invalid));
                }
                Reject(() => BridgeCodec.RequireFloat(1e100));
                Reject(() => BridgeCodec.RequireDouble(true));
                Reject(() => BridgeCodec.EncodeFloat(float.NaN));
                var packet = PacketCodec.Numeric(1e100, 0.1f, uint.MaxValue, long.MinValue, long.MaxValue, ulong.MaxValue, int.MinValue, int.MaxValue,
                    new() { double.Epsilon, double.MaxValue }, new() { float.Epsilon, float.MaxValue }, new() { 0L, (long)uint.MaxValue },
                    new() { long.MinValue }, new() { long.MaxValue }, new() { "0", "18446744073709551615" }, new() { int.MinValue }, new() { int.MaxValue });
                Check(PacketCodec.TryReadNumeric(packet, out double precise, out float single, out uint count, out long low, out long high, out ulong id, out int small, out int signed,
                    out GdArray precisions, out GdArray singles, out GdArray counts, out GdArray lows, out GdArray highs, out GdArray ids, out GdArray smalls, out GdArray signeds), "packet accepted");
                Check(precise == 1e100 && single == 0.1f && count == uint.MaxValue && low == long.MinValue && high == long.MaxValue && id == ulong.MaxValue && small == int.MinValue && signed == int.MaxValue, "packet scalars");
                Check(precisions[0].AsDouble() == double.Epsilon && singles[0].AsDouble() == float.Epsilon && counts[1].AsInt64() == uint.MaxValue && lows[0].AsInt64() == long.MinValue && highs[0].AsInt64() == long.MaxValue && ids[1].AsString() == "18446744073709551615" && smalls[0].AsInt64() == int.MinValue && signeds[0].AsInt64() == int.MaxValue, "packet arrays");
                var payload = packet["numeric"].AsGodotDictionary();
                foreach (var (field, invalid) in new (string, Variant)[] { ("id", "01"), ("count", -1L), ("low", 1.5), ("small", 2147483648L), ("precise", double.NaN), ("single", 1e100), ("ids", new GdArray { "01" }), ("counts", new GdArray { -1L }), ("precisions", new GdArray { true }) })
                {
                    Variant original = payload[field];
                    payload[field] = invalid;
                    Check(!ReadPacket(packet), "packet invalid " + field);
                    payload[field] = original;
                    var action = payload.Duplicate(); action["kind"] = "measure";
                    action[field] = invalid;
                    Reject(() => IntentCodec.DecodeOne(0, new() { ["action"] = action }));
                }
                packet["protocol_version"] = (long)BridgeCodec.ProtocolVersion + (1L << 32);
                Check(!BridgeCodec.IsProtocolSupported(packet) && !ReadPacket(packet), "protocol cannot truncate");
                foreach (var (kind, value, expected) in new (string, Variant, decimal)[] { ("signed_value", long.MinValue, long.MinValue), ("unsigned_value", "18446744073709551615", ulong.MaxValue) })
                {
                    var decoded = IntentCodec.DecodeOne(0, new() { ["action"] = new GdDictionary { ["kind"] = kind, ["mixed"] = value, ["mixed_values"] = new GdArray { value } } }).Action!;
                    Check(decoded.Mixed == expected && decoded.MixedValues[0] == expected, "mixed aggregate does not truncate");
                    var encoded = EventCodec.Encode([new CoreEvent { Type = kind == "signed_value" ? CoreEvent.SignedUnion : CoreEvent.UnsignedUnion, Mixed = expected, MixedValues = new() { expected } }])[0].AsGodotDictionary();
                    Check(encoded["mixed"].VariantType == value.VariantType && (kind == "signed_value" ? encoded["mixed"].AsInt64() == value.AsInt64() : encoded["mixed"].AsString() == value.AsString()), "mixed fallback uses concrete wire type");
                    Check(encoded["mixed_values"].AsGodotArray()[0].VariantType == value.VariantType, "mixed repeated fallback wire type");
                }
                var aliases = PacketCodec.Aliases(long.MaxValue, int.MaxValue, int.MinValue, new() { long.MinValue, long.MaxValue });
                Check(PacketCodec.TryReadAliases(aliases, out long player, out int entity, out int item, out GdArray players) && player == long.MaxValue && entity == int.MaxValue && item == int.MinValue && players[0].AsInt64() == long.MinValue, "ID alias packet preserves ranges");
                aliases["aliases"].AsGodotDictionary()["player"] = 1.5;
                Check(!PacketCodec.TryReadAliases(aliases, out _, out _, out _, out _), "ID alias packet rejects fractions");
                var alias = IntentCodec.DecodeOne(0, new() { ["action"] = new GdDictionary { ["kind"] = "alias_value", ["mixed"] = long.MaxValue, ["mixed_values"] = new GdArray { long.MinValue }, ["entity"] = int.MaxValue, ["item"] = int.MinValue } }).Action!;
                Check(alias.As<AliasIntent>()!.Mixed == long.MaxValue && alias.Mixed == long.MaxValue && alias.MixedValues[0] == long.MinValue, "mixed PlayerId/uint64 alias union");
                var aliasOutput = EventCodec.Encode([new CoreEvent { Type = CoreEvent.AliasUnion, Mixed = alias.Mixed, MixedValues = alias.MixedValues, Entity = alias.Entity, Item = alias.Item }])[0].AsGodotDictionary();
                Check(aliasOutput["mixed"].AsInt64() == long.MaxValue && aliasOutput["mixed_values"].AsGodotArray()[0].AsInt64() == long.MinValue, "alias union fallback round trip");
                Reject(() => EventCodec.Encode([new CoreEvent { Type = CoreEvent.SignedUnion, Mixed = ulong.MaxValue }]));
                Reject(() => EventCodec.Encode([new CoreEvent { Type = CoreEvent.UnsignedUnion, Mixed = -1 }]));
                Reject(() => EventCodec.Encode([new CoreEvent { Type = CoreEvent.SignedUnion, Mixed = 1.5m }]));
                Reject(() => EventCodec.Encode([new CoreEvent { Type = CoreEvent.UnsignedUnion, Mixed = 1.5m }]));
                Reject(() => EventCodec.Encode([new CoreEvent { Type = CoreEvent.AliasUnion, Mixed = ulong.MaxValue }]));
                Reject(() => EventCodec.Encode([new CoreEvent { Type = CoreEvent.AliasUnion, MixedValues = new() { 1.5m } }]));
                GD.Print("BRIDGE_NUMERIC_CS_OK");
            }
            public bool HasAction(GdDictionary action) => IntentCodec.DecodeOne(0, new() { ["action"] = action }).Action != null;
            private static bool ReadPacket(GdDictionary packet) => PacketCodec.TryReadNumeric(packet, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        }
        """;

    private const string BridgeNumericGdProbe = """
        extends SceneTree
        const B = preload("res://scripts/_gen/_bridge.gd")
        const Cs = preload("res://Probe.cs")
        func _initialize() -> void:
            var cs = Cs.new()
            cs.RunChecks()
            var bits := PackedByteArray([1, 0, 0, 0, 0, 0, 0, 0])
            var tiny: float = bits.decode_double(0)
            var action: Dictionary = B.Intent.measure(1e100, 0.1, 4294967295, -9223372036854775807 - 1, 9223372036854775807, "18446744073709551615", -2147483648, 2147483647,
                [tiny, 1.0000000000000002], [0.1], [4294967295], [-9223372036854775807 - 1], [9223372036854775807], ["0", "18446744073709551615"], [-2147483648], [2147483647])
            var output: Dictionary = cs.Echo(action)
            var ev = B.Event.Measured.wrap(output)
            var view = B.View.Game.wrap(output)
            for value in [ev, view]:
                assert(value.precise() == 1e100)
                assert(value.single() == PackedFloat32Array([0.1])[0])
                assert(value.count() == 4294967295)
                assert(value.low() == -9223372036854775807 - 1)
                assert(value.high() == 9223372036854775807)
                assert(value.id() == "18446744073709551615")
                assert(value.small() == -2147483648 and value.signed() == 2147483647)
                assert(value.precisions() == [tiny, 1.0000000000000002])
                assert(value.singles() == [PackedFloat32Array([0.1])[0]])
                assert(value.counts() == [4294967295])
                assert(value.lows() == [-9223372036854775807 - 1])
                assert(value.highs() == [9223372036854775807])
                assert(value.ids() == ["0", "18446744073709551615"])
                assert(value.smalls() == [-2147483648] and value.signeds() == [2147483647])
            var empty = B.View.Game.wrap({})
            assert(empty.id() == "0" and empty.low() == 0 and empty.precise() == 0.0 and empty.ids().is_empty())
            var alias: Dictionary = B.Intent.alias_value(9223372036854775807, [-9223372036854775807 - 1], 2147483647, -2147483648)
            assert(cs.HasAction(alias) and alias.mixed == 9223372036854775807 and alias.mixed_values == [-9223372036854775807 - 1])
            assert(B.View.BridgeView.wrap({"shadow_id": "18446744073709551615"}).shadow_id() == "18446744073709551615")
            assert(B.Event.BridgeEvent.wrap({"shadow_id": "18446744073709551615"}).shadow_id() == "18446744073709551615")
            assert(B.View.Numeric.wrap({"shadow_count": 4294967295}).shadow_count() == 4294967295)
            print("BRIDGE_NUMERIC_GD_OK")
            quit(0)
        """;

    private const string BridgeNumericInvalidGdProbe = """
        extends SceneTree
        const B = preload("res://scripts/_gen/_bridge.gd")
        const Cs = preload("res://Probe.cs")
        func _initialize() -> void:
            var cs = Cs.new()
            for value in ["", "01", "+1", "-1", " 1", "18446744073709551616", 1, true, 1.5]:
                assert(B.Intent.unsigned_value(value, []).is_empty())
                assert(B.View.Game.wrap({"id": value}) == null)
            for value in [1.5, true, "1"]:
                var rejected: Dictionary = B.Intent.signed_value(value, [])
                assert(rejected.is_empty() and not cs.HasAction(rejected))
                rejected = B.Intent.alias_value(value, [], 0, 0)
                assert(rejected.is_empty() and not cs.HasAction(rejected))
            var fractional_count: Dictionary = B.Intent.measure(0.0, 0.0, 1.5, 0, 0, "0", 0, 0, [], [], [], [], [], [], [], [])
            assert(fractional_count.is_empty() and not cs.HasAction(fractional_count))
            for entry in [["count", -1], ["count", 4294967296], ["count", 1.5], ["small", 2147483648], ["single", 1e100], ["precise", INF], ["precise", NAN], ["precise", true], ["ids", ["01"]], ["lows", [1.5]], ["counts", true]]:
                assert(B.View.Game.wrap({entry[0]: entry[1]}) == null)
                assert(B.Event.Measured.wrap({entry[0]: entry[1]}) == null)
            print("BRIDGE_NUMERIC_REJECT_OK")
            quit(0)
        """;

    private static string RunBridgeNumericProcess(string root, string executable, string[] arguments, int timeoutSeconds)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start bridge numeric probe");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(10000)) throw new TimeoutException("bridge numeric probe did not stop");
            throw new TimeoutException($"bridge numeric probe timed out: {executable}");
        }
        string output = stdout.Result + stderr.Result;
        if (process.ExitCode != 0) throw new InvalidOperationException($"bridge numeric probe exited {process.ExitCode}:\n{output}");
        return output;
    }
}
