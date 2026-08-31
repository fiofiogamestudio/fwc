# tool

只在开发期运行的能力，不进入 game 或 host 运行时。

| 名称 | 当前入口 | 职责 |
| --- | --- | --- |
| gen | `csharp/FwGen`、`tools/gen.*` | 生成、同步与检查 |
| train | `tool/train` | AI 训练与评测 |
| e2e | `tool/e2e`、`tools/test.*` | 故障注入与端到端验证 |
| fwe | 宿主 `fwe` | 可视配置编辑器 |
| tpl | `templates` | 新工程模板 |

运行时 Kit 不得依赖本目录；宿主只有在执行对应开发任务时才显式引用 tool 项目。fw 不提供把所有 Core、Kit 和 tool 合并在一起的聚合程序集。
