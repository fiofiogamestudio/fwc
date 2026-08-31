# app

Godot 应用壳与通用服务 Kit。

- 规范能力：app、mode、asset、pool、UI、本地化、音频、显示与调试。
- 当前源码：`scripts/fw/rt` 与 `scripts/fw/vu`，其中 `vu/animation` 属于 `anim`。
- 新工程：`fwgen sync` 只把启用部分投影到 `scripts/_fw/fw`。
- 目标限制：只可用于 game；纯 C# host 不生成 Godot 投影。
- 兼容期：保留旧源码路径一版，之后再把规范源码迁入本目录。

这里不保存游戏物品、地图、玩法规则或其他宿主内容。
