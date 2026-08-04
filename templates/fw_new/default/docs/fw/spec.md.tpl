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
- `AppRoot` 创建 mode host、app system scope、`FUI`、`FPool`、`FAsset`、`FEventBus`、`FLog`、`FAudio`、`FDisplay` 和 `FDebug`，并负责 mode 切换。
- `BaseMode` 负责场景、Godot system 和 presentation 装配。
- `SystemManager` 按 phase 缓存后的顺序执行 `init / tick / shutdown`，shutdown 使用反向顺序。
- C# `SystemRuntime` 使用同样的生命周期和 phase 语义，由 `GameCore` 持有。
- `AppRoot` 持有全局 system scope；`BaseMode` 持有以全局 scope 为 parent 的局部 scope。两端 runtime 都按 phase 和显式 dependency 做稳定拓扑排序。
- 两端 runtime 都显式区分 created、initializing、running、faulted、stopping、stopped；失败初始化会把失败项本身也纳入逆序回滚。
- `AppRoot` 的 mode 切换先清理旧 mode/UI/pool；新 mode enter 失败时再次清理半成品，离开 SceneTree 时执行最终 shutdown。

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
- 环境元数据显式声明确定/随机、完全/不完全信息、顺序/同时行动和收益类型；`GameActors` 保留 chance、terminal 与 simultaneous 特殊行动者，终局和资源上限截断不得混为一类。
- `Model` 的 `IPolicyValueModel` 只接收玩家观察和当前合法动作，返回可变动作集合上的先验与逐玩家价值；高容量模型运行时由宿主注入。框架另提供由宿主特征编码器驱动、可导出 checkpoint 的 `LinearPolicyValueModel`，作为无外部 ML 依赖的可训练基线和接入探针。
- `Search` 提供可暂停的确定性 `BeamSearch` 和 PUCT。Beam 只接受单人或合作型确定性规划；PUCT 支持完全信息的顺序玩家与显式 chance 节点，按稳定动作顺序打破平局。不完全信息必须先由宿主做确定化或实现信息集算法，框架不会把完全信息搜索伪装成公平策略。
- 搜索预算分别按展开节点数和模拟次数计量；终局 `Payoffs` 是完整局面价值，`Transition.Rewards` 留给训练轨迹，搜索不会把两者重复累计。
- `Training` 提供策略目标、策略价值样本、完整/截断轨迹、确定性定容 replay buffer，以及对线性基线执行交叉熵与价值回归的固定顺序小批量训练器；`Evaluation` 分开统计成功、失败、平局、截断、平均收益和 Wilson 成功率区间。
- `Utility` 按稳定注册顺序评分，支持权重、确定性噪声与切换阈值；分数相同保持先注册项。
- `Behavior` 使用 `Success / Failure / Running / Suspended`，只读树定义与 `BehaviorSession` 分离，同一棵树可供多个会话使用。
- `Plan` 提供基于 typed fact/action/goal 的增量 GOAP；`PlanSearch` 在预算耗尽后保留搜索状态，下一 tick 继续。
- `Nav` 提供增量 A*、LRU `PathCache`、反向邻接流场和基于 `System.Numerics.Vector2` 的 Steering，不依赖 Godot 类型。
- `Policy` 只定义本地、远程和回退策略合同；HTTP、API key、prompt、模型供应商、输入过滤与游戏语义留在宿主工程。
- AI 模块不读取宿主 context、不直接修改世界、不生成具体游戏命令，也不要求游戏同时使用全部算法；状态/动作编码、奖励、胜负、课程和训练参数始终属于宿主游戏。

## Present
- `FUI` 使用 `open / close` 管理 form 与 UI layer。
- `FUI.open` 拒绝空 id，先实例化并 setup 新 form，成功后才关闭同 id/同层旧 form；screen stack 只在提交后隐藏前一项。
- `FForms.setup` 与 `FFormLogic.attach_ui/detach_ui` 具有幂等清理语义；form 被外部 `free/queue_free` 时，查询或关闭会剔除失效项并恢复上一层 screen。
- `FPool` 使用 `register_prefab / warmup / spawn / recycle / flush` 管理 actor 和 fx，并跟踪容量、generation、active/free 与重复回收。
- `spawn(key, parent, owner, props)` 把生命周期 owner 和 props 显式传给对象；Pool 同时追踪 active/free 对象。
- `FAsset` 保留 `load / unload` 固定缓存，并提供引用句柄、异步加载、路径归一化和自定义 provider；同路径并发异步请求只执行一次 provider load，加载失败不缓存空值。
- `FAsset` 为每个缓存项保存实际加载 provider，保证 provider 从注册表移除后仍能成对 release；默认不允许在活动 handle 或加载请求存在时替换/注销 provider，`force` 只用于明确接受句柄失效的整体 teardown。
- `FEventBus` 提供去重订阅、优先级、once 和安全快照派发；C# 同一 key 只允许一种 payload 类型，并在调用任何 listener 前完成类型预检。
- `FStateMachine` 提供 guard、payload 与受限链式 transition；C# 自定义状态比较器同时作用于 state 和 transition，生命周期/事件回调抛错后清空当前状态并允许重新启动，`Clear` 即使 exit 失败也会移除注册。
- `FLog` 提供 level、category threshold、结构化数据和固定容量历史；C# `LogBuffer / ILogSink` 的配置、写入与读取可并发使用，入队时固定结构化字典的浅快照，并拒绝直接或间接的 buffer 转发环。
- C# `DeterministicRandomStream` 提供可恢复的 seed/step 和无模偏的 32/64 位整数采样；step 耗尽会明确失败，不允许溢出后重复序列。
- `FAudio` 同时提供 BGM/SFX 注册、淡入淡出、intro-loop、voice 限制、bus 控制，以及 `create_player_3d / play_3d`；它不保存玩法声音或感知真值。
- `FDisplay` 管理 pending/applied 尺寸、全屏、vsync 和可选持久化；`FDebug` 只管理调试能力是否启用。
- `actor / form / widget / fx` 对外统一使用 `setup / clear`，内部扩展点为 `on_setup / on_clear`。
- `actor / form / widget` 使用 `apply(vm, dt)`；`fx` 使用 `play(payload)`，完成后发出 `finished`。
- `view` 使用 `setup(root) / render(root, vm, dt) / clear(root)`，只做渲染适配，不管理对象生命周期。
- `FViewStore` 只为 `FViewRoot/FWidget` 保存 refs、props、binding、节点缓存和当前 VM；它不是 feature view。
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
- DS 按不大于目录建议值的间隔提交人数与状态；超过 stale 时间未心跳的房间自动移除。客户端按 `game id + protocol version` 查询，因此不兼容版本不会互相显示。
- 客户端加入前向目录申请短期 ticket；游戏 authority 应在分配玩家身份与创建权威状态前验证 game、room、过期时间和签名，并自行限制 ticket 重放。
- `RoomDirectoryClient` 仅允许 loopback 使用 HTTP；非本机目录必须使用 HTTPS。注册密钥、心跳 token 和 admission secret 都不得进入客户端或源码配置。

## 生成
- `SystemGen`、`BridgeGen`、`ConfigGen` 只编排流程；schema 层先解析并校验完整语义模型，Godot/C# renderer 只消费同一模型，不再各自推断 root、enum 或字段集合。
- `BridgeModel` 固定一次解析得到 intent/action/event/packet root、enum、view 和字段集合；`ConfigModel` 固定一次解析得到 message、root、字段集合与 schema hash。校验器和 renderer 因此不会使用两套命名或分类规则。
- 所有输出使用确定性排序；重复生成内容保持一致。
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
