# Technical design

> Aligned with v1.36.0 (2026-11). The code is authoritative; this document explains how the pieces fit together.
> Internal engineering logs and raw dependency notes are kept outside the repository.

## 1. Design principles

- **C# is the fact layer, Python is the reasoning layer.** Dependency, call-chain and reference facts are extracted statically by C#; the LLM retrieves and explains them, it never invents project structure.
- **Two agent paths, no fallback.** Cloud models run in the Python agent (LangGraph); local Ollama models run in the C# `ReActLoopEngine`. The Python backend serves cloud models only and rejects local endpoints fail-closed.
- **STA affinity.** Every Openness COM call runs on a dedicated STA thread. The WPF UI thread never touches COM.
- **Process isolation.** TIA attach, indexing and cache maintenance run in `TIAKnowledgeWorker.exe`, so the UI stays responsive and the TIA runtime is isolated.
- **Human approval before writes.** Block writes go through `WriteGate`; generated code is previewed in a diff tab before it is applied.
- **No vector database.** Retrieval is a structured SQLite index with keyword/path matching plus canonical operand paths.

## 2. Solution layout

| Project | Target | Role |
|---|---|---|
| `TIAOpennessStudio.csproj` | net48 | Main WPF application |
| `TIAOpennessStudio.V21.csproj` | net48 (`TIA_V21`) | V21 build variant with the in-process Portal bridge |
| `TIAKnowledgeWorker.csproj` | net48 / x64 | Knowledge-cache worker process |
| `StandardsKbTool.csproj` | net48 | Encrypted standards-library packager / verifier CLI |
| `TIAAgent/` | Python 3.11 | FastAPI + LangGraph backend (cloud models only) |

## 3. Layered architecture

```
WPF UI (Prism + DryIoc)
  ├─ MainWindow partials / Controls / Dialogs
  ├─ ViewModels (MVVM + EventAggregator)
  ├─ C# routing: AgentOrchestrator → UnifiedAgentEngine / ChatRequestRouter
  ├─ Tool layer: ToolRegistry / OpennessService / Portal.* (90+ Openness operations)
  ├─ TiaContextHttpServer :8001  (REST + built-in MCP server JSON-RPC 2.0)
  ├─ Knowledge infrastructure: dependency index / RAG / planning knowledge / worker proxy
  └─ External: Siemens.Engineering.dll / TIA Portal / OPC UA / LLM APIs

Python backend :8000 (cloud only)
  ├─ app/main.py        REST + SSE endpoints
  ├─ agent/supervisor   LangGraph StateGraph, coder / reviewer / chat
  ├─ agent/planner      project-level DAG planning
  ├─ agent/executor     per-task execution and per-block compile
  └─ core/context       token budgeting, history compression, stream parsing
```

## 4. Agent execution

### 4.1 C# routing

`AgentOrchestrator` performs intent classification, context assembly, planning-knowledge recall and compression, then routes:

- **Local models (Ollama)** → `ReActLoopEngine`. Local requests never enter Python.
- **Cloud models** → `UnifiedAgentEngine`, which forwards SSE to the Python backend (`/generate_stream`, optionally `/chat_plan`). The C# side does not parse or execute tools on this path.
- **Legacy path** → static plan build + execute.

### 4.2 Python agent (cloud)

- `supervisor` is a LangGraph `StateGraph`; a deterministic router caps coder↔reviewer rounds and compile retries and never fabricates a PASS.
- `planner` turns a project-level request into a task DAG; `executor` runs tasks in topological order, compiling each block as it is generated.
- Long tasks persist in SQLite (`plan_store`) and run as background jobs (`plan_job`), so an SSE disconnect does not stop execution.
- Milestone approval (`/plan/decide`) and task-failure decisions (`/plan/task_decide`) are explicit endpoints.

Key endpoints: `/generate`, `/generate_stream`, `/chat_plan`, `/plan/run`, `/plan/decide`, `/plan/task_decide`, `/plan/{id}`, `/plan/{id}/stream`, `/convert_xml`, `/validate`, `/health`.

### 4.3 Local agent (C#)

`ReActLoopEngine` implements the ReAct loop in-process, calls `OllamaHostService` on `127.0.0.1:11434`, and shares the same tool registry as the cloud path. This is what makes offline SCL generation possible without any API key.

## 5. Openness integration

- **Main-process STA**: `TiaStaHost` runs a dedicated background STA thread with a WPF dispatcher message pump. All Openness calls are marshalled there.
- **Worker STA**: `TiaStaExecutor` provides the same guarantee inside `TIAKnowledgeWorker.exe`.
- **Export guard**: `ExportBlockXmlAsync` / `ExportTypeCodeAsync` check `block.IsConsistent` and `IsKnowHowProtected` before calling `Export()`. Inconsistent or protected blocks are skipped instead of stalling the STA thread.
- **Version adaptation**: `ITiaVersionAdapter` / `TiaApiPathResolver` detect the installed TIA version and resolve the matching `Siemens.Engineering.dll`; schema profiles come from `LadSchemaRegistry`.

## 6. Tool surface

`TiaContextHttpServer` listens on `127.0.0.1:8001` and serves two faces:

- **REST** for the Python agent: read-only context, knowledge tools, compile/validate, import/delete.
- **MCP** (JSON-RPC 2.0): 40+ tools, as listed by the running `tools/list` (42 definitions at the time of writing).

Tool families: block/type read-write, compile, project/tag queries, knowledge retrieval, HMI builders, `reflect_generation` (SIMATIC ML shape comparison), standards browsing, hardware configuration.

Knowledge tools (`search_project`, `build_context_pack`, `query_variable_references`, `query_call_chain`, `query_variable_impact`, `describe_knowledge_schema`) share one internal method `InvokeKnowledgeToolAsync`, used by both REST and MCP, so the two surfaces cannot drift apart.

**WriteGate** classifies write tools; the user sees an approval card with block name, type, group path and content hash before anything reaches TIA.

### Adding a tool

1. Declare it in `_mcpToolDefinitions`.
2. Add a one-line `case` in the dispatch switch.
3. Implement it in a dedicated partial file (e.g. `TiaContextHttpServer.Builders.cs`).
4. Register it in the Python catalog (`TIAAgent/tools/catalog.py`) so `invoke_tool` accepts it.

## 7. Knowledge index (RAG)

- `TIAKnowledgeWorker.exe` owns project indexing and serves it over local HTTP (default `127.0.0.1:19530`, Bearer token); the main process proxies through `ProjectKnowledgeWorkerHostService`.
- Cache lives in `%LOCALAPPDATA%\TIAOpennessStudio\KnowledgeCache`.
- The index is structured SQLite, not embeddings. Canonical operand paths (for example `DB_Main.Line01.RunCmd`) are the primary keys for reference and impact queries.
- Documented tables: `plc_container`, `plc_block`, `plc_network_lad`, `plc_network_scl`, `plc_interface_member`, `plc_symbol_ref`, `plc_call_edge`, `project_index_state`, `planning_knowledge`, `project_cache_state`, `project_dirty_entry`, `plc_block_snapshot`, plus `repair_experience`.
- The data dictionary lives in `docs/schema/RAG数据表结构说明.md`; changing any `CREATE TABLE` requires updating `KnowledgeSchemaDoc.cs` and that document together.
- Planning knowledge is advisory: successful historical plans are recalled as a separate `planning_knowledge` field, never merged into project facts.

## 8. Encrypted standards library

- Container `TKB1`: 4-byte magic + 16-byte IV + 32-byte HMAC + AES-256-CBC ciphertext.
- Keys come from PBKDF2-SHA256 (120,000 iterations) over an externally supplied secret salt (`TIA_STD_KB_SECRET`) and an optional machine fingerprint.
- The file is HMAC-verified, decrypted in memory, opened as a temporary read-only SQLite database and deleted after use. Search/read only, with rate limits and bounded result sizes.
- Secret providers: official built-in salt (automatic), user passphrase (DPAPI-vaulted), or plaintext (development only).
- Agent access: MCP exposes `list_standard_topics` / `read_standard_section` for autonomous browsing; the local C# agent uses the `search_coding_standard` tool.
- Packaging: `StandardsKbTool build <md-dir|file> <out.kb>`; `verify` proves the encryption/decryption round trip.

## 9. Diff and write-back

- `DiffPreviewControl` is built on DiffPlex and offers side-by-side, unified and edit views, line-level highlighting and synchronized scrolling.
- Every generated or reverted change is shown before it is written; the user presses **Apply** or **Reject**.
- `PatchService` produces unified diffs, applies manifest-based batches atomically and rolls back on failure.
- Block writes reach TIA through `OpennessService.ImportBlockCodeAsync`; group paths are created on demand.

## 10. Context and token budget

- C# `TokenBudget` sizes prompts for the local loop by model family; unknown local models fall back to a small budget.
- Python `core/context.py` budgets only cloud requests: it prefers the real `context_window` reported by C# and falls back to 128k.
- Both sides keep a response reserve and compress history before the window overflows; system summaries live outside the sliding window.

## 11. Version matrix

| TIA version | Schema state | Notes |
|---|---|---|
| V15.1 | Reference baseline | Namespaces confirmed by real exports in `docs/samples/V15.1` |
| V16 | Aligned with official XSD | Interface v4 / FlgNet v4 / SCL v3 |
| V17 | Aligned with official XSD | LAD XSD is byte-identical to V16 |
| V18–V21 | Provisional | Derived from the naming pattern; no XSD/sample verification |

`TIAOpennessStudio.V21.csproj` also provides an experimental in-process Portal bridge (`TIA_V21`).

## 12. Ports

| Port | Service |
|---|---|
| 8000 | Python FastAPI backend |
| 8001 | TIA context REST + built-in MCP server |
| 19530 | Knowledge worker (default) |
| 11434 | Ollama |

## 13. Build and test

```powershell
dotnet build .\TIAOpennessStudio.sln

cd TIAAgent
.\python\python.exe -m pytest tests -q
```

`Siemens.Engineering.dll` must be copied from your own TIA installation before building; it is never committed.
