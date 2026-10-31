using System;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio
{
    public static class XmlCodeParser
    {
        public static string Parse(string xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent)) return "";

            try
            {
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;
                if (root == null) return xmlContent;

                var blockNode = root.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks"));
                var typeNode = root.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Types"));

                if (blockNode != null)
                {
                    return ParseBlock(blockNode);
                }
                else if (typeNode != null)
                {
                    return ParseType(typeNode);
                }

                return xmlContent;
            }
            catch (Exception ex)
            {
                return $"// Error parsing XML: {ex.Message}\r\n\r\n{xmlContent}";
            }
        }

        private static string ParseBlock(XElement blockNode)
        {
            StringBuilder sb = new StringBuilder();

            string blockType = blockNode.Name.LocalName.Replace("SW.Blocks.", ""); 
            string blockName = GetAttributeValue(blockNode, "Name");
            string language = GetAttributeValue(blockNode, "ProgrammingLanguage");

            if (blockType == "FB") sb.AppendLine($"FUNCTION_BLOCK \"{blockName}\"");
            else if (blockType == "FC") sb.AppendLine($"FUNCTION \"{blockName}\" : Void");
            else if (blockType == "OB") sb.AppendLine($"ORGANIZATION_BLOCK \"{blockName}\"");
            else sb.AppendLine($"{blockType} \"{blockName}\"");

            sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
            sb.AppendLine($"VERSION : 0.1");
            sb.AppendLine("");

            var interfaceNode = blockNode.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interface");
            if (interfaceNode != null)
            {
                ParseInterface(interfaceNode, sb);
            }

            sb.AppendLine("BEGIN");

            // Walk each CompileUnit and render per-network by its own language. A single block
            // (e.g. OB1) can mix STL, LAD and SCL networks, so we must not assume the block-level
            // ProgrammingLanguage applies to every network.
            var compileUnits = blockNode.Descendants()
                .Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit" || e.Name.LocalName == "CompileUnit")
                .ToList();

            if (compileUnits.Count > 0)
            {
                int networkNo = 0;
                foreach (var cu in compileUnits)
                {
                    networkNo++;
                    RenderNetwork(cu, networkNo, sb);
                }
            }
            else if (language == "SCL")
            {
                // Fallback (no CompileUnit wrapper): SCL logic in StructuredText nodes.
                var structuredTexts = blockNode.Descendants().Where(e => e.Name.LocalName == "StructuredText");
                foreach (var text in structuredTexts)
                    sb.AppendLine(TiaXmlParseHelper.RebuildStructuredText(text).Trim());
            }

            if (blockType == "FB") sb.AppendLine("END_FUNCTION_BLOCK");
            else if (blockType == "FC") sb.AppendLine("END_FUNCTION");
            else if (blockType == "OB") sb.AppendLine("END_ORGANIZATION_BLOCK");

            return sb.ToString();
        }

        /// <summary>
        /// Render a single CompileUnit (network) as readable text, choosing the reconstruction
        /// strategy from the network's own ProgrammingLanguage:
        ///   SCL → StructuredText token tree, STL → StatementList, LAD/FBD/GRAPH → raw FlgNet XML.
        /// </summary>
        private static void RenderNetwork(XElement compileUnit, int networkNo, StringBuilder sb)
        {
            var attrList = compileUnit.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            var netLang = attrList?.Elements().FirstOrDefault(e => e.Name.LocalName == "ProgrammingLanguage")?.Value ?? "";
            var title = GetNetworkTitle(compileUnit);

            sb.AppendLine();
            sb.AppendLine($"// Network {networkNo}"
                + (string.IsNullOrWhiteSpace(title) ? "" : $": {title}")
                + (string.IsNullOrWhiteSpace(netLang) ? "" : $"  [{netLang}]"));

            var structuredText = compileUnit.Descendants().FirstOrDefault(e => e.Name.LocalName == "StructuredText");
            if (structuredText != null)
            {
                sb.AppendLine(TiaXmlParseHelper.RebuildStructuredText(structuredText).Trim());
                return;
            }

            var statementList = compileUnit.Descendants().FirstOrDefault(e => e.Name.LocalName == "StatementList");
            if (statementList != null)
            {
                sb.AppendLine(TiaXmlParseHelper.RebuildStatementList(statementList).TrimEnd());
                return;
            }

            var flgNet = compileUnit.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
            if (flgNet != null)
            {
                // Graphical logic: text view keeps the raw FlgNet XML (rendered graphically elsewhere).
                sb.AppendLine("// Graphical network (LAD/FBD) - see graphical view. XML:");
                sb.AppendLine(flgNet.ToString());
                return;
            }

            sb.AppendLine("// (empty network)");
        }

        private static string GetNetworkTitle(XElement compileUnit)
        {
            var title = compileUnit.Descendants().FirstOrDefault(e => e.Name.LocalName == "Title");
            if (title == null) return "";
            var text = title.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text" && !string.IsNullOrWhiteSpace(e.Value));
            return (text?.Value ?? title.Value ?? "").Trim();
        }

        private static string ParseType(XElement typeNode)
        {
            StringBuilder sb = new StringBuilder();
            string typeName = GetAttributeValue(typeNode, "Name");

            sb.AppendLine($"TYPE \"{typeName}\"");
            sb.AppendLine("   STRUCT");

            var interfaceNode = typeNode.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interface");
            if (interfaceNode != null)
            {
                var sections = interfaceNode.Descendants().Where(e => e.Name.LocalName == "Section");
                foreach(var section in sections)
                {
                     ParseMembers(section, sb, "      ");
                }
            }

            sb.AppendLine("   END_STRUCT;");
            sb.AppendLine("END_TYPE");
            return sb.ToString();
        }

        private static void ParseInterface(XElement interfaceNode, StringBuilder sb)
        {
            var sections = interfaceNode.Descendants().Where(e => e.Name.LocalName == "Section");
            foreach (var section in sections)
            {
                string sectionName = section.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(sectionName) || sectionName == "None") continue;

                string sclKeyword = sectionName.ToUpper();
                if (sectionName == "Input") sclKeyword = "VAR_INPUT";
                else if (sectionName == "Output") sclKeyword = "VAR_OUTPUT";
                else if (sectionName == "InOut") sclKeyword = "VAR_IN_OUT";
                else if (sectionName == "Static") sclKeyword = "VAR";
                else if (sectionName == "Temp") sclKeyword = "VAR_TEMP";
                else if (sectionName == "Constant") sclKeyword = "VAR_CONSTANT";

                sb.AppendLine($"   {sclKeyword}");
                ParseMembers(section, sb, "      ");
                sb.AppendLine($"   END_VAR");
            }
        }

        private static void ParseMembers(XElement container, StringBuilder sb, string indent)
        {
            var members = container.Elements().Where(e => e.Name.LocalName == "Member");
            foreach (var member in members)
            {
                string name = member.Attribute("Name")?.Value;
                string datatype = member.Attribute("Datatype")?.Value;
                sb.AppendLine($"{indent}{name} : {datatype};");
            }
        }

        private static string GetAttributeValue(XElement element, string attributeName)
        {
            var attrList = element.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            if (attrList != null)
            {
                var attr = attrList.Elements().FirstOrDefault(e => e.Name.LocalName == attributeName);
                if (attr != null) return attr.Value;
            }
            return element.Attribute(attributeName)?.Value ?? "";
        }
    }
}
