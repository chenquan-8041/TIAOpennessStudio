using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// Classic (WinCC Basic/Comfort/Advanced) HMI tag-table XML builder.
    /// Emits the V15.1 SIMATIC ML shape: Hmi.Tag.TagTable / Hmi.Tag.Tag with CompositionName="Tags",
    /// a single AttributeList, a LinkList for DataType/Connection/ControllerTag and multilingual texts.
    /// A tag may declare only an HMI type, or bind to a PLC symbol via Connection + ControllerTag.
    /// </summary>
    public static class ClassicHmiTagTableXmlBuilder
    {
        private const string DefaultCulture = "zh-CN";

        public static JsonObject BuildFromJson(string tableJson, string tiaVersion = "V21")
        {
            var root = JsonNode.Parse(tableJson) as JsonObject
                ?? throw new ArgumentException("Classic HMI tag table JSON root must be an object.", nameof(tableJson));
            var document = BuildDocument(root, tiaVersion);
            var xml = ToXml(document);
            var analysis = AnalyzeXml(xml);
            return new JsonObject
            {
                ["format"] = "tia-classic-hmi-tag-table-xml-offline-v1",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["offlineOnly"] = true,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "Offline XML generation only; TIA Portal is not connected.",
                    ["write"] = "No project, reference project, or delivery package content is modified.",
                    ["binding"] = "ControllerTag is a requested PLC symbolic binding; it is not verified until read back from TIA.",
                    ["apply"] = "Import into a temporary Classic/Basic HMI project first, then read back tags, connections and controller tags."
                },
                ["ok"] = analysis["ok"]?.GetValue<bool>() == true,
                ["analysis"] = analysis,
                ["xml"] = xml
            };
        }

        public static XDocument BuildDocument(JsonObject root, string tiaVersion = "V21")
        {
            var tableName = GetString(root, "Name", "name", GetString(root, "TableName", "tableName", "Classic_HMI_Tags"));
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("Classic HMI tag table name is required.");

            var tags = (root["Tags"] as JsonArray ?? root["tags"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .ToArray();
            ValidateTags(tags);

            var table = new XElement("Hmi.Tag.TagTable",
                new XAttribute("ID", "0"),
                new XElement("AttributeList",
                    new XElement("Name", tableName)),
                new XElement("ObjectList",
                    tags.Select((tag, index) => BuildTagElement(tag, index + 1))));

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Document",
                    new XElement("Engineering", new XAttribute("version", tiaVersion)),
                    new XElement("DocumentInfo",
                        new XElement("Created", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")),
                        new XElement("ExportSetting", "None"),
                        new XElement("InstalledProducts")),
                    table));
        }

        private static XElement BuildTagElement(JsonObject tag, int id)
        {
            var name = GetString(tag, "Name", "name", "");
            var dataType = GetString(tag, "DataType", "dataType", "Bool");
            var length = GetString(tag, "Length", "length", DefaultLength(dataType));
            var connection = GetString(tag, "Connection", "connection", "");
            var controllerTag = GetString(tag, "ControllerTag", "controllerTag",
                GetString(tag, "PlcTag", "plcTag", ""));
            var acquisitionCycle = GetString(tag, "AcquisitionCycle", "acquisitionCycle", "1 s");
            var symbolic = !string.IsNullOrWhiteSpace(connection) && !string.IsNullOrWhiteSpace(controllerTag);

            var links = new List<XElement>();
            if (symbolic)
            {
                links.Add(OpenLink("AcquisitionCycle", acquisitionCycle));
                links.Add(OpenLink("Connection", connection));
                links.Add(OpenLink("ControllerTag", controllerTag));
            }
            links.Add(OpenLink("DataType", dataType));
            links.Add(OpenLink("HmiDataType", dataType));

            var element = new XElement("Hmi.Tag.Tag",
                new XAttribute("ID", id.ToString()),
                new XAttribute("CompositionName", "Tags"),
                new XElement("AttributeList",
                    new XElement("Length", length),
                    new XElement("Name", name)),
                new XElement("LinkList", links),
                new XElement("ObjectList",
                    BuildMultilingualText(id * 10 + 1, "Comment", ""),
                    BuildMultilingualText(id * 10 + 3, "DisplayName", "")));

            return element;
        }

        private static XElement OpenLink(string elementName, string name)
        {
            return new XElement(elementName,
                new XAttribute("TargetID", "@OpenLink"),
                new XElement("Name", QuoteLinkName(name ?? "")));
        }

        private static XElement BuildMultilingualText(int id, string compositionName, string text)
        {
            XElement textElement;
            if (string.IsNullOrEmpty(text))
            {
                textElement = new XElement("Text");
            }
            else
            {
                textElement = new XElement("Text",
                    new XElement("body",
                        new XElement("p", text)));
            }

            return new XElement("MultilingualText",
                new XAttribute("ID", id.ToString()),
                new XAttribute("CompositionName", compositionName),
                new XElement("ObjectList",
                    new XElement("MultilingualTextItem",
                        new XAttribute("ID", (id + 1).ToString()),
                        new XAttribute("CompositionName", "Items"),
                        new XElement("AttributeList",
                            new XElement("Culture", DefaultCulture),
                            textElement))));
        }

        private static JsonObject AnalyzeXml(string xml)
        {
            var errors = new JsonArray();
            var warnings = new JsonArray();
            var result = new JsonObject
            {
                ["ok"] = false,
                ["tableName"] = "",
                ["tagCount"] = 0,
                ["symbolicBindingCount"] = 0,
                ["errors"] = errors,
                ["warnings"] = warnings
            };

            try
            {
                var document = XDocument.Parse(xml);
                var table = document.Descendants("Hmi.Tag.TagTable").FirstOrDefault();
                if (table == null)
                {
                    errors.Add("missing-tag-table: Hmi.Tag.TagTable element is required.");
                    return result;
                }

                result["tableName"] = table.Element("AttributeList")?.Element("Name")?.Value ?? "";
                var tags = document.Descendants("Hmi.Tag.Tag").ToArray();
                result["tagCount"] = tags.Length;
                if (tags.Length == 0) warnings.Add("empty-tag-table: no HMI tags were generated.");

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var symbolicCount = 0;
                foreach (var tag in tags)
                {
                    var attributeLists = tag.Elements("AttributeList").Count();
                    if (attributeLists != 1)
                        errors.Add($"attribute-list-count: Hmi.Tag.Tag must have exactly one AttributeList (found {attributeLists}).");

                    var name = tag.Element("AttributeList")?.Element("Name")?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(name)) errors.Add("unnamed-tag: Hmi.Tag.Tag has no Name.");
                    else if (!names.Add(name)) errors.Add("duplicate-tag-name: " + name);

                    var linkList = tag.Element("LinkList");
                    var controllerTag = linkList?.Element("ControllerTag")?.Element("Name")?.Value ?? "";
                    var connection = linkList?.Element("Connection")?.Element("Name")?.Value ?? "";
                    if (!string.IsNullOrWhiteSpace(controllerTag) || !string.IsNullOrWhiteSpace(connection))
                    {
                        symbolicCount++;
                        if (string.IsNullOrWhiteSpace(controllerTag)) errors.Add("symbolic-binding-missing-controller-tag: " + name);
                        if (string.IsNullOrWhiteSpace(connection)) errors.Add("symbolic-binding-missing-connection: " + name);
                    }

                    if (linkList?.Element("DataType") == null || linkList?.Element("HmiDataType") == null)
                        errors.Add("missing-data-type-link: " + name);
                }

                result["symbolicBindingCount"] = symbolicCount;
                result["ok"] = errors.Count == 0;
                return result;
            }
            catch (Exception ex)
            {
                errors.Add("xml-parse-error: " + ex.Message);
                return result;
            }
        }

        private static void ValidateTags(JsonObject[] tags)
        {
            if (tags.Length == 0)
                throw new ArgumentException("Classic HMI tag table requires at least one tag.");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                var name = GetString(tag, "Name", "name", "");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("Classic HMI tag name is required.");
                if (!names.Add(name))
                    throw new ArgumentException($"Duplicate Classic HMI tag name: {name}");

                var dataType = GetString(tag, "DataType", "dataType", "");
                if (string.IsNullOrWhiteSpace(dataType))
                    throw new ArgumentException($"Classic HMI tag '{name}' data type is required.");

                var connection = GetString(tag, "Connection", "connection", "");
                var controllerTag = GetString(tag, "ControllerTag", "controllerTag",
                    GetString(tag, "PlcTag", "plcTag", ""));
                if (!string.IsNullOrWhiteSpace(connection) ^ !string.IsNullOrWhiteSpace(controllerTag))
                    throw new ArgumentException($"Classic HMI symbolic tag '{name}' requires both Connection and ControllerTag.");
            }
        }

        private static string DefaultLength(string dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return "2";
            switch (dataType.Trim().ToUpperInvariant())
            {
                case "BOOL":
                case "BYTE":
                case "USINT":
                case "SINT":
                case "CHAR":
                    return "1";
                case "WORD":
                case "INT":
                case "UINT":
                    return "2";
                case "DWORD":
                case "DINT":
                case "UDINT":
                case "REAL":
                case "TIME":
                case "TIME_OF_DAY":
                case "DATE_AND_TIME":
                case "TOD":
                    return "4";
                case "LWORD":
                case "LINT":
                case "ULINT":
                case "LREAL":
                case "LTIME":
                case "DTL":
                    return "8";
                default:
                    return "2";
            }
        }

        private static string QuoteLinkName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (!name.Any(c => char.IsWhiteSpace(c) || c == '.' || c == '\\' || c == '"' || c == ':'))
                return name;
            return "\"" + name.Replace("\"", "\"\"") + "\"";
        }

        private static string GetString(JsonObject obj, string name, string alias, string fallback)
        {
            return obj[name]?.ToString() ?? obj[alias]?.ToString() ?? fallback;
        }

        private static string ToXml(XDocument document)
        {
            using var writer = new Utf8StringWriter();
            document.Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
