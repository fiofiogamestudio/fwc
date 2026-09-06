[project]
name = "__PROJECT_NAME__"

[schema]
system = "schema/systems.toml"
bridge = "schema/bridge"
config = "schema/config"

[gen]
gdscript = "scripts/_gen"
csharp = "csharp/_gen"

[data]
config = "data/config"

[pack]
config = "pack/config"

[script]
gdscript = "scripts"
csharp = "csharp"

[use]
game = ["app", "anim", "net", "rec"]
host = ["anim", "net", "rec", "ai"]

[dotnet]
game = "__PROJECT_NAME__.csproj"
fwgen = "__FW_PATH__/csharp/FwGen/FwGen.csproj"
