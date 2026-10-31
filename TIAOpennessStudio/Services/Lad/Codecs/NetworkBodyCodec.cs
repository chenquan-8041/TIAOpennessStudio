using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using TIAOpennessStudio.Models.Lad;

namespace TIAOpennessStudio.Services.Lad.Codecs
{
    /// <summary>
    /// Generic network body codec shared by LAD/FBD (FlgNet), SCL (StructuredText) and STL
    /// (StatementList).
    ///
    /// Design rules:
    ///   1. Unknown elements are never dropped silently: the whole subtree is kept in Body and
    ///      written back on emit.
    ///   2. Attributes are stored as raw strings, never normalized into enums.
    ///   3. The canonical side has no version branches; version differences live only in Facts
    ///      and in the target namespace used on emit.
    /// </summary>
    public sealed class NetworkBodyCodec : INetworkCodec
    {
        public NetworkBodyCodec(LadSchemaFacts facts, string language, string bodyElementName)
        {
            Facts = facts ?? throw new ArgumentNullException(nameof(facts));
            Language = string.IsNullOrEmpty(language) ? "LAD" : language.ToUpperInvariant();
            BodyElementName = string.IsNullOrEmpty(bodyElementName) ? "FlgNet" : bodyElementName;
        }

        public LadSchemaFacts Facts { get; }

        public string Language { get; }

        public string BodyElementName { get; }

        public bool CanParse(XElement body)
        {
            return body != null
                   && string.Equals(body.Name.LocalName, BodyElementName, StringComparison.OrdinalIgnoreCase);
        }

        public LadNetwork Parse(XElement body, LadParseContext context)
        {
            if (!CanParse(body))
                throw new ArgumentException("Element is not " + BodyElementName + " and cannot be parsed.", nameof(body));

            var ctx = context ?? new LadParseContext();
            var network = new LadNetwork
            {
                NetworkId = ctx.NetworkId ?? string.Empty,
                BlockId = ctx.BlockId ?? string.Empty,
                Order = ctx.Order,
                CompileUnitId = ctx.CompileUnitId ?? string.Empty,
                Title = ctx.Title ?? string.Empty,
                Comment = ctx.Comment ?? string.Empty,
                Language = string.IsNullOrEmpty(ctx.Language) ? Language : ctx.Language,
                SourceProfile = Facts.DisplayVersion,
                Body = FromXmlElement(body)
            };

            CollectProvenance(network);
            return network;
        }

        public XElement Emit(LadNetwork network, LadEmitContext context)
        {
            if (network == null || network.Body == null)
                throw new ArgumentException("Network body is empty and cannot be emitted.", nameof(network));

            var target = context?.Target ?? Facts;
            var namespaceUri = NamespaceFor(target, network.Language);
            if (string.IsNullOrEmpty(namespaceUri))
                throw new InvalidOperationException(
                    "Namespace for " + network.Language + " is not confirmed on " + target.DisplayVersion
                    + "; refusing to emit XML guesswork.");

            var ns = XNamespace.Get(namespaceUri);
            var materializeSystemDefined = ShouldMaterializeSystemDefined(Facts, target);
            var root = ToXmlElement(ns, network.Body, materializeSystemDefined, Facts.SystemDefinedDefault);

            if (context?.RenumberUids == true)
                RenumberUids(root);

            return root;
        }

        /// <summary>Language to network body root element name.</summary>
        public static string BodyElementOf(string language)
        {
            switch ((language ?? string.Empty).ToUpperInvariant())
            {
                case "SCL": return "StructuredText";
                case "STL": return "StatementList";
                default: return "FlgNet";
            }
        }

        /// <summary>Network body root element name to language.</summary>
        public static string LanguageOf(string bodyElementName)
        {
            switch ((bodyElementName ?? string.Empty).ToLowerInvariant())
            {
                case "structuredtext": return "SCL";
                case "statementlist": return "STL";
                case "fbdnet": return "FBD";
                default: return "LAD";
            }
        }

        /// <summary>Namespace for a version and language; returns null when unconfirmed.</summary>
        public static string NamespaceFor(LadSchemaFacts facts, string language)
        {
            switch ((language ?? string.Empty).ToUpperInvariant())
            {
                case "SCL": return facts.SclNamespace;
                case "STL": return facts.StlNamespace;
                default: return facts.FlgNetNamespace;
            }
        }

        /// <summary>
        /// XML to generic node. Namespace declarations are skipped, all other attributes are kept.
        /// Meaningful text nodes (mixed content) are stored as "#text" children; whitespace-only
        /// indentation is dropped so SCL comments or inline text survive without bloating the model.
        /// The block parser reuses this for Interface sections.
        /// </summary>
        public static LadNode FromXmlElement(XElement element)
        {
            if (element == null)
                return null;

            var node = new LadNode { Kind = element.Name.LocalName };

            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                    continue;
                node.Attributes[attribute.Name.LocalName] = attribute.Value ?? string.Empty;
            }

            foreach (var childNode in element.Nodes())
            {
                if (childNode is XElement childElement)
                {
                    node.Children.Add(FromXmlElement(childElement));
                    continue;
                }

                var text = childNode as XText;
                if (text == null)
                    continue;
                if (string.IsNullOrWhiteSpace(text.Value))
                    continue;

                node.Children.Add(new LadNode { Kind = "#text", Text = text.Value });
            }

            return node;
        }

        /// <summary>
        /// Emit a block Interface (UDT / DB). The Interface element itself stays in the empty
        /// namespace, as in real exports; its descendants use the Interface namespace.
        /// </summary>
        public static XElement EmitInterface(LadNode interfaceNode, LadSchemaFacts source, LadSchemaFacts target)
        {
            if (interfaceNode == null)
                return null;

            var resolvedTarget = target ?? source ?? LadSchemaRegistry.Baseline;
            if (string.IsNullOrEmpty(resolvedTarget.InterfaceNamespace))
                throw new InvalidOperationException(
                    "Interface namespace is not confirmed on " + resolvedTarget.DisplayVersion
                    + "; refusing to emit XML guesswork.");

            var ns = XNamespace.Get(resolvedTarget.InterfaceNamespace);
            var sourceFacts = source ?? resolvedTarget;
            var materializeSystemDefined = ShouldMaterializeSystemDefined(sourceFacts, resolvedTarget);

            return ToXmlElement(
                ns,
                interfaceNode,
                materializeSystemDefined,
                sourceFacts.SystemDefinedDefault,
                namespacedRoot: false);
        }

        /// <summary>
        /// The single decision point for SystemDefined materialization: SW.Common v2 defaults to
        /// true, v3 defaults to false, so a source tree emitted for a different default must spell
        /// the attribute out (otherwise the meaning flips silently).
        /// </summary>
        public static bool ShouldMaterializeSystemDefined(LadSchemaFacts source, LadSchemaFacts target)
        {
            var resolvedSource = source ?? target;
            var resolvedTarget = target ?? resolvedSource;
            if (resolvedSource == null || resolvedTarget == null) return false;
            return resolvedTarget.SystemDefinedDefault != resolvedSource.SystemDefinedDefault;
        }

        /// <summary>Generic node to XML, always using the target namespace.</summary>
        public static XElement ToXmlElement(
            XNamespace ns,
            LadNode node,
            bool materializeSystemDefined,
            bool sourceSystemDefinedDefault,
            bool namespacedRoot = true)
        {
            if (node == null)
                return null;

            var element = namespacedRoot ? new XElement(ns + node.Kind) : new XElement(node.Kind);

            foreach (var pair in node.Attributes)
                element.SetAttributeValue(pair.Key, pair.Value);

            // SystemDefined defaults flip between Common v2 (true) and v3 (false). When emitting for
            // another version, write the source semantics explicitly so the meaning cannot drift.
            if (materializeSystemDefined
                && (Eq(node.Kind, "Comment") || Eq(node.Kind, "Text"))
                && !node.Attributes.ContainsKey("SystemDefined"))
            {
                element.SetAttributeValue("SystemDefined", sourceSystemDefinedDefault ? "true" : "false");
            }

            foreach (var child in node.Children)
            {
                if (Eq(child.Kind, "#text"))
                {
                    element.Add(new XText(child.Text ?? string.Empty));
                    continue;
                }

                element.Add(ToXmlElement(ns, child, materializeSystemDefined, sourceSystemDefinedDefault));
            }

            if (node.Children.Count == 0 && node.Text != null)
                element.Value = node.Text;

            return element;
        }

        private static void CollectProvenance(LadNetwork network)
        {
            Walk(network.Body);

            void Walk(LadNode node)
            {
                if (node == null)
                    return;
                var uid = node.Attr("UId");
                if (!string.IsNullOrEmpty(uid))
                {
                    network.Provenance.Add(new LadProvenance
                    {
                        NetworkId = network.NetworkId,
                        ElementUid = uid,
                        ElementKind = node.Kind
                    });
                }
                foreach (var child in node.Children)
                    Walk(child);
            }
        }

        /// <summary>
        /// Renumber UIds to 1..N. Both definitions and references use the UId attribute, so a single
        /// mapping keeps references intact.
        /// </summary>
        public static void RenumberUids(XElement root)
        {
            if (root == null)
                return;

            var elements = root.DescendantsAndSelf().ToList();
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var element in elements)
            {
                var attribute = element.Attribute("UId");
                if (attribute == null)
                    continue;
                if (!int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    throw new InvalidOperationException("UId is not an integer and cannot be renumbered: " + attribute.Value);
                if (seen.Add(attribute.Value))
                    order.Add(attribute.Value);
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < order.Count; i++)
                map[order[i]] = (i + 1).ToString(CultureInfo.InvariantCulture);

            foreach (var element in elements)
            {
                var attribute = element.Attribute("UId");
                if (attribute != null)
                    attribute.Value = map[attribute.Value];
            }
        }

        private static bool Eq(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
