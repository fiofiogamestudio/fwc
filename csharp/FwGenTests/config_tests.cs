using Fw.Rt.Config;
using static TestKit;

static class ConfigTests
{
    internal static TestCase[] Cases =>
    [
        new("config pack header", TestConfigPackHeader),
        new("config pack rejects malformed input", TestConfigPackMalformedInput),
        new("config pack rejects every single-byte mutation", TestConfigPackMutationSweep),
        new("invalid Fixed32 marker fails", TestInvalidFixed32Marker),
        new("config enum fields fail early", TestConfigEnumField),
        new("config unsupported scalars fail early", TestUnsupportedConfigScalar),
        new("config rejects generated field collisions", TestGeneratedConfigFieldCollision),
        new("config rejects generated type collisions", TestGeneratedConfigTypeCollision),
        new("config rejects generated parser collisions", TestGeneratedConfigParserCollision),
        new("config rejects enclosing member collisions", TestConfigEnclosingMemberCollision),
        new("config reference validation", TestConfigReferenceValidation),
        new("duplicate config key fails", TestDuplicateConfigKey),
        new("config Godot CSV blank cells use defaults", TestGodotCsvBlankCellsUseDefaults),
        new("config generates FWE schema contract", TestConfigFweContract),
    ];

    private static void TestConfigPackHeader()
    {
        WithTempDir(root =>
        {
            WriteProjectConfig(root);
            Write(root, "schema/config/game.proto", """
                syntax = "proto3";
                message GameConfig {
                  string title = 1;
                }
                """);
            Write(root, "data/config/game.csv.txt", "key,title\ndefault,Audit\n");
            var config = FwConfig.Load(root);
            ConfigGen.Check(root, config);
            ConfigGen.Generate(root, config);
            Write(root, "pack/config/stale.bin", "stale");
            ConfigGen.Pack(root, config);
            True(!File.Exists(Path.Combine(root, "pack/config/stale.bin")), "stale config pack cleanup");

            byte[] bytes = File.ReadAllBytes(Path.Combine(root, "pack/config/game.bin"));
            Equal("WCFG", System.Text.Encoding.ASCII.GetString(bytes, 0, 4), "config pack magic");
            Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)), "config pack version");
            int payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4));
            Equal(bytes.Length - 76, payloadLength, "config pack payload length");
            True(
                System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(76)).AsSpan().SequenceEqual(bytes.AsSpan(44, 32)),
                "config pack checksum"
            );
            string schemaHash = Convert.ToHexString(bytes.AsSpan(8, 32)).ToLowerInvariant();
            Dictionary<string, System.Text.Json.JsonElement> entries = ConfigPack.Decode(bytes, schemaHash);
            True(entries.TryGetValue("default", out var entry), "config pack runtime key");
            Equal("Audit", entry.GetProperty("title").GetString(), "config pack runtime value");
            byte[] tampered = [.. bytes];
            tampered[^1] ^= 0xff;
            Throws(() => ConfigPack.Decode(tampered, schemaHash), "checksum");
            string codec = File.ReadAllText(config.ConfigCodecCsPath(root));
            True(codec.Contains("public static List<string> ReadPackKeys", StringComparison.Ordinal), "config pack keys API");
            True(codec.Contains("ConfigPack.Decode", StringComparison.Ordinal), "shared config pack runtime");
        });
    }

    private static void TestConfigPackMalformedInput()
    {
        byte[] schemaHash = System.Security.Cryptography.SHA256.HashData("schema"u8);
        string schemaHex = Convert.ToHexString(schemaHash);
        byte[] pack = ConfigPack.Encode("[{\"key\":\"default\",\"value\":{}}]"u8, schemaHash);

        Throws(() => ConfigPack.Decode(pack.AsMemory(0, ConfigPack.HeaderSize - 1), schemaHex), "header");
        Throws(() => ConfigPack.Decode(Changed(pack, 0, (byte)'X'), schemaHex), "header");

        byte[] invalidVersion = [.. pack];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(invalidVersion.AsSpan(4, 4), 2);
        Throws(() => ConfigPack.Decode(invalidVersion, schemaHex), "unsupported");
        Throws(() => ConfigPack.Decode(pack, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("other"u8))), "schema hash");

        byte[] invalidLength = [.. pack];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(invalidLength.AsSpan(40, 4), -1);
        Throws(() => ConfigPack.Decode(invalidLength, schemaHex), "payload length");
        Throws(() => ConfigPack.Decode(pack.AsMemory(0, pack.Length - 1), schemaHex), "payload length");

        Throws(() => ConfigPack.Decode(ConfigPack.Encode("not-json"u8, schemaHash), schemaHex), "valid JSON");
        Throws(() => ConfigPack.Decode(ConfigPack.Encode("{}"u8, schemaHash), schemaHex), "must be an array");
        Throws(() => ConfigPack.Decode(ConfigPack.Encode("[{\"key\":\"   \",\"value\":{}}]"u8, schemaHash), schemaHex), "invalid entry");
        Throws(() => ConfigPack.Decode(ConfigPack.Encode("[{\"key\":\"same\",\"value\":1},{\"key\":\"same\",\"value\":2}]"u8, schemaHash), schemaHex), "duplicate key");
    }

    private static void TestConfigPackMutationSweep()
    {
        byte[] schemaHash = System.Security.Cryptography.SHA256.HashData("schema"u8);
        string schemaHex = Convert.ToHexString(schemaHash);
        byte[] pack = ConfigPack.Encode("[{\"key\":\"default\",\"value\":{\"count\":7}}]"u8, schemaHash);

        for (var index = 0; index < pack.Length; index++)
        {
            byte[] changed = [.. pack];
            changed[index] ^= 1;
            int position = index;
            Throws<InvalidDataException>(
                () => ConfigPack.Decode(changed, schemaHex),
                $"config pack mutation at byte {position}"
            );
        }
    }

    private static void TestInvalidFixed32Marker()
    {
        WithTempDir(root =>
        {
            WriteProjectConfig(root);
            Write(root, "schema/config/game.proto", """
                syntax = "proto3";
                message Fixed32 {
                  int32 raw = 1;
                }
                message GameConfig {
                  Fixed32 speed = 1;
                }
                """);
            Write(root, "data/config/game.csv.txt", "key,speed\ndefault,1.0\n");
            var config = FwConfig.Load(root);
            Throws(() => ConfigGen.Check(root, config), "reserved empty marker");
        });
    }

    private static void TestConfigEnumField()
    {
        WithTempDir(root =>
        {
            Write(root, "schema/config/game.proto", """
                syntax = "proto3";
                enum Difficulty {
                  DIFFICULTY_UNSPECIFIED = 0;
                  DIFFICULTY_NORMAL = 1;
                }
                message GameConfig {
                  Difficulty difficulty = 1;
                }
                """);
            Throws(
                () => ConfigSchema.Read(Path.Combine(root, "schema", "config")),
                "config enums are not supported"
            );
        });
    }

    private static void TestUnsupportedConfigScalar()
    {
        WithTempDir(root =>
        {
            Write(root, "schema/config/game.proto", """
                syntax = "proto3";
                message GameConfig {
                  fixed64 seed = 1;
                }
                """);
            Throws(
                () => ConfigSchema.Read(Path.Combine(root, "schema", "config")),
                "unsupported scalar `fixed64`"
            );
        });
    }

    private static void TestGeneratedConfigFieldCollision()
    {
        WithTempDir(root =>
        {
            Write(root, "schema/config/game.proto", """
                syntax = "proto3";
                message GameConfig {
                  string player_id = 1;
                  string player__id = 2;
                }
                """);
            Throws(
                () => ConfigSchema.Read(Path.Combine(root, "schema", "config")),
                "same generated identifier"
            );
        });
    }

    private static void TestGeneratedConfigTypeCollision()
    {
        WithTempDir(root =>
        {
            Write(root, "schema/config/game.proto", """
                syntax = "proto3";
                message GameConfig {
                  string title = 1;
                }
                message CoreConfig {
                  string name = 1;
                }
                """);
            Throws(
                () => ConfigSchema.Read(Path.Combine(root, "schema", "config")),
                "same generated identifier"
            );
        });
    }

    private static void TestGeneratedConfigParserCollision()
    {
        WithTempDir(root =>
        {
            Write(root, "schema/config/game.proto", """
                syntax = "proto3";
                message Int {
                  int32 value = 1;
                }
                """);
            Throws(
                () => ConfigSchema.Read(Path.Combine(root, "schema", "config")),
                "same generated identifier"
            );
        });
    }

    private static void TestConfigEnclosingMemberCollision()
    {
        WithTempDir(root =>
        {
            Write(root, "schema/config/item.proto", """
                syntax = "proto3";
                message Item {
                  string item = 1;
                }
                """);
            Throws(
                () => ConfigSchema.Read(Path.Combine(root, "schema", "config")),
                "same generated identifier"
            );
        });
    }

    private static void TestConfigReferenceValidation()
    {
        WithTempDir(root =>
        {
            WriteProjectConfig(root);
            Write(root, "schema/config/player.proto", """
                syntax = "proto3";
                package audit.config;
                message PlayerConfig {
                  uint32 speed = 1;
                }
                """);
            Write(root, "schema/config/game.proto", """
                syntax = "proto3";
                package audit.config;
                import "schema/config/player.proto";
                message GameConfig {
                  PlayerConfig player = 1;
                }
                """);
            Write(root, "data/config/player.csv.txt", "key,speed\ndefault,10\n");
            Write(root, "data/config/game.csv.txt", "key,player\ndefault,missing\n");
            Throws(() => ConfigGen.Check(root, FwConfig.Load(root)), "references missing PlayerConfig key");
        });
    }

    private static void TestDuplicateConfigKey()
    {
        WithTempDir(root =>
        {
            WriteProjectConfig(root);
            Write(root, "schema/config/game.proto", """
                syntax = "proto3";
                message GameConfig {
                  string title = 1;
                }
                """);
            Write(root, "data/config/game.csv.txt", "key,title\ndefault,First\ndefault,Second\n");
            Throws(() => ConfigGen.Check(root, FwConfig.Load(root)), "duplicate key");
        });
    }

    private static void TestGodotCsvBlankCellsUseDefaults()
    {
        WithTempDir(root =>
        {
            WriteProjectConfig(root);
            Write(root, "schema/config/game.proto", """
                syntax = "proto3";
                message Fixed32 {}
                message GameConfig {
                  uint32 count = 1;
                  Fixed32 radius = 2;
                  bool enabled = 3;
                }
                """);
            Write(root, "data/config/game.csv.txt", "key,count,radius,enabled\ndefault,,,\n");

            var config = FwConfig.Load(root);
            ConfigGen.Check(root, config);
            ConfigGen.Generate(root, config);

            string generated = File.ReadAllText(config.ConfigGdPath(root));
            True(
                generated.Contains(
                    "static func _has_csv_value(row: Dictionary, field: String) -> bool:",
                    StringComparison.Ordinal
                ),
                "Godot config CSV value helper"
            );
            True(
                generated.Contains(
                    "if _has_csv_value(row, \"radius\") else 0.0",
                    StringComparison.Ordinal
                ),
                "blank Fixed32 cell uses zero default"
            );
            True(
                generated.Contains(
                    "if _has_csv_value(row, \"enabled\") else false",
                    StringComparison.Ordinal
                ),
                "blank bool cell uses false default"
            );
            True(
                generated.Contains(
                    "var text: String = str(value).strip_edges()",
                    StringComparison.Ordinal
                ),
                "numeric text trims surrounding whitespace"
            );
        });
    }

    private static void TestConfigFweContract()
    {
        WithTempDir(root =>
        {
            Write(root, "fw.toml", """
                [project]
                name = "audit"
                [gen]
                fwe = "tools/fwe/_gen"
                """);
            Write(root, "schema/config/item.proto", """
                syntax = "proto3";
                message Fixed32 {}
                message ItemConfig {
                  string display_name = 1;
                  uint32 count = 2;
                  Fixed32 weight = 3;
                }
                message GameConfig {
                  ItemConfig initial_item = 1;
                  repeated string tags = 2;
                }
                """);
            Write(root, "data/config/item.csv.txt", "key,display_name,count,weight\ndefault,Item,1,1.5\n");
            Write(root, "data/config/game.json", """
                [
                  {
                    "key": "default",
                    "initial_item": "default",
                    "tags": ["audit"]
                  }
                ]
                """);

            var config = FwConfig.Load(root);
            ConfigGen.Generate(root, config);

            using var document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(config.ConfigFwePath(root))
            );
            var contract = document.RootElement;
            Equal(1, contract.GetProperty("format").GetInt32(), "FWE contract format");
            True(contract.GetProperty("schemaHash").GetString()?.Length == 64, "FWE schema hash");
            var item = contract.GetProperty("roots").GetProperty("item");
            Equal("csv", item.GetProperty("format").GetString(), "FWE csv format");
            Equal("key", item.GetProperty("headers")[0].GetString(), "FWE synthetic key");
            Equal("int", item.GetProperty("fields")[1].GetProperty("editorType").GetString(), "FWE integer type");
            Equal("number", item.GetProperty("fields")[2].GetProperty("editorType").GetString(), "FWE fixed type");
            var game = contract.GetProperty("roots").GetProperty("game");
            Equal("json", game.GetProperty("format").GetString(), "FWE json format");
            Equal(
                "item",
                game.GetProperty("fields")[0].GetProperty("reference").GetString(),
                "FWE config reference"
            );
            Equal("array", game.GetProperty("fields")[1].GetProperty("editorType").GetString(), "FWE array type");
            string codec = File.ReadAllText(config.ConfigCodecCsPath(root));
            True(
                codec.Contains(
                    "ReadPackScalarArray<string>(obj, ConfigField.Tags, value => ReadPackStringItem(value, \"\"))",
                    StringComparison.Ordinal
                ),
                "repeated scalar pack parser supplies type and fallback"
            );
        });
    }

}
