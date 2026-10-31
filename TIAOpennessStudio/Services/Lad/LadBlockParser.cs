using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using TIAOpennessStudio.Models.Blocks;
using TIAOpennessStudio.Models.Lad;
using TIAOpennessStudio.Services.Lad.Codecs;

namespace TIAOpennessStudio.Services.Lad
{
    /// <summary>
    /// Block XML to <see cref="LadBlock"/>. Handles structure navigation (block element, interface,
    /// CompileUnit, network body); the body itself is delegated to the codec chosen by
    /// <see cref="NetworkCodecRegistry"/>.
    ///
    /// Supported: SW.Blocks.FC / FB / OB / GlobalDB / InstanceDB and SW.Types.PlcStruct (UDT).
    /// UDT and DB have no CompileUnit, so Networks stays empty and Interface carries the data.
    /// </summary>
    public static class LadBlockParser
    {
        private static readonly string[] BodyElementNames = { "FlgNet", "FbdNet", "StructuredText", "StatementList" };

        public static LadBlock Parse(string xml, string blockId, LadSchemaFacts facts = null)
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new ArgumentException("XML is empty.", nameof(xml));

            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root == null)
                throw new InvalidOperationException("XML has no root element.");

            var blockElement = FindBlockElement(root);
            if (blockElement == null)
                throw new InvalidOperationException("No SW.Blocks.* / SW.Types.* block element found.");

            var schema = facts ?? LadSchemaRegistry.Baseline;
            var rawKind = LastLocalSegment(blockElement.Name.LocalName);
            var block = new LadBlock
            {
                BlockId = blockId ?? string.Empty,
                XmlBlockElement = blockElement.Name.LocalName,
                XmlBlockKind = rawKind,
                Kind = MapKind(rawKind)
            };

            ReadMeta(blockElement, block);
            block.Title = ReadObjectListText(blockElement, "Title");
            block.Comment = ReadObjectListText(blockElement, "Comment");

            var interfaceElement = blockElement.Descendants()
                .FirstOrDefault(e => Eq(e.Name.LocalName, "Interface"));
            if (interfaceElement != null)
                block.Interface = NetworkBodyCodec.FromXmlElement(interfaceElement);

            var compileUnits = blockElement.Descendants()
                .Where(e => e.Name.LocalName.EndsWith("CompileUnit", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var order = 0;
            foreach (var unit in compileUnits)
            {
                var bodyElement = FindNetworkBody(unit);
                if (bodyElement == null)
                    continue;

                order++;
                var compileUnitId = unit.Attribute("ID")?.Value ?? string.Empty;

                // FlgNet is shared by LAD and FBD: the block's ProgrammingLanguage is the only
                // discriminator. Every other body root (StructuredText / StatementList) names
                // its own language, so it must not inherit the block language.
                var isFlgNet = string.Equals(bodyElement.Name.LocalName, "FlgNet", StringComparison.OrdinalIgnoreCase);
                var networkLanguage = isFlgNet && string.Equals(block.Language, "FBD", StringComparison.OrdinalIgnoreCase)
                    ? "FBD"
                    : (isFlgNet ? "LAD" : null);

                var context = new LadParseContext
                {
                    NetworkId = (block.BlockId ?? string.Empty) + "|cu:" + compileUnitId + "|n:" + order,
                    BlockId = block.BlockId,
                    Order = order,
                    CompileUnitId = compileUnitId,
                    Language = networkLanguage,
                    Title = ReadObjectListText(unit, "Title"),
                    Comment = ReadObjectListText(unit, "Comment")
                };

                var codec = NetworkCodecRegistry.ForXml(bodyElement, schema);
                block.Networks.Add(codec.Parse(bodyElement, context));
            }

            // The block language came from ProgrammingLanguage; only fill it in when the export
            // did not carry one (never overwrite FBD with the shared FlgNet body name).
            if (string.IsNullOrWhiteSpace(block.Language) && block.Networks.Count > 0)
                block.Language = block.Networks[0].Language;

            return block;
        }

        private static XElement FindBlockElement(XElement root)
        {
            if (IsBlockElement(root))
                return root;

            return root.Descendants().FirstOrDefault(IsBlockElement);
        }

        private static bool IsBlockElement(XElement element)
        {
            var local = element?.Name.LocalName ?? string.Empty;
            return local.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase)
                   || local.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase);
        }

        private static XElement FindNetworkBody(XElement unit)
        {
            return unit.Descendants().FirstOrDefault(e =>
                BodyElementNames.Any(name => Eq(e.Name.LocalName, name)));
        }

        private static void ReadMeta(XElement blockElement, LadBlock block)
        {
            var attributeList = blockElement.Elements().FirstOrDefault(e => Eq(e.Name.LocalName, "AttributeList"));
            if (attributeList == null)
                return;

            block.Name = ReadChildText(attributeList, "Name");

            var number = ReadChildText(attributeList, "Number");
            if (int.TryParse(number, out var parsed))
                block.Number = parsed;

            // Language is a block fact, not a model default: an export without ProgrammingLanguage
            // is a UDT (UDT) and must not inherit the LadBlock "LAD" default.
            var language = ReadChildText(attributeList, "ProgrammingLanguage");
            block.Language = !string.IsNullOrWhiteSpace(language)
                ? language
                : (string.Equals(block.XmlBlockKind, "PlcStruct", StringComparison.OrdinalIgnoreCase) ? "UDT" : string.Empty);

            // Instance DB identity: the element kind and/or InstanceOfName prove it.
            block.InstanceOfName = ReadChildText(attributeList, "InstanceOfName");
            block.IsInstanceDb = string.Equals(block.XmlBlockKind, "InstanceDB", StringComparison.OrdinalIgnoreCase)
                                 || !string.IsNullOrWhiteSpace(block.InstanceOfName);

            // Full block-level AttributeList (scalars verbatim, complex nodes marked).
            block.Attributes = ReadAttributes(attributeList);
        }

        private static List<BlockAttribute> ReadAttributes(XElement attributeList)
        {
            var result = new List<BlockAttribute>();
            if (attributeList == null)
                return result;

            foreach (var element in attributeList.Elements())
            {
                var isComplex = element.HasElements;
                result.Add(new BlockAttribute
                {
                    Name = element.Name.LocalName,
                    Value = isComplex ? string.Empty : (element.Value ?? string.Empty).Trim(),
                    IsComplex = isComplex,
                    ChildCount = isComplex ? element.Elements().Count() : 0
                });
            }
            return result;
        }

        private static string ReadChildText(XElement parent, string childName)
        {
            var child = parent.Elements().FirstOrDefault(e => Eq(e.Name.LocalName, childName));
            return child?.Value?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// Read a block/network title or comment from the direct ObjectList of that element.
        /// Scoping to the direct ObjectList keeps a block's Title from picking up a network Title.
        /// </summary>
        private static string ReadObjectListText(XElement parent, string compositionName)
        {
            var objectList = parent?.Elements().FirstOrDefault(e => Eq(e.Name.LocalName, "ObjectList"));
            if (objectList == null)
                return string.Empty;

            var text = objectList.Descendants().FirstOrDefault(e =>
                Eq(e.Name.LocalName, "MultilingualText") &&
                Eq(e.Attribute("CompositionName")?.Value, compositionName));
            return text?.Descendants().FirstOrDefault(e => Eq(e.Name.LocalName, "Text"))?.Value?.Trim() ?? string.Empty;
        }

        private static string MapKind(string localName)
        {
            var last = LastLocalSegment(localName);
            if (string.Equals(last, "GlobalDB", StringComparison.OrdinalIgnoreCase))
                return "GlobalDB";
            if (string.Equals(last, "InstanceDB", StringComparison.OrdinalIgnoreCase))
                return "InstanceDB";
            if (string.Equals(last, "PlcStruct", StringComparison.OrdinalIgnoreCase))
                return "UDT";
            return last.ToUpperInvariant();
        }

        private static string LastLocalSegment(string localName)
        {
            var text = localName ?? string.Empty;
            var idx = text.LastIndexOf('.');
            return idx >= 0 ? text.Substring(idx + 1) : text;
        }

        private static bool Eq(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
