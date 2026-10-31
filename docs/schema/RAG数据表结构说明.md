# RAG 知识库数据表结构说明（Agent 可读数据字典）

> 本文件是 RAG / Agent 检索所依赖的 SQLite 数据表的**权威数据字典**。
> 供 Agent 在生成查询、理解检索结果、推理块/网络/符号之间的语法与调用关系时阅读。
>
> - 数据库文件：`<AppBase>/Index/project_index.db`（单库，WAL 模式，单写多读）
> - 表所有者（写入方）：C# 端
>   - `PlcKnowledgeStore.cs` —— v3 分层知识存储（9 张表）
>   - `ProjectIndexStore.cs`（类名 `ProjectIndexService`）—— 辅助表（3 张表）
> - 读取方：索引 worker、UI、Python 后端 RAG 检索
> - 所有表都带 `project_key` 前缀列，实现多项目隔离；主键均为复合主键。
>
> 维护约定：本文件与 C# 端 `CREATE TABLE` 定义保持同步。**改表结构时必须同步更新本文件**，否则 Agent 会基于过期结构生成错误查询。

---

## 0. 表清单与职责总览

| 表名 | 归属 | 一句话作用 | 粒度（一行代表） |
|------|------|-----------|-----------------|
| `plc_container` | KnowledgeStore | 目录/分组层级树（自引用） | 一个块分组 / 类型分组 |
| `plc_block` | KnowledgeStore | 所有 FC/FB/DB/OB/UDT/Tag 的清单（完整性锚点） | 一个程序块 |
| `plc_block_snapshot` | KnowledgeStore | 块级快照：索引所用的原始 XML + revision + 块属性 + DB/UDT 接口 JSON | 一个块 |
| `plc_network_lad` | KnowledgeStore | LAD（梯形图）网络，含触点/线圈投影 | 一个 LAD 网络 |
| `plc_network_scl` | KnowledgeStore | SCL（结构化文本）网络，含读/写变量投影 | 一个 SCL 网络 |
| `plc_interface_member` | KnowledgeStore | DB/FB/FC/UDT 接口成员（自引用，支持嵌套 Struct） | 一个接口成员 |
| `plc_symbol_ref` | KnowledgeStore | 逐条符号引用（交叉引用明细） | 一次符号使用 |
| `plc_call_edge` | KnowledgeStore | 块到块的调用图（调用关系边） | 一条调用边 |
| `project_index_state` | KnowledgeStore | 项目级索引状态 | 一个项目 |
| `planning_knowledge` | IndexStore | 历史规划经验（成功/失败的工具路径） | 一条规划经验 |
| `project_cache_state` | IndexStore | 项目缓存状态（索引进度/git） | 一个项目 |
| `project_dirty_entry` | IndexStore | 脏块跟踪（待重新索引的块） | 一个脏块 |

> 注：`plc_block` 是**完整性锚点**——项目中每个块无论解析成功与否都必须有一行，`parse_status` 标记结果。

---

## 1. `plc_container` —— 目录/分组层级树

作用：还原 TIA 项目里块分组、类型分组的目录树结构，支持按路径定位块。自引用（`parent_container_id` 指向本表 `container_id`）。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 复合主键之一 |
| `container_id` | TEXT NOT NULL | 分组唯一 ID | 复合主键之一 |
| `parent_container_id` | TEXT | 父分组 ID（自引用） | 根分组为空 |
| `name` | TEXT NOT NULL | 分组显示名 | |
| `kind` | TEXT NOT NULL | 分组类型 | `BlockGroup` \| `TypeGroup` |
| `path` | TEXT NOT NULL | 完整路径 | 如 `Group\Sub` |
| `depth` | INTEGER NOT NULL | 树深度 | 默认 0 |
| `updated_at` | TEXT | 更新时间戳 | |

主键：`(project_key, container_id)`
索引：`idx_container_parent (project_key, parent_container_id)`
语法关系：**目录树**，通过 `parent_container_id → container_id` 自引用递归。

---

## 2. `plc_block` —— 程序块清单（完整性锚点）

作用：项目中每个块（FC/FB/DB/OB/UDT/Tag）一行。是判断"项目是否索引完整"的锚点表，也是调用图和接口成员的父级。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 复合主键之一 |
| `block_id` | TEXT NOT NULL | 块路径键 | 形如 `FC:Group\Name`；复合主键之一 |
| `parent_container_id` | TEXT | 所属分组 | → `plc_container.container_id` |
| `name` | TEXT NOT NULL | 块名 | |
| `canonical_name` | TEXT | 规范化名（用于检索匹配） | |
| `block_type` | TEXT NOT NULL | 块类型 | `OB` \| `FB` \| `FC` \| `DB` \| `UDT` \| `Tag` |
| `block_number` | TEXT | 块号 | 如 FB100 的 100 |
| `language` | TEXT | 编程语言 | LAD \| SCL \| ... |
| `version_hash` | TEXT | 版本哈希（变更检测） | 含解析器版本与 RAG 载荷契约版本；任一升号即全量重建 |
| `revision` | TEXT | 内容 revision（sha256） | 与 `get_block_content.revision` 同一哈希；`NULL` = 快照时代之前的行，视为脏 |
| `source_profile` | TEXT | 索引 XML 的来源 TIA 版本 | 如 `V15.1` / `V16` |
| `network_count` | INTEGER | 网络数 | 默认 0 |
| `member_count` | INTEGER | 接口成员数 | 默认 0 |
| `summary` | TEXT | 块摘要 | |
| `parse_status` | TEXT | 解析结果 | `ok` \| `failed` \| `partial` |
| `failure_reason` | TEXT | 解析失败原因 | |
| `source_project_path` | TEXT | XML 定位：`.ap` 项目路径 | |
| `source_block_path` | TEXT | XML 定位：项目内块路径 | |
| `updated_at` | TEXT | 更新时间戳 | |
| `git_branch` | TEXT | 索引时 git 分支 | |
| `git_commit` | TEXT | 索引时 git commit | |

主键：`(project_key, block_id)`
索引：`idx_block_container`、`idx_block_name (canonical_name)`、`idx_block_type (block_type)`
关系：块的实现细节分散在 `plc_network_lad` / `plc_network_scl`（按 `block_id` 关联）；接口在 `plc_interface_member`；调用关系在 `plc_call_edge`；原始 XML 与 revision 在 `plc_block_snapshot`。

---

## 3. `plc_network_lad` —— LAD 梯形图网络

作用：每个 LAD 网络一行。除完整 JSON 外，额外把触点/线圈变量投影成分隔符拼接的字符串列，便于 RAG 做 LIKE 检索。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 复合主键之一 |
| `network_id` | TEXT NOT NULL | 网络唯一 ID | 复合主键之一 |
| `block_id` | TEXT NOT NULL | 所属块 | → `plc_block.block_id` |
| `network_order` | INTEGER NOT NULL | 网络在块内的顺序 | |
| `compile_unit_id` | TEXT | 编译单元 ID | 用于回溯原始 XML 片段 |
| `title` | TEXT | 网络标题 | |
| `comment` | TEXT | 网络注释 | |
| `inputs` | TEXT | 全部触点变量（分隔符拼接） | 语法：条件/输入侧 |
| `outputs` | TEXT | 全部线圈变量（分隔符拼接） | 语法：输出/赋值侧 |
| `delimiter` | TEXT | inputs/outputs 使用的分隔符 | 默认 ` \| ` |
| `call_summary` | TEXT | FB/FC 调用摘要（分隔符拼接） | 如 TON/CTU… |
| `source_text` | TEXT | 文本类网络的源码投影 | STL 等；纯图形 LAD/FBD/GRAPH 为空。SCL 网络见 `plc_network_scl` |
| `json_payload` | TEXT | 网络 canonical JSON | LadJson 节点树；GRAPH 为 GraphJsonBridge 序列 |
| `content_hash` | TEXT | 内容哈希 | |
| `source_ref` | TEXT | 源定位引用 | |
| `parse_status` | TEXT | 解析结果 | `ok` \| `failed` \| `partial` |
| `failure_reason` | TEXT | 解析失败原因 | |
| `indexed_at` | TEXT | 索引时间戳 | |

主键：`(project_key, network_id)`
索引：`idx_lad_block (block_id, network_order)`、`idx_lad_inputs (inputs)`、`idx_lad_outputs (outputs)`
AST/语法关系：`json_payload` 是网络的完整语法树；`inputs`（触点=读侧）与 `outputs`（线圈=写侧）是从 AST 投影出的语义摘要；`source_text` 是文本类网络的源码投影；`compile_unit_id` + `source_ref` 可回溯到原始 XML 片段。

---

## 4. `plc_network_scl` —— SCL 结构化文本网络

作用：每个 SCL 网络一行。保留原始 SCL 源码文本（Agent 可直接阅读），并投影出读变量/写变量。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 复合主键之一 |
| `network_id` | TEXT NOT NULL | 网络唯一 ID | 复合主键之一 |
| `block_id` | TEXT NOT NULL | 所属块 | → `plc_block.block_id` |
| `network_order` | INTEGER NOT NULL | 网络顺序 | |
| `compile_unit_id` | TEXT | 编译单元 ID | |
| `title` | TEXT | 网络标题 | |
| `comment` | TEXT | 网络注释 | |
| `read_vars` | TEXT | 被读取的变量（分隔符拼接） | 语法：表达式右值 |
| `write_vars` | TEXT | 赋值目标变量（分隔符拼接） | 语法：赋值左值 |
| `delimiter` | TEXT | 分隔符 | 默认 ` \| ` |
| `call_summary` | TEXT | 调用摘要 | |
| `source_text` | TEXT | 原始 SCL 源码 | Agent 可直接阅读 |
| `json_payload` | TEXT | 保留列，T12 起固定为空 | SCL 载荷只保留 `source_text`，token 树不再重复存储（体积约为其 1/15） |
| `content_hash` | TEXT | 内容哈希 | |
| `source_ref` | TEXT | 源定位引用 | |
| `parse_status` | TEXT | 解析结果 | `ok` \| `failed` \| `partial` |
| `failure_reason` | TEXT | 失败原因 | |
| `indexed_at` | TEXT | 索引时间戳 | |

主键：`(project_key, network_id)`
索引：`idx_scl_block (block_id, network_order)`
AST/语法关系：`source_text` 为原始语法源；`read_vars`（右值/读）与 `write_vars`（左值/写）是数据流语义投影。

---

## 5. `plc_interface_member` —— 接口成员（自引用，支持嵌套 Struct）

作用：DB/FB/FC/UDT 的接口成员逐条一行。自引用支持嵌套 Struct 的层级还原。检索命中的 `Members:` 分区即来自本表。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 复合主键之一 |
| `block_id` | TEXT NOT NULL | 所属块 | → `plc_block.block_id`；复合主键之一 |
| `member_id` | TEXT NOT NULL | 成员唯一 ID | 复合主键之一 |
| `parent_member_id` | TEXT | 父成员 ID（自引用） | 用于嵌套 Struct |
| `member_path` | TEXT NOT NULL | 成员全路径 | 如 `a.b.c` |
| `name` | TEXT NOT NULL | 成员名 | |
| `data_type` | TEXT | 数据类型 | |
| `section` | TEXT | 接口区段 | `Input` \| `Output` \| `InOut` \| `Static` \| `Temp` \| `Constant` |
| `initial_value` | TEXT | 初始值 | |
| `comment` | TEXT | 成员注释 | |
| `order_index` | INTEGER | 排序索引 | |

主键：`(project_key, block_id, member_id)`
索引：`idx_member_block (block_id)`、`idx_member_path (member_path)`
语法关系：**成员树**，`parent_member_id → member_id` 自引用表示嵌套 Struct；`member_path` 是扁平化的点分路径。

---

## 6. `plc_symbol_ref` —— 符号引用明细（交叉引用）

作用：逐条记录符号在何处被使用（读/写/读写），是交叉引用（cross-reference）的明细来源。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 复合主键之一 |
| `ref_id` | TEXT NOT NULL | 引用唯一 ID | 复合主键之一 |
| `block_id` | TEXT NOT NULL | 引用发生的块 | → `plc_block.block_id` |
| `network_id` | TEXT | 引用发生的网络 | → `plc_network_*.network_id` |
| `network_order` | INTEGER | 网络顺序 | |
| `canonical_path` | TEXT NOT NULL | 符号规范路径（检索键） | |
| `symbol_name` | TEXT | 符号名 | |
| `scope` | TEXT | 作用域 | |
| `direction` | TEXT | 访问方向 | `Read` \| `Write` \| `ReadWrite` |
| `source_ref` | TEXT | 源定位引用 | |

主键：`(project_key, ref_id)`
索引：`idx_symref_path (canonical_path)`、`idx_symref_dir (canonical_path, direction)`、`idx_symref_block (block_id)`
语义关系：`direction` 支撑数据流分析（谁读了/谁写了某符号）；按 `canonical_path` 聚合即得某变量的全部使用点。

---

## 7. `plc_call_edge` —— 块调用图（调用关系边）

作用：块到块的调用关系边，构成整个项目的调用图（call graph）。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 复合主键之一 |
| `edge_id` | TEXT NOT NULL | 边唯一 ID | 复合主键之一 |
| `caller_block_id` | TEXT NOT NULL | 调用方块 | → `plc_block.block_id` |
| `caller_name` | TEXT | 调用方名 | |
| `callee_name` | TEXT NOT NULL | 被调用方名 | |
| `callee_block_type` | TEXT | 被调用方块类型 | `FB` \| `FC` \| … |
| `instance_path` | TEXT | 实例路径（FB 背景块） | |
| `network_id` | TEXT | 调用所在网络 | |
| `network_order` | INTEGER | 网络顺序 | |
| `source_ref` | TEXT | 源定位引用 | |
| `callee_resolved` | INTEGER | callee 是否解析到当前索引内块（重建后调和打标） | `1`=可解析；`0`=不可解析（系统块如 TON/外部块/已重命名/已删除的陈旧边）；`-1`=未调和 |

主键：`(project_key, edge_id)`
索引：`idx_call_callee (callee_name)`、`idx_call_caller (caller_block_id)`
语义关系：**有向图**，`caller_block_id → callee_name` 构成调用边；正向查"某块调用了谁"用 `caller_block_id`，反向查"谁调用了某块"用 `callee_name`。
注意：`callee_name` 为字符串匹配、无外键，块重命名/删除后未重新索引的调用方会残留陈旧边（`callee_resolved=0`）；精确反向查询应过滤 `callee_resolved=1`。

---

## 8. `project_index_state` —— 项目索引状态（KnowledgeStore）

作用：记录 v3 分层存储的项目级索引状态。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 主键 |
| `status` | TEXT | 索引状态 | `ready` \| `indexing` \| `stale` \| `missing` |
| `last_indexed_at` | TEXT | 最近索引时间 | |
| `last_reason` | TEXT | 最近刷新原因 | |
| `block_count` | INTEGER | 已索引块数 | 默认 0 |
| `git_branch` | TEXT | git 分支 | |
| `git_commit` | TEXT | git commit | |
| `updated_at` | TEXT | 更新时间戳 | |

主键：`(project_key)`

---

## 9. `planning_knowledge` —— 历史规划经验（IndexStore）

作用：沉淀过往任务的规划经验（哪些工具路径成功/失败），供规划器参考。**这是经验，不是项目事实**，Agent 不应把它当作 ground truth。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 复合主键之一 |
| `knowledge_key` | TEXT NOT NULL | 经验唯一键 | 复合主键之一 |
| `task_intent` | TEXT | 任务意图 | |
| `prompt_pattern` | TEXT | 提示模式 | |
| `target_kinds` | TEXT | 目标块类型 | |
| `used_tools` | TEXT | 使用过的工具链 | |
| `plan_summary` | TEXT | 规划摘要 | |
| `success` | INTEGER | 是否成功 | 0 / 1 |
| `completed_steps` | INTEGER | 完成步数 | 默认 0 |
| `total_steps` | INTEGER | 总步数 | 默认 0 |
| `deviation_count` | INTEGER | 偏离次数 | 默认 0 |
| `execution_status` | TEXT | 执行状态 | |
| `success_count` | INTEGER | 累计成功次数 | 默认 0 |
| `failure_count` | INTEGER | 累计失败次数 | 默认 0 |
| `updated_at` | TEXT | 更新时间戳 | |

主键：`(project_key, knowledge_key)`
索引：`idx_planning_project_intent (project_key, task_intent, success)`

---

## 10. `project_cache_state` —— 项目缓存状态（IndexStore）

作用：辅助表，记录项目缓存/索引进度状态（与 `project_index_state` 类似，属 IndexStore 侧）。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 主键 |
| `status` | TEXT | 缓存状态 | |
| `last_indexed_at` | TEXT | 最近索引时间 | |
| `last_refresh_reason` | TEXT | 最近刷新原因 | |
| `git_branch` | TEXT | git 分支 | |
| `git_commit` | TEXT | git commit | |
| `updated_at` | TEXT | 更新时间戳 | |

主键：`(project_key)`
说明：块计数在读取时从 `plc_block` 实时聚合，不冗余存储。

---

## 11. `project_dirty_entry` —— 脏块跟踪（IndexStore）

作用：记录被标记为"脏"（需要重新索引）的块。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 复合主键之一 |
| `block_key` | TEXT NOT NULL | 脏块键 | 复合主键之一 |
| `reason` | TEXT | 标脏原因 | |
| `updated_at` | TEXT | 更新时间戳 | |

主键：`(project_key, block_key)`
索引：`idx_project_dirty_updated (project_key, updated_at)`

---

## 12. `plc_block_snapshot` —— 块级快照（索引所用的原始 XML）

作用：每个块一行，保存建立本次索引时的原始导出 XML、内容 revision、来源版本、块级属性，以及 DB/UDT 的接口 canonical JSON。它是读契约（IR / 源码 / 接口 / XML 四投影）的索引侧真相源。

| 字段 | 类型 | 作用 | 取值 / 说明 |
|------|------|------|------------|
| `project_key` | TEXT NOT NULL | 项目隔离键 | 复合主键之一 |
| `block_id` | TEXT NOT NULL | 块路径键 | → `plc_block.block_id`；复合主键之一 |
| `revision` | TEXT NOT NULL | `raw_xml` 的 sha256 | revision 也参与哈希计算（`BlockSnapshotService.ComputeRevision`）；与 `get_block_content` 返回的 revision 相同 |
| `source_profile` | TEXT | 导出 XML 的来源 TIA 版本 | 如 `V15.1` / `V16` |
| `raw_xml` | TEXT | 本次索引使用的原始导出 XML | 写回骨架 |
| `block_attributes_json` | TEXT | 块级属性 JSON | 形状同 IR 的 `blockAttributes`（如 `MemoryLayout` / `SetENOAutomatically`） |
| `interface_json` | TEXT | 接口 canonical JSON | 仅 DB/UDT；其余块为空（逻辑块以网络为证据） |
| `indexed_at` | TEXT | 索引时间戳 | |

主键：`(project_key, block_id)`

关系：与 `plc_block` 同 `block_id` 一一对应；每次重建在该块事务内整体替换。索引失败（无 XML/revision）时不写快照，且会先删除旧行，绝不保留旧 revision 供 patch 误信。

用途：
- 用 `get_block_content` 返回的 revision 与本表 `revision` 对比，即可判断索引是否落后于 TIA 现况。
- DB/UDT 无需再解析 XML 即可取接口结构。

---

## 13. 表间关系图谱（供推理用）

```
plc_container ──(parent_container_id 自引用)── 目录树
      │ container_id
      ▼ parent_container_id
plc_block ──(完整性锚点, block_id)──┐
      │                            │
      ├── plc_block_snapshot (block_id)   块级快照(原始 XML + revision + 属性 + DB/UDT interface_json)
      ├── plc_network_lad (block_id)     LAD 网络 → inputs(触点/读) / outputs(线圈/写)
      ├── plc_network_scl (block_id)     SCL 网络 → read_vars(读) / write_vars(写) + source_text
      ├── plc_interface_member (block_id, parent_member_id 自引用)  接口成员树(嵌套 Struct)
      ├── plc_symbol_ref (block_id, network_id)   符号引用明细(direction: 读/写)
      └── plc_call_edge (caller_block_id → callee_name)   调用图(有向)

project_index_state / project_cache_state / project_dirty_entry  →  索引状态与调度(非项目事实)
planning_knowledge  →  历史规划经验(非项目事实, 勿作 ground truth)
```

关键语法/语义关系速查：
- **目录层级**：`plc_container.parent_container_id → container_id`
- **块归属分组**：`plc_block.parent_container_id → plc_container.container_id`
- **块实现**：`plc_network_lad / plc_network_scl.block_id → plc_block.block_id`
- **块快照**：`plc_block_snapshot.block_id → plc_block.block_id`（1:1，索引时整体替换）
- **嵌套 Struct**：`plc_interface_member.parent_member_id → member_id`
- **数据流（读/写）**：LAD 用 `inputs/outputs`，SCL 用 `read_vars/write_vars`，明细在 `plc_symbol_ref.direction`
- **调用图**：`plc_call_edge.caller_block_id → callee_name`（正向查被调，反向按 `callee_name` 查主调）
- **原始 XML 回溯**：`compile_unit_id` + `source_ref`（LAD/SCL 网络）
