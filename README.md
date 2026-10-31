# TIA Openness Studio

Desktop tooling for Siemens TIA Portal Openness (WPF / .NET Framework 4.8): PLC project operations, block workflows, AI-assisted code generation, a project knowledge index, and library backup / version comparison.

> **Status: v1.30.0 — usable baseline, AI long-task orchestration is experimental.**
> The desktop/TIA workflow is stable. The cloud-agent pipeline (Planner → Executor → per-block compile) is implemented but has not yet been validated end-to-end on a real project.

## Disclaimer

- This project is **not affiliated with, endorsed by, or supported by Siemens**.
- `TIA Portal`, `STEP 7`, `WinCC` and `Openness` are trademarks of Siemens AG.
- **No Siemens binaries are distributed in this repository.** You must own a valid TIA Portal license and copy `Siemens.Engineering.dll` from your own installation.
- The MIT license covers only the code in this repository, not Siemens software or third-party standards content.

## Features

- **Project & device operations** — open / save / close / compile TIA projects, load device and PLC trees.
- **Block & type workflows** — import PLC Blocks and PLC Data Types, export objects as XML, import/export project texts.
- **Native PLC generation** — offline builders for UDT, global DB, FC, FB and LAD (FlgNet) blocks, then Openness import.
- **AI workflow** — chat-style SCL/XML generation, streaming output, project-index-assisted context retrieval.
- **Cloud agent (experimental)** — Python FastAPI + LangGraph supervisor, project-level DAG planning, per-block execution, milestone approval and resume.
- **Knowledge index / RAG** — SQLite-backed block, network, symbol, call-graph and cross-reference index, served to the agent over a local MCP endpoint.
- **Library backup & compare** — create backups, inspect history, restore with a safety copy, and compare types/master copies via Openness.
- **Localization** — English / German / Chinese.

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

- The built-in MCP server on `127.0.0.1:8001` is the only tool surface for Openness operations. The Python agent never touches COM directly.
- All Openness COM access runs on a dedicated STA thread (`TiaStaHost`). Do not add `Task.Run` or `Dispatcher.InvokeAsync` around COM objects.
- Writeshaping tools are gated by `WriteGate`; the user approves writes in the chat drawer before they reach TIA.

## Requirements

- Windows x64
- .NET Framework 4.8 runtime (build with .NET SDK / MSBuild)
- TIA Portal with Openness installed
  - V15.1 is the empirically verified target (Interface v3 / FlgNet v3 / SCL v2).
  - V16–V21 schema profiles are present but still marked `TODO(verify)`.
- Python 3.11 for the cloud-agent backend (the repo also supports an embedded interpreter)

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

## Tests

```powershell
cd TIAAgent
python -m pytest tests -q
```

Current coverage is limited to plan-model serialization, persistence and topology (33 tests, `tests/test_plan_models.py`). There is no C# unit-test project yet.

## Known limitations

- **Version matrix**: only TIA V15.1 schema output has been verified. V16–V21 profiles are placeholders (`TODO(verify)`).
- **LAD expressiveness**: `build_lad_fc` / `build_lad_fb` only emit call-type networks (one FC/FB call wired to tags/constants). Free contacts, coils, parallel branches, compares and timers are not supported.
- **HMI**: builders produce offline XML/JSON only; they do not write screens into TIA yet.
- **Long-task orchestration**: experimental. Known open issues include `list_udt_types` timeouts, empty DB coder output and dirty-project compile state after repeated test writes.
- **Cloud write safety**: only tools covered by `WriteGate` require per-write approval. Do not expose port 8001 beyond localhost without `TIA_MCP_AUTH_TOKEN`.
- **Compare output**: the XML report is for humans, not a TIA-importable artifact.

## Contributing

The most valuable contributions right now:

1. **Version verification** — validate the generated XML against V16 / V17 / V18 / V19 / V20 / V21 and update the schema profiles.
2. **Integration tests** — end-to-end plan → generate → compile → write, and failure-path coverage.
3. **HMI write-back** — turn the offline HMI builders into Openness writers.

Please keep changes focused, add tests where practical, and avoid committing Siemens binaries, customer project data or standard-library content.

## License

MIT — see [LICENSE](LICENSE).

## Commercial support and official builds

The source is and will remain free under MIT. Prebuilt, version-verified packages and priority support are planned as a paid option to fund TIA version testing and hardware; they do not remove any functionality from the open-source build.

Contact: **TBD (to be filled before the first public release)**.
