using System;
using System.Collections.Generic;
using System.Linq;

namespace TIAOpennessStudio.Models.Lad
{
    /// <summary>
    /// Generic LAD node shaped after the TIA SIMATIC ML schema. <see cref="Kind"/> keeps the
    /// original schema element name (Part / Call / Wire / NameCon / IdentCon / OpenCon / Access /
    /// Symbol / Constant / Component ...).
    ///
    /// The model deliberately carries no namespace and no version: version differences belong to
    /// the codec, and elements added by future versions land in <see cref="Children"/> automatically.
    /// Attributes are kept as raw strings, never normalized into enums.
    /// </summary>
    public sealed class LadNode
    {
        /// <summary>Original schema element name.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>
        /// Text content for text nodes (Kind "#text"); null for element nodes.
        /// </summary>
        public string Text { get; set; }

        /// <summary>Raw attributes. Keys are ordinally sorted so serialization and diffs stay stable.</summary>
        public SortedDictionary<string, string> Attributes { get; set; } =
            new SortedDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Child nodes in XML order (the schema uses xs:sequence, so order must be preserved).</summary>
        public List<LadNode> Children { get; set; } = new List<LadNode>();

        public string Attr(string name)
        {
            if (string.IsNullOrEmpty(name) || Attributes == null)
                return null;
            return Attributes.TryGetValue(name, out var value) ? value : null;
        }

        public bool Has(string kind)
        {
            return Child(kind) != null;
        }

        public LadNode Child(string kind)
        {
            if (Children == null || string.IsNullOrEmpty(kind))
                return null;
            for (var i = 0; i < Children.Count; i++)
            {
                if (string.Equals(Children[i].Kind, kind, StringComparison.OrdinalIgnoreCase))
                    return Children[i];
            }
            return null;
        }

        public IEnumerable<LadNode> ChildrenOf(string kind)
        {
            if (Children == null || string.IsNullOrEmpty(kind))
                return Enumerable.Empty<LadNode>();
            return Children.Where(c => string.Equals(c.Kind, kind, StringComparison.OrdinalIgnoreCase));
        }

        public LadNode Add(LadNode child)
        {
            if (child != null)
                Children.Add(child);
            return this;
        }

        public LadNode SetAttr(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
                return this;
            Attributes[name] = value ?? string.Empty;
            return this;
        }
    }
}
