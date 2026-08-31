using static TestKit;

static class SystemTests
{
    internal static TestCase[] Cases =>
    [
        new("valid system schema", TestValidSystemSchema),
        new("duplicate core system fails", TestDuplicateCoreSystem),
        new("unknown core phase fails", TestUnknownCorePhase),
        new("generated system name collision fails", TestGeneratedSystemNameCollision),
        new("generated phase name collision fails", TestGeneratedPhaseNameCollision),
        new("project name validation", TestProjectNameValidation),
        new("fw config rejects unknown keys", TestUnknownFwConfigKey),
        new("fw config contains paths", TestFwConfigPathContainment),
        new("fw config validates kit use", TestFwConfigKitUse),
        new("fw check requires a real kit import", TestProjectImport),
        new("fw sync selects kit outputs", TestKitSync),
        new("manifest requires complete outputs", TestManifestOutputSet),
        new("generation batch normalizes text", TestGenerationBatchText),
        new("generation batch rejects conflicting targets", TestGenerationBatchTargets),
        new("generation batch rolls back all outputs", TestGenerationBatchRollback),
    ];

    private static void TestValidSystemSchema()
    {
        WithTempDir(root =>
        {
            Write(root, "scripts/input_system.gd", "extends RefCounted\n");
            Write(root, "scripts/input_context.gd", "extends RefCounted\n");
            Write(root, "scripts/view_system.gd", "extends RefCounted\n");
            Write(root, "scripts/view_context.gd", "extends RefCounted\n");
            var schemaPath = Write(root, "schema/systems.toml", """
                [godot.phases]
                order = ["input", "present"]

                [godot.system.input]
                phase = "input"
                script = "res://scripts/input_system.gd"
                context = "res://scripts/input_context.gd"

                [godot.system.view]
                phase = "present"
                script = "res://scripts/view_system.gd"
                context = "res://scripts/view_context.gd"
                input = "input"

                [core.phases]
                order = ["simulation"]

                [core.system.simulation]
                phase = "simulation"
                type = "SimulationSystem"
                """);

            var schema = SystemSchemaParser.Parse(root, schemaPath);
            Equal(2, schema.Godot.Systems.Count, "godot system count");
            Equal("input", schema.Godot.Systems[1].Refs[0].Target, "godot ref target");
            Equal("SimulationSystem", schema.Core.Systems[0].Type, "core type");
        });
    }

    private static void TestDuplicateCoreSystem()
    {
        WithTempDir(root =>
        {
            var schemaPath = WriteMinimalGodot(root, """
                [core.phases]
                order = ["simulation"]

                [core.system.simulation]
                phase = "simulation"
                type = "SimulationSystem"

                [core.system.simulation]
                phase = "simulation"
                type = "OtherSystem"
                """);
            Throws(() => SystemSchemaParser.Parse(root, schemaPath), "duplicate core system");
        });
    }

    private static void TestUnknownCorePhase()
    {
        WithTempDir(root =>
        {
            var schemaPath = WriteMinimalGodot(root, """
                [core.phases]
                order = ["simulation"]

                [core.system.simulation]
                phase = "missing"
                type = "SimulationSystem"
                """);
            Throws(() => SystemSchemaParser.Parse(root, schemaPath), "outside phases.order");
        });
    }

    private static void TestGeneratedSystemNameCollision()
    {
        WithTempDir(root =>
        {
            var schemaPath = WriteMinimalGodot(root, """
                [core.phases]
                order = ["simulation"]

                [core.system.game_loop]
                phase = "simulation"
                type = "GameLoopSystem"

                [core.system.game__loop]
                phase = "simulation"
                type = "OtherLoopSystem"
                """);
            Throws(() => SystemSchemaParser.Parse(root, schemaPath), "same generated identifier");
        });
    }

    private static void TestGeneratedPhaseNameCollision()
    {
        WithTempDir(root =>
        {
            var schemaPath = WriteMinimalGodot(root, """
                [core.phases]
                order = ["pre_tick", "pre__tick"]

                [core.system.game]
                phase = "pre_tick"
                type = "GameSystem"
                """);
            Throws(() => SystemSchemaParser.Parse(root, schemaPath), "same generated identifier");
        });
    }

    private static void TestProjectNameValidation()
    {
        Craft.ValidateProjectName("valid_game2");
        Throws(() => Craft.ValidateProjectName("2invalid"), "start with a letter");
        Throws(() => Craft.ValidateProjectName("invalid game"), "start with a letter");
        Throws(() => Craft.ValidateProjectName("../escape"), "start with a letter");
    }

    private static void TestUnknownFwConfigKey()
    {
        WithTempDir(root =>
        {
            Write(root, "fw.toml", """
                [project]
                name = "audit"
                typo = "ignored"
                """);
            Throws(() => FwConfig.Load(root), "unsupported fw.toml key");
        });
    }

    private static void TestFwConfigPathContainment()
    {
        WithTempDir(root =>
        {
            Write(root, "fw.toml", """
                [gen]
                csharp = "../outside"

                [use]
                game = []
                host = []
                """);
            var config = FwConfig.Load(root);
            Throws(() => config.GenerationManifestPath(root), "escapes project root");
        });
        WithTempDir(root =>
        {
            Write(root, "fw.toml", """
                [gen]
                fwe = "../outside"

                [use]
                game = []
                host = []
                """);
            var config = FwConfig.Load(root);
            Throws(() => config.ConfigFwePath(root), "escapes project root");
        });
    }

    private static void TestFwConfigKitUse()
    {
        WithTempDir(root =>
        {
            Write(root, "fw.toml", "[project]\nname = \"audit\"\n");
            Throws(() => FwConfig.Load(root), "missing required [use] section");
        });
        WithTempDir(root =>
        {
            Write(root, "fw.toml", """
                [use]
                game = ["app", "anim", "net"]
                host = []
                """);
            var config = FwConfig.Load(root);
            True(config.HasUseSection(), "use section");
            Equal("app,anim,net", string.Join(',', config.GameKits()), "game kits");
            Equal(0, config.HostKits().Count, "empty host kits");
        });
        WithTempDir(root =>
        {
            Write(root, "fw.toml", "[use]\ngame = [\"core\"]\nhost = []\n");
            Throws(() => FwConfig.Load(root), "core is automatic");
        });
        WithTempDir(root =>
        {
            Write(root, "fw.toml", "[use]\ngame = [\"app\", \"app\"]\nhost = []\n");
            Throws(() => FwConfig.Load(root), "repeats kit");
        });
        WithTempDir(root =>
        {
            Write(root, "fw.toml", "[use]\ngame = [\"unknown\"]\n");
            Throws(() => FwConfig.Load(root), "missing [use].host");
        });
        WithTempDir(root =>
        {
            Write(root, "fw.toml", "[use]\ngame = [\"unknown\"]\nhost = []\n");
            Throws(() => FwConfig.Load(root), "unknown kit");
        });
        WithTempDir(root =>
        {
            Write(root, "fw.toml", "[use]\ngame = []\nhost = [\"app\"]\n");
            Throws(() => FwConfig.Load(root), "app is projected only for game");
        });
    }

    private static void TestProjectImport()
    {
        WithTempDir(root =>
        {
            var project = Write(root, "game.csproj", "<Project><!-- csharp/_gen/_fw_game.props --></Project>\n");
            var expected = Path.Combine(root, "csharp/_gen/_fw_game.props");
            True(!FwCheck.ImportsProject(project, expected), "comment is not an import");

            Write(root, "game.csproj", "<Project><Import Project=\"csharp/_gen/_fw_game.props\" /></Project>\n");
            True(FwCheck.ImportsProject(project, expected), "relative import resolves");

            Write(root, "game.csproj", "<Project><Import Project=\"csharp/_gen/_fw_game.props\" Condition=\"false\" /></Project>\n");
            True(!FwCheck.ImportsProject(project, expected), "disabled import is rejected");

            Write(
                root,
                "game.csproj",
                "<Project><Import Project=\"$(MSBuildProjectDirectory)/csharp/_gen/_fw_game.props\" /></Project>\n"
            );
            True(FwCheck.ImportsProject(project, expected), "project directory import resolves");
        });
    }

    private static void TestKitSync()
    {
        WithTempDir(root =>
        {
            Write(root, "fw.toml", """
                [gen]
                csharp = "csharp/_gen"
                [script]
                gdscript = "scripts"
                [dotnet]
                fwgen = "fw/csharp/FwGen/FwGen.csproj"
                [use]
                game = ["app", "anim"]
                host = ["net", "rec"]
                """);
            Write(root, "fw/Directory.Build.props", "<Project />\n");
            Write(root, "fw/csharp/Directory.Build.props", "<Project />\n");
            Write(root, "fw/csharp/FwGen/FwGen.csproj", "<Project />\n");
            Write(root, "fw/csharp/FwGen/source.cs", "class Source {}\n");
            foreach (var project in new[]
            {
                "fw/core/cs/Fw.Core.csproj",
                "fw/kit/anim/cs/Fw.Anim.csproj",
                "fw/kit/net/cs/Fw.Net.csproj",
                "fw/kit/net/cs/lite/Fw.Net.Lite.csproj",
                "fw/kit/rec/cs/Fw.Rec.csproj",
            })
            {
                Write(root, project, "<Project />\n");
            }
            Write(root, "fw/scripts/fw/rt/system/_app_root.gd", "extends Node\n");
            Write(
                root,
                "fw/scripts/fw/vu/ui/_form.gd",
                "extends \"res://fw/scripts/fw/rt/system/_app_root.gd\"\n"
            );
            Write(root, "fw/scripts/fw/vu/animation/_rig.gd", "extends Node3D\n");

            var config = FwConfig.Load(root);
            KitSync.Run(root, config);
            True(File.Exists(Path.Combine(root, "fw/scripts/.gdignore")), "source scripts ignored");

            var gameProps = File.ReadAllText(config.GameKitPropsPath(root));
            var hostProps = File.ReadAllText(config.HostKitPropsPath(root));
            True(gameProps.Contains("Fw.Core.csproj", StringComparison.Ordinal), "game core ref");
            True(gameProps.Contains("Fw.Anim.csproj", StringComparison.Ordinal), "game anim ref");
            True(!gameProps.Contains("Fw.Net.csproj", StringComparison.Ordinal), "game excludes net");
            True(hostProps.Contains("Fw.Net.Lite.csproj", StringComparison.Ordinal), "host net ref");
            True(hostProps.Contains("Fw.Rec.csproj", StringComparison.Ordinal), "host rec ref");
            True(!hostProps.Contains("Fw.Anim.csproj", StringComparison.Ordinal), "host excludes anim");

            var projectedForm = Path.Combine(config.GodotFwDir(root), "fw/vu/ui/_form.gd");
            True(File.Exists(projectedForm), "app projection");
            True(
                File.ReadAllText(projectedForm).Contains("res://scripts/_fw/fw/rt/system/_app_root.gd", StringComparison.Ordinal),
                "projected resource path"
            );
            True(
                File.Exists(Path.Combine(config.GodotFwDir(root), "fw/vu/animation/_rig.gd")),
                "anim projection"
            );

            Write(root, "fw.toml", """
                [gen]
                csharp = "csharp/_gen"
                [script]
                gdscript = "client_scripts"
                [dotnet]
                fwgen = "fw/csharp/FwGen/FwGen.csproj"
                [use]
                game = ["anim"]
                host = []
                """);
            config = FwConfig.Load(root);
            KitSync.Run(root, config);
            True(!File.Exists(projectedForm), "disabled app projection removed");
            var movedProjection = Path.Combine(config.GodotFwDir(root), "fw/vu/animation/_rig.gd");
            True(
                File.Exists(movedProjection),
                "enabled anim projection kept"
            );
            True(
                !File.Exists(Path.Combine(root, "scripts/_fw/fw/vu/animation/_rig.gd")),
                "old projection root removed"
            );
            True(
                File.ReadAllText(config.GameKitPropsPath(root)).Contains("Fw.Anim.csproj", StringComparison.Ordinal),
                "updated game refs"
            );

        });
    }

    private static void TestManifestOutputSet()
    {
        WithTempDir(root =>
        {
            Write(root, "fw.toml", """
                [project]
                name = "audit"
                [schema]
                system = "schema/systems.toml"
                bridge = "schema/bridge"
                config = "schema/config"
                [gen]
                gdscript = "scripts/_gen"
                csharp = "csharp/_gen"
                fwe = "tools/fwe/_gen"
                [data]
                config = "data/config"
                [pack]
                config = "pack/config"
                [script]
                gdscript = "scripts"
                csharp = "csharp"
                [dotnet]
                game = "audit.csproj"
                fwgen = "fw/csharp/FwGen/FwGen.csproj"

                [use]
                game = []
                host = []
                """);
            Write(root, "fw/csharp/FwGen/source.cs", "class Source {}\n");
            Write(root, "fw/csharp/FwGen/FwGen.csproj", "<Project />\n");
            Write(root, "fw/csharp/Directory.Build.props", "<Project />\n");
            Write(root, "fw/Directory.Build.props", "<Project />\n");
            Write(root, "fw/core/cs/Fw.Core.csproj", "<Project />\n");
            Write(root, "fw/kit/.keep", "");
            Write(root, "schema/systems.toml", "systems\n");
            Write(root, "schema/bridge/value.proto", "syntax = \"proto3\";\n");
            Write(root, "schema/config/game.proto", "syntax = \"proto3\";\n");
            Write(root, "data/config/game.csv.txt", "key\ndefault\n");

            var config = FwConfig.Load(root);
            KitSync.Run(root, config);
            var outputs = new[]
            {
                config.GodotSystemsGdPath(root),
                config.CoreSystemsCsPath(root),
                Path.Combine(config.GodotGenDir(root), "_bridge.gd"),
                config.BridgeTypesCsPath(root),
                config.BridgeCodecCsPath(root),
                config.BridgeIntentCodecCsPath(root),
                config.BridgeEventCodecCsPath(root),
                config.BridgePacketCodecCsPath(root),
                config.ConfigGdPath(root),
                config.ConfigContractCsPath(root),
                config.ConfigCodecCsPath(root),
                config.ConfigFwePath(root),
            };
            foreach (var output in outputs)
            {
                Write(root, Path.GetRelativePath(root, output), "generated\n");
            }

            GenerationManifest.UpdateSystem(root, config);
            GenerationManifest.UpdateBridge(root, config);
            GenerationManifest.UpdateConfig(root, config);
            File.WriteAllText(Path.Combine(root, "data/config/game.csv.txt"), "key\ndefault_changed\n");
            GenerationManifest.Verify(root, config);
            var manifestPath = config.GenerationManifestPath(root);
            var model = System.Text.Json.JsonSerializer.Deserialize<GenerationManifestModel>(File.ReadAllText(manifestPath))!;
            model.Commands["bridge"].Outputs.RemoveAt(0);
            File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(model));
            Throws(() => GenerationManifest.Verify(root, config), "output set is incomplete");
        });
    }

    private static void TestGenerationBatchText()
    {
        WithTempDir(root =>
        {
            var path = Path.Combine(root, "out", "value.txt");
            var first = new GenerationBatch(root);
            first.StageText(path, "first  \r\n\r\n\r");
            first.Commit();
            Equal("first\n", File.ReadAllText(path), "normalized CRLF output");
            var second = new GenerationBatch(root);
            second.StageText(path, "second  \n\n\n");
            second.Commit();
            Equal("second\n", File.ReadAllText(path), "normalized LF output");
            Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.fwgen.*").Length, "transaction artifacts");
        });
    }

    private static void TestGenerationBatchRollback()
    {
        WithTempDir(root =>
        {
            var replaced = Write(root, "out/replaced.txt", "old replaced\n");
            var created = Path.Combine(root, "out", "created.txt");
            var deleted = Write(root, "out/deleted.txt", "old deleted\n");
            var failure = Write(root, "out/failure.txt", "old failure\n");
            var applyCount = 0;
            var batch = new GenerationBatch(root, _ =>
            {
                applyCount++;
                if (applyCount == 4)
                {
                    throw new IOException("injected commit failure");
                }
            });
            batch.StageText(replaced, "new replaced\n");
            batch.StageText(created, "new created\n");
            batch.StageDelete(deleted);
            batch.StageDelete(failure);

            Throws<IOException>(batch.Commit, "generation batch failure");
            Equal("old replaced\n", File.ReadAllText(replaced), "replaced output rollback");
            True(!File.Exists(created), "created output rollback");
            Equal("old deleted\n", File.ReadAllText(deleted), "deleted output rollback");
            Equal("old failure\n", File.ReadAllText(failure), "failed output unchanged");
            Equal(0, Directory.GetFiles(Path.Combine(root, "out"), "*.fwgen.*").Length, "transaction artifacts");
        });
    }

    private static void TestGenerationBatchTargets()
    {
        WithTempDir(root =>
        {
            var path = Path.Combine(root, "out", "value.txt");
            var duplicate = new GenerationBatch(root);
            duplicate.StageText(path, "first\n");
            Throws(() => duplicate.StageText(path, "second\n"), "written more than once");

            var conflict = new GenerationBatch(root);
            conflict.StageText(path, "value\n");
            Throws(() => conflict.StageDelete(path), "both written and deleted");

            var escape = new GenerationBatch(root);
            Throws(() => escape.StageText("../outside.txt", "value\n"), "escapes project root");
        });
    }

    private static string WriteMinimalGodot(string root, string coreSchema)
    {
        Write(root, "scripts/game_system.gd", "extends RefCounted\n");
        Write(root, "scripts/game_context.gd", "extends RefCounted\n");
        return Write(root, "schema/systems.toml", """
            [godot.phases]
            order = ["present"]

            [godot.system.game]
            phase = "present"
            script = "res://scripts/game_system.gd"
            context = "res://scripts/game_context.gd"

            """ + coreSchema);
    }
}
