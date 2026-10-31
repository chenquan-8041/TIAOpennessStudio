# 定位并加载 Siemens.Engineering.dll

从 Windows 注册表解析已安装 TIA 版本的 Openness API 路径，再用 AssemblyResolve 加载。
来源：Program.cs、Services/Implementations/TiaApiPathResolver.cs（本项目已验证）。

## 关键事实
- Openness DLL 名：`Siemens.Engineering.dll`，位于各版本的 PublicAPI 目录。
- 版本注册表键（LocalMachine\SOFTWARE，需试 Registry64 / Registry32 视图）：
  - 已装版本枚举：`SOFTWARE\Siemens\Automation\_InstalledSW\Global\Bundles\`（子键名含 "TIA Portal V18" 等）
  - 各版本 Openness 根：`SOFTWARE\Siemens\Automation\_InstalledSW\TIAP{18|17|16|15|15_1|14}\TIA_Opns`（Value "Path"）
  - 64 位机回退：同上加 `Wow6432Node\`
- API 路径候选（在 Openness 根下按序试，取存在 Siemens.Engineering.dll 的）：
  `PublicAPI\V{ver}` → `Bin\PublicAPI\V{ver}` → `Bin\PublicAPI`
- Legacy 回退：`C:\Program Files\Siemens\Automation\Portal V{ver}\PublicAPI\V{ver}`

## 加载（AssemblyResolve）
```csharp
AppDomain.CurrentDomain.AssemblyResolve += (s, args) => {
    var name = args.Name.Substring(0, args.Name.IndexOf(',')) + ".dll";
    if (name == "Siemens.Engineering.dll") {
        var path = Path.Combine(apiPath, name);  // apiPath = 上面解析出的 PublicAPI 目录
        if (File.Exists(path)) return Assembly.LoadFrom(Path.GetFullPath(path));
    }
    return null;
};
```
注意：AssemblyResolve 必须在**首次触碰任何 Siemens.Engineering 类型之前**注册。
