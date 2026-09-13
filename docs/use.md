# FWC Use

## 接入
- 代码仓库是 `https://github.com/fiofiogamestudio/fwc.git`；在已有 Git 游戏工程中用 `git submodule add https://github.com/fiofiogamestudio/fwc.git fwc` 接入，也可由顶层 FW 初始化。
- 新工程默认安装在 `fwc/`；`fw.toml`、`Fw.*`、生成协议与 `scripts/_fw/fw` 保持原合同，不随安装位置改名。
- 前提：在工程内安装 FWC；默认脚本以组件父目录为工程根，嵌套安装需显式提供 `-ProjectRoot` / `--project-root`。
- 空目录也可由默认模板创建最小 `project.godot`；已有文件默认不会覆盖。
- 初始化：`powershell -ExecutionPolicy Bypass -File fwc/tools/new.ps1 -ProjectRoot . -Name MyGame`。
- Linux/macOS：`bash fwc/tools/new.sh --project-root . --name MyGame`。
- 非默认安装：`new.ps1 -ProjectRoot . -FrameworkPath "modules/code kit" -Name MyGame`；Unix 对应 `--framework-path "modules/code kit"`。先把 FWC 安装到该路径，脚本不会重复拉取框架。
- 路径只接受字母、数字、空格、`_`、`-`、`.` 和目录分隔符；拒绝 `..`、绝对路径、尾空格/点和 shell 元字符。模板及 `justfile` 自动写入实际路径；后续生成从 `[dotnet].fwgen` 定位，不猜测同名目录。
- 项目名必须以字母开头，只使用字母、数字和下划线；C# namespace 会自动转换成 PascalCase。
- `fw.toml` 只写模板列出的 section/key，路径使用工程根目录内的相对路径；未知字段、重复字段、空值或 `../` 越界路径会直接失败。
- `new` 会生成 system/bridge/config，随后运行配置检查和整体检查；失败不会报告创建成功。
- `fw.toml [use]` 分别配置 `game` 与 `host`。game 可选 `app / anim / net / rec / ai / lua`，host 可选 `anim / net / rec / ai / lua`；不要写 `core`，它始终自动加入。
- `project.godot` 必须包含 `[dotnet] project/assembly_name="<project name>"`；`fw check` 会在运行前发现不一致。

## 可选 Agent 技能
- 通用 `fw-code` 技能从独立 FWS 的 `skills/fw-code/` 安装；FWC 和默认游戏模板不维护技能副本。
- FWS 是可选工作流工具，不是 FWC 的生成、构建、运行或文档阅读依赖；不使用 Agent 也可按本文完成全部操作。
- 已有宿主的旧 `.codex/skills/fw/SKILL.md` 不会被 `new/gen/build` 自动删除。迁移前检查宿主定制，确认后显式移除旧副本并使用 FWS 技能，避免新旧技能并存漂移。

## Kit
- `app` 是 Godot 应用壳与通用服务，`anim` 是动作/Rig，`net` 是联机，`rec` 是帧归档，`ai` 是运行时决策，`lua` 是可选脚本沙箱。
- 修改 `[use]` 或更新 fw commit 后先运行 `fwc/tools/sync.ps1`；Unix 使用 `bash fwc/tools/sync.sh`。
- game C# 工程导入 `csharp/_gen/_fw_game.props`；声明 `[dotnet].host` 的纯 C# 工程导入 `_fw_host.props`。
- `scripts/_fw`、两个 `_fw_*.props` 和框架脚本根的 `.gdignore` 都由 `sync` 管理，不手改。禁用 Kit 后再次同步会清理旧投影和引用。
- `[use].game` 与 `[use].host` 均为必填；C# 必须导入对应 props，Godot 只使用 `res://scripts/_fw/fw`，框架不提供旧程序集或旧路径回退。
- 自己实现 `INetTransport` 时，在 `[use]` 为选中 `net` 的目标增加 `game_net_adapter = "none"` 或 `host_net_adapter = "none"`，再运行 `sync`。默认值 `"lite"` 保留已有项目行为；`"none"` 只删除默认 adapter 的引用，不自动创建替代 transport，也不影响另一个目标。

## 日常命令
- 同步 Kit：`fwc/tools/sync.ps1`。
- 生成 system：`fwc/tools/gen.ps1 system`。
- 生成 bridge：`fwc/tools/gen.ps1 bridge`。
- 生成 config：`fwc/tools/gen.ps1 config`。
- 检查配置：`fwc/tools/gen.ps1 config_check`。
- 打包配置：`fwc/tools/gen.ps1 config_pack`。
- 检查工程：`fwc/tools/check.ps1`。
- 完整构建：`fwc/tools/build.ps1`。
- 完整测试：`fwc/tools/test.ps1`。
- Unix 使用同名 `.sh` 脚本；Windows/Unix 默认 build 流程一致。
- `sync / system / bridge / config / config_pack` 都按批次提交；命令中途失败时保留调用前的完整产物与 manifest，不需要手工修补 `_gen` 或 `_fw`。
- `config_pack` 会删除已不再对应当前 config root 的旧 `.bin`，`pack/config` 不应存放手写文件。

## 修改 System
1. 修改 `schema/systems.toml`。
2. Godot system 声明 phase、script、context 和 context refs。
3. C# core system 声明 phase 和 type。
4. 运行 `fwc/tools/gen.ps1 system`。
5. 不手改 `_godot_systems.gd` 或 `_core_systems.cs`。
6. lifecycle 串行调用，不递归 Tick。Tick 内关闭后本帧不再推进剩余 system；Init 内关闭表示取消初始化，检查返回值/异常与 stopped 状态。不要在已经关闭的 runtime 上继续运行。

## 修改 Bridge
1. 按语义修改 `schema/bridge/value.proto`、`intent.proto`、`view.proto`、`event.proto` 或 `packet.proto`。
2. 只使用 `fwc/docs/spec.md` 声明的 proto3 子集。
3. 运行 `fwc/tools/gen.ps1 bridge`。
4. 任意未知语法或重复字段都会阻止生成，先修 schema，不绕过 parser。
5. 五个 proto 必须保留固定语义与共享 package；不要新建第六类 bridge proto。
6. 宽数值 codec 升级后，重新生成并构建发送端与接收端；使用受影响标量的旧 packet 协议会被拒绝。迁移 C# `uint32` 到 `uint`、`double` 到 `double`、`uint64` 到 `ulong`；不要以强制降位转换掩盖编译错误。
7. GDScript 的 `uint64` 参数/值使用规范十进制字符串，如 `"18446744073709551615"`；它不是 `int` 或 `float`。C# 手组 View/Packet 的 Godot 字典时调用生成的 `BridgeCodec.EncodeULong`；默认模板的 tick 已按此迁移。
8. `*Id` 只保留空整数 marker 的既有约定（`PlayerId` 为 signed64，其余为 signed32）。非空 `*Id` message 或 `*Id` enum 会明确报错：结构化值改用其他类型名，整数直接声明对应 `int32 / int64`；不能依赖旧生成器忽略字段的行为。
9. `BridgeView / BridgeEvent` 的 Godot wrapper 保留完整名称以免遮蔽 `Bridge` 入口；若旧代码引用过 `Bridge.View.Bridge` 或 `Bridge.Event.Bridge`，升级后改为相应完整 wrapper 名称。

## 修改 Config
1. 只改数据值：修改 `data/config/*.csv.txt` 或 `.json`，运行 `config_check`，发布前运行 `config_pack`。
2. 改字段/schema 或切换 CSV/JSON 文件布局：同步 schema/data，再运行 `config`、`config_check`、`config_pack`。
3. 使用小数定点时在 schema 声明一次空 `message Fixed32 {}`，字段类型写 `Fixed32`；不要给 marker 添加字段。
4. 宿主需要 FWE 等结构化编辑器时，在 `[gen]` 增加 `fwe = "tools/fwe/_gen"`；编辑器只消费生成的 `_config_schema.json`，不要再维护表头或字段类型副本。
5. FWE 接入需要可选 source/model 适配器把该合同及 CSV/JSON 转成编辑域；仅设置 `[gen].fwe` 不会启动或安装编辑器。未接编辑器时，fw 的生成、检查、打包和运行链照常独立工作。

数值与只读合同：

- `uint32` 可以使用完整 `0..4294967295`；`double` 不再缩窄成 C# `float`。数值配置拒绝 bool、整数小数、越界值、NaN 和 Infinity。
- 数值原文（含首尾空白）最多 4096 个字符，过长时改用等价的短科学记数法，不通过截断有效数字绕过检查。Godot 的 CSV/JSON/pack 读取统一使用生成器提供的精确浮点转换，包含子正规数及中点舍入边界。
- JSON 中的大整数写成字符串，例如 `"id": "18446744073709551615"`、`"tick": "9223372036854775807"`。超出安全整数范围的 64 位 JSON 数值会在 check/pack 时失败；不要先用 JavaScript Number 转换再 stringify。CSV 单元格直接写十进制文本即可。
- GDScript 的 `uint64` 配置是字符串，即使是零也为 `"0"`。将它作为精确 ID 传递或显示；需要算术时在 C# 的 `ulong` 边界处理，不调用 `int()`/`float()` 丢失高位。
- C# repeated 配置可枚举和索引，不可 Add/Clear；装配时可传入列表，生成属性会复制并冻结。Godot 配置返回递归只读集合；需要独立可编辑数据时显式复制，修改副本不能成为权威配置变更。
- Godot 配置加载返回空结果且报告错误时，应停止启动并修复源数据；失败不会占用有效缓存，修复后可重试，不应把空结果当成零值配置继续运行。
- 升级旧生成合同后必须重新运行 `config`、`config_check`、`config_pack` 并构建宿主。迁移 C# 的 `uint32: int -> uint`、`double: float -> double`、`Fixed32: float -> double` 与 `List<T> -> IReadOnlyList<T>` 调用点；Godot 不再修改配置返回值，64 位旧 pack 必须重打包。本次不是源码/数据完全兼容升级；Bridge 使用自己的生成入口，另按上节同步迁移，不能只生成 config。

## 程序化骨骼
- 在宿主 schema/data 中维护一份整数 tick 程序动作轨道；C# Core 使用 `Fw.Rt.Animation.ProceduralActionSampler`，Godot 使用 `FProceduralPose.group_tracks/sample_track`。两端必须使用相同 easing、Y-X-Z 角约定、Quaternion slerp 与采样上限，不能各自重建曲线。
- 固定 tick 是规则事实；Godot 可以在相邻 tick 间做只读的子 tick 插值。快速旋转或位移需要调用 `RequiredSubsamples / required_subsamples`，用相同角度、距离和最大样本数覆盖一个 Core tick。
- 刚体道具轨道应位于宿主定义的统一角色坐标系中，再把模型自己的攻击轴映射到该坐标系；例如宿主可约定 `GripPivot`、`-Z` 前向和以道具为 anchor 的局部部件盒。具体武器、命中盒和阶段字段仍由宿主 schema 定义。
- 需要从动画库选择自然移动时，宿主先离线或启动时采样 `velocity / trajectory / pose / pose_velocity / phase / contacts` 候选，再把数据库和当前查询交给 `FMotionMatcher.match_motion`。关节速度应由相邻姿态采样计算并使用独立低权重，避免覆盖方向意图。宿主负责 AnimationPlayer、phase seek、crossfade、缓存和查询节流；方向、步态或 clip 所有权改变时立即重查。匹配器只选表现候选，不能提供 Core 位移或 root motion。
- 先定义稳定的 `pose_family`，再选择 `FProceduralRigAdapter` 实现。正式角色通常用 `FProceduralHumanoidRigAdapter` 驱动完整 skinned humanoid，其 profile 的 `bones` 把 `left_upper_arm` 等语义名映射到模型骨骼名；`FSegmentedHumanoidRigAdapter` 不需要 Skin、权重或 AnimationPlayer，适合碰撞/姿态调试、原型或明确采用刚性分段的美术风格。上层 view 不能分支读取具体骨骼名，只调用 adapter 的 setup、submit、solve、socket、fit、clear 和 metrics。
- 每帧由上层 view 先生成模型无关的 locomotion 和上下身语义姿态，再采样并定位权威刚体道具，最后调用 adapter 提交 `left_hand_transform`、`right_hand_transform`、手肘/膝盖 pole 与 `hips/spine/chest` 的旋转附加姿态。内部脊柱骨的 `*_position` 不得用于手部可达补偿；应先用 `fit_hand_targets` 求出道具目标所需的刚体平移，取反后作为 `torso_reach_offset`，必要时基于实际倾斜后的残差做少量闭环迭代，但不得把修正施加回道具轨道。
- `torso_reach_lean_degrees` 控制定长躯干倾斜上限，`left/right_shoulder_swing_degrees` 控制定长锁骨或肩部摆动；框架默认肩部上限为 35 度，profile 可在不超过 65 度硬上限内为具体 Rig 收紧或放宽。双骨 IK 随后把腕/足目标夹到真实骨长可达区间，并通过 `metrics.torso_reach / shoulder_offsets / endpoint_errors / reach_clamps` 报告倾斜、肩移和未达到距离；禁止靠平移胸骨、缩放骨骼、拉伸末端骨或伪造零误差隐藏不可达目标。
- 双手装备只允许主手与道具轨道成为权威目标。副手先沿装备握柄轴在宿主限定距离内寻找可达握点，再用受限的髋-脊柱-胸倾斜和锁骨旋转共同收敛；不得为了贴合副手而旋转、缩放或平移权威武器。法杖等允许释放副手的动作应在语义轨道中显式省略副手目标，不能由 Rig 类型隐式猜测。
- 需要上下身朝向分离时，调用 `FBodyOrientation.resolve`：角色根节点使用 `lower_yaw`，独立动作坐标系使用 `action_yaw`，把 `upper_twist_yaw` 按 Rig 比例分配给脊柱与胸部。下一帧应把上次 `upper_twist_yaw` 作为 `options.current_upper_twist_yaw` 传回，以获得默认约 `0.12s` 渐入和 `0.16s` 回正；`target_upper_twist_yaw` 可用于调试目标。移动中的 `upper` 和 `committed` 都保持根朝向随速度，`committed` 只按权重混入脚部站姿，`full_body` 才完整接管；动作坐标系必须继续跟随权威瞄准。
- 宿主负责按身体策略决定是否提交脚目标：`upper` 不提交 `*_foot_transform`，让基础步态完整保留；`committed` 从当前动画脚姿态向动作脚姿态按权重混合；`full_body` 可提交完整脚目标。不要仅仅继续播放 locomotion 后又用 100% 脚 IK 覆盖它。
- 需要有限捏脸或体型差异时，把 `FSkeletonAppearanceModifier` 作为 `Skeleton3D` 子节点，先用 `configure({bones, limits})` 绑定语义骨骼和安全缩放范围，再用 `submit_adjustments` 提交 `Vector3` 缩放并由 Modifier 帧阶段求解；编辑器即时预览可调用 `solve_now`。`clear_adjustments` 和重新 `configure` 都会恢复原始基准，`metrics` 会报告夹取、忽略项与耗时。换装、发型场景、材质和装备属性仍由宿主分别管理。
- 动作起手若需从 locomotion 挂点过渡到统一动作坐标系，过渡必须在首个权威命中 tick 前结束；命中期间画面道具姿态必须等于共享采样结果。root motion、命中窗口、去重和伤害仍由宿主 Core 决议，modifier 只让身体追随。

## C# Node
- GDScript 需要创建 C# bridge Node 时调用 `FCSharp.create_node("res://csharp/bridge/<name>_bridge.cs")`。
- C# 文件名、类名必须大小写完全一致，类型必须继承 `Godot.Node`。
- 若创建失败，先检查 Godot .NET、Debug 构建和 `project.godot` assembly name，不回退到裸 `script.new()`。

## 本地化
- 用 `FLocalizationCatalog.setup(namespace).load_catalog(data)` 载入标准目录，再用 `FLocalization.register_provider(id, catalog, priority)` 注册基础游戏、DLC 和 Mod；优先级越大越先查找。
- 用 `FLocalization.setup(default_locale, supported_locales, initial_locale, fallback_chains, locale_store)` 配置语言。`locale_store` 可选实现 `load_locale(fallback)` 和 `save_locale(locale)`，持久化策略留在宿主。
- 业务代码使用稳定 ID：`localization.translate("game.ui.turns", {"count": turns}, "{count} turns")`。不要把中文原文当长期 ID；旧项目可在适配 provider 中维护 source-text alias。
- 标准目录消息结构为 `{ "schema_version": 1, "namespace": "game", "messages": { "ui.ok": { "zh-CN": "确定", "en": "OK" } }, "assets": {}, "aliases": {} }`。
- 复数和选择使用 `{count, plural, one {# item} other {# items}}` 与 `{role, select, admin {Administrator} other {Player}}`；所有语言必须保留相同参数名。
- UI 可用 `bind_property(control, &"text", id, args, fallback)` 或 `bind_asset_property` 自动刷新；生命周期结束时调用 `unbind(token)`，服务也会清理已经释放的目标。
- 开发/测试构建可把 `qps-ploc` 加入 supported locales 检查截断；发布门禁应读取 `diagnostics()` 并对缺失消息、参数错误和目录校验问题失败。
- C# core 用 `LocalizedMessage`/`LocalizedAsset` 只传语义 ID、参数和 fallback；具体语言、字体、图片、音频与排版始终由表现层解析。

## AI 算法
- 宿主在自己的 core system 中创建并调用 AI 对象；不要给 Utility、Behavior、StateTree、Plan 或 Nav 再套 `init/tick/shutdown` 生命周期。
- 可视 AI 分别维护条件、目标、行为树、状态机和 GOAP 资产；不要让策划直接编辑 `DecisionGraph` IR，也不要把这些语义重新混入一张通用蓝图。
- 启动时先用 `DecisionAssets.Parse` 装载条件和行为树，再用 `Utility / State / Plan` 编译运行程序；只有需要检查或合并多个 IR 根时才使用 `Build*` 与 `DecisionGraph.Compose`。任何结构、引用或参数错误必须在进入战局前失败。
- 每次权威计算把允许读取的事实写入 `Blackboard`，创建 `DecisionScope(tick, budget, random, trace)`；budget 使用候选数或节点数，不使用毫秒数。同一个 scope 只用于当前串行计算链，不并发或重入共享。
- 为每个 AI 对象保存独立 `DecisionSession`，并实现 `ITaskHost`。task host 只暴露最小能力，先收集/校验结果，整次执行成功后再提交会话与玩法状态。
- 正在执行宿主定义的原子动作时调用 `UtilityProgram.Tick(..., reselect: false)`；当前目标条件失效时框架仍会立即退出并重选，条件有效时才保持目标。动作结束或允许中断后恢复常规重选。学习模型若只是辅助 Utility，应把输出写成评分事实，不得另行提交目标。
- 训练或仿真先用 `IsAvailable` 生成合法目标集合，再用 `TickGoal` 执行采样结果；线上 Bot 不调用 `TickGoal`，也不直接修改 `DecisionSession.ChoiceId`。
- Utility 目标负责决定“为什么做”，行为树负责“怎样做”，状态机负责“何时切换状态”，GOAP 负责从目标与动作事实中规划步骤；条件资产供前三者复用。
- 接入整局 AI 时先实现 `IGameEnvironment<TState,TObservation,TAction>`：`Clone` 必须隔离可变状态，`StateKey` 必须确定，合法动作必须唯一且顺序稳定，`Step` 只修改传入的搜索副本。
- 双方同一时刻决策时实现 `IParallelGameEnvironment<TState,TObservation,TAction>`：按稳定顺序返回 active actor，为每个 actor 生成只含其可知信息的 observation 和非空合法动作集合，并在 `Step` 前调用 `ParallelEnvironmentGuard` 校验完整动作批次。
- `Result` 对仍在进行的局返回 `Running`，真实结局返回 `Terminated`，动作/回合上限返回 `Truncated`；每一步 rewards 和最终 payoffs 都按 `Spec.PlayerCount` 返回。
- 单人确定性关卡先用 `BeamSearch` 和宿主启发式函数建立可解释基线；每帧继续对同一实例调用 `Step(scope)`，成功后只提交结果中的第一个动作并重新从权威状态搜索。
- 大分支、随机或多玩家顺序游戏使用 `PuctSearch`；当前实现要求完全信息，隐藏信息游戏应由宿主先按玩家信息集确定化。`IPolicyValueModel` 必须只根据传入 observation 与 legal actions 生成先验和逐玩家价值，缺失先验会被归一化为零，全部为零时退化为均匀先验。
- 自博弈时把根节点访问分布、最终选中动作、即时 rewards 和最终 payoffs 写入 `PolicyValueSample`/`TrainingTrajectory`；用 `ReplayBuffer` 固定容量并用框架随机流确定性抽样。
- 无 ML 运行时的首个训练闭环可实现 `IPolicyValueFeatureEncoder`，用 `LinearPolicyValueTrainer` 训练并通过 `LinearPolicyValueCheckpoint.ToJson/FromJson` 持久化；policy 特征必须同时编码观察、行动者和候选动作，value 特征必须明确观察者与被估值玩家，输入应在宿主侧归一化。宿主还应在 checkpoint 外层记录特征 schema id、数据集哈希和训练配置。
- 线性模型只用于验证数据、训练、checkpoint、推理和搜索接线，不能替代复杂游戏所需的神经网络；升级模型时保持 observation/action、样本和评测合同不变，由宿主提供新的 `IPolicyValueModel`。
- 固定离散动作的实时策略可使用 `DenseActorCriticModel` 与 `DensePpoTrainer`：先用可解释教师产生 `DenseImitationSample`，再冻结当前模型采集 `DensePpoSample` 并训练候选；候选不得直接覆盖线上模型，必须用固定 seed 同时对旧冠军、教师和关键场景矩阵评测。策略容易因单轮更新过大而退化时可设置 `DensePpoTrainingOptions.targetKl`，训练器会在完整 epoch 后按非负近似 KL 判断是否提前停止；默认 `0` 保持关闭，不能把提前停止本身当作质量提升。
- 自博弈对手池使用 `ZeroSumLeague` 记录候选、当前冠军和历史冠军的双向收益；对手采样只使用框架确定性随机流。checkpoint 必须绑定 observation schema，维度或 schema 不匹配时宿主应拒绝加载并回退到可解释策略。
- 批量评测用 `EvaluationAccumulator`；发布门槛同时检查成功率、Wilson 下界、截断率、平均步数和与旧策略同种子对照，不只看单局通关。
- Utility 用 `UtilitySelector` 选择目标，FSM 用现有 `StateMachine` 稳定执行动作，Behavior Tree 用 `BehaviorSession` 保存 Running 节点。
- 直接使用低层 GOAP 时通过 `GoalPlanner.Begin` 创建 `PlanSearch`；执行可视 GOAP 资产时通过 `PlanGraph.Begin` 创建。A* 直接创建 `PathSearch`；返回 `Searching` 时保留对象并在后续 tick 继续调用 `Step`。
- 流场图必须通过 `IFlowGraph.Incoming` 提供反向邻接；普通 A* 图通过 `IPathGraph.Neighbors` 提供正向邻接。
- 远程模型通过 `RemotePolicy` 注入宿主自己的异步调用，并用 `FallbackPolicy` 接本地策略；密钥和网络客户端不得进入 `fw`。
- 算法结果必须先由宿主验证，再转换为自己的 intent 或 command；框架算法不得直接修改玩法状态。

## Optional Lua Script
Lua 只在玩法确实需要文本脚本时启用，不是可视 AI 图的组成部分。
1. 宿主把 JSON 图和 `.lua` 文件放在自己的数据目录，并由自己的编辑器维护。
2. Core 启动时创建 `ScriptRuntime`，调用 `Load`，再用 `RequireFunction` 验证入口。
3. 每次调用只把普通字典/数组快照传给脚本，不传 context、state、Node 或任意 CLR 对象。
4. 用 `ScriptHost.RegisterQuery` 注册最小只读能力；确定性随机、寻路等能力也通过 query 注入。
5. 脚本通过 `api.command` 描述意图；宿主拿到 `ScriptCallResult` 后先验证整批 command，再统一执行。
6. 需要调整资源上限时传入 `ScriptRuntimeOptions`；指令、结构深度、集合项、字符串、源码和 command 上限必须保持为正数，不能用放大上限代替内容拆分。
7. 关闭 Core 时调用 `ScriptRuntime.Dispose`；DS 和本地模式必须装载同一份脚本事实源。

## 网络
- server/client 分别创建 `INetTransport` 并调用 `StartServer / StartClient`；发现器调用 `StartUnconnected`，不需要建立会话。
- authority 结束会话或回收房间时调用 `Disconnect(remoteEndPoint)` 主动释放连接；不要只删除宿主自己的 peer 状态，否则底层连接仍会占用容量。
- `NetTransportOptions.MaxQueuedMessages` 设置 adapter 的接收队列上限；保持默认值或按实际 poll 预算调大，监控 `NetTransportSnapshot.QueueDrops`，不要用无限队列掩盖消费速度不足。
- 宿主为每类 packet 固定 channel 和 `NetDelivery`。一次性命令用 `ReliableOrdered`，可覆盖状态用 `ReliableSequenced`，允许自然丢失的观测才用 `UnreliableSequenced`。
- 客户端先把一次性操作写入 `NetCommandJournal`，每次只发送最早未确认项；authority 用命令 id 去重，返回终态确认后调用 `Complete` 或 `CompleteThrough`。
- journal 满时停止接受新命令并把背压暴露给上层，不要删除最旧或最新输入。重连恢复同一权威 session 时保留 journal；权威 session 被替换时显式拒绝旧命令。
- 高频状态可存入 `NetInputHistory<TState>` 并发送 latest 窗口；authority 只应用最新合法 tick，一次性边沿不得放进状态历史重复执行。
- 测试协议时使用 `NetFaultSimulator<T>` 固定 seed，分别覆盖丢失、重复、乱序、延迟、断线恢复和队列背压；真实 adapter 另做 loopback 测试。
- 读取 `NetTransportSnapshot` 时区分 payload bytes 与 wire bytes；带宽基准使用 wire bytes/datagrams，业务 packet 分布使用宿主 codec 的 payload 统计。

## 帧归档
1. 用 `FrameArchive.Create(path, content, options)` 创建 writer；`content` 保存整份文件共享且可独立校验的格式元数据。
2. 每个权威 tick 调用 `Append(tick, isCheckpoint, payload)`；第一帧应是 checkpoint，之后按固定间隔再写 checkpoint，其他帧只保存增量。
3. 正常结束调用 `Complete()` 得到最终路径；异常退出保留 `.part`，下次启动先调用 `FrameArchive.Recover(partialPath, options)` 再决定继续读取或归档。
4. 播放时用 `Open`，`FindFrameIndex` 定位目标帧，`FindCheckpointIndex` 找起点，从 checkpoint 到目标依次 `ReadAt`；跳转追帧期间是否派发事件由宿主决定。
5. 不要把可执行宿主对象、引擎资源或未版本化二进制布局直接放入 payload；content 应声明宿主格式版本和最低 reader 版本。

## 房间目录
- 服务端使用 `RoomDirectoryStore` 提供 register、heartbeat、unregister、list、allocate 和 join HTTP 端点；具体 Web host、数据库和部署方式由游戏工程决定。
- DS 使用 `RoomDirectoryClient.RegisterAsync` 注册并保管返回的 heartbeat token 与 admission secret，随后按不大于 `HeartbeatIntervalMilliseconds` 的间隔调用 `HeartbeatAsync`。房间地图、容量或标签可在心跳中更新；不要在客户端重复猜测目录的 stale 配置。
- 客户端浏览和普通加入使用 `ListAsync / JoinAsync`；创建新房构造 `RoomAllocationRequest`，把经过宿主预校验的创建参数编码为不超过 1024 个 UTF-8 字节的 `CreatePayload`，需要指定现有 DS 时同时填写 `PreferredHost / PreferredPort`，再调用 `AllocateAsync(request)` 原子预留匹配的无人房间。两项留空和 `0` 表示由目录任选；找不到匹配 endpoint 时分配失败，不得降级为客户端直连。admission secret 永远不返回客户端。
- authority 使用 `RoomTicket.TryValidate` 校验短期票据并取得已签名的 `CreatePayload`，由宿主规则在创建玩家前完成最终校验和房间初始化，再按 ticket nonce 与连接身份实现一次性或绑定式消费。
- 宿主开始战局后以 `RoomStatus.Playing` 心跳；观战列表调用 `ListSpectatableAsync`，申请观战票据调用 `SpectateAsync`。authority 必须再检查 ticket purpose，spectate 连接不得复用玩家分配与输入路径。
- 本机开发可以使用 `http://127.0.0.1`；公网必须在反向代理或 Web host 上配置 HTTPS，否则 `RoomDirectoryClient` 会拒绝连接。

## 表现对象
- Pool 创建：`pool.spawn(key, parent, owner, props)`。
- Pool 回收：`pool.recycle(node)`。
- 引用式资源加载：`handle = asset.acquire(path, expected_type)`，使用完调用幂等的 `handle.release()`。
- 并发异步资源加载：`await asset.acquire_async(path, expected_type)`；同一规范化路径共享一次在途 load，每个调用方仍获得独立 handle 并分别 release。
- Provider 注销：`asset.unregister_provider(source)` 默认拒绝活动 handle/在途 load；保留缓存时传 `false`，框架仍保存原 provider 负责后续 release。`force=true` 会使现有句柄失效，只用于整体 teardown。
- UI 打开：`ui.open(layer, id, scene, context, props)`。
- UI 关闭：`ui.close(id)`。
- BGM/SFX 先用 `register_bgm / register_sfx` 注册，再用 `play_bgm / play_sfx` 播放；音量和静音通过 Audio Bus API 调整。
- 3D 音频节点：`audio.create_player_3d(parent, name, bus)`。
- 3D 音频播放：`audio.play_3d(player, stream, volume_db, pitch_scale, max_distance, unit_size)`。
- mode 通过 `audio()` 获取 `FAudio`；玩法判定所需的噪声、距离或感知状态必须来自 core，不能从正在播放的音频反推。
- 状态对象由 logic 调用 `apply(vm, dt)`。
- 一次性 fx 由 logic 调用 `play(payload)`，监听 `finished` 后回收。
- logic 通过 context 数据入口或 intent 提交操作，不保存 system 本体。
- C# `EventBus<TKey>` 的一个 key 只绑定一种 payload 类型；错误类型 publish 会在任何 listener 执行前失败。
- C# `StateMachine` 的 enter/exit/transition/Started/Transitioned 回调抛错后会停机并清空 current；`Clear` 仍保证移除注册，修复原因后可重新注册并 `Start`。
- `LogBuffer` 会复制结构化 data 的字典层，调用方可继续修改原字典；字典内对象仍由调用方负责不可变性或深拷贝，`ForwardTo` 不允许形成 buffer 转发环。

## GM

启用 `app` Kit 并同步后，`AppRoot` 已装配 `FGM` 和 `FGMLogic`。在宿主 `on_app_setup()` 中显式启用调试并注册命令；F1 打开或关闭面板，Escape 先关闭可见下拉或文本菜单，否则关闭面板。默认 `FDebug` 不启用，发布构建还受其 release 许可限制。

下面的 GD 示例只回显输入，可直接放进继承 `AppRoot` 的宿主 app 脚本：

```gdscript
extends AppRoot

func on_app_setup() -> void:
    debug_service().set_enabled(true)
    var result: Dictionary = gm_service().register_command(
        &"example.app",
        {
            "id": "debug.echo",
            "title": "回显文本",
            "description": "验证 GM 参数输入和结果显示。",
            "category": "诊断",
            "risk": "normal",
            "args": [
                {"id": "text", "label": "内容", "kind": "text",
                 "required": true, "default": "GM ready"}
            ]
        },
        Callable(self, "_gm_echo")
    )
    if not result.get("ok", false):
        push_error(str(result.get("message", "GM registration failed")))

func _gm_echo(values: Dictionary) -> Dictionary:
    return {"ok": true, "message": values["text"]}
```

- 在 AppRoot 外独立使用时，先 `gm.setup(debug, log, history_limit)`，再 `panel.setup(ui, gm)`，由宿主转发输入到 `panel.handle_input(event)` 并每帧调用 `panel.tick(dt)`。`panel` 提供 `open_panel / toggle / close / is_open / blocks_input / refresh / clear`；已有 AppRoot 已负责装配和推进，不需要重复调用。
- 动态下拉在注册时传入 `options_provider`：`func(arg_id, previous_values) -> Dictionary` 返回 `{"ok": true, "options": [{"value": "stable_id", "label": "显示名"}]}`。dropdown 参数的 `depends_on` 列出前置参数 ID；依赖为空或无效时不会调用 provider，provider 使用全部已规范化的有效前置值查询当前选项。失败返回 `{"ok": false, "message": "原因"}`。
- `availability` 回调接收规范化的参数并返回 `{ok, message?}`。把场景、目标和当前业务前置条件放在此处；handler 仍须通过宿主的权威入口执行并返回明确的成功或失败。可选 int/float 留空时不会出现在 values 中；其他可选文本/下拉空值为 `""`，布尔缺省为 `false`，handler 应使用适当的 `get` 默认值。
- 用 `inspect_command(id, values)` 更新自定义面板，它的 `options` 按参数 ID 分组，`errors` 给出参数错误，`ok/code` 标识本次检查结果。执行统一调用 `execute(id, values)`；`risk = "caution"` 或 `"destructive"` 都需要明确确认后传入 `confirmed = true`。框架自带面板负责确认交互，其他入口也不能跳过最终校验。
- 一个 mode 的命令使用稳定且独占的 owner；离开 mode 时在依赖释放前调用 `gm_service().unregister_owner(owner)`，避免留下引用旧 context 的回调。AppRoot 退出时会清理内存中的服务；已保存的收藏和历史在下次装载缓存时恢复，当前未注册的命令不能执行。
- 面板打开时框架会拦截事件输入；自行调用 `Input.is_action_pressed` 等轮询 API 的宿主，在生成玩法意图前先检查 `blocks_gameplay_input()`。这个检查只阻止新输入，不暂停模拟或删除已提交意图。
- C# 游戏的适配链为 `FGM handler -> 宿主 Godot bridge -> GameCore 意图入口 -> 权威 system/rules -> view/event`。新增协议时修改现有 `schema/bridge/intent.proto` 及相应值、事件定义，再运行 bridge 生成；adapter 使用宿主实际生成的类型和 codec，GM 框架不提供游戏专用消息类型。bridge 可做格式转换，业务条件、范围和目标合法性由 core 再校验；入队成功不等于权威执行完成。
- handler 的可选 `data` 只返回普通值或无循环的数组、字典，不返回 `Object / Callable / Signal`。`history()` 的每项包含顺序、时间、命令与来源元数据、参数和结果，用 `result.ok` 区分成功和失败，不通过显示文本猜测。面板展示服务保留的全部记录（默认上限 100 条），支持载入参数和再次执行；重执行重新校验当前定义、选项与条件，有风险的命令仍须确认。
- AppRoot 默认将收藏和历史保存在 `user://gm/history_and_favorites.dat`；用 `FW_GM_STORAGE_PATH` 指定独立缓存。独立服务通过 `set_storage_path(path)` 启用持久化，传空字符串关闭后续保存。缓存采用版本、长度、摘要和类型校验，读取或写入失败可在系统诊断中查看，当前会话数据继续保留。
- 收藏保存命令元数据，不保存参数预设。打开窗口后切换命令保留各自参数草稿；完整关闭再打开时清空草稿，回到最近使用、展开并居中。拖动标题栏可移动面板，收起和展开保持同一中心并持续拦截游戏输入；收起保留待确认操作，危险快捷执行会展开确认。
- provider、availability 和 handler 都是同步 Callable，分别接受 2、1、1 个参数；把可预期错误返回为结构化结果，不依赖 GDScript 捕获任意运行时错误。GM 不替代权限边界、沙箱或业务事务，不能保证回调发生部分写入后的回滚。

在 FWC 仓库根运行 `powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ./tools/test.ps1`，既有完整测试入口包含 GM 服务行为与公共 API 检查。

## 验证
- 最小验证：`fwc/tools/check.ps1`。
- 提交前验证：`fwc/tools/test.ps1`。
- 通用运行时扩展验证：`fwc/tools/verify_runtime.ps1 -ProjectRoot .`；完整测试已经自动执行同一 C#/Godot 探针。
- 测试会创建并清理临时项目，不写入宿主工程。
- 临时 FWC 副本会初始化为独立 Git 仓库；生成、构建、Godot 导入和运行后都检查组件仍然干净，测试提交只发生在临时副本内。
- 本机安装 Godot .NET 时，测试会额外执行 headless 脚本扫描和主场景启动；可用 `GODOT_BIN` 指定版本，或用 `-SkipGodot` 跳过。
- 冷缓存较慢时可用 `FW_GODOT_EDITOR_TIMEOUT_SECONDS` 和 `FW_GODOT_RUN_TIMEOUT_SECONDS` 调整 headless 超时；默认分别为 90 秒和 30 秒。
- 正式提交不应使用 `-SkipGodot`；该参数只用于明确缺少 Godot 的临时环境。
- 路径矩阵：`tools/test.ps1 -FrameworkPath "modules/code kit"`；Unix 用 `FW_TEST_FRAMEWORK_PATH="modules/code kit" bash tools/test.sh`。不提供时默认测试 `fwc`；两种位置都在 CI 执行生成、构建和 Godot 验证。
- 公共 API snapshot 变化默认直接失败。只有确认该变化符合 SemVer 和迁移要求后，框架维护者才可临时设置 `FW_UPDATE_API=1`，分别运行 FwGenTests 与 Godot runtime test 更新基线；随后必须取消变量、审阅 diff 并重新完整测试。

## 升级
1. 保持宿主工作区可区分，记录当前 `fwc` submodule commit。
2. 把 `fwc` 切换到目标 SemVer tag 或明确 commit，不直接依赖远端浮动分支。
3. 依次运行 system、bridge、config 生成和 `config_check`，再运行 `check`、`build`、`test`。
4. 审阅公共 API、schema 合同和生成产物差异；按 `CHANGELOG.md` 完成必要迁移。
5. 验证通过后，在同一宿主变更中提交 `fwc` 指针和对应生成产物，避免其他电脑检出不一致组合。
6. 任一步失败时先恢复旧 submodule commit，不手改 `_gen` 产物绕过检查。

## Hook
- 框架维护者可显式启用：`git -C fwc config core.hooksPath hooks`。
- hook 只同步 FWC 自己的 `docs/rule.md`、`docs/spec.md`、`docs/use.md` 到默认模板的 `docs/fw/`，不读取或镜像 FWS 技能。
- 若同步产生差异，提交会停止；检查并暂存派生文件后再次提交。
- 普通 `new/gen/build` 不会修改 Git hook 配置。
