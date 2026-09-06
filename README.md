# FWC

FWC 是 Godot + C# 的代码框架，仓库为 `fiofiogamestudio/fwc`。新工程默认安装在 `fwc/`，可由顶层 FW 统一初始化，也可独立安装。

安装位置与代码合同分开：`fw.toml`、`Fw.*` 命名空间、生成协议和 `scripts/_fw/fw` 投影不因组件目录改变。FWC 不依赖 FW、FWE、FWA 或 FWS 才能生成和运行游戏。

它提供四类能力：
- Godot 运行时骨架：`AppRoot -> BaseMode -> SystemManager`
- Godot UI / View 子框架：`FUI`、`FForm`、`FWidget`、`FViewRoot`、`FViewStore`、`FRefs`、`FProps`、`FBinding`、`FViewModel`，以及程序姿态采样与人形两段 IK
- 通用运行时服务：资源、本地化、对象池、事件、状态机、日志、2D/3D 音频、显示、调试开关，以及可替换 transport 的 `Fw.Rt.Net`
- C# 工具链：`fwc/tools/new.*`、`gen.*`、`build.*`、`test.*`

框架维护文档在：
- `fwc/docs/rule.md`
- `fwc/docs/spec.md`
- `fwc/docs/use.md`

## 接入前提
- Godot 4.6.2 .NET
- .NET SDK 10.0.201（由 `global.json` 固定）
- 已有 Godot 工程，或允许模板创建最小 `project.godot`
- 框架位于工程内；默认 `fwc/`，显式路径也支持嵌套目录和空格
- 游戏核心逻辑使用 Godot C# 项目承载

目录形态：

```text
<game_root>/
  fwc/
  project.godot
  <game>.csproj
```

## 快速开始
在已有 Git 游戏工程中添加框架：

```powershell
git submodule add https://github.com/fiofiogamestudio/fwc.git fwc
```

初始化最小工程骨架：

```powershell
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\fwc\tools\new.ps1 -ProjectRoot . -Name MyGame
```

或：

```bash
bash ./fwc/tools/new.sh --project-root . --name MyGame
```

生成和构建：

```powershell
.\fwc\tools\gen.ps1 system
.\fwc\tools\gen.ps1 bridge
.\fwc\tools\gen.ps1 config
.\fwc\tools\build.ps1
```

如果使用 `just`：

```powershell
just build
```

完整验证：

```powershell
.\fwc\tools\test.ps1
```

`new` 会在返回成功前完成生成、配置检查和框架检查。测试会在临时目录创建全新工程，并验证生成、篡改检测、配置打包、Release/Debug 构建、Godot 运行时和主场景启动。

非默认位置使用 `new.ps1 -ProjectRoot . -FrameworkPath "modules/code kit"`，或 `new.sh --project-root . --framework-path "modules/code kit"`。路径必须在工程内，拒绝 `..`、绝对路径和 shell 元字符；安装目录需已包含 FWC。模板、`justfile`、Kit 引用及生成器源码指纹都使用该路径。之后的入口来自 `fw.toml [dotnet].fwgen`，嵌套安装的命令需显式传 `-ProjectRoot` / `--project-root`；生成的 `justfile` 已传入项目根。

宿主通过 submodule commit 锁定实际框架版本；升级时选择明确的 SemVer tag 或 commit，并把更新后的 `fwc` 指针与重新生成的 `_gen` 产物放在同一宿主提交中。

## Hook
框架维护者需要时显式启用：

```powershell
git -C fwc config core.hooksPath hooks
```

hook 只把 FWC 的 `docs/rule.md`、`docs/spec.md`、`docs/use.md` 同步到默认模板的 `docs/fw/`；产生差异时会中止提交等待审阅，不会读取宿主工程或自动暂存。

## 可选 Agent 技能

通用 `fw-code` 技能的唯一维护源在独立 FWS 仓库的 `skills/fw-code/`。FWC 不再维护项目内旧 `fw` 技能副本，默认模板也不安装技能。需要 Agent 工作流时单独安装 FWS；FWC 的规范、生成、构建和运行无需 FWS 或任何 Agent 技能。

已有宿主若保留旧 `.codex/skills/fw/SKILL.md`，先检查其中是否有宿主定制，再显式迁移到 FWS 提供的 `fw-code`；框架升级不会自动删除或覆盖宿主技能。

## 边界
`fwc/` 只承载可复用代码框架能力，不放当前游戏玩法；顶层 FW 是独立的工程编排工具。

FWC、FWE、FWA 和 FWS 是可独立使用的组件。FWC 游戏不要求安装或运行 FWE/FWA/FWS；需要编辑器时，通过可选宿主适配器把生成的配置合同接到 FWE 的 source/model/view。只输出 `_config_schema.json` 不等于已经支持全部 FW CSV/JSON 格式。Kit 也按目标选择；自有网络 transport 可以关闭默认 LiteNetLib adapter，见 [使用说明](docs/use.md)。

属于 `fwc/`：
- Godot 通用运行时
- Godot UI / View 子框架
- C# 通用网络 transport、可靠命令语义和故障模拟
- 框架生成器、工具脚本、模板

不属于 `fwc/`：
- 当前游戏的 `scripts/app`
- 当前游戏的 `scripts/mode`
- 当前游戏的 `schema/*`
- 当前游戏的 `csharp/core`
- 当前游戏的 `csharp/bridge`
