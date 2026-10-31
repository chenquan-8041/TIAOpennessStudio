#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// SCL StructuredText XML fragment builder (namespace version resolved by TIA version).
    /// The first version covers only IF/ELSE, assignments, local variables, constants, and line/indent handling, serving a minimal verifiable use case first.
    /// </summary>
    public sealed class StructuredTextXmlBuilder
    {
        private int _uid;
        private readonly StringBuilder _xml = new StringBuilder();

        public StructuredTextXmlBuilder(int firstUid = 21)
        {
            _uid = firstUid;
        }

        public StructuredTextXmlBuilder Token(string text)
        {
            _xml.Append("<Token Text=\"").Append(Escape(text)).Append("\" UId=\"").Append(Next()).AppendLine("\" />");
            return this;
        }

        public StructuredTextXmlBuilder Blank(int count = 1)
        {
            if (count <= 1)
                _xml.Append("<Blank UId=\"").Append(Next()).AppendLine("\" />");
            else
                _xml.Append("<Blank Num=\"").Append(count).Append("\" UId=\"").Append(Next()).AppendLine("\" />");
            return this;
        }

        public StructuredTextXmlBuilder NewLine()
        {
            _xml.Append("<NewLine UId=\"").Append(Next()).AppendLine("\" />");
            return this;
        }

        public StructuredTextXmlBuilder LocalVariable(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("SCL local variable name is required.", nameof(name));
            return LocalVariable(new[] { name });
        }

        // Local-variable multi-segment path: used for multi-instance FB outputs (#trig.Q) / local STRUCT members, etc.
        // The V21 schema requires <Token Text="."/> separators between the Symbol's Components.
        public StructuredTextXmlBuilder LocalVariable(params string[] components)
        {
            if (components == null || components.Length == 0)
                throw new ArgumentException("SCL local variable name is required.", nameof(components));
            foreach (var c in components)
            {
                if (string.IsNullOrWhiteSpace(c))
                    throw new ArgumentException("SCL local variable path segment is required.", nameof(components));
                EnsureLocalSymbolSegment(c);
            }

            _xml.Append("<Access Scope=\"LocalVariable\" UId=\"").Append(Next()).AppendLine("\">");
            _xml.Append("<Symbol UId=\"").Append(Next()).AppendLine("\">");
            for (var i = 0; i < components.Length; i++)
            {
                if (i > 0)
                    _xml.Append("<Token Text=\".\" UId=\"").Append(Next()).AppendLine("\" />");
                _xml.Append("<Component Name=\"").Append(Escape(components[i])).Append("\" UId=\"").Append(Next()).AppendLine("\" />");
            }
            _xml.AppendLine("</Symbol>");
            _xml.AppendLine("</Access>");
            return this;
        }

        // Global variable access: supports a single segment ("I_Start") or a multi-segment path ("DB_Motor.Speed" → ["DB_Motor","Speed"]).
        // The V21 schema requires <Token Text="."/> separators between the Symbol's Components; a single-segment global usually carries
        // a <BooleanAttribute Name="HasQuotes">true</BooleanAttribute> marker indicating the quoted form.
        public StructuredTextXmlBuilder GlobalVariable(params string[] components)
        {
            if (components == null || components.Length == 0)
                throw new ArgumentException("SCL global variable name is required.", nameof(components));
            foreach (var c in components)
                if (string.IsNullOrWhiteSpace(c))
                    throw new ArgumentException("SCL global variable path segment is required.", nameof(components));

            _xml.Append("<Access Scope=\"GlobalVariable\" UId=\"").Append(Next()).AppendLine("\">");
            _xml.Append("<Symbol UId=\"").Append(Next()).AppendLine("\">");
            for (var i = 0; i < components.Length; i++)
            {
                if (i > 0)
                    _xml.Append("<Token Text=\".\" UId=\"").Append(Next()).AppendLine("\" />");
                if (i == 0 && components.Length == 1)
                {
                    // Single-segment global: includes the HasQuotes marker
                    _xml.Append("<Component Name=\"").Append(Escape(components[i])).Append("\" UId=\"").Append(Next()).AppendLine("\">");
                    _xml.Append("<BooleanAttribute Name=\"HasQuotes\" UId=\"").Append(Next()).AppendLine("\">true</BooleanAttribute>");
                    _xml.AppendLine("</Component>");
                }
                else
                {
                    _xml.Append("<Component Name=\"").Append(Escape(components[i])).Append("\" UId=\"").Append(Next()).AppendLine("\" />");
                }
            }
            _xml.AppendLine("</Symbol>");
            _xml.AppendLine("</Access>");
            return this;
        }

        // Smart Symbol: automatically decides scope by TIA convention. Supported input forms:
        //   "I_Start"           → global variable, single segment
        //   "DB.member"         → global variable, multi-segment (whole path quoted)
        //   "DB".member         → global variable, multi-segment (only the DB name quoted; common in TIA SCL text)
        //   "DB.m1".m2          → global variable, multi-segment (partially quoted)
        //   var / #var          → local variable, single segment
        //   #trig.Q / var.m     → local variable, multi-segment
        // Rule: if the raw input contains ", treat it as global; dots are always split into segments.
        public StructuredTextXmlBuilder Symbol(string nameSpec)
        {
            if (string.IsNullOrWhiteSpace(nameSpec))
                throw new ArgumentException("SCL variable name is required.", nameof(nameSpec));
            var trimmed = nameSpec.Trim();
            if (trimmed.StartsWith("#")) trimmed = trimmed.Substring(1);
            var hasQuote = trimmed.Contains('"');
            // Remove all quotes, then split by .
            var stripped = trimmed.Replace("\"", "");
            var parts = stripped.Split('.');
            if (hasQuote) return GlobalVariable(parts);
            if (parts.Length > 1) return LocalVariable(parts);
            return LocalVariable(stripped);
        }

        // Guard: a local-variable segment must be a valid SCL identifier. All {sym} values in condition / source / line go through this check,
        // moving the silent error of "a whole expression treated as a single variable name" from TIA compile time to the offline dryRun stage.
        // Global (quoted) symbol names allow spaces/special characters, so only local segments are checked, not in GlobalVariable.
        private static void EnsureLocalSymbolSegment(string segment)
        {
            var s = segment.Trim();
            bool valid = s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_');
            for (var i = 1; valid && i < s.Length; i++)
                if (!char.IsLetterOrDigit(s[i]) && s[i] != '_')
                    valid = false;

            if (!valid)
                throw new ArgumentException(
                    "Invalid SCL local symbol: \"" + segment + "\". It contains operators/spaces/parentheses and looks like an expression rather than a single variable name. " +
                    "condition / source / {sym} accept a single variable name only; for complex expressions use op:\"line\" + items[], " +
                    "and for CASE/FOR/WHILE or function calls (ABS/LIMIT/TON etc.) use external SCL (ImportPlcExternalSource + GenerateBlocksFromExternalSource).",
                    nameof(segment));

            if (string.Equals(s, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "FALSE", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "SCL boolean literal \"" + segment + "\" cannot be used as a symbol. Use literalValue/value for assignments, or {lit:\"" + s.ToUpperInvariant() + "\"} inline.",
                    nameof(segment));
        }

        public StructuredTextXmlBuilder LiteralConstant(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("SCL constant is required.", nameof(value));

            _xml.Append("<Access Scope=\"LiteralConstant\" UId=\"").Append(Next()).AppendLine("\">");
            _xml.Append("<Constant UId=\"").Append(Next()).AppendLine("\">");
            _xml.Append("<ConstantValue UId=\"").Append(Next()).Append("\">").Append(Escape(value)).AppendLine("</ConstantValue>");
            _xml.AppendLine("</Constant>");
            _xml.AppendLine("</Access>");
            return this;
        }

        public StructuredTextXmlBuilder Assignment(string target, string literalValue, int indent = 0)
        {
            if (indent > 0) Blank(indent);
            // target uses Symbol to auto-detect scope ("x" global / x local)
            Symbol(target).Blank().Token(":=").Blank().LiteralConstant(literalValue).Token(";").NewLine();
            return this;
        }

        // Symbol-to-symbol assignment, e.g. "Q_RunLamp4" := "Q_RunLamp3". target and source each go through Symbol detection.
        public StructuredTextXmlBuilder AssignFromSymbol(string target, string source, int indent = 0)
        {
            if (indent > 0) Blank(indent);
            Symbol(target).Blank().Token(":=").Blank().Symbol(source).Token(";").NewLine();
            return this;
        }

        public StructuredTextXmlBuilder IfHeader(string conditionVariable, int indent = 0)
        {
            if (indent > 0) Blank(indent);
            Token("IF").Blank().Symbol(conditionVariable).Blank().Token("THEN").NewLine();
            return this;
        }

        // Added: ELSIF — commonly used for multi-stage priority logic
        public StructuredTextXmlBuilder ElsIfHeader(string conditionVariable, int indent = 0)
        {
            if (indent > 0) Blank(indent);
            Token("ELSIF").Blank().Symbol(conditionVariable).Blank().Token("THEN").NewLine();
            return this;
        }

        public StructuredTextXmlBuilder ElseLine(int indent = 0)
        {
            if (indent > 0) Blank(indent);
            Token("ELSE").NewLine();
            return this;
        }

        public StructuredTextXmlBuilder EndIf(int indent = 0)
        {
            if (indent > 0) Blank(indent);
            Token("END_IF").Token(";").NewLine();
            return this;
        }

        public string BuildInnerXml()
        {
            return _xml.ToString();
        }

        public string BuildStructuredTextXml()
        {
            return "<StructuredText xmlns=\"" + PlcBlockXmlHelpers.StructuredTextNs + "\">" +
                   Environment.NewLine +
                   BuildInnerXml() +
                   "</StructuredText>";
        }

        public static JsonObject RunProbe(string fixtureDirectory, string reportDirectory)
        {
            Directory.CreateDirectory(reportDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var goldenPath = Path.Combine(fixtureDirectory, "FC_StartStop.xml");
            var generatedPath = Path.Combine(reportDirectory, "StructuredText_StartStop.generated_" + stamp + ".xml");
            var jsonPath = Path.Combine(reportDirectory, "structured_text_builder_probe_" + stamp + ".json");
            var mdPath = Path.Combine(reportDirectory, "structured_text_builder_probe_" + stamp + ".md");

            var generatedXml = new StructuredTextXmlBuilder()
                .IfHeader("EStop")
                .Assignment("Run", "FALSE", indent: 2)
                .ElseLine()
                .IfHeader("Stop", indent: 2)
                .Assignment("Run", "FALSE", indent: 4)
                .EndIf(indent: 2)
                .IfHeader("Start", indent: 2)
                .Assignment("Run", "TRUE", indent: 4)
                .EndIf(indent: 2)
                .Token("END_IF").Token(";")
                .BuildStructuredTextXml();

            File.WriteAllText(generatedPath, generatedXml, Encoding.UTF8);
            var golden = AnalyzeStructuredText(goldenPath);
            var generated = AnalyzeStructuredText(generatedPath);
            var semanticEqual = CompareSemantics(golden, generated);

            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["mode"] = "offline-structured-text-builder-probe",
                ["goldenPath"] = goldenPath,
                ["generatedPath"] = generatedPath,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "离线生成和解析 StructuredText XML，不连接 TIA Portal，不导入 PLC 块。",
                    ["write"] = "只写 reports 目录下的生成样本和探针报告，不修改 TMP_EXPORT 或交付包。"
                },
                ["golden"] = golden,
                ["generated"] = generated,
                ["semanticEqual"] = semanticEqual,
                ["ok"] = golden["ok"]?.GetValue<bool>() == true &&
                       generated["ok"]?.GetValue<bool>() == true &&
                       semanticEqual
            };

            File.WriteAllText(jsonPath, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildProbeMarkdown(root, jsonPath), Encoding.UTF8);
            root["jsonPath"] = jsonPath;
            root["markdownPath"] = mdPath;
            return root;
        }

        public static JsonObject AnalyzeStructuredText(string path)
        {
            var root = new JsonObject
            {
                ["path"] = path,
                ["exists"] = File.Exists(path)
            };
            if (!File.Exists(path))
            {
                root["ok"] = false;
                root["error"] = "文件不存在。";
                return root;
            }

            try
            {
                var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
                var structuredText = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "StructuredText");
                var tokens = doc.Descendants().Where(x => x.Name.LocalName == "Token")
                    .Select(x => x.Attribute("Text")?.Value ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray();
                var variables = doc.Descendants().Where(x => x.Name.LocalName == "Component")
                    .Select(x => x.Attribute("Name")?.Value ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray();
                var constants = doc.Descendants().Where(x => x.Name.LocalName == "ConstantValue")
                    .Select(x => x.Value)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray();

                root["ok"] = structuredText != null && tokens.Length > 0;
                root["tokens"] = new JsonArray(tokens.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                root["variables"] = new JsonArray(variables.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                root["constants"] = new JsonArray(constants.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                root["assignmentCount"] = tokens.Count(x => x == ":=");
                root["ifCount"] = tokens.Count(x => x == "IF");
                root["endIfCount"] = tokens.Count(x => x == "END_IF");
                return root;
            }
            catch (Exception ex)
            {
                root["ok"] = false;
                root["error"] = ex.Message;
                return root;
            }
        }

        private static bool CompareSemantics(JsonObject golden, JsonObject generated)
        {
            return NormalizeArray(golden, "tokens").SequenceEqual(NormalizeArray(generated, "tokens"), StringComparer.Ordinal) &&
                   NormalizeArray(golden, "variables").SequenceEqual(NormalizeArray(generated, "variables"), StringComparer.Ordinal) &&
                   NormalizeArray(golden, "constants").SequenceEqual(NormalizeArray(generated, "constants"), StringComparer.Ordinal) &&
                   golden["assignmentCount"]?.ToString() == generated["assignmentCount"]?.ToString() &&
                   golden["ifCount"]?.ToString() == generated["ifCount"]?.ToString() &&
                   golden["endIfCount"]?.ToString() == generated["endIfCount"]?.ToString();
        }

        private static string[] NormalizeArray(JsonObject root, string name)
        {
            return (root[name] as JsonArray ?? new JsonArray())
                .Select(x => x?.ToString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
        }

        private static string BuildProbeMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# StructuredText Builder Probe");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- 离线生成和解析 StructuredText XML，不连接 TIA Portal，不导入 PLC 块。");
            md.AppendLine("- 只写 reports 目录下的生成样本和探针报告，不修改 TMP_EXPORT 或交付包。");
            md.AppendLine();
            md.AppendLine("## Summary");
            md.AppendLine("- OK: " + root["ok"]);
            md.AppendLine("- Semantic equal to golden: " + root["semanticEqual"]);
            md.AppendLine("- Golden: " + root["goldenPath"]);
            md.AppendLine("- Generated: " + root["generatedPath"]);
            md.AppendLine();
            if (root["generated"] is JsonObject generated)
            {
                md.AppendLine("## Generated Semantics");
                md.AppendLine("- IF count: " + generated["ifCount"]);
                md.AppendLine("- END_IF count: " + generated["endIfCount"]);
                md.AppendLine("- Assignment count: " + generated["assignmentCount"]);
                md.AppendLine("- Variables: " + string.Join(", ", NormalizeArray(generated, "variables")));
                md.AppendLine("- Constants: " + string.Join(", ", NormalizeArray(generated, "constants")));
            }

            return md.ToString();
        }

        private int Next()
        {
            return _uid++;
        }

        private static string Escape(string value)
        {
            return SecurityElement.Escape(value) ?? "";
        }
    }
}
