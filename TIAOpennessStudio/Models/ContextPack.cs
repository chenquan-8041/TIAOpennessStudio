using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TIAOpennessStudio.Models
{
    public sealed class ContextPack
    {
        public string Rid { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int TotalTokenEstimate { get; set; }
        public List<CodeFragment> CodeFragments { get; set; } = new List<CodeFragment>();
        public ProjectFacts ProjectFacts { get; set; } = new ProjectFacts();
        public RuntimeContext RuntimeContext { get; set; } = new RuntimeContext();
        public DependencyContext DependencyContext { get; set; } = new DependencyContext();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        public string ToPromptString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 项目上下文 ===");
            
            if (ProjectFacts != null)
            {
                if (ProjectFacts.UDTs.Count > 0)
                {
                    sb.AppendLine("\n--- 用户定义类型 (UDT) ---");
                    foreach (var udt in ProjectFacts.UDTs.Take(10))
                    {
                        sb.AppendLine(udt.ToPromptString());
                    }
                }
                
                if (ProjectFacts.TagTables.Count > 0)
                {
                    sb.AppendLine("\n--- 标签表 ---");
                    foreach (var tag in ProjectFacts.TagTables.Take(5))
                    {
                        sb.AppendLine($"- {tag.Name}: {tag.Type}");
                    }
                }
            }

            if (CodeFragments.Count > 0)
            {
                sb.AppendLine("\n--- 相关代码片段 ---");
                foreach (var frag in CodeFragments.Take(5))
                {
                    sb.AppendLine($"\n[{frag.Source}] {frag.BlockName}/{frag.BlockType}");
                    sb.AppendLine(frag.Content);
                }
            }

            // Retrieval coverage/truncation note (P1a): lets the Agent know whether the results were truncated by LIMIT.
            if (Metadata != null && Metadata.TryGetValue("search_coverage", out var coverage) && !string.IsNullOrWhiteSpace(coverage))
                sb.AppendLine("\n[检索覆盖] " + coverage);

            if (DependencyContext != null && DependencyContext.DependencyRefs.Count > 0)
            {
                sb.AppendLine("\n--- 结构化依赖引用 ---");
                foreach (var dep in DependencyContext.DependencyRefs.Take(20))
                {
                    var kind = dep.AccessKind == "call" ? "调用" : dep.AccessKind;
                    sb.AppendLine($"[{kind}] {dep.BlockName}({dep.BlockType})");
                    if (!string.IsNullOrWhiteSpace(dep.CanonicalPath))
                        sb.AppendLine($"  -> {dep.CanonicalPath}");
                    if (!string.IsNullOrWhiteSpace(dep.TargetBlock) && dep.AccessKind == "call")
                        sb.AppendLine($"  -> 目标块: {dep.TargetBlock}");
                    if (dep.NetworkIndex > 0)
                        sb.AppendLine($"  -> 网络: {dep.NetworkIndex}");
                    if (!string.IsNullOrWhiteSpace(dep.SourceRef))
                        sb.AppendLine($"  -> 位置: {dep.SourceRef}");
                }
            }

            if (RuntimeContext != null && !string.IsNullOrWhiteSpace(RuntimeContext.CompileResult))
            {
                sb.AppendLine("\n--- 最近编译结果 ---");
                sb.AppendLine(RuntimeContext.CompileResult);
            }

            return sb.ToString();
        }
    }

    public sealed class CodeFragment
    {
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public string Language { get; set; }
        public string Content { get; set; }
        public string Summary { get; set; }
        public string Source { get; set; }
        public int RelevanceScore { get; set; }
        public int TokenEstimate { get; set; }
        public string Uid { get; set; } = Guid.NewGuid().ToString("N");
    }

    public sealed class ProjectFacts
    {
        public List<UdtInfo> UDTs { get; set; } = new List<UdtInfo>();
        public List<TagInfo> TagTables { get; set; } = new List<TagInfo>();
        public List<string> BlockSignatures { get; set; } = new List<string>();
        public List<string> Constraints { get; set; } = new List<string>();
        public Dictionary<string, string> GlobalConstants { get; set; } = new Dictionary<string, string>();
    }

    public sealed class UdtInfo
    {
        public string Name { get; set; }
        public string RawXml { get; set; }
        public List<UdtMember> Members { get; set; } = new List<UdtMember>();
        public List<string> NestedUdtNames { get; set; } = new List<string>();
        
        public string ToPromptString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"TYPE {Name}");
            foreach (var m in Members)
            {
                var typeStr = m.TypeName;
                if (NestedUdtNames.Contains(m.TypeName))
                {
                    typeStr = $"{m.TypeName} (嵌套UDT)";
                }
                sb.AppendLine($"  {m.Name} : {typeStr}; // {m.DataType}");
            }
            sb.AppendLine("END_TYPE");
            return sb.ToString();
        }
    }

    public sealed class UdtMember
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
        public string DataType { get; set; }
        public string InitialValue { get; set; }
        public string Comment { get; set; }
    }

    public sealed class TagInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Address { get; set; }
        public string Comment { get; set; }
    }

    public sealed class RuntimeContext
    {
        public string CompileResult { get; set; }
        public List<CompileError> RecentErrors { get; set; } = new List<CompileError>();
        public string SessionSummary { get; set; }
    }

    public sealed class CompileError
    {
        public int Line { get; set; }
        public string Block { get; set; }
        public string Message { get; set; }
        public string Severity { get; set; }
    }

    public sealed class DependencyContext
    {
        public List<DependencyRefEntry> DependencyRefs { get; set; } = new List<DependencyRefEntry>();
        public int TotalCallRelations { get; set; }
        public int TotalSymbolRefs { get; set; }
    }

    public sealed class DependencyRefEntry
    {
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public string Language { get; set; }
        public string CanonicalPath { get; set; }
        public string AccessKind { get; set; }
        public string TargetBlock { get; set; }
        public int NetworkIndex { get; set; }
        public string SourceRef { get; set; }
    }
}
