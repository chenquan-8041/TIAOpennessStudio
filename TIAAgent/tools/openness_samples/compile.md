# 编译 PLC 软件 / 单个块

来源：Services/Siemens/Portal.Software.cs、OpennessService.cs（本项目已验证）。

## 编译整个 PlcSoftware
```csharp
using Siemens.Engineering.Compiler;

var compilable = plcSoftware.GetService<ICompilable>();
if (compilable == null) throw new Exception("Compile service not available");
CompilerResult result = compilable.Compile();

// 结果结构
int errors = result.ErrorCount;
int warnings = result.WarningCount;
foreach (var msg in result.Messages) {
    string state = msg.State.ToString();      // "Error" | "Warning" | ...
    string desc  = msg.Description;           // 消息文本（含块名，但不含行号）
    // 注意：Siemens 的 Message 不暴露行号，定位精度到块级。
}
bool ok = result.ErrorCount == 0;
```

## 只编译目标块（避免连带编译 F-Program 安全程序报错）
```csharp
var blockCompilable = block.GetService<ICompilable>();
var blockResult = blockCompilable.Compile();
if (blockResult.ErrorCount > 0) { /* 回滚 / 反馈错误 */ }
```
本项目 import 块时用"块级质量门编译"：写入后只编译该块，失败即回滚。
