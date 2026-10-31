using System.Collections.Generic;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// Shared PLC XML helpers: version-aware schema resolution (namespace suffix versions +
    /// structural flags) and member comments / block-level MultilingualText.
    /// </summary>
    internal static class PlcBlockXmlHelpers
    {
        // Version behavior is owned by LadSchemaRegistry (single source of truth). These helpers
        // only project the facts into XNamespace values so every builder emits one consistent
        // namespace set for the detected TIA version.
        private static TIAOpennessStudio.Models.Lad.LadSchemaFacts Facts =>
            TIAOpennessStudio.Models.Lad.LadSchemaRegistry.Resolve(
                TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion);

        public static XNamespace InterfaceNs => XNamespace.Get(Facts.InterfaceNamespace);

        public static XNamespace FlgNetNs => XNamespace.Get(Facts.FlgNetNamespace);

        public static XNamespace StructuredTextNs => XNamespace.Get(Facts.SclNamespace);

        /// <summary>
        /// GRAPH network-source namespace. Unlike LAD, the GRAPH export uses its own Graph/vN namespace,
        /// and every nested FlgNet/Parts/Wires element inherits it (V15.1 export: Graph/v4).
        /// </summary>
        public static XNamespace GraphNs => XNamespace.Get(Facts.GraphNamespace);

        /// <summary>True when generated blocks must emit an empty Namespace element (V18+ Software Unit namespaces).</summary>
        public static bool HasNamespaceElement => Facts.EmitNamespaceElement;

        /// <summary>True when UDT members may carry the ExternalWritable BooleanAttribute (V21+).</summary>
        public static bool UdtMemberExternalWritable => Facts.UdtMembersSupportExternalWritable;

        /// <summary>True when DB members may carry the ExternalWritable BooleanAttribute.</summary>
        public static bool DbMemberExternalWritable => Facts.DbMembersSupportExternalWritable;

        /// <summary>
        /// Version-gated block-level AttributeList element. Returns null when this version does not
        /// accept the attribute, so XContainer skips it instead of each builder keeping its own check.
        /// </summary>
        public static XElement BlockAttribute(string name, string value)
            => Facts.SupportsBlockAttribute(name) ? new XElement(name, value) : null;

        /// <summary>True when the detected version accepts this block-level attribute on XML import.</summary>
        public static bool SupportsBlockAttribute(string name) => Facts.SupportsBlockAttribute(name);

        /// <summary>Detected version label, for refusal messages.</summary>
        public static string DisplayVersion => Facts.DisplayVersion;

        /// <summary>
        /// Strip ExternalWritable from a canonical interface tree when the target version does not
        /// accept it. A requested "true" is reported through <paramref name="hadTrue"/> so callers
        /// can fail closed instead of silently changing member semantics.
        /// </summary>
        public static TIAOpennessStudio.Models.Lad.LadNode ApplyExternalWritablePolicy(
            TIAOpennessStudio.Models.Lad.LadNode node, bool supported, out bool hadTrue)
        {
            hadTrue = false;
            if (node == null) return null;

            // Canonical interface trees model it as <BooleanAttribute Name="ExternalWritable">true</BooleanAttribute>.
            var isExternalNode = string.Equals(node.Kind, "BooleanAttribute", System.StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(node.Attr("Name"), "ExternalWritable", System.StringComparison.OrdinalIgnoreCase);
            if (isExternalNode)
            {
                // FromXmlElement stores mixed content as a "#text" child, not node.Text.
                var value = node.Text;
                if (string.IsNullOrWhiteSpace(value))
                {
                    foreach (var child in node.Children)
                    {
                        if (string.Equals(child.Kind, "#text", System.StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(child.Text))
                        {
                            value = child.Text;
                            break;
                        }
                    }
                }
                value = (value ?? node.Attr("Value") ?? "").Trim();
                if (!supported)
                {
                    if (string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase))
                        hadTrue = true;
                    return null; // older versions reject the whole AttributeList element
                }
                return CloneNode(node);
            }

            var copy = new TIAOpennessStudio.Models.Lad.LadNode { Kind = node.Kind, Text = node.Text };
            foreach (var pair in node.Attributes)
            {
                if (string.Equals(pair.Key, "ExternalWritable", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (supported)
                    {
                        copy.Attributes[pair.Key] = pair.Value;
                    }
                    else if (string.Equals(pair.Value, "true", System.StringComparison.OrdinalIgnoreCase))
                    {
                        hadTrue = true;
                    }
                    continue;
                }
                copy.Attributes[pair.Key] = pair.Value;
            }

            foreach (var child in node.Children)
            {
                var childCopy = ApplyExternalWritablePolicy(child, supported, out var childHadTrue);
                if (childHadTrue) hadTrue = true;
                if (childCopy != null) copy.Children.Add(childCopy);
            }
            return copy;
        }

        private static TIAOpennessStudio.Models.Lad.LadNode CloneNode(TIAOpennessStudio.Models.Lad.LadNode node)
        {
            var copy = new TIAOpennessStudio.Models.Lad.LadNode { Kind = node.Kind, Text = node.Text };
            foreach (var pair in node.Attributes) copy.Attributes[pair.Key] = pair.Value;
            foreach (var child in node.Children) copy.Children.Add(CloneNode(child));
            return copy;
        }

        /// <summary>Returns the Namespace element for V18+, or null (skipped by XContainer) for older versions.</summary>
        public static object NamespaceElement()
            => HasNamespaceElement ? new XElement("Namespace") : null;

        // Append a zh-CN comment to a Member element (when non-empty)
        public static void AppendMemberCommentIfAny(XElement member, string commentZhCn)
        {
            if (string.IsNullOrWhiteSpace(commentZhCn)) return;
            member.Add(new XElement(InterfaceNs + "Comment",
                new XElement(InterfaceNs + "MultiLanguageText",
                    new XAttribute("Lang", "zh-CN"),
                    commentZhCn)));
        }

        // Build a block/network-level MultilingualText node inside ObjectList (CompositionName=Comment / Title)
        public static XElement BuildMultilingualText(string id, string itemId, string compositionName, string textZhCn, string culture = "zh-CN")
        {
            return new XElement("MultilingualText",
                new XAttribute("ID", id),
                new XAttribute("CompositionName", compositionName),
                new XElement("ObjectList",
                    new XElement("MultilingualTextItem",
                        new XAttribute("ID", itemId),
                        new XAttribute("CompositionName", "Items"),
                        new XElement("AttributeList",
                            new XElement("Culture", culture),
                            new XElement("Text", textZhCn ?? "")))));
        }
    }
}
