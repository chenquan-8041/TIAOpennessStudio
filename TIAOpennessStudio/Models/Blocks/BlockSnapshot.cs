using System;
using System.Collections.Generic;
using System.Linq;

namespace TIAOpennessStudio.Models.Blocks
{
    /// <summary>
    /// One block-level attribute entry from the export's AttributeList.
    /// Scalar values keep their text; complex nodes (Interface, ObjectList, ...) are
    /// represented by their presence and child count, because the raw XML is the source
    /// of truth for anything that must be written back unchanged.
    /// </summary>
    public sealed class BlockAttribute
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public bool IsComplex { get; set; }
        public int ChildCount { get; set; }

        public override string ToString()
        {
            return IsComplex
                ? Name + "=<" + Name + ":" + ChildCount + ">"
                : Name + "=" + (Value ?? "");
        }
    }

    /// <summary>
    /// Block-level snapshot: the single read-side object every projection (IR / source text /
    /// XML / interface) will be derived from. It carries the raw export XML as the write-back
    /// skeleton plus the facts that the current IR loses (block attributes, instance-DB identity,
    /// source version, revision).
    ///
    /// T1 scope: parsing only. IR/source projections are added in T2.
    /// </summary>
    public sealed class BlockSnapshot
    {
        public string ProjectKey { get; set; } = "";
        public string BlockId { get; set; } = "";

        public string Name { get; set; } = "";
        /// <summary>Normalized block type: FB | FC | OB | DB | UDT.</summary>
        public string BlockType { get; set; } = "";
        /// <summary>Raw block element local name, for example "SW.Blocks.InstanceDB".</summary>
        public string XmlBlockElement { get; set; } = "";
        /// <summary>Raw block kind, for example "InstanceDB" / "GlobalDB" / "PlcStruct".</summary>
        public string XmlBlockKind { get; set; } = "";

        public int Number { get; set; }
        public string Language { get; set; } = "";
        public string Title { get; set; } = "";
        public string Comment { get; set; } = "";

        /// <summary>Engineering version of the export itself ("V15.1", "V16", ...).</summary>
        public string SourceProfile { get; set; } = "";
        /// <summary>Content revision over (raw XML, contract version, source profile).</summary>
        public string Revision { get; set; } = "";

        public string RawXml { get; set; } = "";

        public bool IsInstanceDb { get; set; }
        public string InstanceOfName { get; set; } = "";
        public bool IsGraph { get; set; }

        public IReadOnlyList<BlockAttribute> Attributes { get; set; } = Array.Empty<BlockAttribute>();

        public string AttributeValue(string name)
        {
            if (string.IsNullOrEmpty(name) || Attributes == null) return "";
            var attribute = Attributes.FirstOrDefault(a =>
                string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            return attribute?.Value ?? "";
        }

        public bool HasAttribute(string name)
        {
            if (string.IsNullOrEmpty(name) || Attributes == null) return false;
            return Attributes.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public override string ToString()
        {
            return BlockType + " " + Name + " (#" + Number + ") [" + Language + "] rev=" + Revision;
        }
    }
}
