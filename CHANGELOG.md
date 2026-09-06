# Changelog

## Unreleased

- 代码仓库更名为 FWC（`fiofiogamestudio/fwc`），宿主 `fw/`、`fw.toml`、`Fw.*`、生成协议与 CLI 保持兼容。通用 Agent 技能迁至独立 FWS 的 `fw-code`；FWC 和默认模板移除旧 `fw` 技能，文档仍由 FWC 自己维护。已有宿主技能需审阅定制后显式迁移，升级不会自动删除。
- Net 默认 adapter 变为目标级可选：新增 `[use].game_net_adapter / host_net_adapter = "lite" | "none"`，默认保持 `lite`，自有 transport 可排除 LiteNetLib 引用。FW 编辑元数据仍为可选数据合同，不引入 FWE/FWA 依赖。
- 修复 Tick 内 shutdown 的遍历失效和 stopped 状态被覆盖，拒绝递归 Tick，Init 内 shutdown 不再复活已清理 runtime；C#/Godot 配套行为回归覆盖取消、清理异常和后续派发。
- 完整测试在生成数值探针前解析并传递 Godot，CI 禁止静默跳过；Linux 编辑器导入行为与 Windows 对齐。
- **配置合同破坏性修正**：C# `double / uint32 / Fixed32` 分别生成 `double / uint / double`，不再缩窄或改变符号；check、pack 和生成 codec 检查数值范围及有限性。Fixed32 在 CSV/JSON/pack 两端一致执行 Q24.8 量化并保留完整精度。
- **64 位配置迁移**：JSON 源中超过安全整数范围的 `int64 / sint64 / uint64` 必须为十进制字符串；CSV 保留完整文本范围，pack 统一写 64 位十进制字符串。Godot `uint64` 始终返回字符串，C# 保留 `ulong`；可选编辑合同为大整数选择 string 编辑器并携带无损范围元信息。
- **配置只读迁移**：C# repeated 属性改为防御复制并冻结的 `IReadOnlyList<T>`，嵌套配置及 ConfigPath 列表不泄漏可变集合；Godot 返回递归只读快照，错误加载不缓存部分结果或伪造默认值。升级须重新生成、检查、打包并迁移宿主类型/集合调用点，不手改 `_gen`。
- Godot 配置加载保留 JSON 数值原文并精确执行 binary32/binary64 最近值、中点取偶舍入，修复极小数归零及边界精度漂移；数值原文统一设 4096 字符预算，超限显式拒绝，不截掉有效数字。编辑器仍可直接读取源数据。
- C# 配置读取在解析前无损规范化十进制阶，绕过 .NET 8 超长系数的错误归零；默认回归追加 512 组原始 IEEE 位模式往返，不依赖 Godot 字面量作为期望值。
- **Bridge 数值与协议迁移**：typed payload 保留 `double / uint / long / ulong`，读写不再隐式截断，Godot `uint64` 使用规范十进制字符串。含宽数值的 schema 引入新的 codec 协议指纹，旧协议明确拒绝；发送/接收端均须重新生成和迁移，默认模板的计数事件与 tick 已配套更新。
- Bridge 的既有 ID 别名进入统一数值校验；非空 `*Id` message 与歧义 `*Id` enum 在生成前报错，不再静默忽略真实字段。整数 marker 保留原范围，结构化类型应改名后重新生成。
- Bridge wrapper 避免遮蔽生成入口：`BridgeView / BridgeEvent` 保留完整名称，不再缩成 `Bridge`；使用过旧短名的 GDScript 调用点需同步迁移。C# payload 的限定名称也避免合法 `System` 类型与系统命名空间冲突。
- 新增真实生成/编译/运行的 C# 配置往返与 Godot 4.6.2 数值探针，覆盖完整整数范围、浮点有限性、Q24.8、CSV/JSON/pack 一致性、拒绝矩阵、集合别名与错误加载恢复。
- 运行时拆为必带 `core` 与 `app / anim / net / rec / ai / lua` Kit，训练和网络故障模拟移入 tool；新增必填的 `[use].game/host`、`fw sync`、目标级 C# 引用与 Godot 投影，不再提供 `FwRuntime` 聚合程序集、类型转发或旧 Godot 路径回退。
- 新增 `FLocalization`、标准目录 provider 与 C# `LocalizedMessage/LocalizedAsset` 语义契约，支持确定性 provider 覆盖、语言回退、命名参数、复数/选择、伪本地化、文本/资源绑定和缺失诊断。
- 新增通用整局 AI 基础设施：游戏环境/机会节点合同、策略价值模型、固定预算 Beam 与 PUCT、训练轨迹、确定性 replay buffer、可持久化线性 policy/value 训练基线和带 Wilson 区间的批量评测。
- 新增可独立组合的纯 C# AI 模块：固定工作量预算与追踪、Utility、Behavior Tree、增量 GOAP、增量 A*、LRU 路径缓存、流场、Steering，以及本地/远程/回退 Policy 合同。
- 补齐 RollDice EasyGame 可复用运行时：app/mode system scope、资源/provider、事件、状态机、日志、随机、音频、显示、调试与对象池，并保持玩法权威和表现层边界。
- 加固 `FAsset` 同路径异步请求合并、provider 注销/释放所有权与失效 Object 检查；加固 `FUI` 空 id、外部释放和 queued-for-deletion 栈恢复。
- C# EventBus 增加单 key payload 契约和派发前预检；StateMachine 统一自定义比较器并在生命周期回调失败后复位；LogBuffer 增加线程安全、即时容量裁剪、结构化字典快照和转发环拒绝。
- `DeterministicRandomStream` 增加无模偏 `NextInt64` 与 step 耗尽保护，`RandomPicker` 不再用浮点数采样大权重。
- Windows/Linux 完整测试链现在固定执行 `Fw.Verify` 和 Godot 通用服务探针，并继续把非白名单 Godot 日志错误视为失败。
- Godot 编辑器探针改用完成资源导入后退出的 `--import`，避免 4.6 在首帧 `--quit` 时读取尚未初始化的编辑器设置。

## 0.1.4 - 2026-07-19

- Bridge/Config 先解析为单一语义模型，校验器与两端 renderer 共用 root、enum、字段和生成命名结果。
- System、Bridge、Config 与 Config Pack 改为批次事务提交；代码、旧产物删除和 manifest 任一步失败都会回滚，配置打包同时清理过期 `.bin`。
- C# 与 Godot 增加自动发现的精确公共 API snapshot，覆盖继承、默认值、常量、属性访问器、事件和运算符；生成事务补齐新增、替换、删除与故障回滚测试。

## 0.1.3 - 2026-07-19

- System、Bridge 与 Config schema 共用生成标识符冲突校验，并在写出产物前拒绝 C#/GDScript 名称归一化、保留名和后缀折叠冲突。

## 0.1.2 - 2026-07-18

- schema 在写文件前校验目标端支持类型与生成标识符冲突，避免产出无法编译或无法双端还原的代码。
- 生成锁与路径归属判断遵循平台大小写语义，bridge 入口严格限制为单一 Godot.Node 类型。
- 框架和默认项目把 C# 警告视为错误；CI 增加最小权限、并发取消、超时边界，并使用 Node.js 24 版 checkout action。

## 0.1.1 - 2026-07-18

- 测试工程改用独立的框架源码夹具，避免符号链接路径身份导致 MSBuild reference assembly 竞态。
- Godot .NET 测试统一识别 `GODOT_BIN`、`GODOT` 与 `GODOT4`，并使用无窗口原生进程启动，兼容 Windows CI 的非交互会话。

## 0.1.0 - 2026-07-18

- System runtime 增加显式生命周期、初始化回滚和幂等 shutdown。
- Pool、binding、view store、UI wrapper/form logic、UI stack 和 mode 切换补齐失效对象与失败回滚处理。
- Bridge parser 增加 import/package/oneof 校验，拒绝 import 穿越与歧义；生成器统一 proto3 零值并只解析一次 schema。
- Bridge/Config 生成器按编排、schema、Godot 渲染、C# types/codec 与 config pack 拆分内部实现，生成命令和产物保持兼容。
- 新增纯 C# `WireFrame`，统一版本、长度、压缩上限和 checksum，并加固长度溢出边界。
- Config pack 增加 schema hash、payload checksum、缓存与原子写入；格式编解码收束到纯 C# `Fw.Rt.Config.ConfigPack`，并明确 `Fixed32` Q24.8 约定。
- 默认模板改为可运行的 Godot/C# 双向最小闭环，并固定 .NET/Godot 工具链。
- CI 在 Windows/Linux 运行 FwGen、模板、Godot runtime 和主场景测试。
- 补齐 WireFrame、ConfigPack、SystemRuntime 与 proto 数字溢出的边界测试；空白配置 key 和超范围 proto 数字现在会明确失败。
- 明确 SemVer tag、submodule commit、公共 API 与宿主生成产物的兼容升级边界。
- 增加 C# 与 Godot 公共 API 合同测试，并以逐字节确定性变异验证 WireFrame 和 ConfigPack 的损坏拒绝能力。
- Godot .NET 测试探测现在会解析 WinGet/Unix 符号链接并统一校验显式 `GODOT_BIN`，避免 Mono 已安装却被静默跳过。
