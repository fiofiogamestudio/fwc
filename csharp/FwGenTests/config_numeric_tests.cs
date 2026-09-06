using System.Diagnostics;
using Fw.Rt.Config;
using static TestKit;

static partial class ConfigTests
{
    private const string NumericSchema = """
        syntax = "proto3";
        message Fixed32 {}
        message Nested {
          repeated uint64 ids = 1;
        }
        message GameConfig {
          double precise = 1;
          float single = 2;
          uint32 count = 3;
          int64 low = 4;
          int64 high = 5;
          uint64 id = 6;
          repeated string tags = 7;
          repeated Nested groups = 8;
          Fixed32 fixed_high = 9;
          Fixed32 fixed_fraction = 10;
          repeated string array = 11;
          string numeric_text = 12;
        }
        message ScalarConfig {
          double precise = 1;
          float single = 2;
          uint32 count = 3;
          int64 low = 4;
          uint64 id = 5;
          Fixed32 fixed_high = 6;
          Fixed32 fixed_fraction = 7;
        }
        """;

    private static void WriteNumericFixture(string root)
    {
        WriteProjectConfig(root);
        Write(root, "schema/config/game.proto", NumericSchema);
        string longZero = "1" + new string('0', 4000) + "e-4000";
        string longOnes = "1" + new string('1', 4080) + "e-4080";
        Write(root, "data/config/game.json", $$"""
            [{"key":"default","precise":1e100,"single":0.1,"count":4294967295,
              "low":"-9223372036854775808","high":"9223372036854775807","id":"18446744073709551615",
              "tags":["original"],"groups":[{"ids":["18446744073709551615"]}],"fixed_high":8388607.99609375,"fixed_fraction":1.1},
             {"key":"empty"},
             {"key":"bounds","precise":1.7976931348623157e308,"single":3.4028234663852886e38,"count":0,"low":-9007199254740991,"high":9007199254740991,"id":9007199254740991},
             {"key":"small","precise":5e-324,"single":1.401298464324817e-45},
             {"key":"precision","precise":1.0000000000000002},
             {"key":"single_midpoint","single":1.0000000596046447753906250000000001},
             {"key":"negative_zero","precise":-0.0,"single":"-0","numeric_text":9007199254740991},
             {"key":"fixed_midpoint","fixed_fraction":-0.001953125},
             {"key":"long_zero","precise":"{{longZero}}","single":"{{longZero}}"},
             {"key":"long_ones","precise":"{{longOnes}}","single":"{{longOnes}}"}]
            """);
        Write(root, "data/config/scalar.csv.txt", "key,precise,single,count,low,id,fixed_high,fixed_fraction\ndefault,1e100,0.1,4294967295,-9223372036854775808,18446744073709551615,8388607.99609375,1.1\n");
    }

    private static void TestNumericValidation()
    {
        (string Type, string Value)[] invalid =
        [
            ("uint32", "-1"), ("uint32", "4294967296"), ("uint32", "1.5"), ("uint32", "true"),
            ("int32", "2147483648"), ("int32", "-2147483649"),
            ("int64", "\"9223372036854775808\""), ("int64", "\"-9223372036854775809\""),
            ("int64", "9007199254740992"), ("int64", "1.25"), ("int64", "true"),
            ("uint64", "\"18446744073709551616\""), ("uint64", "-1"), ("uint64", "9007199254740992"),
            ("float", "1e100"), ("float", "\"NaN\""), ("float", "\"Infinity\""), ("float", "true"),
            ("double", "1e400"), ("double", "\"NaN\""), ("double", "\"-Infinity\""), ("double", "true"),
            ("double", "\"Infinity\""), ("float", "\"-Infinity\""),
            ("uint64", "\"-1\""), ("uint64", "1.5"), ("uint64", "true"),
            ("int64", "\"1.5\""), ("uint32", "null"), ("double", "null"),
            ("Fixed32", "8388608"), ("Fixed32", "-8388608.1"), ("Fixed32", "\"NaN\""),
            ("double", System.Text.Json.JsonSerializer.Serialize(new string(' ', 4096) + "1")),
            ("uint32", System.Text.Json.JsonSerializer.Serialize(new string(' ', 4096) + "1")),
            ("Fixed32", System.Text.Json.JsonSerializer.Serialize(new string(' ', 4096) + "1")),
        ];
        foreach (var (type, value) in invalid)
        {
            WithTempDir(root =>
            {
                WriteProjectConfig(root);
                string marker = type == "Fixed32" ? "message Fixed32 {}\n" : "";
                Write(root, "schema/config/game.proto", $"syntax = \"proto3\";\n{marker}message GameConfig {{\n  {type} value = 1;\n}}\n");
                Write(root, "data/config/game.json", $"[{{\"key\":\"default\",\"value\":{value}}}]");
                var config = FwConfig.Load(root);
                ConfigSchema.Resolve(root, config);
                Throws<Exception>(() => ConfigGen.Check(root, config), $"check rejects {type} {value}");
                Throws<Exception>(() => ConfigGen.Pack(root, config), $"pack rejects {type} {value}");
                True(!File.Exists(Path.Combine(root, "pack/config/game.bin")), "invalid numeric data produces no pack");
                // Unsafe JSON integer numbers are deliberately allowed in lossless CSV text.
                if (value == "9007199254740992") return;
                File.Delete(Path.Combine(root, "data/config/game.json"));
                Write(root, "data/config/game.csv.txt", $"key,value\ndefault,{value.Trim('"')}\n");
                Throws<Exception>(() => ConfigGen.Check(root, config), $"CSV check rejects {type} {value}");
                Throws<Exception>(() => ConfigGen.Pack(root, config), $"CSV pack rejects {type} {value}");
            });
        }
        WithTempDir(root =>
        {
            WriteNumericFixture(root);
            var config = FwConfig.Load(root);
            ConfigGen.Check(root, config);
            ConfigGen.Pack(root, config);
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, "pack/config/game.bin"));
            var entry = ConfigPack.Decode(bytes, Convert.ToHexString(bytes.AsSpan(8, 32)))["default"];
            Equal(uint.MaxValue, entry.GetProperty("count").GetUInt32(), "full uint32 range");
            Equal(1e100, entry.GetProperty("precise").GetDouble(), "double pack precision");
            Equal("9223372036854775807", entry.GetProperty("high").GetString(), "int64 pack is lossless text");
            Equal("18446744073709551615", entry.GetProperty("id").GetString(), "uint64 pack is lossless text");
        });
    }

    private static void TestGeneratedNumericRuntime()
    {
        WithTempDir(root =>
        {
            WriteNumericFixture(root);
            var config = FwConfig.Load(root);
            ConfigGen.Generate(root, config);
            ConfigGen.Pack(root, config);
            string coreDll = System.Security.SecurityElement.Escape(typeof(ConfigPack).Assembly.Location)!;
            Write(root, "probe.csproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><RollForward>Major</RollForward><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><Reference Include="Fw.Core"><HintPath>{{coreDll}}</HintPath></Reference></ItemGroup></Project>
                """);
            Write(root, "program.cs", """
                using Audit.Core;
                using System.Collections;
                using System.Text.Json;
                using Fw.Rt.Config;
                var data = ConfigCodec.ReadCoreConfigPack("default");
                Check(data.Precise == 1e100 && double.IsFinite(data.Precise), "double");
                Check(data.Single == 0.1f && data.Count == uint.MaxValue, "float/uint32");
                Check(data.Low == long.MinValue && data.High == long.MaxValue && data.Id == ulong.MaxValue, "64 bit");
                Check(data.Groups[0].Ids[0] == ulong.MaxValue, "nested uint64");
                Check(data.FixedHigh == 8388607.99609375 && data.FixedFraction == 1.1015625, "full Q24.8 range/rounding");
                Check(data.Precise.GetType() == typeof(double) && data.Count.GetType() == typeof(uint), "declared C# types");
                var input = new List<string> { "kept" };
                var custom = new CoreConfig { Tags = input };
                input[0] = "changed";
                Check(custom.Tags[0] == "kept", "init alias detached");
                RejectMutation((IList)data.Tags);
                RejectMutation((IList)data.Array);
                RejectMutation((IList)data.Groups);
                RejectMutation((IList)data.Groups[0].Ids);
                RejectMutation((IList)ConfigPath.AllSourcePaths);
                Check(ConfigCodec.ReadCoreConfigPack("empty").Id == 0, "missing zero");
                var bounds = ConfigCodec.ReadCoreConfigPack("bounds");
                Check(bounds.Precise == double.MaxValue && bounds.Single == float.MaxValue, "finite floating maxima");
                Check(bounds.Low == -9007199254740991L && bounds.High == 9007199254740991L && bounds.Id == 9007199254740991UL, "safe JSON integer endpoints");
                var small = ConfigCodec.ReadCoreConfigPack("small");
                Check(small.Precise == double.Epsilon && small.Single == float.Epsilon, "floating subnormals");
                Check(ConfigCodec.ReadCoreConfigPack("precision").Precise == 1.0000000000000002, "binary64 precision retained");
                Check(BitConverter.SingleToInt32Bits(ConfigCodec.ReadCoreConfigPack("single_midpoint").Single) == 0x3f800001, "direct decimal to binary32 avoids double rounding");
                var negativeZero = ConfigCodec.ReadCoreConfigPack("negative_zero");
                Check(BitConverter.DoubleToInt64Bits(negativeZero.Precise) == long.MinValue && BitConverter.SingleToInt32Bits(negativeZero.Single) == int.MinValue, "signed floating zero");
                Check(negativeZero.NumericText == "9007199254740991", "numeric string field preserves text");
                Check(ConfigCodec.ReadCoreConfigPack("fixed_midpoint").FixedFraction == -0.00390625, "negative Q24.8 tie rounds away from zero");
                var longZero = ConfigCodec.ReadCoreConfigPack("long_zero");
                var longOnes = ConfigCodec.ReadCoreConfigPack("long_ones");
                Check(longZero.Precise == 1.0 && longZero.Single == 1.0f, "long coefficient scale cancellation");
                Check(longOnes.Precise == 10.0 / 9.0 && longOnes.Single == 10.0f / 9.0f, "long significant coefficient rounding");
                var longCsv = ConfigCodec.ReadScalarConfig(new() { ["precise"] = "1" + new string('0', 4000) + "e-4000", ["single"] = "1" + new string('1', 4080) + "e-4080" });
                Check(longCsv.Precise == longZero.Precise && longCsv.Single == longOnes.Single, "generated CSV long decimal normalization");
                Reject(() => ConfigCodec.ReadScalarConfig(new() { ["precise"] = new string(' ', 4096) + "1" }));
                var row = ConfigCodec.ReadScalarConfig(ConfigCodec.ReadRow(ConfigPath.ScalarSource, "default"));
                Check(row.Precise == data.Precise && row.Single == data.Single && row.Count == data.Count && row.Low == data.Low && row.Id == data.Id, "CSV/pack parity");
                Check(row.FixedHigh == data.FixedHigh && row.FixedFraction == data.FixedFraction, "Q24.8 CSV/pack parity");
                foreach (var (field, bad) in new (string,string)[] { ("precise","NaN"), ("single","1e100"), ("count","-1"), ("low","9223372036854775808"), ("id","-1"), ("count","1.5"), ("precise","true") })
                    Reject(() => ConfigCodec.ReadScalarConfig(new() { [field] = bad }));
                byte[] original = File.ReadAllBytes("pack/config/game.bin");
                foreach (var payload in new[] { "{\"precise\":\"NaN\"}", "{\"single\":1e100}", "{\"count\":-1}", "{\"low\":\"9223372036854775808\"}", "{\"id\":-1}", "{\"count\":1.5}", "{\"precise\":true}", "{\"low\":9007199254740992}", "{\"tags\":false}" })
                {
                    File.WriteAllBytes("pack/config/game.bin", ConfigPack.Encode(System.Text.Encoding.UTF8.GetBytes("[{\"key\":\"default\",\"value\":" + payload + "}]"), original.AsSpan(8,32)));
                    ConfigCodec.ClearPackCache();
                    Reject(() => ConfigCodec.ReadCoreConfigPack("default"));
                }
                Console.WriteLine("CONFIG_NUMERIC_CS_OK");
                static void Check(bool value, string label) { if (!value) throw new Exception(label); }
                static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("invalid value accepted"); }
                static void RejectMutation(IList list) { Check(list.IsReadOnly, "read-only view"); try { list.Clear(); } catch (NotSupportedException) { return; } throw new Exception("mutable config"); }
                namespace Godot { public static class FileAccess { public static byte[] GetFileAsBytes(string path) => File.ReadAllBytes(path.Replace("res://", "")); public static string GetFileAsString(string path) => File.ReadAllText(path.Replace("res://", "")); } }
                """);
            string output = RunNumericProcess(root, "dotnet", ["run", "--project", "probe.csproj", "-c", "Release"], 120);
            True(output.Contains("CONFIG_NUMERIC_CS_OK", StringComparison.Ordinal), "generated runtime completion marker");
        });
    }

    private static void TestGeneratedNumericGodot()
    {
        string? godot = Environment.GetEnvironmentVariable("GODOT_BIN");
        if (string.IsNullOrWhiteSpace(godot))
        {
            Console.WriteLine("[skip] config numeric Godot runtime: GODOT_BIN is not set");
            return;
        }
        WithTempDir(root =>
        {
            WriteNumericFixture(root);
            var config = FwConfig.Load(root);
            ConfigGen.Generate(root, config);
            ConfigGen.Pack(root, config);
            WriteNumericBitCases(root);
            Write(root, "project.godot", "config_version=5\n[application]\nconfig/name=\"config-numeric-probe\"\n");
            Write(root, "probe.gd", """
                extends SceneTree
                const Config = preload("res://scripts/_gen/_config.gd")
                func _initialize() -> void:
                    var exact: Dictionary = Config._parse_json_exact(r'{"text":"escaped quote \" and slash \\ and digits 123e-8","key\u0031":"9007199254740992","__fw_numeric_token":"original","__fw_numeric_token_":"also original","n":9007199254740991}', "escaped_numeric_tokens")
                    assert(exact.text == 'escaped quote " and slash \\ and digits 123e-8' and exact.key1 == "9007199254740992")
                    assert(exact.__fw_numeric_token == "original" and exact.__fw_numeric_token_ == "also original")
                    assert(Config._parse_text_long(exact.n, "exact_safe_integer") == 9007199254740991)
                    var text: Dictionary = Config._load_game_text()[0]["value"]
                    var packed: Dictionary = Config._load_game_bin()[0]["value"]
                    assert(text == packed, "JSON source / pack mismatch")
                    assert(text.precise == 1e100 and is_finite(text.precise))
                    assert(text.single == PackedFloat32Array([0.1])[0])
                    assert(text.count == 4294967295)
                    assert(text.low == -9223372036854775807 - 1 and text.high == 9223372036854775807)
                    assert(text.id == "18446744073709551615" and text.groups[0].ids[0] == text.id)
                    assert(text.fixed_high == 8388607.99609375 and text.fixed_fraction == 1.1015625)
                    var csv: Dictionary = Config._load_scalar_text()[0]["value"]
                    var csv_pack: Dictionary = Config._load_scalar_bin()[0]["value"]
                    assert(csv == csv_pack and csv.id == text.id and csv.low == text.low)
                    assert(Config._load_game_text()[1]["value"].id == "0")
                    var bounds: Dictionary = Config._load_game_text()[2]["value"]
                    assert(bounds == Config._load_game_bin()[2]["value"] and is_finite(bounds.precise) and is_finite(bounds.single))
                    assert(bounds.low == -9007199254740991 and bounds.high == 9007199254740991 and bounds.id == "9007199254740991")
                    var small: Dictionary = Config._load_game_text()[3]["value"]
                    assert(small == Config._load_game_bin()[3]["value"] and small.precise > 0 and small.single > 0)
                    assert(Config._load_game_text()[4]["value"].precise == 1.0000000000000002)
                    assert(_double_bits(bounds.precise) == "ffffffffffffef7f")
                    assert(_double_bits(Config._load_game_text()[4]["value"].precise) == "010000000000f03f")
                    assert(_double_bits(small.precise) == "0100000000000000")
                    var single_midpoint: float = Config._load_game_text()[5]["value"].single
                    var single_bits: PackedByteArray = PackedByteArray()
                    single_bits.resize(4)
                    single_bits.encode_float(0, single_midpoint)
                    assert(single_bits.hex_encode() == "0100803f")
                    var negative_zero: Dictionary = Config._load_game_text()[6]["value"]
                    assert(_double_bits(negative_zero.precise) == "0000000000000080")
                    assert(_double_bits(negative_zero.single) == "0000000000000080")
                    assert(negative_zero.numeric_text == "9007199254740991")
                    assert(negative_zero == Config._load_game_bin()[6]["value"])
                    assert(Config._load_game_text()[7]["value"].fixed_fraction == -0.00390625)
                    var long_zero: Dictionary = Config._load_game_text()[8]["value"]
                    var long_ones: Dictionary = Config._load_game_text()[9]["value"]
                    assert(long_zero == Config._load_game_bin()[8]["value"] and long_zero.precise == 1.0 and long_zero.single == 1.0)
                    assert(long_ones == Config._load_game_bin()[9]["value"])
                    var cases: Array = JSON.parse_string(FileAccess.get_file_as_string("res://numeric_bits.json"))
                    for entry in cases:
                        var parsed: float
                        var parsed_bits: PackedByteArray = PackedByteArray()
                        if entry.precision == "double":
                            parsed = Config._decimal_to_double(entry.text, "random_binary64")
                            parsed_bits.resize(8)
                            parsed_bits.encode_double(0, parsed)
                        else:
                            parsed = Config._decimal_to_single(entry.text, "random_binary32")
                            parsed_bits.resize(4)
                            parsed_bits.encode_float(0, parsed)
                        assert(parsed_bits.hex_encode() == entry.bits, "%s %s expected %s got %s" % [entry.precision, entry.text, entry.bits, parsed_bits.hex_encode()])
                    var config: Dictionary = Config.game_default_config()
                    assert(config.is_read_only() and config.tags.is_read_only() and config.groups.is_read_only() and config.groups[0].ids.is_read_only())
                    var all: Array = Config.game_all()
                    assert(all.is_read_only() and all[0].is_read_only() and all[0].value.is_read_only())
                    print("CONFIG_NUMERIC_GD_OK")
                    quit(0)
                func _double_bits(value: float) -> String:
                    var bits: PackedByteArray = PackedByteArray()
                    bits.resize(8)
                    bits.encode_double(0, value)
                    return bits.hex_encode()
                """);
            string output = RunNumericProcess(root, godot, ["--headless", "--path", root, "--script", "probe.gd", "--quit-after", "5"], 30);
            if (!output.Contains("CONFIG_NUMERIC_GD_OK", StringComparison.Ordinal) || output.Contains("ERROR", StringComparison.Ordinal))
                throw new InvalidOperationException($"generated Godot numeric probe did not pass:\n{output}");
            Write(root, "invalid_probe.gd", """
                extends SceneTree
                const Config = preload("res://scripts/_gen/_config.gd")
                func _initialize() -> void:
                    for invalid in ['{"count":01}', '{"count":1.}', '{"count":1e}', '{"count":-}', '{"precise":NaN}', '{"precise":Infinity}', '{"count":+1}']:
                        var previous: int = Config._error_count
                        assert(Config._parse_json_exact(invalid, "numeric_invalid_json") == null)
                        assert(Config._error_count > previous, "invalid numeric token accepted")
                    assert(Config._parse_text_string(Config._parse_json_exact('123', "numeric_string"), "numeric_string") == "123")
                    Config._parse_text_game_config({"count": -1}, "numeric_negative_unsigned")
                    Config._parse_bin_game_config({"count": 4294967296}, "numeric_unsigned_overflow")
                    Config._parse_text_game_config({"count": 1.5}, "numeric_fractional_integer")
                    Config._parse_bin_game_config({"low": "9223372036854775808"}, "numeric_int64_overflow")
                    Config._parse_text_game_config({"id": "18446744073709551616"}, "numeric_uint64_overflow")
                    Config._parse_bin_game_config({"low": 9007199254740992.0}, "numeric_unsafe_integer")
                    Config._parse_text_game_config(Config._parse_json_exact('{"low":9007199254740992}', "raw_big_integer"), "numeric_unsafe_integer")
                    Config._parse_text_game_config({"precise": NAN}, "numeric_nan")
                    Config._parse_bin_game_config({"precise": INF}, "numeric_infinity")
                    Config._parse_text_game_config({"single": 1e100}, "numeric_float_overflow")
                    Config._parse_bin_game_config({"precise": true}, "numeric_boolean")
                    Config._parse_text_game_config({"precise": " ".repeat(4096) + "1"}, "numeric_text_budget")
                    Config._parse_text_game_config({"count": " ".repeat(4096) + "1"}, "numeric_text_budget")
                    var file: FileAccess = FileAccess.open("res://data/config/game.json", FileAccess.WRITE)
                    file.store_string('[{"key":"default","count":-1}]')
                    file.close()
                    assert(Config.game_by_key("default").is_empty(), "invalid data returned a partial default")
                    assert(not Config._game_loaded and Config._game_entries.is_empty(), "invalid data entered the cache")
                    assert(Config.game_default_config().is_empty(), "invalid data fabricated a default")
                    file = FileAccess.open("res://data/config/game.json", FileAccess.WRITE)
                    file.store_string('[{"key":123,"count":7}]')
                    file.close()
                    assert(Config.game_all().is_empty() and not Config._game_loaded, "numeric source key accepted")
                    file = FileAccess.open("res://data/config/game.json", FileAccess.WRITE)
                    file.store_string('[{"key":"default","count":7}]')
                    file.close()
                    assert(Config.game_by_key("default").count == 7 and Config._game_loaded, "failed load poisoned subsequent valid reload")
                    print("CONFIG_NUMERIC_GD_REJECTION_OK")
                    quit(0)
                """);
            string rejected = RunNumericProcess(root, godot, ["--headless", "--path", root, "--script", "invalid_probe.gd", "--quit-after", "5"], 30);
            True(rejected.Contains("CONFIG_NUMERIC_GD_REJECTION_OK", StringComparison.Ordinal), "Godot negative matrix completion");
            foreach (string label in new[] { "negative_unsigned", "unsigned_overflow", "fractional_integer", "int64_overflow", "uint64_overflow", "unsafe_integer", "nan", "infinity", "float_overflow", "boolean", "text_budget" })
                True(rejected.Contains("ERROR: numeric_" + label, StringComparison.Ordinal), $"Godot rejects {label}");
            True(!rejected.Contains("Parse Error", StringComparison.Ordinal), "negative tests run real generated parsers");
        });
    }

    private static void WriteNumericBitCases(string root)
    {
        var random = new Random(90210);
        var cases = new List<object>();
        byte[] bytes = new byte[8];
        for (int index = 0; index < 512; index++)
        {
            random.NextBytes(bytes);
            if (index % 2 == 0)
            {
                double value = BitConverter.ToDouble(bytes);
                if (!double.IsFinite(value)) { index--; continue; }
                cases.Add(new { precision = "double", text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), bits = Convert.ToHexString(bytes).ToLowerInvariant() });
            }
            else
            {
                float value = BitConverter.ToSingle(bytes);
                if (!float.IsFinite(value)) { index--; continue; }
                cases.Add(new { precision = "single", text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), bits = Convert.ToHexString(bytes.AsSpan(0, 4)).ToLowerInvariant() });
            }
        }
        Write(root, "numeric_bits.json", System.Text.Json.JsonSerializer.Serialize(cases));
    }

    private static string RunNumericProcess(string root, string executable, string[] arguments, int timeoutSeconds)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start numeric probe");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"numeric probe timed out: {executable}\n{stdout.Result}\n{stderr.Result}");
        }
        string output = stdout.Result + stderr.Result;
        if (process.ExitCode != 0) throw new InvalidOperationException($"numeric probe exited {process.ExitCode}:\n{output}");
        return output;
    }
}
