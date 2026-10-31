using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// LAD block XML composer: wraps one or more FlgNet networks into a complete LAD block.
    /// Unlike SCL blocks, the programming language is LAD and the CompileUnit's NetworkSource
    /// contains FlgNet (not StructuredText).
    ///
    /// Supports both FC and FB. The ladder network part (CompileUnit + FlgNet) is identical for
    /// FC and FB; only the outer interface differs:
    ///   - FC: block element SW.Blocks.FC, has a Return section, no Static section.
    ///   - FB: block element SW.Blocks.FB, has a Static section, no Return section.
    /// The FB outer layout mirrors <see cref="PlcFbBlockXmlComposer"/> (the proven SCL FB composer).
    /// </summary>
    public static class PlcLadFcBlockXmlComposer
    {
        private static XNamespace InterfaceNs => PlcBlockXmlHelpers.InterfaceNs;

        /// <summary>LAD block kind: FC or FB. The ladder networks are identical; only the outer interface differs.</summary>
        public enum LadBlockKind
        {
            Fc,
            Fb
        }

        /// <summary>Description of one network: a FlgNet element plus optional Chinese title/comment.</summary>
        public sealed class LadNetwork
        {
            public LadNetwork(XElement flgNet, string titleZhCn = "", string commentZhCn = "", string language = "LAD")
            {
                FlgNet = flgNet ?? throw new ArgumentNullException(nameof(flgNet));
                TitleZhCn = titleZhCn ?? "";
                CommentZhCn = commentZhCn ?? "";
                Language = string.IsNullOrWhiteSpace(language) ? "LAD" : language.Trim().ToUpperInvariant();
            }
            public XElement FlgNet { get; }
            public string TitleZhCn { get; }
            public string CommentZhCn { get; }

            /// <summary>LAD or FBD: FlgNet is the body element for both, only ProgrammingLanguage differs.</summary>
            public string Language { get; }
        }

        /// <summary>
        /// Compose a complete LAD block (FC or FB). The <paramref name="kind"/> selects the outer
        /// interface shape; the networks (FlgNet) are embedded identically in either case.
        /// InOut/Static/Temp members are ignored for FC (FC has no Static section and this composer
        /// keeps FC's InOut/Temp empty, matching the previous FC-only behavior).
        /// </summary>
        public static XDocument Compose(
            string blockName,
            int blockNumber,
            IEnumerable<PlcBlockMemberDefinition> inputMembers,
            IEnumerable<PlcBlockMemberDefinition> outputMembers,
            IEnumerable<LadNetwork> networks,
            LadBlockKind kind = LadBlockKind.Fc,
            IEnumerable<PlcBlockMemberDefinition> inOutMembers = null,
            IEnumerable<PlcBlockMemberDefinition> staticMembers = null,
            IEnumerable<PlcBlockMemberDefinition> tempMembers = null,
            string blockCommentZhCn = "",
            string blockTitleZhCn = "",
            string culture = "zh-CN",
            string blockLanguage = null,
            XElement interfaceOverride = null)
        {
            var kindLabel = kind == LadBlockKind.Fb ? "FB" : "FC";
            if (string.IsNullOrWhiteSpace(blockName))
                throw new ArgumentException($"LAD {kindLabel} block name is required.", nameof(blockName));
            if (blockNumber <= 0)
                throw new ArgumentException($"LAD {kindLabel} block number must be greater than 0.", nameof(blockNumber));

            var inputs = inputMembers?.ToArray() ?? Array.Empty<PlcBlockMemberDefinition>();
            var outputs = outputMembers?.ToArray() ?? Array.Empty<PlcBlockMemberDefinition>();
            var inouts = inOutMembers?.ToArray() ?? Array.Empty<PlcBlockMemberDefinition>();
            var statics = staticMembers?.ToArray() ?? Array.Empty<PlcBlockMemberDefinition>();
            var temps = tempMembers?.ToArray() ?? Array.Empty<PlcBlockMemberDefinition>();
            var nets = networks?.ToArray() ?? throw new ArgumentNullException(nameof(networks));
            if (nets.Length == 0)
                throw new ArgumentException($"LAD {kindLabel} requires at least one network.", nameof(networks));

            // Block language: explicit when the caller says so, otherwise the first network's
            // language (FBD networks in an FBD block, LAD otherwise).
            var effectiveLanguage = string.IsNullOrWhiteSpace(blockLanguage)
                ? (string.Equals(nets[0].Language, "FBD", StringComparison.OrdinalIgnoreCase) ? "FBD" : "LAD")
                : blockLanguage.Trim().ToUpperInvariant();

            // ID allocation: 1=Comment, 2=Title-Item, 3..=CompileUnits (each +3 IDs for the Comment/Title
            // children), then the block-level Title. Simplified to a monotonically increasing string
            // ID (hex, as TIA expects)
            int idCounter = 1;
            string NextId() => idCounter++.ToString("X");

            var blockObjList = new XElement("ObjectList");
            blockObjList.Add(PlcBlockXmlHelpers.BuildMultilingualText(NextId(), NextId(), "Comment", blockCommentZhCn, culture));

            for (var i = 0; i < nets.Length; i++)
            {
                var net = nets[i];
                var compileUnitId = NextId();
                var netCommentId = NextId();
                var netCommentItemId = NextId();
                var netTitleId = NextId();
                var netTitleItemId = NextId();

                var compileUnitObjList = new XElement("ObjectList",
                    PlcBlockXmlHelpers.BuildMultilingualText(netCommentId, netCommentItemId, "Comment", net.CommentZhCn, culture),
                    PlcBlockXmlHelpers.BuildMultilingualText(netTitleId, netTitleItemId, "Title", net.TitleZhCn, culture));

                var compileUnit = new XElement("SW.Blocks.CompileUnit",
                    new XAttribute("ID", compileUnitId),
                    new XAttribute("CompositionName", "CompileUnits"),
                    new XElement("AttributeList",
                        new XElement("NetworkSource", net.FlgNet),
                        new XElement("ProgrammingLanguage", string.IsNullOrWhiteSpace(net.Language) ? "LAD" : net.Language)),
                    compileUnitObjList);

                blockObjList.Add(compileUnit);
            }

            blockObjList.Add(PlcBlockXmlHelpers.BuildMultilingualText(NextId(), NextId(), "Title", blockTitleZhCn, culture));

            // Outer interface differs by kind. The ladder networks (blockObjList) are identical.
            XElement blockElement = kind == LadBlockKind.Fb
                ? BuildFbBlockElement(blockName, blockNumber, inputs, outputs, inouts, statics, temps, blockObjList, effectiveLanguage)
                : BuildFcBlockElement(blockName, blockNumber, inputs, outputs, blockObjList, effectiveLanguage);

            // Canonical round trip: the interface tree from export_block_ir replaces the section
            // skeleton verbatim (comments, start values and nested members included).
            if (interfaceOverride != null)
            {
                var attrList = blockElement.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                var oldInterface = attrList?.Elements().FirstOrDefault(e => e.Name.LocalName == "Interface");
                if (oldInterface != null) oldInterface.ReplaceWith(interfaceOverride);
                else attrList?.AddFirst(interfaceOverride);
            }

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Document",
                    new XElement("Engineering", new XAttribute("version", TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentEngineeringVersion)),
                    new XElement("DocumentInfo",
                        new XElement("Created", "2000-01-01T00:00:00.0000000Z"),
                        new XElement("ExportSetting", "None"),
                        new XElement("InstalledProducts")),
                    blockElement));
        }

        /// <summary>FC outer: SW.Blocks.FC with a Return section, no Static section (unchanged legacy shape).</summary>
        private static XElement BuildFcBlockElement(
            string blockName, int blockNumber,
            PlcBlockMemberDefinition[] inputs, PlcBlockMemberDefinition[] outputs,
            XElement blockObjList, string language)
        {
            return new XElement("SW.Blocks.FC",
                new XAttribute("ID", "0"),
                new XElement("AttributeList",
                    new XElement("Interface",
                        new XElement(InterfaceNs + "Sections",
                            BuildSection("Input", inputs),
                            BuildSection("Output", outputs),
                            BuildSection("InOut", Array.Empty<PlcBlockMemberDefinition>()),
                            BuildSection("Temp", Array.Empty<PlcBlockMemberDefinition>()),
                            BuildSection("Constant", Array.Empty<PlcBlockMemberDefinition>()),
                            BuildSection("Return", new[] { new PlcBlockMemberDefinition("Ret_Val", "Void") }))),
                    PlcBlockXmlHelpers.BlockAttribute("MemoryLayout", "Optimized"),
                    new XElement("Name", blockName),
                    PlcBlockXmlHelpers.NamespaceElement(),
                    new XElement("Number", blockNumber),
                    new XElement("ProgrammingLanguage", language),
                    PlcBlockXmlHelpers.BlockAttribute("SetENOAutomatically", "false")),
                blockObjList);
        }

        /// <summary>
        /// FB outer: SW.Blocks.FB with Input/Output/InOut/Static/Temp/Constant sections, no Return.
        /// Section order mirrors <see cref="PlcFbBlockXmlComposer"/> (the proven SCL FB layout).
        /// </summary>
        private static XElement BuildFbBlockElement(
            string blockName, int blockNumber,
            PlcBlockMemberDefinition[] inputs, PlcBlockMemberDefinition[] outputs,
            PlcBlockMemberDefinition[] inouts, PlcBlockMemberDefinition[] statics, PlcBlockMemberDefinition[] temps,
            XElement blockObjList, string language)
        {
            return new XElement("SW.Blocks.FB",
                new XAttribute("ID", "0"),
                new XElement("AttributeList",
                    new XElement("Interface",
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
                    new XElement("ProgrammingLanguage", language),
                    PlcBlockXmlHelpers.BlockAttribute("SetENOAutomatically", "false")),
                blockObjList);
        }

        /// <summary>Compose a LAD block to an XML string. See <see cref="Compose"/> for parameter semantics.</summary>
        public static string ComposeXml(
            string blockName,
            int blockNumber,
            IEnumerable<PlcBlockMemberDefinition> inputMembers,
            IEnumerable<PlcBlockMemberDefinition> outputMembers,
            IEnumerable<LadNetwork> networks,
            LadBlockKind kind = LadBlockKind.Fc,
            IEnumerable<PlcBlockMemberDefinition> inOutMembers = null,
            IEnumerable<PlcBlockMemberDefinition> staticMembers = null,
            IEnumerable<PlcBlockMemberDefinition> tempMembers = null,
            string blockCommentZhCn = "",
            string blockTitleZhCn = "",
            string culture = "zh-CN",
            string blockLanguage = null,
            XElement interfaceOverride = null)
        {
            using var writer = new Utf8StringWriter();
            Compose(blockName, blockNumber, inputMembers, outputMembers, networks, kind,
                inOutMembers, staticMembers, tempMembers, blockCommentZhCn, blockTitleZhCn, culture, blockLanguage, interfaceOverride)
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

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
