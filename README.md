# FWC

FWC 是 Godot + C# 的代码框架，仓库为 `fiofiogamestudio/fwc`。安装到游戏工程时仍使用 `fw/` 路径。

仓库名称与宿主接口分开演进：`fw/`、`fw.toml`、`Fw.*` 命名空间、生成协议和 `fw`/`fwgen` 命令保持兼容，不因仓库更名而整体改名。

它提供四类能力：
- Godot 运行时骨架：`AppRoot -> BaseMode -> SystemManager`
- Godot UI / View 子框架：`FUI`、`FForm`、`FWidget`、`FViewRoot`、`FViewStore`、`FRefs`、`FProps`、`FBinding`、`FViewModel`，以及程序姿态采样与人形两段 IK
- 通用运行时服务：资源、本地化、对象池、事件、状态机、日志、2D/3D 音频、显示、调试开关，以及可替换 transport 的 `Fw.Rt.Net`
- C# 工具链：`fw/tools/new.*`、`gen.*`、`build.*`、`test.*`

框架维护文档在：
- `fw/docs/rule.md`
- `fw/docs/spec.md`
- `fw/docs/use.md`

## 接入前提
- Godot 4.6.2 .NET
- .NET SDK 10.0.201（由 `global.json` 固定）
- 已有 Godot 工程，或允许模板创建最小 `project.godot`
- `fw/` 位于工程根目录
- 游戏核心逻辑使用 Godot C# 项目承载

目录形态：

```text
<game_root>/
  fw/
  project.godot
  <game>.csproj
```

## 快速开始
在已有 Git 游戏工程中添加框架，保留兼容的安装路径：

```powershell
git submodule add https://github.com/fiofiogamestudio/fwc.git fw
```

初始化最小工程骨架：

```powershell
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\fw\tools\new.ps1 -ProjectRoot . -Name MyGame
```

或：

```bash
bash ./fw/tools/new.sh --project-root . --name MyGame
```

生成和构建：

```powershell
.\fw\tools\gen.ps1 system
.\fw\tools\gen.ps1 bridge
.\fw\tools\gen.ps1 config
.\fw\tools\build.ps1
```

如果使用 `just`：

```powershell
just build
```

完整验证：

```powershell
.\fw\tools\test.ps1
```

`new` 会在返回成功前完成生成、配置检查和框架检查。测试会在临时目录创建全新工程，并验证生成、篡改检测、配置打包、Release/Debug 构建、Godot 运行时和主场景启动。

宿主通过 submodule commit 锁定实际框架版本；升级时选择明确的 SemVer tag 或 commit，并把更新后的 `fw` 指针与重新生成的 `_gen` 产物放在同一宿主提交中。

## Hook
框架维护者需要时显式启用：

```powershell
git -C fw config core.hooksPath hooks
```

hook 只把 FWC 的 `docs/rule.md`、`docs/spec.md`、`docs/use.md` 同步到默认模板的 `docs/fw/`；产生差异时会中止提交等待审阅，不会读取宿主工程或自动暂存。

## 可选 Agent 技能

通用 `fw-code` 技能的唯一维护源在独立 FWS 仓库的 `skills/fw-code/`。FWC 不再维护项目内旧 `fw` 技能副本，默认模板也不安装技能。需要 Agent 工作流时单独安装 FWS；FWC 的规范、生成、构建和运行无需 FWS 或任何 Agent 技能。

已有宿主若保留旧 `.codex/skills/fw/SKILL.md`，先检查其中是否有宿主定制，再显式迁移到 FWS 提供的 `fw-code`；框架升级不会自动删除或覆盖宿主技能。

## 边界
`fw/` 只承载可复用框架能力，不放当前游戏玩法。

FWC、FWE、FWA 和 FWS 是可独立使用的组件。FWC 游戏不要求安装或运行 FWE/FWA/FWS；需要编辑器时，通过可选宿主适配器把生成的配置合同接到 FWE 的 source/model/view。只输出 `_config_schema.json` 不等于已经支持全部 FW CSV/JSON 格式。Kit 也按目标选择；自有网络 transport 可以关闭默认 LiteNetLib adapter，见 [使用说明](docs/use.md)。

属于 `fw/`：
- Godot 通用运行时
- Godot UI / View 子框架
- C# 通用网络 transport、可靠命令语义和故障模拟
- 框架生成器、工具脚本、模板

不属于 `fw/`：
- 当前游戏的 `scripts/app`
- 当前游戏的 `scripts/mode`
- 当前游戏的 `schema/*`
- 当前游戏的 `csharp/core`
- 当前游戏的 `csharp/bridge`
