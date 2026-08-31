# Fw Spec

## 结构
- `fw/`：可复用框架仓库，只保存运行时、生成器、模板、工具和通用文档。
- `fw.toml`：宿主工程路径与 .NET 工程入口，只接受固定 section/key，所有路径必须位于工程根目录内。
- `schema/systems.toml`：Godot system 与 C# core system 的统一事实源。
- `schema/bridge/*.proto`：intent、view、event、packet 和公共值类型事实源。
- `schema/config/*.proto`：配置结构事实源。
- `data/config/*`：人工维护的配置源数据。
- `pack/config/*`：生成的运行时配置包，不手改。
- `scripts/_gen`、`csharp/_gen`：生成代码，不手改；宿主配置 `[gen].fwe` 时还可生成 FWE 配置结构契约。
- `Directory.Build.props`：宿主与 DS 的 target framework 事实源；当前以 Godot 4.6 使用的 `net8.0` 为兼容基线，并允许工具在仅安装较新运行时时按 `Major` 向前运行。
- `global.json`：固定构建用 .NET SDK 和 `Godot.NET.Sdk`；游戏项目中 Godot 写回的 SDK/target 必须与两项配置一致。
- `scenes/app`、`scenes/env`：应用入口和 mode 环境场景。
- `prefabs/actor`、`prefabs/form`、`prefabs/widget`、`prefabs/fx`：按表现对象角色组织的资源。
- `Fw.Rt.Net`：通用 transport 与可靠输入语义；当前内置 LiteNetLib adapter，宿主只依赖 `INetTransport`。
- `Fw.Rt.Animation`：与玩法无关的固定 tick 程序动作采样器；C# Core 与 Godot 表现可对同一组姿态键执行相同缓动、Y-X-Z 四元数插值和有界自适应子采样。

## Compatibility
- Godot：`4.6.2 .NET`，由模板、`global.json` 与 CI 共同固定。
- 构建 SDK：`.NET SDK 10.0.201`；只负责还原和编译，不改变游戏程序集的 API 基线。
- Target framework：`net8.0`；游戏、DS 与 `FwRuntime` 保持一致，命令行工具在缺少 8 运行时时允许 `Major` 向前运行。
- 自动验证平台：Windows 与 Linux；macOS 在成为发布目标前再加入 CI。
- Git tag 提供人类可读的 SemVer 版本，宿主 submodule commit 提供实际的精确版本锁定；二者职责不同。
- 公共兼容边界覆盖 Godot runtime、`FwRuntime`、配置入口、生成命令、schema 子集和生成合同。内部生成器类型与实现文件不属于宿主 API。
- `_fwgen_manifest.json` 负责发现生成器、输入或产物漂移，但不代替版本号；宿主升级必须同时审阅 submodule 指针与生成差异。

## Runtime
- Godot 运行链是 `AppRoot -> BaseMode -> SystemManager`。
- `AppRoot` 创建 mode host、app system scope、`FUI`、`FPool`、`FAsset`、`FLocalization`、`FEventBus`、`FLog`、`FAudio`、`FDisplay` 和 `FDebug`，并负责 mode 切换。
- `BaseMode` 负责场景、Godot system 和 presentation 装配。
- `SystemManager` 按 phase 缓存后的顺序执行 `init / tick / shutdown`，shutdown 使用反向顺序。
- C# `SystemRuntime` 使用同样的生命周期和 phase 语义，由 `GameCore` 持有。
- C# `SystemRuntime` 为每个 system 保留有界的 tick 耗时与当前线程分配采样；`GetTimingSnapshots` 只读导出诊断，不改变 phase、依赖、故障或确定性语义。
- `AppRoot` 持有全局 system scope；`BaseMode` 持有以全局 scope 为 parent 的局部 scope。两端 runtime 都按 phase 和显式 dependency 做稳定拓扑排序。
- 两端 runtime 都显式区分 created、initializing、running、faulted、stopping、stopped；失败初始化会把失败项本身也纳入逆序回滚。
- `AppRoot` 的 mode 切换先清理旧 mode/UI/pool；新 mode enter 失败时再次清理半成品，离开 SceneTree 时执行最终 shutdown。

## Archive
- `Fw.Rt.Archives.FrameArchive` 是与玩法无关的追加式帧归档格式；宿主自行定义 content 和 frame payload，框架不认识录像、玩家或观战语义。
- `FrameArchive.Create` 先写入 `<目标>.part`，只接受严格递增的非负 tick；每帧带 checkpoint 标记并独立使用 `WireFrame` 压缩、长度限制和 SHA-256 损坏校验。
- `Complete` 在完整 flush 后原子改名为最终文件；进程中断会保留 `.part`，`Recover` 只截断损坏或未写完的尾记录，不伪造已丢失帧。
- `FrameArchiveReader` 启动时校验并索引全部记录，提供二分 tick 定位、最近 checkpoint 定位、单帧与区间读取；宿主从 checkpoint 重建状态后再应用增量帧。
- content、单帧、编码结果和 flush 间隔都由 `FrameArchiveOptions` 显式设限；调用方必须按自己的数据规模设置有限预算。

## System
- `fwgen system` 只解析一次 `schema/systems.toml`，形成共享 `SystemSchema`，再分别生成 Godot 与 C# 注册入口。
- Godot system 使用独立 context；生成器创建 system/context，并把 `refs` 绑定到目标 system context。
- C# core system 当前共享聚合 `CoreContext`，结构仍是 `Refs / Config / State`。
- 两端统一的是生命周期、phase 和 context 三分法，不要求 context 实例粒度完全相同。
- 当聚合 `CoreContext` 变大时，可拆成 typed context slice；system 只接收需要的 Config/State/Refs。
- schema 会校验重复 system、重复 phase、未知 phase、缺失脚本/context/type、无效 refs，以及映射到同一 C#/GDScript 标识符的 system/phase 名。

## Core
- C# core 是玩法权威；Godot 只消费 view、event 和配置。
- `GameCore` 是 bridge 调用的唯一 core facade。
- `core/system` 保存有 phase 的权威运行阶段。
- `core/state` 保存可变权威状态。
- `core/rules` 保存无状态领域计算。
- `core/config` 负责只读配置装配。
- `core/const` 只保存少量编译期常量。
- bridge 不保存玩法真值，不把 Godot scene 或 UI 类型传入 core。

## AI
- `Fw.Rt.AI` 是纯 C# 通用算法库，不是第二套 system runtime；宿主 system 负责生命周期、读取 context 和把结果转换成自己的 intent。
- `Core` 提供固定工作量 `DecisionBudget`、确定性 `DecisionScope` 和有界 `DecisionTrace`；真实耗时只能监控，不得决定权威算法的停止位置。
- `Environment` 用 `IGameEnvironment<TState,TObservation,TAction>` 描述 reset、clone、状态 key、当前行动者、玩家观察、合法动作、机会结果、终局收益和 step；搜索只操作 clone，不直接修改宿主权威状态。
- 同时行动游戏使用 `IParallelGameEnvironment<TState,TObservation,TAction>`；每个决策点必须返回全部 active actor 的独立观察与合法动作，并一次提交每个 actor 恰好一个动作。`ParallelEnvironmentGuard` 统一检查 actor 和动作集合。
- 环境元数据显式声明确定/随机、完全/不完全信息、顺序/同时行动和收益类型；`GameActors` 保留 chance、terminal 与 simultaneous 特殊行动者，终局和资源上限截断不得混为一类。
- `Model` 的 `IPolicyValueModel` 只接收玩家观察和当前合法动作，返回可变动作集合上的先验与逐玩家价值；高容量模型运行时由宿主注入。框架另提供由宿主特征编码器驱动、可导出 checkpoint 的 `LinearPolicyValueModel`，作为无外部 ML 依赖的可训练基线和接入探针。
- 固定离散动作空间可使用 `DenseActorCriticModel`：单隐层 actor-critic 同时输出动作概率与有界价值，推理始终应用合法动作 mask；`DenseActorCriticCheckpoint` 固定版本、schema id、维度和有限权重，不保存宿主玩法语义。
- `Search` 提供可暂停的确定性 `BeamSearch` 和 PUCT。Beam 只接受单人或合作型确定性规划；PUCT 支持完全信息的顺序玩家与显式 chance 节点，按稳定动作顺序打破平局。不完全信息必须先由宿主做确定化或实现信息集算法，框架不会把完全信息搜索伪装成公平策略。
- 搜索预算分别按展开节点数和模拟次数计量；终局 `Payoffs` 是完整局面价值，`Transition.Rewards` 留给训练轨迹，搜索不会把两者重复累计。
- `Training` 提供策略目标、策略价值样本、完整/截断轨迹、确定性定容 replay buffer，以及对线性基线执行交叉熵与价值回归的固定顺序小批量训练器；`Evaluation` 分开统计成功、失败、平局、截断、平均收益和 Wilson 成功率区间。
- `DensePpoTrainer` 支持带动作 mask 的行为克隆和 PPO clipped surrogate、价值回归、熵正则、梯度裁剪与 Adam；近似 KL 使用非负形式 `ratio - 1 - log(ratio)`，宿主可配置目标 KL 在完整 epoch 后提前停止，`0` 表示关闭。宿主负责生成 advantage/value target、冻结 rollout 旧概率并执行晋级门槛。`ZeroSumLeague` 保存双向零和对局收益，用确定性元策略采样历史对手。
- `Utility` 按稳定注册顺序评分，支持权重、确定性噪声与切换阈值；分数相同保持先注册项。
- `Behavior` 使用 `Success / Failure / Running / Suspended`，只读树定义与 `BehaviorSession` 分离，同一棵树可供多个会话使用；响应式节点抢占分支时会递归清理旧分支游标。
- `DecisionGraph.Parse` 读取并冻结运行时节点/连线 IR；`DecisionGraph.Compose` 合并多个 IR 片段，并只在同类型、同名且参数完全一致时合并根节点。`Blackboard` 提供 typed fact 读取，`DecisionExpression` 执行有预算的数字、布尔和确定性随机表达式；表达式递归路径由本次 `DecisionScope` 复用并在每个入口退出时清空，不产生逐表达式临时集合。
- `DecisionAssets.Parse` 装载可复用条件与严格行为树；常规接入用 `Utility / State / Plan` 直接得到运行程序，只有检查或组合 IR 时才用 `BuildUtility / BuildState / BuildPlan`。策划数据不需要直接书写 IR 节点和端口。
- 行为树资产使用单根、单父级结构；状态机资产使用根级 `initial`、`states` 和每个状态的 `transitions`，转换目标必须存在，触发时机只允许 `tick / success / failure`。
- `UtilityProgram` 把 `UtilityRoot / UtilityGoal` 与 Behavior Tree 编译为唯一目标选择程序；目标条件先于评分执行，不可用目标不会参与选择，当前目标条件失效时即使 `reselect: false` 也会退出并重新选择。`Tick(..., reselect: false)` 只在当前目标仍可用时保持目标并继续行为，适合宿主的原子动作承诺。`TickGoal` 只供训练、仿真和确定性测试执行调用方已验证的目标；`IsAvailable` 可在不执行行为树时复用目标条件。`StateProgram` 把层级 `StateTreeRoot / State / Transition` 与 Behavior Tree 编译为状态程序。状态在首次执行后计为一个 tick，后置转换的新状态保持 tick 0 到下一次执行。
- `DecisionSession` 保存当前目标、Behavior Tree 游标和 StateTree 状态；定义可共享，会话必须按权威对象隔离。预算不足时返回 `Suspended`，不会使用部分评分结果。
- `ITaskHost` 是图与宿主的唯一动作边界。图决定流程，宿主实现最小能力并验证参数；框架不认识游戏实体、伤害、动画或网络。
- `Plan` 提供基于 typed fact/action/goal 的增量 GOAP；`PlanSearch` 在预算耗尽后保留搜索状态，下一 tick 继续。
- `PlanGraph` 把 `GoapRoot / GoapGoal / GoapAction` 编译为同一套增量 GOAP 搜索，不强制 Utility、StateTree 或 GOAP 同时使用。
- `Nav` 提供增量 A*、LRU `PathCache`、反向邻接流场和基于 `System.Numerics.Vector2` 的 Steering，不依赖 Godot 类型。
- `Policy` 只定义本地、远程和回退策略合同；HTTP、API key、prompt、模型供应商、输入过滤与游戏语义留在宿主工程。
- AI 模块不读取宿主 context、不直接修改世界、不生成具体游戏命令，也不要求游戏同时使用全部算法；状态/动作编码、奖励、胜负、课程和训练参数始终属于宿主游戏。

## Optional Script
- `Fw.Rt.Script` 是独立可选能力；可用于确实适合文本脚本的玩法扩展，但可视决策图无需加载 Lua。
- `Fw.Rt.Script.ScriptRuntime` 使用纯 C# Lua 解释器，并只启用基础表、字符串、数学和错误处理模块。
- `os / io / package / require / load / loadfile / dofile / debug / collectgarbage` 与 Lua 随机函数不可用；宿主不会注册 CLR userdata。
- `ScriptRuntime.Load(id, source)` 要求模块返回函数表；`RequireFunction` 用于启动阶段验证公开入口。
- `ScriptRuntime.Call` 为本次调用创建独立 API，只暴露 `api.query(name, ...)` 与 `api.command(name, ...)`。
- `ScriptHost` 保存宿主白名单 query；一次调用的输入、query 返回、command 参数和脚本结果共享 JSON-like 总预算，拒绝 CLR/DynValue、非有限数字、过深结构、过多集合项与过长字符串。
- `ScriptCallResult` 同时返回纯数据结果、事务化 command 列表和指令计数；command 数量也受限，宿主先校验完整结果，再修改权威状态。
- `ScriptGraph.Parse` 限制 JSON 源码与标识符长度，校验根、节点/边唯一性和边端点完整性，并把图、节点值和数组冻结为只读集合；具体节点类型与字段仍由宿主定义和验证。
- 启动校验可把 JSON-like 配置编译成脚本模块内的只读索引；运行期调用只传动态事实，可变权威状态仍必须显式进入调用输入和返回值。
- 同一个已加载模块的调用串行执行；不同 `ScriptRuntime` 实例互不共享全局表。

## Present
- `FUI` 使用 `open / close` 管理 form 与 UI layer。
- `FUI.open` 拒绝空 id，先实例化并 setup 新 form，成功后才关闭同 id/同层旧 form；screen stack 只在提交后隐藏前一项。
- `FForms.setup` 与 `FFormLogic.attach_ui/detach_ui` 具有幂等清理语义；form 被外部 `free/queue_free` 时，查询或关闭会剔除失效项并恢复上一层 screen。
- `FPool` 使用 `register_prefab / warmup / spawn / recycle / flush` 管理 actor 和 fx，并跟踪容量、generation、active/free 与重复回收。
- `spawn(key, parent, owner, props)` 把生命周期 owner 和 props 显式传给对象；Pool 同时追踪 active/free 对象。
- `FAsset` 保留 `load / unload` 固定缓存，并提供引用句柄、异步加载、路径归一化和自定义 provider；同路径并发异步请求只执行一次 provider load，加载失败不缓存空值。
- `FAsset` 为每个缓存项保存实际加载 provider，保证 provider 从注册表移除后仍能成对 release；默认不允许在活动 handle 或加载请求存在时替换/注销 provider，`force` 只用于明确接受句柄失效的整体 teardown。
- `FLocalization` 按“请求语言优先、provider 优先级次之”的确定顺序解析消息和资源；高优先级 provider 缺少当前语言时，先使用低优先级 provider 的当前语言，再进入配置回退链。
- `FLocalizationCatalog` 使用稳定的点分消息 ID、显式 locale 字典、alias 和可选资源值；目录校验拒绝 alias 悬空/循环、空翻译和语言间参数漂移。
- 文本格式支持命名参数、`plural/select/selectordinal`、显式数值分支和转义花括号；缺失消息与格式错误进入可查询诊断并只在首次出现时发 signal。
- `bind_property/bind_asset_property` 在绑定时立即赋值，并在 locale 改变或 provider 变化时刷新；目标释放后自动剔除弱引用绑定。
- C# core 只产生 `LocalizedMessage/LocalizedAsset` 语义引用，不选择语言、不读取目录；宿主 bridge 负责把 ID、参数和 fallback 传给表现层。
- `FEventBus` 提供去重订阅、优先级、once 和安全快照派发；C# 同一 key 只允许一种 payload 类型，并在调用任何 listener 前完成类型预检。
- `FStateMachine` 提供 guard、payload 与受限链式 transition；C# 自定义状态比较器同时作用于 state 和 transition，生命周期/事件回调抛错后清空当前状态并允许重新启动，`Clear` 即使 exit 失败也会移除注册。
- `FLog` 提供 level、category threshold、结构化数据和固定容量历史；C# `LogBuffer / ILogSink` 的配置、写入与读取可并发使用，入队时固定结构化字典的浅快照，并拒绝直接或间接的 buffer 转发环。
- C# `DeterministicRandomStream` 提供可恢复的 seed/step 和无模偏的 32/64 位整数采样；step 耗尽会明确失败，不允许溢出后重复序列。
- `FAudio` 同时提供 BGM/SFX 注册、淡入淡出、intro-loop、voice 限制、bus 控制，以及 `create_player_3d / play_3d`；它不保存玩法声音或感知真值。
- `FDisplay` 管理 pending/applied 窗口尺寸、原生独占全屏、vsync 和可选持久化；全屏始终使用当前屏幕原生尺寸，`size` 保留退出全屏后恢复的窗口尺寸，不伪装成显示器视频模式；`FDebug` 只管理调试能力是否启用。
- `actor / form / widget / fx` 对外统一使用 `setup / clear`，内部扩展点为 `on_setup / on_clear`。
- `actor / form / widget` 使用 `apply(vm, dt)`；`fx` 使用 `play(payload)`，完成后发出 `finished`。
- `view` 使用 `setup(root) / render(root, vm, dt) / clear(root)`，只做渲染适配，不管理对象生命周期。
- `FViewStore` 只为 `FViewRoot/FWidget` 保存 refs、props、binding、节点缓存和当前 VM；它不是 feature view。
- `ProceduralActionSampler / FProceduralPose` 是一对跨 Core/Godot 的纯采样器：宿主提供按整数 tick 排序的语义轨道，两端执行相同缓动、位置/缩放插值、Y-X-Z Quaternion slerp，并按旋转角与位移上限计算有界子采样。框架不定义游戏动作或命中语义，但宿主可以让权威规则与表现共同消费这一个轨道，禁止复制第二套曲线。
- `FMotionMatcher` 是与模型和 AnimationPlayer 解耦的特征匹配器。宿主提交带 `gait / velocity / trajectory / pose / pose_velocity / phase / contacts` 的候选数据库和同结构查询，框架按配置权重、切换代价、最短保持时间与最小收益选择候选，并返回 phase 对齐时间、过渡时长和分项误差。它不采样模型、不推进动画、不读取输入，也不产生 root motion；候选采样、查询频率、缓存和动画播放均由宿主持有。
- `FProceduralRigAdapter` 是模型无关的语义姿态合同。宿主只向它提交 `pose_family`、语义目标、socket 查询和目标拟合请求，不得让动作 view 直接依赖模型骨骼名、Skin 或 AnimationPlayer。`FSegmentedHumanoidRigAdapter` 是无 Skin 的刚性分段实现，适合调试、原型或特定美术风格；`FProceduralHumanoidRigAdapter` 驱动完整 skinned humanoid。两种实现必须暴露相同 readiness、socket、fit 和 metrics 语义，使宿主可替换外观而不重写动作。
- `FProceduralHumanoidModifier` 是完整 skinned humanoid 适配器使用的低层解算器。它通过宿主提供的语义骨骼 profile 解算可选双臂/双腿两段 IK，以及可选 `hips -> spine -> chest` 躯干链的独立附加姿态、定长可达倾斜和受限肩部摆动，并可在宿主给定的平移轴上预拟合多个手部目标。`torso_reach_offset` 必须通过固定长度的髋-脊柱-胸段映射为倾斜，`*_shoulder_swing_degrees` 必须受框架硬上限约束；不得平移胸骨或拉长肩臂来伪造可达。躯干和目标四肢的姿态都必须相对当前基础动画叠加、不得逐帧累积；清空或重新配置时只恢复仍等于 Modifier 上次输出的局部位置/旋转通道，并保留 AnimationPlayer 或其他 Modifier 已独立更新的通道。IK 必须夹取不可达目标并通过 `metrics.torso_reach / shoulder_offsets / endpoint_errors / reach_clamps` 报告真实结果；刚体道具先由共享轨道定位，身体、手和脚只能追随，IK 不得反向移动权威道具。
- `FBodyOrientation` 是不读取输入或场景树的纯表现解析器。它把移动朝向、权威瞄准、动作状态和宿主策略解析为下身朝向、动作坐标系朝向、目标上身扭转与平滑后的可见扭转。移动时下身始终由移动朝向所有，`committed` 权重只可在站立时带动下身；站立且超过软限制时才允许归中。动作坐标系必须立即跟随权威瞄准，不得被归中速度、可见扭转平滑或扭转上限修改。
- `FSkeletonAppearanceModifier` 是与具体模型无关的非权威骨骼比例层。宿主用语义骨骼 profile 映射 Rig，并为每根骨骼声明正数最小/最大缩放；Modifier 会在应用前夹取请求、忽略未映射或非法调整、保留原始姿态基准，并在清空或重新配置时恢复该基准。它只处理骨骼局部缩放，不负责换装资源、材质、碰撞体或属性。
- logic 读取 context 中的 VM/event，通过 context 数据入口或 intent 提交操作，不持有 system 本体。

## Bridge
- bridge 是 Godot 与 C# core 的唯一运行时边界。
- proto 是合同 DSL，当前不是严格 protobuf wire runtime。
- bridge 只接受固定五文件；parser 先收集完整文件集，再验证 import、共享 package 和类型引用。import 禁止父目录穿越、大小写漂移与歧义匹配。
- 支持的 proto3 子集：`syntax`、`package`、`import`、`message`、`enum`、普通字段、`repeated`、`oneof`，以及 message 内的 `reserved` 字段号/范围/名称。
- bridge 字段支持 `string / bool / float / double / int32 / int64 / uint32 / uint64 / sint32 / sint64`、同 schema message 和 enum；其他 protobuf 标量在生成前失败。
- `optional`、`map`、`service`、`option` 等未声明语法会直接报错；`reserved` 会校验字段冲突、重叠范围和非法编号。
- parser 会拒绝未知类型、重复 message/enum、重复 field 名/编号、重复 enum 名/编号、非法 tag、proto3 enum 首项非零和未闭合 block。
- schema 会按实际 C#/GDScript 命名规则检查生成的字段、成员、类型和 wrapper；不同声明映射到同一标识符时，在写文件前失败。
- `fwgen bridge` 生成 Godot 统一入口、C# bridge 类型、基础 codec、intent/event/packet codec。
- bridge schema 在一次 parse 后派生全部产物；同名 oneof payload 字段只有兼容类型才能合并，否则生成失败。
- 基础 codec 的协议版本由解析后的五文件语义生成稳定 SHA-256 指纹并截取为正整数；注释、空白和声明顺序不改变版本，package、文件角色、类型、字段、编号、重复性、oneof 或 enum 变化会自动改变版本。
- 生成 DTO 保留 proto3 零值：整数为 0、bool 为 false、string/enum unspecified 为 `""`。
- intent 表达“想做什么”，view 表达“允许看到什么”，event 表达“一次发生了什么”，packet 只做信封。
- `Fw.Rt.Bridge.WireFrame` 是纯 C# 传输帧：`FWIR + version + flags + decoded length + payload length + SHA-256 + payload`；Brotli 只在确实缩小时启用，长度校验避免整数溢出。SHA-256 只提供损坏检测，不代替认证或加密。

## Network
- `INetTransport` 统一 server、client、无连接端点、connected/unconnected 收发、主动断开、channel、投递语义和诊断快照；实现可替换，宿主无需改 packet 或 core。
- `LiteNetTransport` 是当前默认 adapter，使用独立逻辑线程与有界线程安全接收队列；提供 8 个 channel、可靠有序、可靠无序、可靠 latest、不可靠 latest 与未连接消息。队列溢出时不可靠消息计入 drop，可靠消息会断开连接以触发宿主 journal 重放。
- `NetTransportSnapshot` 同时区分应用消息 payload 与底层 wire 数据报统计，包含连接数、字节、包数、传输层丢失、错误和队列丢弃。
- `NetCommandJournal` 按单调 id 保存待确认命令，支持累计完成和显式背压；`NetCommandLedger` 为 authority 保存有界终态并保证相同 id 首次结果不被覆盖。
- `NetInputHistory<TState>` 保存有界 latest 状态历史并正确处理 `uint` tick 回绕；状态重复 tick 只替换最新值，过期 tick 不回退。
- `NetFaultSimulator<T>` 以固定 seed 注入丢失、重复、乱序和延迟，用于 transport 之外的确定性协议测试。
- LiteNetLib 只存在于 adapter 文件和 NuGet 依赖中；宿主 bridge 决定哪些 packet 是 command、state、snapshot、map 或 discovery，框架不认识这些业务分类。

## Config
- `fwgen config` 从 config schema 生成 Godot config 入口、C# typed config、路径常量和 codec。
- 宿主在 `fw.toml` 配置可选 `[gen].fwe` 后，`fwgen config` 还会生成 `_config_schema.json`，包含 schema hash、根表来源与格式、CSV 表头、字段编辑类型、嵌套 message 和 config 引用。
- config 字段支持 bridge 的基础标量、空 `Fixed32` marker 和同 schema message；enum、`bytes`、`fixed*`、`sfixed*` 当前不进入生成阶段。
- schema 会检查生成的 C# 字段、类型、配置路径和 GDScript parser 名；保留名或名称归一化冲突在写文件前失败。
- `config_check` 检查 schema 与 `data/config` 的字段一致性。
- `config_pack` 把源配置打包到 `pack/config`。
- CSV 空白单元格按字段缺省处理；Godot 编辑器态读取源 CSV 与 `config_check / config_pack` 使用相同语义，标量回落到 proto3 零值，数组回落为空数组，配置引用回落到默认项。
- config pack 使用 76-byte `WCFG` header，校验版本、schema SHA-256、payload length 和 payload SHA-256；纯 C# `Fw.Rt.Config.ConfigPack` 是格式实现，生成器负责调用它，生成 codec 只负责文件读取与 typed 映射。
- 空 `message Fixed32 {}` 是 signed Q24.8 marker；pack 时乘 256 并检查 int32 范围，读取时除 256。
- 生成清单只把 config schema 与数据文件布局视为结构输入，普通数据值变化不会要求重生成代码。
- 默认模板自带最小 `data/config/game.csv.txt`，生成后可立即通过 check/build。
- 默认模板是最小但完整的 `Godot intent -> C# GameSystem -> view/event -> Godot VM` 计数器闭环，不默认塞入网络、DS 或具体世界玩法。

## Rooms
- `Fw.Rt.Rooms` 提供与游戏无关的房间目录合同、内存目录存储、HTTP 客户端和 HMAC 入场票据；框架不默认启动目录进程，也不规定游戏网络协议。
- `RoomDirectoryStore` 只保存房间发现与准入元数据，不保存玩家档案、玩法状态或游戏网络连接；当前实现是单进程内存存储，不提供持久化或多实例一致性。
- DS 使用目录注册密钥注册房间，目录返回心跳 token、该房间独享的 admission secret 和基于租约计算的建议心跳间隔；注册密钥不直接签发玩家票据，单台 DS 不能伪造其他房间的票据。
- DS 按不大于目录建议值的间隔提交人数、状态以及可变的地图、容量和标签；超过 stale 时间未心跳的房间自动移除。客户端按 `game id + protocol version` 查询，因此不兼容版本不会互相显示。
- 客户端普通加入前向目录申请短期 ticket；创建房间使用带可选 `CreatePayload` 的原子 `Allocate` 预留一台无人房间，避免并发创建者取得同一台 DS。`RoomAllocationRequest.PreferredHost / PreferredPort` 可把候选限制到已注册的 endpoint，空字符串和端口 `0` 表示不限制；它们不能绕过目录直接连接未注册服务。目录不解释 payload 的游戏语义，只限制其为最多 1024 个 UTF-8 字节，并把原文签入 ticket 后返回。预留随 ticket 到期自动释放，首名玩家加入并被心跳确认后转为普通开放房间。
- 游戏 authority 应在分配玩家身份与创建权威状态前验证 game、room、过期时间、签名和 `CreatePayload`，自行限制 ticket 重放，并由宿主规则原子校验和应用创建参数；不得先创建默认玩法状态再用普通房间命令补改。
- `RoomStatus.Playing` 表示已开始且允许宿主提供观战的房间；普通 `List` 只返回可加入状态，`ListSpectatable` 单独返回 playing，避免玩家加入与观战准入混用。
- `RoomTicket` v3 把 `play / spectate` purpose 与规范化 permission 列表纳入 HMAC；v2 票据只按 `play` 兼容读取。`Join` 只签发 play ticket，`Spectate` 只签发 spectate ticket，具体视角、延迟和人数策略仍由宿主 authority 执行。
- `RoomDirectoryClient` 仅允许 loopback 使用 HTTP；非本机目录必须使用 HTTPS。注册密钥、心跳 token 和 admission secret 都不得进入客户端或源码配置。

## 生成
- `SystemGen`、`BridgeGen`、`ConfigGen` 只编排流程；schema 层先解析并校验完整语义模型，Godot/C# renderer 只消费同一模型，不再各自推断 root、enum 或字段集合。
- `BridgeModel` 固定一次解析得到 intent/action/event/packet root、enum、view 和字段集合；`ConfigModel` 固定一次解析得到 message、root、字段集合与 schema hash。校验器和 renderer 因此不会使用两套命名或分类规则。
- 所有输出使用确定性排序；重复生成内容保持一致。
- 所有文本产物统一使用 LF、移除行尾空白，并且文件末尾恰好保留一个换行，避免编辑器规范化造成清单误报。
- `system / bridge / config / config_pack` 先把本次写入和删除全部放入内存批次，再准备同目录临时文件并提交；进程内任一步失败都会逆序恢复旧文件，新建文件会删除。
- 生成清单与对应代码在同一批次提交；清单 hash 直接基于待提交字节计算，不会提前认可磁盘旧产物。
- 内容未变化的文件不会重写；`config_pack` 同批删除不再对应当前 config root 的旧 `.bin`。
- `csharp/_gen/_fwgen_manifest.json` 记录生成器、输入和完整输出集合的 hash，包括启用的 FWE 契约；`fw check` 拒绝缺失、过期、集合异常或被手改的生成产物。
- `fw check` 同时检查路径、目录、角色后缀、禁止引用以及 system/bridge/config schema。
- `new` 在返回成功前自动完成生成、`config_check` 和 `fw check`。

## 测试
- `FwGenTests` 按 `proto / system / bridge / config / runtime / api` 分组，覆盖合法/非法 proto、import/package/oneof、proto 零值、生成标识符冲突、system phase/回滚/fault 清理、生成锁、批次新增/替换/删除回滚、生成清单、config pack 和 wire frame，包括 import 穿越/歧义、数字溢出、格式头、版本、校验和、长度边界与逐字节变异。
- `tools/test.ps1`、`tools/test.sh` 会构建 runtime/generator，运行 `FwGenTests` 与 `FwRuntime.Verify`，并在全新临时目录验证 `new -> check -> config_pack -> build`。
- 测试会比较规范源与模板镜像，并验证重复生成、重复打包的内容完全一致。
- 本地存在 Godot .NET 时继续执行 headless editor 扫描、编辑器改写后的二次 check/build、runtime 故障注入、通用服务探针和主场景启动；可用 `GODOT_BIN` 显式指定可执行文件。
- `.github/workflows/ci.yml` 使用只读仓库权限，在 Windows 与 Linux 安装固定 Godot .NET，并执行同一完整测试链；同一引用的新任务会取消旧任务，单个 job 最长运行 30 分钟。
- `fw/csharp/Directory.Build.props` 统一 FwGen、FwRuntime 与测试工程的 target framework，并把 C# 警告视为错误；默认模板对宿主使用同一规则。
- C# snapshot 冻结 `Fw.Rt.*` 的公开类型、继承关系、构造、字段、属性访问器、事件、方法和运算符；Godot snapshot 自动扫描 `fw/scripts/fw` 下全部 `class_name`，冻结直接基类、方法签名与默认值、signal、属性和常量值。
- `fw/tests/runtime_test.gd` 覆盖 binding 所有权、pool 状态互斥、ViewStore 缓存、UI wrapper/form logic、失效 UI stack、GDScript system 与 mode 回滚；`fw/tools/verify_runtime.gd` 覆盖 event/FSM/system、asset 并发与 provider 生命周期、pool/log/audio/display/debug。普通 Godot `ERROR` 默认会让测试失败，仅逐条列出的故障注入可放行。

## 治理
- `fw/docs` 与 `fw/.codex/skills/fw/SKILL.md` 是框架规范源。
- 模板中的 skill/docs 是派生产物。
- `hooks/pre-commit` 只做 `fw -> template` 同步；若更新派生文件会中止提交，要求审阅并重新暂存。
- hook 不读取父工程、不自动 `git add`，`new/gen/build` 也不修改 Git 配置。
