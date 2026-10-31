using System;
using System.Collections.Generic;
using System.Xml;
using Siemens.Engineering.SW;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Block validation helpers, with logic extracted from MainWindow.AI.cs.
    /// </summary>
    public static class BlockValidationHelper
    {
        /// <summary>
        /// Checks whether the target block exists in the PLC project.
        /// </summary>
        public static bool ExistsTargetInProject(PlcSoftware plcSoftware, string targetName, string blockType)
        {
            if (string.IsNullOrEmpty(targetName) || plcSoftware == null) return false;
            if (blockType == "UDT")
                return plcSoftware.TypeGroup.Types.Find(targetName) != null;
            return plcSoftware.BlockGroup.Blocks.Find(targetName) != null;
        }

        /// <summary>
        /// Validates that the XML content matches the target block.
        /// </summary>
        public static bool ValidateXmlTargetConstraint(string xml, string targetName, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(targetName))
            {
                message = "Target name is empty.";
                return false;
            }

            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var objectNodes = doc.SelectNodes("//*[starts-with(name(), 'SW.Blocks.') or starts-with(name(), 'SW.Types.')]");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (objectNodes != null)
                {
                    foreach (XmlNode node in objectNodes)
                    {
                        var attr = node.Attributes?["Name"];
                        if (attr != null && !string.IsNullOrWhiteSpace(attr.Value))
                            names.Add(attr.Value.Trim());
                    }
                }

                if (names.Count == 0)
                {
                    message = "XML has no target object names.";
                    return false;
                }

                if (!names.Contains(targetName))
                {
                    message = $"XML target mismatch. Expected '{targetName}', got '{string.Join(", ", names)}'.";
                    return false;
                }

                if (names.Count > 1)
                {
                    message = $"XML contains multiple targets: {string.Join(", ", names)}. Only '{targetName}' is allowed.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                message = "XML parse validation failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Builds a line-level diff preview.
        /// </summary>
        public static string BuildLineDiffPreview(string oldText, string newText)
        {
            var oldLines = (oldText ?? "").Replace("\r\n", "\n").Split('\n');
            var newLines = (newText ?? "").Replace("\r\n", "\n").Split('\n');
            int max = Math.Max(oldLines.Length, newLines.Length);
            var sb = new System.Text.StringBuilder();
            int changed = 0;
            for (int i = 0; i < max; i++)
            {
                string o = i < oldLines.Length ? oldLines[i] : "";
                string n = i < newLines.Length ? newLines[i] : "";
                if (o == n) continue;
                changed++;
                sb.AppendLine($"L{i + 1}");
                sb.AppendLine($"- {o}");
                sb.AppendLine($"+ {n}");
                if (changed >= 300)
                {
                    sb.AppendLine("...<truncated>");
                    break;
                }
            }
            if (changed == 0) sb.AppendLine("No textual difference.");
            return sb.ToString();
        }
    }
}
