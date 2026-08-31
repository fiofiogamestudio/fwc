# Changelog

## Unreleased

- 运行时拆为必带 `core` 与 `app / anim / net / rec / ai / lua` Kit，训练和网络故障模拟移入 tool；新增 `[use].game/host`、`fw sync`、目标级 C# 引用与 Godot 投影，同时用完整类型转发保留 `FwRuntime` 和旧 Godot 路径的一版兼容入口，并清理清单记录的旧投影根。
- 新增 `FLocalization`、标准目录 provider 与 C# `LocalizedMessage/LocalizedAsset` 语义契约，支持确定性 provider 覆盖、语言回退、命名参数、复数/选择、伪本地化、文本/资源绑定和缺失诊断。
- 新增通用整局 AI 基础设施：游戏环境/机会节点合同、策略价值模型、固定预算 Beam 与 PUCT、训练轨迹、确定性 replay buffer、可持久化线性 policy/value 训练基线和带 Wilson 区间的批量评测。
- 新增可独立组合的纯 C# AI 模块：固定工作量预算与追踪、Utility、Behavior Tree、增量 GOAP、增量 A*、LRU 路径缓存、流场、Steering，以及本地/远程/回退 Policy 合同。
- 补齐 RollDice EasyGame 可复用运行时：app/mode system scope、资源/provider、事件、状态机、日志、随机、音频、显示、调试与对象池，并保持玩法权威和表现层边界。
- 加固 `FAsset` 同路径异步请求合并、provider 注销/释放所有权与失效 Object 检查；加固 `FUI` 空 id、外部释放和 queued-for-deletion 栈恢复。
- C# EventBus 增加单 key payload 契约和派发前预检；StateMachine 统一自定义比较器并在生命周期回调失败后复位；LogBuffer 增加线程安全、即时容量裁剪、结构化字典快照和转发环拒绝。
- `DeterministicRandomStream` 增加无模偏 `NextInt64` 与 step 耗尽保护，`RandomPicker` 不再用浮点数采样大权重。
- Windows/Linux 完整测试链现在固定执行 `FwRuntime.Verify` 和 Godot 通用服务探针，并继续把非白名单 Godot 日志错误视为失败。
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
- Config pack 增加 schema hash、payload checksum、缓存与原子写入；格式编解码收束到纯 C# `FwRuntime.ConfigPack`，并明确 `Fixed32` Q24.8 约定。
- 默认模板改为可运行的 Godot/C# 双向最小闭环，并固定 .NET/Godot 工具链。
- CI 在 Windows/Linux 运行 FwGen、模板、Godot runtime 和主场景测试。
- 补齐 WireFrame、ConfigPack、SystemRuntime 与 proto 数字溢出的边界测试；空白配置 key 和超范围 proto 数字现在会明确失败。
- 明确 SemVer tag、submodule commit、公共 API 与宿主生成产物的兼容升级边界。
- 增加 C# 与 Godot 公共 API 合同测试，并以逐字节确定性变异验证 WireFrame 和 ConfigPack 的损坏拒绝能力。
- Godot .NET 测试探测现在会解析 WinGet/Unix 符号链接并统一校验显式 `GODOT_BIN`，避免 Mono 已安装却被静默跳过。
