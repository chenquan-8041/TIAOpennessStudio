using System.Text.RegularExpressions;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Language-neutral structure of an SCL block: block type, name, declaration
    /// section, and code body.
    /// </summary>
    public class SclStructure
    {
        public string BlockType { get; set; }
        public string BlockName { get; set; }
        public string Declarations { get; set; }
        public string Body { get; set; }
    }

    /// <summary>
    /// Parses SCL source into a <see cref="SclStructure"/> (block type, name,
    /// declarations, body). Extracted from AgentOrchestrator to isolate these
    /// internal tool helpers.
    /// </summary>
    public static class SclStructureParser
    {
        public static SclStructure ParseSclToStructuredData(string sclCode)
        {
            if (string.IsNullOrWhiteSpace(sclCode)) return null;

            var structure = new SclStructure();
            structure.BlockType = ExtractBlockTypeFromScl(sclCode) ?? "FB";
            structure.BlockName = ExtractBlockNameFromScl(sclCode) ?? "AI_Generated_Block";

            int beginIndex = sclCode.IndexOf("BEGIN", System.StringComparison.OrdinalIgnoreCase);
            if (beginIndex >= 0)
            {
                structure.Declarations = sclCode.Substring(0, beginIndex).Trim();

                int endBlockIndex = sclCode.LastIndexOf($"END_{structure.BlockType}", System.StringComparison.OrdinalIgnoreCase);
                if (endBlockIndex < 0 && structure.BlockType == "FB")
                    endBlockIndex = sclCode.LastIndexOf("END_FUNCTION_BLOCK", System.StringComparison.OrdinalIgnoreCase);
                else if (endBlockIndex < 0 && structure.BlockType == "FC")
                    endBlockIndex = sclCode.LastIndexOf("END_FUNCTION", System.StringComparison.OrdinalIgnoreCase);

                if (endBlockIndex > beginIndex)
                    structure.Body = sclCode.Substring(beginIndex + 5, endBlockIndex - beginIndex - 5).Trim();
                else
                    structure.Body = sclCode.Substring(beginIndex + 5).Trim();
            }
            else
            {
                structure.Body = sclCode;
            }

            return structure;
        }

        public static string ExtractBlockNameFromScl(string scl)
        {
            if (string.IsNullOrWhiteSpace(scl)) return null;
            var patterns = new[]
            {
                @"FUNCTION_BLOCK\s+""?([A-Za-z_][A-Za-z0-9_]*)""?",
                @"DATA_BLOCK\s+""?([A-Za-z_][A-Za-z0-9_]*)""?",
                @"ORGANIZATION_BLOCK\s+""?([A-Za-z_][A-Za-z0-9_]*)""?",
                @"FUNCTION\s+""?([A-Za-z_][A-Za-z0-9_]*)""?\s*:",
                @"TYPE\s+""?([A-Za-z_][A-Za-z0-9_]*)""?",
            };
            foreach (var p in patterns)
            {
                var m = Regex.Match(scl, p, RegexOptions.IgnoreCase);
                if (m.Success && m.Groups.Count > 1) return m.Groups[1].Value;
            }
            return null;
        }

        public static string ExtractBlockTypeFromScl(string scl)
        {
            if (string.IsNullOrWhiteSpace(scl)) return null;
            if (scl.IndexOf("FUNCTION_BLOCK", System.StringComparison.OrdinalIgnoreCase) >= 0) return "FB";
            if (scl.IndexOf("ORGANIZATION_BLOCK", System.StringComparison.OrdinalIgnoreCase) >= 0) return "OB";
            if (scl.IndexOf("DATA_BLOCK", System.StringComparison.OrdinalIgnoreCase) >= 0) return "DB";
            if (Regex.IsMatch(scl, @"\bTYPE\s+\S", RegexOptions.IgnoreCase)
                && scl.IndexOf("END_TYPE", System.StringComparison.OrdinalIgnoreCase) >= 0) return "UDT";
            if (scl.IndexOf("FUNCTION ", System.StringComparison.OrdinalIgnoreCase) >= 0
                && scl.IndexOf("END_FUNCTION", System.StringComparison.OrdinalIgnoreCase) >= 0) return "FC";
            return null;
        }
    }
}
