# RollDice EasyGame Parity

## 范围

本报告对比：

- Unity：`D:/UGit/YaoTou/Assets/Scripts/EasyGame`
- Godot：`<game_root>/fw`
- 实际使用统计：只统计 `Assets/Scripts/RollDice` 中引用该 EasyGame 类型的 C# 文件数，不把框架自己的定义计入。

目标是迁移可跨游戏复用的语义，不追求 Unity API 或类名逐字兼容。Godot 已有可靠原生能力时直接采用原生能力；玩法规则、内容协议和第三方解释器留在宿主工程。

## 对比结果

| Unity 能力 | RollDice 使用 | Godot fw 结果 | 结论 |
|---|---:|---|---|
| `ERoot / ESystems` | `ESystems` 5 个文件 | `AppRoot / BaseMode / SystemManager`，C# `SystemRuntime` | 已迁移并增强：app/mode 父子 scope、显式依赖、稳定拓扑、初始化回滚、反向 shutdown、依赖安全移除、snapshot |
| `EFormSystem / EViewBinder / EFormHost` | 23 个文件 | `FUI / FForm / FWidget / FView / FRefs / FProps / FBinding` | 已有 Godot 等价层；scene 引用与 Inspector 配置替代 Unity attribute/reflection 绑定 |
| `ELoadSystem` | 32 个文件 | `FAsset` | 已迁移：同步固定缓存、引用句柄、threaded load、同路径异步合并、`res/user` 路径归一化、自定义 provider、安全注销与成对 release |
| `EAudioSystem` | 12 个文件 | `FAudio` | 已迁移：BGM crossfade、intro-loop、一次性/循环 SFX、可停止 player handle、voice pool/limit、bus volume/mute |
| `EDisplaySystem` | 6 个文件 | `FDisplay` | 已迁移：尺寸档位、pending/applied、changed、前后档位、fullscreen/vsync、apply/cancel、可选持久化 |
| `ELog / ELogSystem` | 35 个文件 | Godot `FLog`，C# `LogBuffer` | 已迁移：level、category threshold、结构化字段快照、固定容量历史、并发访问、signal/event、可选 sink |
| `ERandomStream / ERandomPicker` | 6 个文件 | C# `DeterministicRandomStream / RandomPicker` | 已迁移并接管 CardCiv 原重复实现；保留 seed/step 存档恢复和原 `Fork(channel)` 结果，32/64 位整数采样无模偏 |
| `EDebugRuntime` | 6 个文件 | `FDebug` | 通用开关已迁移；Unity `MaskableGraphic/VertexHelper` 绘图不迁移，Godot View 使用 `_draw`、mesh 或 gizmo |
| `EEvent<TKey>` | 0 个文件 | Godot `FEventBus`，C# `EventBus<TKey>` | 已迁移为公共能力，并由 `FUIEvent` 复用；增加 priority、once、订阅句柄、派发快照和 C# payload 预检 |
| `EFSMSystem` | 0 个文件 | Godot/C# `FStateMachine / StateMachine` | 已迁移为公共能力；增加 guard、payload、比较器一致性、失败恢复和链式迁移上限 |
| `EPoolSystem` | 0 个文件 | `FPool` | fw 原有能力已加固：活动/空闲跟踪、幂等 warmup、容量、重复回收保护、全量清理 |
| `ETweenSystem / EFloatCurve` | 0 个文件 | Godot `Tween / Curve` | 不复制 Unity 调度器；Godot 原生对象负责插值、并行/串行动画和 easing |
| `ECommandSystem` | 0 个文件 | generated intent/event + core system | 不迁移未使用的第二套命令队列；跨边界命令继续由 schema、bridge 和 core 权威层负责 |
| `ELuaSystem` | 13 个文件 | `Fw.Rt.Script` + CardCiv `core/config` 与 `core/rules` | 通用 Lua 沙箱、预算和值边界进入 fw；Mod 路径、effect command 和玩法函数合同仍属于宿主 |
| `ETalkRun` | 4 个文件 | 宿主 mode/flow | 不放入 fw；节点类型、条件、动作和关闭原因都是具体游戏的对话领域模型 |
| `Fix32` | EasyGame 基础类型 | config pack 的 `Fixed32` 合同 | 不再增加第二套运行时数值类型；fw 只保持生成/打包格式，权威 core 按项目需求选择数值实现 |

## 运行时所有权

| Scope | 所有者 | 生命周期 | 典型内容 |
|---|---|---|---|
| app | `AppRoot` | 应用全程 | asset、audio、display、event、log、debug、跨 mode system |
| mode | `BaseMode` | 单个 mode | 当前玩法/表现 system；可读取 app scope，不可反向被 app scope 依赖 |
| core | `GameCore` | 一局权威状态 | C# rules/state/system；不持有 Godot Node 或 UI |
| view | Form/Widget/Actor/Fx | scene 节点生命周期 | 输入、渲染、动画、音效触发；不保存玩法权威状态 |

## 验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\fw\tools\verify_runtime.ps1 -ProjectRoot .
dotnet build .\CardCiv.csproj -nologo
godot --headless --path . --quit-after 3
```

`Fw.Verify` 覆盖 system scope/dependency/rollback/removal、event payload、FSM 比较器/失败恢复、32/64 位 random 和并发 log。Godot probe 覆盖 system refs/order、asset sync/threaded/coalescing/provider/handle、pool、audio、display、debug 和严格 GDScript 日志检查；Windows/Linux 主测试脚本都会执行两者。

## 非目标

- 不承诺 Unity 源码级 API 兼容。
- 不把 CardCiv 或 RollDice 的玩法、Lua effect contract、对话节点、GM 命令搬进 fw。
- 不包装 Godot 已有的 Tween/Curve 形成第二套动画运行时。
- “完整”以已列公共语义、自动测试和宿主集成通过为准，不表示未来游戏不再需要新增框架能力。
