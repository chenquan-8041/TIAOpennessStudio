using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// PLC FB XML composer.
    /// Only generates the SCL FB XML offline; real import, compile, and instance-DB generation
    /// must be verified by TIA Portal.
    /// </summary>
    public static class PlcFbBlockXmlComposer
    {
        private static XNamespace InterfaceNs => PlcBlockXmlHelpers.InterfaceNs;
        private static XNamespace StructuredTextNs => PlcBlockXmlHelpers.StructuredTextNs;

        public static XDocument Compose(
            string blockName,
            int blockNumber,
            IEnumerable<PlcBlockMemberDefinition> inputMembers,
            IEnumerable<PlcBlockMemberDefinition> outputMembers,
            IEnumerable<PlcBlockMemberDefinition> inOutMembers,
            IEnumerable<PlcBlockMemberDefinition> staticMembers,
            IEnumerable<PlcBlockMemberDefinition> tempMembers,
            string structuredTextInnerXml,
            string blockCommentZhCn = "",
            string blockTitleZhCn = "",
            string networkCommentZhCn = "",
            string networkTitleZhCn = "",
            string culture = "zh-CN",
            XElement interfaceOverride = null)
        {
            if (string.IsNullOrWhiteSpace(blockName))
                throw new ArgumentException("FB block name is required.", nameof(blockName));
            if (blockNumber <= 0)
                throw new ArgumentException("FB block number must be greater than 0.", nameof(blockNumber));
            if (string.IsNullOrWhiteSpace(structuredTextInnerXml))
                throw new ArgumentException("StructuredText content is required.", nameof(structuredTextInnerXml));

            var inputs = inputMembers?.ToArray() ?? throw new ArgumentNullException(nameof(inputMembers));
            var outputs = outputMembers?.ToArray() ?? throw new ArgumentNullException(nameof(outputMembers));
            var inouts = inOutMembers?.ToArray() ?? throw new ArgumentNullException(nameof(inOutMembers));
            var statics = staticMembers?.ToArray() ?? throw new ArgumentNullException(nameof(staticMembers));
            var temps = tempMembers?.ToArray() ?? throw new ArgumentNullException(nameof(tempMembers));
            ValidateMembers(inputs.Concat(outputs).Concat(inouts).Concat(statics).Concat(temps).ToArray());

            var st = XElement.Parse("<StructuredText xmlns=\"" + StructuredTextNs + "\">" + structuredTextInnerXml + "</StructuredText>");

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Document",
                    new XElement("Engineering", new XAttribute("version", TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentEngineeringVersion)),
                    new XElement("DocumentInfo",
                        new XElement("Created", "2000-01-01T00:00:00.0000000Z"),
                        new XElement("ExportSetting", "None"),
                        new XElement("InstalledProducts")),
                    new XElement("SW.Blocks.FB",
                        new XAttribute("ID", "0"),
                        new XElement("AttributeList",
                            interfaceOverride ?? new XElement("Interface",
                                new XElement(InterfaceNs + "Sections",
                                    BuildSection("Input", inputs),
                                    BuildSection("Output", outputs),
                                    BuildSection("InOut", inouts),
                                    BuildSection("Static", statics),
                                    BuildSection("Temp", temps),
                                    BuildSection("Constant", Array.Empty<PlcBlockMemberDefinition>()))),
                            PlcBlockXmlHelpers.BlockAttribute("MemoryLayout", "Optimized"),
                            new XElement("Name", blockName),
                            PlcBlockXmlHelpers.NamespaceElement(),
                            new XElement("Number", blockNumber),
                            new XElement("ProgrammingLanguage", "SCL"),
                            PlcBlockXmlHelpers.BlockAttribute("SetENOAutomatically", "false")),
                        BuildObjectList(blockCommentZhCn, blockTitleZhCn, networkCommentZhCn, networkTitleZhCn, st, culture))));
        }

        private static XElement BuildObjectList(string blockComment, string blockTitle, string networkComment, string networkTitle, XElement st, string culture = "zh-CN")
        {
            // CompileUnit's ObjectList: network-level Comment + Title
            var compileUnitObjList = new XElement("ObjectList",
                PlcBlockXmlHelpers.BuildMultilingualText("4", "5", "Comment", networkComment, culture),
                PlcBlockXmlHelpers.BuildMultilingualText("6", "7", "Title", networkTitle, culture));
            var compileUnit = new XElement("SW.Blocks.CompileUnit",
                new XAttribute("ID", "3"),
                new XAttribute("CompositionName", "CompileUnits"),
                new XElement("AttributeList",
                    new XElement("NetworkSource", st),
                    new XElement("ProgrammingLanguage", "SCL")),
                compileUnitObjList);
            // Block-level ObjectList: Comment → CompileUnit → Title (matching TIA's real export schema order)
            return new XElement("ObjectList",
                PlcBlockXmlHelpers.BuildMultilingualText("1", "2", "Comment", blockComment, culture),
                compileUnit,
                PlcBlockXmlHelpers.BuildMultilingualText("8", "9", "Title", blockTitle, culture));
        }

        public static string ComposeXml(
            string blockName,
            int blockNumber,
            IEnumerable<PlcBlockMemberDefinition> inputMembers,
            IEnumerable<PlcBlockMemberDefinition> outputMembers,
            IEnumerable<PlcBlockMemberDefinition> inOutMembers,
            IEnumerable<PlcBlockMemberDefinition> staticMembers,
            IEnumerable<PlcBlockMemberDefinition> tempMembers,
            string structuredTextInnerXml,
            string blockCommentZhCn = "",
            string blockTitleZhCn = "",
            string networkCommentZhCn = "",
            string networkTitleZhCn = "",
            string culture = "zh-CN",
            XElement interfaceOverride = null)
        {
            using var writer = new Utf8StringWriter();
            Compose(blockName, blockNumber, inputMembers, outputMembers, inOutMembers, staticMembers, tempMembers, structuredTextInnerXml,
                blockCommentZhCn, blockTitleZhCn, networkCommentZhCn, networkTitleZhCn, culture, interfaceOverride)
                .Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        private static XElement BuildSection(string name, IReadOnlyCollection<PlcBlockMemberDefinition> members)
        {
            if (members.Count == 0)
                return new XElement(InterfaceNs + "Section", new XAttribute("Name", name));
            var section = new XElement(InterfaceNs + "Section", new XAttribute("Name", name));
            foreach (var m in members)
            {
                var memEl = new XElement(InterfaceNs + "Member",
                    new XAttribute("Name", m.Name),
                    new XAttribute("Datatype", m.Datatype));
                PlcBlockXmlHelpers.AppendMemberCommentIfAny(memEl, m.CommentZhCn);
                section.Add(memEl);
            }
            return section;
        }

        private static void ValidateMembers(IReadOnlyCollection<PlcBlockMemberDefinition> members)
        {
            var duplicates = members.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .ToArray();
            if (duplicates.Length > 0)
                throw new ArgumentException("Duplicate FB interface member names: " + string.Join(", ", duplicates));

            foreach (var member in members)
            {
                if (string.IsNullOrWhiteSpace(member.Name))
                    throw new ArgumentException("FB interface member name is required.");
                if (string.IsNullOrWhiteSpace(member.Datatype))
                    throw new ArgumentException("FB interface member data type is required: " + member.Name);
            }
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
