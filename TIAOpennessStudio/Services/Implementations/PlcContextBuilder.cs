using System;
using System.Linq;
using System.Text;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>Builds PLC project context (blocks, members, references) for the agent from the knowledge index.</summary>
    public sealed class PlcContextBuilder : IPlcContextBuilder
    {
        private readonly IPlcKnowledgeIndexService _knowledgeIndexService;

        public PlcContextBuilder(IPlcKnowledgeIndexService knowledgeIndexService)
        {
            _knowledgeIndexService = knowledgeIndexService ?? throw new ArgumentNullException(nameof(knowledgeIndexService));
        }

        public ContextPack BuildContextPack(string projectKey, string query, string rid = null, int topK = 5)
        {
            return _knowledgeIndexService.BuildContextPack(projectKey, query, rid, topK);
        }

        public string BuildRelatedContext(string projectKey, string query, int remainingTokens, int topK = 5)
        {
            var pack = BuildContextPack(projectKey, query, null, topK);
            if (pack == null || pack.CodeFragments.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Structured PLC knowledge hits:");
            foreach (var fragment in pack.CodeFragments.Take(topK))
            {
                sb.AppendLine("- " + fragment.BlockName + "/" + fragment.BlockType + "/" + fragment.Language);
                if (!string.IsNullOrWhiteSpace(fragment.Summary))
                {
                    sb.AppendLine("  Summary: " + fragment.Summary);
                }

                var detailLines = (fragment.Content ?? string.Empty)
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .Take(8)
                    .ToList();

                foreach (var line in detailLines)
                {
                    sb.AppendLine("  " + line);
                }
            }

            var text = sb.ToString().TrimEnd();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            const int charsPerToken = 4;
            var maxChars = Math.Max(remainingTokens, 0) * charsPerToken;
            if (maxChars <= 0 || text.Length <= maxChars)
            {
                return text;
            }

            return text.Substring(0, maxChars) + "\n... (truncated)";
        }
    }
}
