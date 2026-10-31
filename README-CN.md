# TIA Openness Studio

基于 Siemens TIA Portal Openness 的桌面工具（WPF / .NET Framework 4.8），覆盖 PLC 工程操作、块工作流、AI 辅助生成、项目知识索引与库备份/版本对比。

> **当前状态：v1.30.0 — 基础功能可用，AI 长任务编排仍属实验性。**
> 桌面/TIA 工作流已稳定；云端 Agent 链路（Planner → Executor → 逐块编译）已实现，但尚未在真实工程上端到端验证。

## 免责声明

- 本项目**与 Siemens 无任何关联**，未获其背书或支持。
- `TIA Portal`、`STEP 7`、`WinCC`、`Openness` 均为 Siemens AG 的商标。
- **仓库不包含任何 Siemens 二进制文件。** 使用者必须自备有效的 TIA Portal 授权，并从自己的安装目录复制 `Siemens.Engineering.dll`。
- MIT 协议仅覆盖本仓库代码，不覆盖 Siemens 软件或第三方标准内容。

## 核心能力

- **项目与设备**：打开/保存/关闭/编译 TIA 项目，加载设备与 PLC 树。
- **块与类型**：导入 PLC Blocks / PLC Data Types，导出对象 XML，项目文本导入导出。
- **原生 PLC 生成**：离线构建 UDT、全局 DB、FC、FB、LAD（FlgNet）块，再经 Openness 导入。
- **AI 工作流**：聊天式 SCL/XML 生成、流式输出、项目索引辅助上下文检索。
- **云端 Agent（实验性）**：Python FastAPI + LangGraph supervisor，项目级 DAG 规划、逐块执行、里程碑审批与断点续跑。
- **知识索引 / RAG**：基于 SQLite 的块、网络、符号、调用图与交叉引用索引，经本地 MCP 端点提供给 Agent。
- **库备份与对比**：创建备份、查看历史、带安全副本的恢复、基于 Openness 的类型/主副本比较。
- **多语言**：English / Deutsch / 中文。

## 架构

```
WPF UI（Prism + DryIoc）
  ├─ MainWindow（AI / Chat / Project / Tree / Graph / LibraryCompare 分域）
  ├─ ViewModels（MVVM）
  └─ Services
       ├─ OpennessService / TiaStaHost       所有 COM 访问固定在单一 STA 线程
       ├─ TiaContextHttpServer (:8001)       内建 MCP 工具面 + WriteGate 写审批
       ├─ Services/Builders                  PLC/HMI 离线 XML & JSON 构建器
       ├─ ProjectIndex / KnowledgeWorker     SQLite RAG 索引（TIAKnowledgeWorker.exe）
       └─ LibraryCompare                    备份 / 恢复 / 对比

Python 后端（TIAAgent/，FastAPI :8000）
  ├─ app/         路由、模型、SSE
  ├─ agent/       supervisor、planner、executor、plan store
  ├─ tools/       MCP 目录与适配层
  ├─ core/        提示词
  └─ converters/  数据转换
```

- 内建 MCP 服务（`127.0.0.1:8001`）是唯一面向 Openness 的工具面，Python Agent 不直接访问 COM。
- 所有 Openness COM 调用都跑在独立 STA 线程（`TiaStaHost`）上；不要在 COM 对象外使用 `Task.Run` 或 `Dispatcher.InvokeAsync`。
- 写操作由 `WriteGate` 拦截，用户在聊天抽屉中批卡后才会真正写入 TIA。

## 环境要求

- Windows x64
- .NET Framework 4.8 运行时（使用 .NET SDK / MSBuild 构建）
- 已安装 TIA Portal 与 Openness
  - V15.1 为已实证目标（Interface v3 / FlgNet v3 / SCL v2）。
  - V16–V21 schema 档位已预留，但仍标记为 `TODO(verify)`。
- Python 3.11（云端 Agent 后端，可使用仓库附带的嵌入式解释器）

## 快速开始

### 1. 克隆并准备 Openness DLL

```powershell
git clone <your-fork-url> TIAOpennessStudio
cd TIAOpennessStudio
```

从自己的 TIA 安装目录复制 `Siemens.Engineering.dll` 到 `TIAOpennessStudio\`：

```powershell
# 以 V15.1 为例，请按实际安装路径调整
Copy-Item "C:\Program Files\Siemens\Automation\Portal V15_1\PublicAPI\V15.1\Siemens.Engineering.dll" `
          ".\TIAOpennessStudio\Siemens.Engineering.dll"
```

`TIAOpennessStudio.csproj` 通过 `HintPath` 引用该文件，缺失时会构建失败。该 DLL 已被 git 忽略，**绝不能提交**。

### 2. 构建

```powershell
dotnet build .\TIAOpennessStudio.sln
```

`TIAKnowledgeWorker.csproj` 以源码链接方式编译主程序的部分文件，因此务必构建整个解决方案，确保 `TIAOpennessStudio.exe` 与 `TIAKnowledgeWorker.exe` 同步更新。

若应用正在运行并锁住 exe（`MSB3021` / `MSB3027`），关闭应用或加 `-p:UseAppHost=false`。

### 3. 启动 Python 后端（可选，用于云端 AI）

```powershell
cd TIAAgent
python -m pip install -r requirements.txt
python run.py --port 8000
```

C# 端默认连接 `http://127.0.0.1:8000`；内建 MCP/上下文服务监听 `127.0.0.1:8001`，随应用自动启动。

### 4. 配置 AI 提供方

创建 `%APPDATA%\TIAOpennessStudio\ai_config.json`：

```json
{
  "Endpoint": "https://your-provider/v1",
  "Model": "your-model",
  "ApiKey": "sk-...",
  "LocalBackendUrl": "http://127.0.0.1:8000"
}
```

该文件已被 git 忽略，切勿提交。

## 测试

```powershell
cd TIAAgent
python -m pytest tests -q
```

当前测试仅覆盖 plan 模型的序列化、持久化与拓扑排序（33 个用例，`tests/test_plan_models.py`）。C# 侧暂无单元测试工程。

## 已知限制

- **版本矩阵**：仅 TIA V15.1 的 schema 输出经过实证；V16–V21 档位为占位（`TODO(verify)`）。
- **LAD 表达力**：`build_lad_fc` / `build_lad_fb` 只支持 call-type 网络（一个网络调用一个 FC/FB 并接线），不支持自由触点/线圈/并联支路/比较/定时器。
- **HMI**：构建器只产离线 XML/JSON，尚未写回 TIA。
- **长任务编排**：实验性。已知问题包括 `list_udt_types` 超时、DB 块 coder 吐空、多轮测试写入后工程处于未编译脏状态。
- **云端写安全**：仅 `WriteGate` 覆盖的工具需要逐次审批。若将 8001 端口暴露到 localhost 之外，必须设置 `TIA_MCP_AUTH_TOKEN`。
- **对比输出**：XML 报告是给人看的，不是可导入 TIA 的工程产物。

## 参与贡献

当前最需要帮助的方向：

1. **版本验证**：在 V16 / V17 / V18 / V19 / V20 / V21 上校验生成 XML，并更新 schema 档位。
2. **集成测试**：规划 → 生成 → 编译 → 写入的端到端链路，以及失败路径覆盖。
3. **HMI 写回**：把离线 HMI 构建器扩展为 Openness 写入器。

请保持改动聚焦、尽量补测试，并避免提交 Siemens 二进制、客户工程数据或标准库内容。

## 许可证

MIT — 见 [LICENSE](LICENSE)。

## 商业支持与官方构建

源码将始终保持 MIT 免费。计划提供"预编译、已验版本"的官方构建包与优先支持，作为付费选项，用于补贴 TIA 版本测试与硬件成本；付费不会削减开源版的任何功能。

联系方式：**待补充（首次公开发布前填入）**。
