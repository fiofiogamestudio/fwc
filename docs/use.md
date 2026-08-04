# Fw Use

## 接入
- 前提：工程根目录已有 `project.godot`，并把 `fw/` 放在根目录。
- 空目录也可由默认模板创建最小 `project.godot`；已有文件默认不会覆盖。
- 初始化：`powershell -ExecutionPolicy Bypass -File fw/tools/new.ps1 -ProjectRoot . -Name MyGame`。
- Linux/macOS：`bash fw/tools/new.sh --project-root . --name MyGame`。
- 项目名必须以字母开头，只使用字母、数字和下划线；C# namespace 会自动转换成 PascalCase。
- `fw.toml` 只写模板列出的 section/key，路径使用工程根目录内的相对路径；未知字段、重复字段、空值或 `../` 越界路径会直接失败。
- `new` 会生成 system/bridge/config，随后运行配置检查和整体检查；失败不会报告创建成功。
- `project.godot` 必须包含 `[dotnet] project/assembly_name="<project name>"`；`fw check` 会在运行前发现不一致。

## 日常命令
- 生成 system：`fw/tools/gen.ps1 system`。
- 生成 bridge：`fw/tools/gen.ps1 bridge`。
- 生成 config：`fw/tools/gen.ps1 config`。
- 检查配置：`fw/tools/gen.ps1 config_check`。
- 打包配置：`fw/tools/gen.ps1 config_pack`。
- 检查工程：`fw/tools/check.ps1`。
- 完整构建：`fw/tools/build.ps1`。
- 完整测试：`fw/tools/test.ps1`。
- Unix 使用同名 `.sh` 脚本；Windows/Unix 默认 build 流程一致。
- `system / bridge / config / config_pack` 都按批次提交；命令中途失败时保留调用前的完整产物与 manifest，不需要手工修补 `_gen`。
- `config_pack` 会删除已不再对应当前 config root 的旧 `.bin`，`pack/config` 不应存放手写文件。

## 修改 System
1. 修改 `schema/systems.toml`。
2. Godot system 声明 phase、script、context 和 context refs。
3. C# core system 声明 phase 和 type。
4. 运行 `fw/tools/gen.ps1 system`。
5. 不手改 `_godot_systems.gd` 或 `_core_systems.cs`。

## 修改 Bridge
1. 按语义修改 `schema/bridge/value.proto`、`intent.proto`、`view.proto`、`event.proto` 或 `packet.proto`。
2. 只使用 `fw/docs/spec.md` 声明的 proto3 子集。
3. 运行 `fw/tools/gen.ps1 bridge`。
4. 任意未知语法或重复字段都会阻止生成，先修 schema，不绕过 parser。
5. 五个 proto 必须保留固定语义与共享 package；不要新建第六类 bridge proto。

## 修改 Config
1. 只改数据值：修改 `data/config/*.csv.txt` 或 `.json`，运行 `config_check`，发布前运行 `config_pack`。
2. 改字段/schema 或切换 CSV/JSON 文件布局：同步 schema/data，再运行 `config`、`config_check`、`config_pack`。
3. 使用小数定点时在 schema 声明一次空 `message Fixed32 {}`，字段类型写 `Fixed32`；不要给 marker 添加字段。
4. 宿主需要 FWE 等结构化编辑器时，在 `[gen]` 增加 `fwe = "tools/fwe/_gen"`；编辑器只消费生成的 `_config_schema.json`，不要再维护表头或字段类型副本。

## C# Node
- GDScript 需要创建 C# bridge Node 时调用 `FCSharp.create_node("res://csharp/bridge/<name>_bridge.cs")`。
- C# 文件名、类名必须大小写完全一致，类型必须继承 `Godot.Node`。
- 若创建失败，先检查 Godot .NET、Debug 构建和 `project.godot` assembly name，不回退到裸 `script.new()`。

## AI 算法
- 宿主在自己的 core system 中创建并调用 AI 对象；不要给 Utility、Behavior、Plan 或 Nav 再套 `init/tick/shutdown` 生命周期。
- 每次权威计算创建 `DecisionScope(tick, budget, random, trace)`；budget 使用候选数或节点数，不使用毫秒数。
- 接入整局 AI 时先实现 `IGameEnvironment<TState,TObservation,TAction>`：`Clone` 必须隔离可变状态，`StateKey` 必须确定，合法动作必须唯一且顺序稳定，`Step` 只修改传入的搜索副本。
- `Result` 对仍在进行的局返回 `Running`，真实结局返回 `Terminated`，动作/回合上限返回 `Truncated`；每一步 rewards 和最终 payoffs 都按 `Spec.PlayerCount` 返回。
- 单人确定性关卡先用 `BeamSearch` 和宿主启发式函数建立可解释基线；每帧继续对同一实例调用 `Step(scope)`，成功后只提交结果中的第一个动作并重新从权威状态搜索。
- 大分支、随机或多玩家顺序游戏使用 `PuctSearch`；当前实现要求完全信息，隐藏信息游戏应由宿主先按玩家信息集确定化。`IPolicyValueModel` 必须只根据传入 observation 与 legal actions 生成先验和逐玩家价值，缺失先验会被归一化为零，全部为零时退化为均匀先验。
- 自博弈时把根节点访问分布、最终选中动作、即时 rewards 和最终 payoffs 写入 `PolicyValueSample`/`TrainingTrajectory`；用 `ReplayBuffer` 固定容量并用框架随机流确定性抽样。
- 无 ML 运行时的首个训练闭环可实现 `IPolicyValueFeatureEncoder`，用 `LinearPolicyValueTrainer` 训练并通过 `LinearPolicyValueCheckpoint.ToJson/FromJson` 持久化；policy 特征必须同时编码观察、行动者和候选动作，value 特征必须明确观察者与被估值玩家，输入应在宿主侧归一化。宿主还应在 checkpoint 外层记录特征 schema id、数据集哈希和训练配置。
- 线性模型只用于验证数据、训练、checkpoint、推理和搜索接线，不能替代复杂游戏所需的神经网络；升级模型时保持 observation/action、样本和评测合同不变，由宿主提供新的 `IPolicyValueModel`。
- 批量评测用 `EvaluationAccumulator`；发布门槛同时检查成功率、Wilson 下界、截断率、平均步数和与旧策略同种子对照，不只看单局通关。
- Utility 用 `UtilitySelector` 选择目标，FSM 用现有 `StateMachine` 稳定执行动作，Behavior Tree 用 `BehaviorSession` 保存 Running 节点。
- GOAP 使用 `GoalPlanner.Begin` 创建独立 `PlanSearch`，A* 直接创建 `PathSearch`；返回 `Searching` 时保留对象并在后续 tick 继续调用 `Step`。
- 流场图必须通过 `IFlowGraph.Incoming` 提供反向邻接；普通 A* 图通过 `IPathGraph.Neighbors` 提供正向邻接。
- 远程模型通过 `RemotePolicy` 注入宿主自己的异步调用，并用 `FallbackPolicy` 接本地策略；密钥和网络客户端不得进入 `fw`。
- 算法结果必须先由宿主验证，再转换为自己的 intent 或 command；框架算法不得直接修改玩法状态。

## 房间目录
- 服务端使用 `RoomDirectoryStore` 提供 register、heartbeat、unregister、list 和 join HTTP 端点；具体 Web host、数据库和部署方式由游戏工程决定。
- DS 使用 `RoomDirectoryClient.RegisterAsync` 注册并保管返回的 heartbeat token 与 admission secret，随后按不大于 `HeartbeatIntervalMilliseconds` 的间隔调用 `HeartbeatAsync`；不要在客户端重复猜测目录的 stale 配置。
- 客户端只调用 `ListAsync(gameId, protocolVersion)` 和 `JoinAsync(roomId)`；admission secret 永远不返回客户端。
- authority 使用 `RoomTicket.TryValidate` 校验短期票据后才创建玩家，并按 ticket nonce 与连接身份实现一次性或绑定式消费。
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

## 验证
- 最小验证：`fw/tools/check.ps1`。
- 提交前验证：`fw/tools/test.ps1`。
- 通用运行时扩展验证：`fw/tools/verify_runtime.ps1 -ProjectRoot .`；完整测试已经自动执行同一 C#/Godot 探针。
- 测试会创建并清理临时项目，不写入宿主工程。
- 本机安装 Godot .NET 时，测试会额外执行 headless 脚本扫描和主场景启动；可用 `GODOT_BIN` 指定版本，或用 `-SkipGodot` 跳过。
- 冷缓存较慢时可用 `FW_GODOT_EDITOR_TIMEOUT_SECONDS` 和 `FW_GODOT_RUN_TIMEOUT_SECONDS` 调整 headless 超时；默认分别为 90 秒和 30 秒。
- 正式提交不应使用 `-SkipGodot`；该参数只用于明确缺少 Godot 的临时环境。
- 公共 API snapshot 变化默认直接失败。只有确认该变化符合 SemVer 和迁移要求后，框架维护者才可临时设置 `FW_UPDATE_API=1`，分别运行 FwGenTests 与 Godot runtime test 更新基线；随后必须取消变量、审阅 diff 并重新完整测试。

## 升级
1. 保持宿主工作区可区分，记录当前 `fw` submodule commit。
2. 把 `fw` 切换到目标 SemVer tag 或明确 commit，不直接依赖远端浮动分支。
3. 依次运行 system、bridge、config 生成和 `config_check`，再运行 `check`、`build`、`test`。
4. 审阅公共 API、schema 合同和生成产物差异；按 `CHANGELOG.md` 完成必要迁移。
5. 验证通过后，在同一宿主变更中提交 `fw` 指针和对应生成产物，避免其他电脑检出不一致组合。
6. 任一步失败时先恢复旧 submodule commit，不手改 `_gen` 产物绕过检查。

## Hook
- 框架维护者可显式启用：`git -C fw config core.hooksPath hooks`。
- hook 只同步 `fw` 自己的 skill/docs 到默认模板。
- 若同步产生差异，提交会停止；检查并暂存派生文件后再次提交。
- 普通 `new/gen/build` 不会修改 Git hook 配置。
