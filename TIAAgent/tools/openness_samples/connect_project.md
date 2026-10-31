# 连接 TIA Portal 并打开项目

优先 attach 已运行的 TIA 进程，没有再新建实例。来源：Services/Siemens/Portal.cs、OpennessService.cs（本项目已验证）。

## 连接
```csharp
using Siemens.Engineering;

TiaPortal portal;
var processes = TiaPortal.GetProcesses();      // 枚举已运行的 TIA 进程
if (processes.Any())
    portal = processes.First().Attach();        // attach 现有进程（复用用户已打开的工程）
else
    portal = new TiaPortal(TiaPortalMode.WithUserInterface);  // 或 WithoutUserInterface 无界面
```
注意：`Attach()` 可能阻塞，本项目放到后台线程 + 超时保护，避免卡死 UI/agent 循环。

## 打开项目
```csharp
var fi = new System.IO.FileInfo(projectPath);   // .ap18 / .ap17 ...
Project project;
try   { project = portal.Projects.OpenWithUpgrade(fi); } // 跨版本自动升级
catch { project = portal.Projects.Open(fi); }            // 部分环境只有 Open
```

## 定位 PlcSoftware（后续 block/编译操作的入口）
```csharp
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

PlcSoftware plcSw = null;
foreach (var device in project.Devices)
  foreach (var item in device.DeviceItems) {
    var container = ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
    if (container?.Software is PlcSoftware sw) { plcSw = sw; break; }
  }
```
