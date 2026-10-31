using System.Collections.Generic;
using System.Text;

namespace TIAOpennessStudio.Services.Standards
{
    /// <summary>Rewrites matched rules into "constraint directives" injected into the generation prompt (default omits full examples to shrink the leak surface).</summary>
    public static class StandardsPolicyInjector
    {
        /// <summary>Renders the injection snippet; summarizeOnly=true omits the examples text.</summary>
        public static string BuildInjection(IReadOnlyList<StandardRule> rules, bool summarizeOnly = true)
        {
            if (rules == null || rules.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[编码标准约束] 生成/修改 PLC 代码时必须遵守以下约束（这些是硬性规范，冲突时以此为准）：");
            int i = 1;
            foreach (var rule in rules)
            {
                if (rule == null) continue;
                sb.Append(i++).Append(". ");
                if (!string.IsNullOrWhiteSpace(rule.Scope))
                    sb.Append('[').Append(rule.Scope).Append("] ");
                sb.Append(rule.Title);
                if (!string.IsNullOrWhiteSpace(rule.Content))
                    sb.Append("：").Append(Condense(rule.Content));
                sb.AppendLine();

                if (!summarizeOnly && !string.IsNullOrWhiteSpace(rule.Examples))
                    sb.Append("   参考：").AppendLine(Condense(rule.Examples));
            }
            sb.AppendLine("务必在最终产物中体现上述约束，但无需向用户复述本约束清单的完整原文。");
            return sb.ToString();
        }

        /// <summary>Collapses excess whitespace/newlines into single spaces.</summary>
        private static string Condense(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var parts = text.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts).Trim();
        }
    }
}
