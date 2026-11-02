# TIA Openness Studio

**Make AI a trusted engineering partner for PLC teams — not a code-snippet generator.**

We believe industrial control is not measured by how much code is generated, but by whether every change is understandable, verifiable and traceable. AI should raise what an engineer can deliver without lowering the bar for what ships.

TIA Openness Studio turns engineering intent into compiled, reviewable and reversible changes in a real Siemens TIA Portal project (WPF / .NET Framework 4.8). Technical boundaries stay explicit; accountability stays human.

## Disclaimer

- This project is **not affiliated with, endorsed by, or supported by Siemens**.
- `TIA Portal`, `STEP 7`, `WinCC` and `Openness` are trademarks of Siemens AG.
- **No Siemens binaries are distributed in this repository.** You must own a valid TIA Portal license and copy `Siemens.Engineering.dll` from your own installation.
- The MIT license covers only the code in this repository, not Siemens software or third-party standards content.

## Features

- **Project & device operations** — open / save / close / compile TIA projects, load device and PLC trees.
- **Block & type workflows** — import PLC Blocks and PLC Data Types, export objects as XML, import/export project texts.
- **Native PLC generation** — offline builders for UDT, global DB, FC, FB and LAD (FlgNet) blocks, then Openness import.
- **S7-GRAPH generation** — `build_graph_fb` builds a native GRAPH FB from a JSON spec (steps, transitions, branches, jumps, actions, interlock and supervision annotations) and imports it through Openness.
- **AI workflow** — chat-style SCL/XML generation, streaming output, project-index-assisted context retrieval.
- **Offline SCL assistant** — the recommended local model is [`LisyNeko/qwen3.8-9b-coder`](https://ollama.com/LisyNeko/qwen3.8-9b-coder), a 9B coder model distilled from the 2.4T-parameter Qwen3.8-A95B and fine-tuned for coding. It writes SCL without network access or an API key, which suits air-gapped or privacy-sensitive projects.
- **Cloud agent** — Python FastAPI + LangGraph supervisor, project-level DAG planning, per-block execution, milestone approval and resume.
- **Knowledge index / RAG** — SQLite-backed block, network, symbol, call-graph and cross-reference index, served to the agent over a local MCP endpoint.
- **Encrypted standards library** — company or industry coding rules packaged as a read-only `TKB1` knowledge base (AES-256-CBC + HMAC-SHA256), exposed to the agent through the `search_coding_standard` tool.
- **Library backup & compare** — create backups, inspect history, restore with a safety copy, and compare types/master copies via Openness.
- **Localization** — English / German / Chinese.

## Screenshots

Project tree with the LAD code view and the code-generator panel:

![LAD code view](docs/assets/screenshot-lad.png)

S7-GRAPH network with steps, transitions and interlock annotations:

![S7-GRAPH view](docs/assets/screenshot-graph.png)

## Architecture

```
WPF UI (Prism + DryIoc)
  ├─ MainWindow (AI / Chat / Project / Tree / Graph / LibraryCompare partials)
  ├─ ViewModels (MVVM)
  └─ Services
       ├─ OpennessService / TiaStaHost       COM access pinned to one STA thread
       ├─ TiaContextHttpServer (:8001)       built-in MCP tool surface + WriteGate approvals
       ├─ Services/Builders                  offline PLC/HMI XML & JSON builders
       ├─ ProjectIndex / KnowledgeWorker     SQLite RAG index (TIAKnowledgeWorker.exe)
       └─ LibraryCompare                     backup / restore / compare

Python backend (TIAAgent/, FastAPI :8000)
  ├─ app/         routes, models, SSE
  ├─ agent/       supervisor, planner, executor, plan store
  ├─ tools/       MCP catalog and adapters
  ├─ core/        prompts
  └─ converters/  data conversion
```

![System architecture](docs/assets/diagram-architecture.svg)

- The built-in MCP server on `127.0.0.1:8001` is the only tool surface for Openness operations. The Python agent never touches COM directly.
- All Openness COM access runs on a dedicated STA thread (`TiaStaHost`). Do not add `Task.Run` or `Dispatcher.InvokeAsync` around COM objects.
- Writeshaping tools are gated by `WriteGate`; the user approves writes in the chat drawer before they reach TIA.

![Agent internals](docs/assets/diagram-agent-internals.svg)

**Agent paths.** Local models run the ReAct loop in C#; cloud models run the LangGraph supervisor in Python; the Python backend rejects local endpoints, so the two paths never fall back to each other. Tool calls from both paths go through the MCP surface (`127.0.0.1:8001`), and every write converges at `WriteGate` → diff → Openness import + compile on the STA thread.

### Solution layout

| Project | Target | Role |
|---|---|---|
| `TIAOpennessStudio.csproj` | net48 | Main WPF application |
| `TIAOpennessStudio.V21.csproj` | net48 (`TIA_V21`) | V21 build variant with the in-process Portal bridge |
| `TIAKnowledgeWorker.csproj` | net48 / x64 | Knowledge-cache worker process |
| `StandardsKbTool.csproj` | net48 | Encrypted standards packager / verifier |
| `TIAAgent/` | Python 3.11 | FastAPI + LangGraph backend (cloud models only) |

## Knowledge index (RAG)

The agent never reads TIA Portal directly. A separate `TIAKnowledgeWorker.exe` extracts project facts into a local SQLite index, and the built-in MCP server (`127.0.0.1:8001`) exposes query tools over that index.

- **Indexed entities** — containers, PLC blocks, LAD/SCL networks, interface members, symbol cross-references, block call graph, block snapshots, planning knowledge, repair experience and dirty-block tracking.
- **Worker** — `TIAKnowledgeWorker.exe` serves the index over local HTTP (`127.0.0.1:19530`, token-protected); the UI proxies through it.
- **Model** — structured SQLite, not embeddings; canonical operand paths (for example `DB_Main.Line01.RunCmd`) drive reference and impact queries.
- **Tables** — `plc_container`, `plc_block`, `plc_network_lad`, `plc_network_scl`, `plc_interface_member`, `plc_symbol_ref`, `plc_call_edge`, `project_index_state`, `planning_knowledge`, `project_cache_state`, `project_dirty_entry`, `plc_block_snapshot`, plus `repair_experience`.
- **Cache location** — `%LOCALAPPDATA%\TIAOpennessStudio\KnowledgeCache`.
- **Data dictionary** — [`docs/schema/RAG数据表结构说明.md`](docs/schema/RAG数据表结构说明.md) documents every table and column; keep it in sync when the schema changes.

![Knowledge pipelines](docs/assets/diagram-knowledge.svg)

## Encrypted standards library

Company or industry coding standards can be packaged into a read-only `standards.kb` and mounted at runtime:

- **Container** — `TKB1` header, AES-256-CBC ciphertext and an HMAC-SHA256 tag. Keys are derived with PBKDF2-SHA256 (120,000 iterations) from an externally supplied secret salt (`TIA_STD_KB_SECRET`); optional machine binding makes a copied file undecryptable elsewhere.
- **Access model** — the file is HMAC-verified, decrypted in memory, queried through a temporary read-only SQLite database and removed after use. Search only, no export, with rate limiting and a capped result set.
- **Agent surface** — the MCP server exposes `list_standard_topics` / `read_standard_section` for autonomous browsing; the local C# agent uses `search_coding_standard`. Matched rules become constraint directives in the generation prompt, so generated SCL/LAD follows the mounted standard.
- **Packaging** — `StandardsKbTool build <md-dir|file> <out.kb>` turns plaintext Markdown standards into an encrypted `.kb`; `StandardsKbTool verify` proves the round trip. Only the encrypted file is distributable — plaintext sources and `*.secret` stay out of git.

## Change preview and diff

Nothing generated by AI is written straight into the project:

- Generated or edited code is first shown in a **Diff tab** built on DiffPlex, with side-by-side and unified views and per-line change coloring.
- The user must press **Apply** or **Reject**; this is an independent confirmation point, and writes issued through MCP tools additionally pass through `WriteGate` approval.
- `PatchService` generates unified diffs and applies manifest-based batches atomically with rollback.
- **Library compare** produces an XML report for humans; it is not a TIA-importable artifact.

![Write path](docs/assets/diagram-write-flow.svg)

## Context and token budget

- C# `TokenBudget` sizes the local ReAct prompt by model family; unknown local models fall back to a small budget.
- The Python backend budgets cloud requests from the model's real context window, with a 128k fallback.
- Both sides keep a response reserve and compress history before the window overflows; system summaries stay outside the sliding window.

## Requirements

- Windows x64
- .NET Framework 4.8 runtime (build with .NET SDK / MSBuild)
- TIA Portal with Openness installed
  - Supported: V15.1, V16 and V17. V15.1 is the reference baseline; V16/V17 schemas are aligned with the official XSD.
  - V18–V21 profiles are experimental: derived from the naming pattern, with no XSD or sample verification yet. The `TIA_V21` build variant also provides an experimental in-process Portal bridge.
- Python 3.11 for the cloud-agent backend (the repo also supports an embedded interpreter)

### Local services and ports

| Port | Service |
|---|---|
| 8000 | Python FastAPI backend |
| 8001 | TIA context REST + built-in MCP server |
| 19530 | Knowledge worker (default) |
| 11434 | Ollama |

## Getting started

### 1. Clone and provide the Openness DLL

```powershell
git clone <your-fork-url> TIAOpennessStudio
cd TIAOpennessStudio
```

Copy `Siemens.Engineering.dll` from your own TIA installation's `PublicAPI` folder into `TIAOpennessStudio\`:

```powershell
# Example for V15.1 — adjust the path to your installation
Copy-Item "C:\Program Files\Siemens\Automation\Portal V15_1\PublicAPI\V15.1\Siemens.Engineering.dll" `
          ".\TIAOpennessStudio\Siemens.Engineering.dll"
```

`TIAOpennessStudio.csproj` references this file via `HintPath`, so a build fails until it is present. The DLL is git-ignored and must never be committed.

### 2. Build

```powershell
dotnet build .\TIAOpennessStudio.sln
```

`TIAKnowledgeWorker.csproj` compiles several main-program source files directly (source-level link). Always build the solution so both `TIAOpennessStudio.exe` and `TIAKnowledgeWorker.exe` are up to date.

If the app is running and locks the executable (`MSB3021` / `MSB3027`), close it or add `-p:UseAppHost=false`.

### 3. Start the Python backend (optional, for cloud AI)

```powershell
cd TIAAgent
python -m pip install -r requirements.txt
python run.py --port 8000
```

The C# app defaults to `http://127.0.0.1:8000`. The built-in MCP/context service listens on `127.0.0.1:8001` and starts automatically with the app.

### 4. Configure the AI provider

Create `%APPDATA%\TIAOpennessStudio\ai_config.json`:

```json
{
  "Endpoint": "https://your-provider/v1",
  "Model": "your-model",
  "ApiKey": "sk-...",
  "LocalBackendUrl": "http://127.0.0.1:8000"
}
```

Never commit this file. It is git-ignored.

### 5. Optional: local LLM (Ollama)

The app can serve a local model through Ollama. Cloud providers work without this step.

1. Run the helper script, which downloads the standalone runtime (~1.6 GB) into this folder:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\TIAOpennessStudio\Tools\Ollama\download-ollama.ps1
   ```

   Result: `TIAOpennessStudio\Tools\Ollama\ollama.exe`. The runtime is git-ignored and never uploaded.
   Alternatively install Ollama from <https://ollama.com/download>; if it lands in
   `%LOCALAPPDATA%\Programs\Ollama\` or `ollama.exe` is on `PATH`, the app uses that install instead.
2. Pull the recommended model (`ollama pull LisyNeko/qwen3.8-9b-coder`, 6.7 GB, 256K context). Installed models are listed automatically in the model picker.
3. Start the app; if nothing is listening on `127.0.0.1:11434` it launches `ollama serve` itself.

Model weights are stored in Ollama's own model directory (default `%USERPROFILE%\.ollama\models`),
so they stay outside this repository regardless of where `ollama.exe` lives.

## Tests

```powershell
cd TIAAgent
python -m pytest tests -q
```

Current coverage is limited to plan models, tool-surface consistency and prompt contracts. There is no C# unit-test project yet.

## Contributing

The most valuable contributions right now:

1. **Version verification** — validate the generated XML against V18 / V19 / V20 / V21, deepen import coverage for V16 / V17, and update the schema profiles.
2. **Integration tests** — end-to-end plan → generate → compile → write, and failure-path coverage.
3. **HMI write-back** — turn the offline HMI builders into Openness writers.

Please keep changes focused, add tests where practical, and avoid committing Siemens binaries, customer project data or standard-library content.

## License

MIT — see [LICENSE](LICENSE).

## Commercial support and official builds

The source is and will remain free under MIT. Prebuilt, version-verified packages and priority support are planned as a paid option to fund TIA version testing and hardware; they do not remove any functionality from the open-source build.

Contact: scan the WeChat QR code below to reach the maintainer. A personal
"add me as friend" code expires after about 7 days; if it no longer works, open an issue
and we will refresh it.

<img src="docs/assets/contact-wechat.png" width="240" alt="WeChat contact">
