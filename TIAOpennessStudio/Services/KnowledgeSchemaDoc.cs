using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Data dictionary for the RAG knowledge index (single source of truth, for the Agent
    /// to read via the MCP tool <c>describe_knowledge_schema</c>).
    ///
    /// Describes the SQLite tables owned by <see cref="PlcKnowledgeStore"/> and
    /// <see cref="ProjectIndexService"/>: each table's purpose, every column's meaning and
    /// allowed values, and the AST/syntax/call-graph relations between tables.
    ///
    /// Maintenance convention: when the C# side's CREATE TABLE definitions change, the
    /// corresponding entry here must be updated in sync, otherwise the Agent will generate
    /// wrong queries from a stale structure. The human-readable mirror doc lives at
    /// docs/schema/RAG数据表结构说明.md (both should stay consistent).
    /// </summary>
    internal static class KnowledgeSchemaDoc
    {
        /// <summary>
        /// Returns the data dictionary text. When <paramref name="table"/> is given, only
        /// that table's entry is returned; otherwise the full dictionary (overview + all
        /// tables + relation graph) is returned.
        /// </summary>
        public static string Describe(string table)
        {
            if (string.IsNullOrWhiteSpace(table))
                return _fullDoc;

            var key = table.Trim();
            if (_perTable.TryGetValue(key, out var section))
                return _header + section;

            var known = string.Join(", ", _perTable.Keys);
            return $"Unknown table '{key}'. Known tables: {known}.\n\n" +
                   "Call describe_knowledge_schema with no 'table' argument to get the full data dictionary.";
        }

        private const string _header =
"# RAG 知识索引数据字典\n" +
"DB: <AppBase>/Index/project_index.db (SQLite, WAL, 单写多读)。所有表带 project_key 前缀列做多项目隔离，主键均为复合主键。\n" +
"这些表由 search_project / build_context_pack / query_variable_references / query_call_chain / query_variable_impact 检索。\n\n" +
"# 检索返回字段与使用指引\n" +
"search_project / build_context_pack 的每条命中按分区输出带标签的行：\n" +
"- Members: 接口成员(DB/UDT/FB/FC)，每行 [Section] path : type = 初始值；超过 30 条折叠，完整接口用 get_block_content / export_block_ir\n" +
"- Source: SCL/STL 重建源码(plc_network_scl.source_text / plc_network_lad.source_text)，可直接分析\n" +
"- JsonPreview: 图形网络 canonical JSON 预览(4000 字符截断，plc_network_lad.json_payload)，用于理解结构，不是写回载荷\n" +
"- Title/Comment: 定位信息（可能为空，注释为空属正常）\n" +
"- Inputs/Outputs: 图形网络（LAD/FBD/STL/GRAPH）的读/写变量投影，' | ' 分隔\n" +
"内容来自索引快照，可能滞后于 TIA 最新修改；需要逐字核对最新代码或准备改动某块时，\n" +
"必须再调 get_block_content(format='source'|'xml')（STA 实时导出，永远最新）。\n" +
"plc_block_snapshot 保存索引时的原始 XML 与 revision；该 revision 与 get_block_content 返回的 revision 是同一 sha256，可用于判断索引是否已过期。\n\n" +
"# 检索行为与截断可见性\n" +
"- search_project 首条命中的 SearchNote 字段（build_context_pack 在 Metadata[\"search_coverage\"]）给出\n" +
"  '返回数/匹配数' 覆盖说明；出现 '(截断)' 即结果不完整，应用更精确关键词或按块取全量。\n" +
"- 网络行按相关性排序（token×列加权命中数），每块最多返回 20 条网络行，单块不会吃光全部预算。\n" +
"- 关键词分词会对标识符按下划线/驼峰边界展开子词（FC_MotorControl 可用 MotorControl 或 Motor 查到）；\n" +
"  块搜索同时匹配 name/canonical_name/summary/block_number（支持 FC123 式块号查询）。\n" +
"- FTS 语义检索与 LIKE 标识符检索并行执行、结果交错合并，互不争抢。\n" +
"- query_variable_references 的 TotalHits 为库内真实匹配行数（截断前），Note 字段会提示是否截断。\n\n";

        // Per-table entries (key = table name). Content kept aligned with docs/schema/RAG数据表结构说明.md.
        private static readonly Dictionary<string, string> _perTable =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["plc_container"] =
"## plc_container —— 目录/分组层级树\n" +
"作用: 还原块分组/类型分组的目录树，支持按路径定位块。自引用。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- container_id TEXT NOT NULL: 分组唯一 ID(PK)\n" +
"- parent_container_id TEXT: 父分组 ID(自引用)，根分组为空\n" +
"- name TEXT NOT NULL: 分组显示名\n" +
"- kind TEXT NOT NULL: 分组类型，取值 BlockGroup | TypeGroup\n" +
"- path TEXT NOT NULL: 完整路径，如 Group\\Sub\n" +
"- depth INTEGER NOT NULL: 树深度(默认0)\n" +
"- updated_at TEXT: 更新时间戳\n" +
"PK: (project_key, container_id); INDEX: idx_container_parent(project_key, parent_container_id)\n" +
"关系: 目录树，parent_container_id -> container_id 自引用递归。\n\n",

            ["plc_block"] =
"## plc_block —— 程序块清单(完整性锚点)\n" +
"作用: 每个 FC/FB/DB/OB/UDT/Tag 一行。判断项目索引是否完整的锚点，也是调用图/接口成员的父级。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- block_id TEXT NOT NULL: 块路径键，形如 FC:Group\\Name(PK)\n" +
"- parent_container_id TEXT: 所属分组 -> plc_container.container_id\n" +
"- name TEXT NOT NULL: 块名\n" +
"- canonical_name TEXT: 规范化名(检索匹配用)\n" +
"- block_type TEXT NOT NULL: 块类型，取值 OB | FB | FC | DB | UDT | Tag\n" +
"- block_number TEXT: 块号(如 FB100 的 100)\n" +
"- language TEXT: 编程语言(LAD|SCL|...)\n" +
"- version_hash TEXT: 版本哈希(变更检测，含解析器版本与 RAG 载荷契约版本；任一升号即全量重建)\n" +
"- revision TEXT: 内容 revision(sha256，与 get_block_content.revision 同一哈希；NULL=快照时代之前的行，视为脏)\n" +
"- source_profile TEXT: 索引 XML 的来源 TIA 版本(如 V15.1 / V16)\n" +
"- network_count INTEGER: 网络数(默认0)\n" +
"- member_count INTEGER: 接口成员数(默认0)\n" +
"- summary TEXT: 块摘要\n" +
"- parse_status TEXT: 解析结果，取值 ok | failed | partial\n" +
"- failure_reason TEXT: 解析失败原因\n" +
"- source_project_path TEXT: XML 定位: .ap 项目路径\n" +
"- source_block_path TEXT: XML 定位: 项目内块路径\n" +
"- updated_at TEXT: 更新时间戳\n" +
"- git_branch / git_commit TEXT: 索引时的 git 分支/commit\n" +
"PK: (project_key, block_id); INDEX: idx_block_container, idx_block_name(canonical_name), idx_block_type(block_type)\n" +
"关系: 实现在 plc_network_lad/plc_network_scl(按 block_id)；接口在 plc_interface_member；调用在 plc_call_edge；原始 XML 在 plc_block_snapshot。\n\n",

            ["plc_block_snapshot"] =
"## plc_block_snapshot —— 块级快照(索引所用的原始 XML)\n" +
"作用: 每个块一行，保存建立本次索引时的原始导出 XML、内容 revision、来源版本与块级属性。是读契约(IR/源码/接口/XML 四投影)的索引侧真相源。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- block_id TEXT NOT NULL: 块路径键 -> plc_block.block_id(PK)\n" +
"- revision TEXT NOT NULL: raw_xml 的 sha256(revision 也参与计算，见 BlockSnapshotService.ComputeRevision)，与 get_block_content 返回的 revision 相同\n" +
"- source_profile TEXT: 导出 XML 的来源 TIA 版本\n" +
"- raw_xml TEXT: 本次索引使用的原始导出 XML(写回骨架)\n" +
"- block_attributes_json TEXT: 块级属性 JSON(形状同 IR 的 blockAttributes，如 MemoryLayout/SetENOAutomatically)\n" +
"- interface_json TEXT: 接口 canonical JSON(仅 DB/UDT；其余块为空，逻辑块以网络为证据)\n" +
"- indexed_at TEXT: 索引时间戳\n" +
"PK: (project_key, block_id)\n" +
"关系: 1:1 对应 plc_block(同 block_id)；每次重建在该块事务内整体替换；索引失败(无 XML/revision)时不写快照，绝不保留旧 revision 供 patch 误信。\n" +
"用途: 判断索引是否落后于 TIA 现况(get_block_content.revision 对比)；DB/UDT 无需再解析 XML 即可取接口结构。\n\n",

            ["plc_network_lad"] =
"## plc_network_lad —— LAD 梯形图网络\n" +
"作用: 每个 LAD 网络一行。除完整 JSON 外，把触点/线圈变量投影成分隔符拼接字符串，便于 LIKE 检索。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- network_id TEXT NOT NULL: 网络唯一 ID(PK)\n" +
"- block_id TEXT NOT NULL: 所属块 -> plc_block.block_id\n" +
"- network_order INTEGER NOT NULL: 网络在块内顺序\n" +
"- compile_unit_id TEXT: 编译单元 ID(回溯原始 XML)\n" +
"- title TEXT / comment TEXT: 网络标题/注释\n" +
"- inputs TEXT: 全部触点变量(分隔符拼接)，语法上为条件/读侧\n" +
"- outputs TEXT: 全部线圈变量(分隔符拼接)，语法上为输出/写侧\n" +
"- delimiter TEXT: inputs/outputs 分隔符(默认 ' | ')\n" +
"- call_summary TEXT: FB/FC 调用摘要(如 TON/CTU)\n" +
"- source_text TEXT: 文本类网络的源码投影(STL 等；纯图形 LAD/FBD/GRAPH 为空)。SCL 网络在 plc_network_scl\n" +
"- json_payload TEXT: 网络 canonical JSON(LadJson 节点树；GRAPH 为序列 JSON)\n" +
"- content_hash TEXT / source_ref TEXT: 内容哈希/源定位引用\n" +
"- parse_status TEXT: ok | failed | partial; failure_reason TEXT; indexed_at TEXT\n" +
"PK: (project_key, network_id); INDEX: idx_lad_block(block_id, network_order), idx_lad_inputs(inputs), idx_lad_outputs(outputs)\n" +
"AST/语法: json_payload 为 canonical 节点树(kind/attrs/children)；inputs(读)/outputs(写)是语义投影；compile_unit_id+source_ref 可回溯 XML 片段。\n\n",

            ["plc_network_scl"] =
"## plc_network_scl —— SCL 结构化文本网络\n" +
"作用: 每个 SCL 网络一行。保留原始 SCL 源码(Agent 可直接阅读)，并投影读/写变量。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- network_id TEXT NOT NULL: 网络唯一 ID(PK)\n" +
"- block_id TEXT NOT NULL: 所属块 -> plc_block.block_id\n" +
"- network_order INTEGER NOT NULL: 网络顺序\n" +
"- compile_unit_id TEXT: 编译单元 ID\n" +
"- title TEXT / comment TEXT: 标题/注释\n" +
"- read_vars TEXT: 被读取的变量(分隔符拼接)，语法上为表达式右值\n" +
"- write_vars TEXT: 赋值目标变量(分隔符拼接)，语法上为赋值左值\n" +
"- delimiter TEXT: 分隔符(默认 ' | ')\n" +
"- call_summary TEXT: 调用摘要\n" +
"- source_text TEXT: 原始 SCL 源码(Agent 可直接阅读)\n" +
"- json_payload TEXT: T12 起固定为空。SCL 载荷只保留 source_text，token 树不再重复存储(体积约为其 1/15)\n" +
"- content_hash / source_ref TEXT; parse_status(ok|failed|partial); failure_reason; indexed_at\n" +
"PK: (project_key, network_id); INDEX: idx_scl_block(block_id, network_order)\n" +
"AST/语法: source_text 为原始语法源；read_vars(读)/write_vars(写)为数据流语义投影。\n\n",

            ["plc_interface_member"] =
"## plc_interface_member —— 接口成员(自引用，支持嵌套 Struct)\n" +
"作用: DB/FB/FC/UDT 接口成员逐条一行。自引用支持嵌套 Struct 层级还原。检索命中的 Members: 分区即来自本表。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- block_id TEXT NOT NULL: 所属块 -> plc_block.block_id(PK)\n" +
"- member_id TEXT NOT NULL: 成员唯一 ID(PK)\n" +
"- parent_member_id TEXT: 父成员 ID(自引用)，用于嵌套 Struct\n" +
"- member_path TEXT NOT NULL: 成员全路径，如 a.b.c\n" +
"- name TEXT NOT NULL: 成员名\n" +
"- data_type TEXT: 数据类型\n" +
"- section TEXT: 接口区段，取值 Input | Output | InOut | Static | Temp | Constant\n" +
"- initial_value TEXT: 初始值\n" +
"- comment TEXT: 成员注释\n" +
"- order_index INTEGER: 排序索引\n" +
"PK: (project_key, block_id, member_id); INDEX: idx_member_block(block_id), idx_member_path(member_path)\n" +
"语法: 成员树，parent_member_id -> member_id 自引用表示嵌套 Struct；member_path 为扁平点分路径。\n\n",

            ["plc_symbol_ref"] =
"## plc_symbol_ref —— 符号引用明细(交叉引用)\n" +
"作用: 逐条记录符号在何处被使用(读/写/读写)，是交叉引用明细来源。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- ref_id TEXT NOT NULL: 引用唯一 ID(PK)\n" +
"- block_id TEXT NOT NULL: 引用发生的块 -> plc_block.block_id\n" +
"- network_id TEXT: 引用发生的网络 -> plc_network_*.network_id\n" +
"- network_order INTEGER: 网络顺序\n" +
"- canonical_path TEXT NOT NULL: 符号规范路径(检索键)\n" +
"- symbol_name TEXT: 符号名\n" +
"- scope TEXT: 作用域\n" +
"- direction TEXT: 访问方向，取值 Read | Write | ReadWrite\n" +
"- source_ref TEXT: 源定位引用\n" +
"PK: (project_key, ref_id); INDEX: idx_symref_path(canonical_path), idx_symref_dir(canonical_path, direction), idx_symref_block(block_id)\n" +
"语义: direction 支撑数据流分析(谁读/谁写)；按 canonical_path 聚合即得某变量全部使用点。\n\n",

            ["plc_call_edge"] =
"## plc_call_edge —— 块调用图(调用关系边)\n" +
"作用: 块到块的调用关系边，构成整个项目的调用图。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- edge_id TEXT NOT NULL: 边唯一 ID(PK)\n" +
"- caller_block_id TEXT NOT NULL: 调用方块 -> plc_block.block_id\n" +
"- caller_name TEXT: 调用方名\n" +
"- callee_name TEXT NOT NULL: 被调用方名\n" +
"- callee_block_type TEXT: 被调用方块类型(FB|FC|...)\n" +
"- instance_path TEXT: 实例路径(FB 背景块)\n" +
"- network_id TEXT / network_order INTEGER: 调用所在网络/顺序\n" +
"- source_ref TEXT: 源定位引用\n" +
"- callee_resolved INTEGER: callee 是否解析到当前索引内块(P2b 重建后调和打标)。1=可解析; 0=不可解析(系统块如 TON/外部块/已重命名/已删除的陈旧边); -1=未调和\n" +
"PK: (project_key, edge_id); INDEX: idx_call_callee(callee_name), idx_call_caller(caller_block_id)\n" +
"语义: 有向图，caller_block_id -> callee_name。正向查'某块调用了谁'用 caller_block_id；反向查'谁调用了某块'用 callee_name。\n" +
"注意: callee_name 为字符串匹配无外键，块重命名/删除后未重新索引的调用方会残留陈旧边(callee_resolved=0)；精确反向查询应过滤 callee_resolved=1。\n\n",

            ["project_index_state"] =
"## project_index_state —— 项目索引状态(KnowledgeStore)\n" +
"作用: 记录 v3 分层存储的项目级索引状态。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- status TEXT: 索引状态，取值 ready | indexing | stale | missing\n" +
"- last_indexed_at TEXT: 最近索引时间\n" +
"- last_reason TEXT: 最近刷新原因\n" +
"- block_count INTEGER: 已索引块数(默认0)\n" +
"- git_branch / git_commit TEXT: git 分支/commit\n" +
"- updated_at TEXT: 更新时间戳\n" +
"PK: (project_key)\n\n",

            ["planning_knowledge"] =
"## planning_knowledge —— 历史规划经验(IndexStore)\n" +
"作用: 沉淀过往任务规划经验(成功/失败的工具路径)供规划器参考。这是经验，不是项目事实，勿当 ground truth。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- knowledge_key TEXT NOT NULL: 经验唯一键(PK)\n" +
"- task_intent TEXT: 任务意图\n" +
"- prompt_pattern TEXT: 提示模式\n" +
"- target_kinds TEXT: 目标块类型\n" +
"- used_tools TEXT: 使用过的工具链\n" +
"- plan_summary TEXT: 规划摘要\n" +
"- success INTEGER: 是否成功(0/1)\n" +
"- completed_steps / total_steps INTEGER: 完成/总步数(默认0)\n" +
"- deviation_count INTEGER: 偏离次数(默认0)\n" +
"- execution_status TEXT: 执行状态\n" +
"- success_count / failure_count INTEGER: 累计成功/失败次数(默认0)\n" +
"- updated_at TEXT: 更新时间戳\n" +
"PK: (project_key, knowledge_key); INDEX: idx_planning_project_intent(project_key, task_intent, success)\n\n",

            ["project_cache_state"] =
"## project_cache_state —— 项目缓存状态(IndexStore)\n" +
"作用: 辅助表，记录项目缓存/索引进度状态。块计数在读取时从 plc_block 实时聚合，不冗余存储。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- status TEXT: 缓存状态\n" +
"- last_indexed_at TEXT: 最近索引时间\n" +
"- last_refresh_reason TEXT: 最近刷新原因\n" +
"- git_branch / git_commit TEXT: git 分支/commit\n" +
"- updated_at TEXT: 更新时间戳\n" +
"PK: (project_key)\n\n",

            ["project_dirty_entry"] =
"## project_dirty_entry —— 脏块跟踪(IndexStore)\n" +
"作用: 记录被标记为脏(需重新索引)的块。\n" +
"字段:\n" +
"- project_key TEXT NOT NULL: 项目隔离键(PK)\n" +
"- block_key TEXT NOT NULL: 脏块键(PK)\n" +
"- reason TEXT: 标脏原因\n" +
"- updated_at TEXT: 更新时间戳\n" +
"PK: (project_key, block_key); INDEX: idx_project_dirty_updated(project_key, updated_at)\n\n",
        };

        private const string _overview =
"## 表清单与职责总览\n" +
"- plc_container: 目录/分组层级树(自引用)。一行=一个块分组/类型分组。\n" +
"- plc_block: 所有块清单(完整性锚点)。一行=一个程序块。\n" +
"- plc_block_snapshot: 块级快照(原始 XML + revision + 属性 + DB/UDT 接口 JSON)。一行=一个块。\n" +
"- plc_network_lad: LAD 网络，含触点/线圈投影。一行=一个 LAD 网络。\n" +
"- plc_network_scl: SCL 网络，含读/写变量投影+源码。一行=一个 SCL 网络。\n" +
"- plc_interface_member: 接口成员(自引用嵌套 Struct)。一行=一个成员。\n" +
"- plc_symbol_ref: 逐条符号引用(交叉引用)。一行=一次符号使用。\n" +
"- plc_call_edge: 块到块调用图。一行=一条调用边。\n" +
"- project_index_state: 项目级索引状态(KnowledgeStore)。\n" +
"- planning_knowledge: 历史规划经验(非项目事实)。\n" +
"- project_cache_state: 项目缓存状态(IndexStore)。\n" +
"- project_dirty_entry: 脏块跟踪(IndexStore)。\n" +
"注: plc_block 是完整性锚点——每个块无论解析成功与否都必须有一行，parse_status 标记结果。\n\n";

        private const string _relations =
"## 表间关系速查(供推理)\n" +
"- 目录层级: plc_container.parent_container_id -> container_id\n" +
"- 块归属分组: plc_block.parent_container_id -> plc_container.container_id\n" +
"- 块实现: plc_network_lad/plc_network_scl.block_id -> plc_block.block_id\n" +
"- 块快照: plc_block_snapshot.block_id -> plc_block.block_id(1:1，索引时整体替换)\n" +
"- 嵌套 Struct: plc_interface_member.parent_member_id -> member_id\n" +
"- 数据流(读/写): LAD 用 inputs/outputs，SCL 用 read_vars/write_vars，明细在 plc_symbol_ref.direction\n" +
"- 调用图: plc_call_edge.caller_block_id -> callee_name(反向按 callee_name 查主调)\n" +
"- 原始 XML 回溯: compile_unit_id + source_ref (LAD/SCL 网络)\n";

        private static readonly string _fullDoc = BuildFullDoc();

        private static string BuildFullDoc()
        {
            var sb = new StringBuilder();
            sb.Append(_header);
            sb.Append(_overview);
            foreach (var kv in _perTable)
                sb.Append(kv.Value);
            sb.Append(_relations);
            return sb.ToString();
        }
    }
}
