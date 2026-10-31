using System;
using System.Collections.Generic;
using System.Linq;
using TIAOpennessStudio.Models.Blocks;

namespace TIAOpennessStudio.Models.Lad
{
    /// <summary>
    /// One network shared by LAD, FBD, SCL and STL — the single representation used by the read,
    /// write and RAG paths.
    ///
    /// The network body is stored as a generic node tree: FlgNet for LAD/FBD, StructuredText for
    /// SCL and StatementList for STL. Parts/Wires/Labels are views over that tree, not separate
    /// storage, so the same data never exists in two places.
    /// </summary>
    public sealed class LadNetwork
    {
        private static readonly IReadOnlyList<LadNode> NoNodes = new LadNode[0];

        /// <summary>Stable identity, independent of display name and order.</summary>
        public string NetworkId { get; set; } = string.Empty;

        public string BlockId { get; set; } = string.Empty;

        /// <summary>1-based order inside the block.</summary>
        public int Order { get; set; }

        public string CompileUnitId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Comment { get; set; } = string.Empty;

        /// <summary>LAD / FBD / SCL / STL.</summary>
        public string Language { get; set; } = "LAD";

        /// <summary>Source profile for traceability only (for example "V15.1"); never used for semantics.</summary>
        public string SourceProfile { get; set; } = string.Empty;

        /// <summary>
        /// Root node of the network body: FlgNet for LAD/FBD, StructuredText for SCL,
        /// StatementList for STL. It preserves the full schema shape, including elements that
        /// are not modelled with dedicated fields yet.
        /// </summary>
        public LadNode Body { get; set; }

        /// <summary>Element-level provenance: identifiers only, no embedded XML fragments.</summary>
        public List<LadProvenance> Provenance { get; set; } = new List<LadProvenance>();

        /// <summary>Parts members: Access | Part | Call (view over Body/Parts).</summary>
        public IReadOnlyList<LadNode> Parts
        {
            get { return ContainerOf("Parts"); }
        }

        /// <summary>Wires members: Wire (view over Body/Wires).</summary>
        public IReadOnlyList<LadNode> Wires
        {
            get { return ContainerOf("Wires"); }
        }

        /// <summary>Labels members: LabelDeclaration (view over Body/Labels).</summary>
        public IReadOnlyList<LadNode> Labels
        {
            get { return ContainerOf("Labels"); }
        }

        /// <summary>Variable accesses (Access nodes inside Parts).</summary>
        public IEnumerable<LadNode> Accesses
        {
            get { return Parts.Where(p => Is(p, "Access")); }
        }

        /// <summary>Instruction / contact / coil / call view (Parts minus Access).</summary>
        public IEnumerable<LadNode> LogicParts
        {
            get { return Parts.Where(p => !Is(p, "Access")); }
        }

        public int PartCount
        {
            get { return Parts.Count(p => !Is(p, "Access")); }
        }

        public int AccessCount
        {
            get { return Parts.Count(p => Is(p, "Access")); }
        }

        public int WireCount
        {
            get { return Wires.Count; }
        }

        /// <summary>True when the body is a graphical network (FlgNet).</summary>
        public bool IsGraphical
        {
            get { return Body != null && string.Equals(Body.Kind, "FlgNet", StringComparison.OrdinalIgnoreCase); }
        }

        public static bool Is(LadNode node, string kind)
        {
            return node != null && string.Equals(node.Kind, kind, StringComparison.OrdinalIgnoreCase);
        }

        private IReadOnlyList<LadNode> ContainerOf(string kind)
        {
            var container = Body?.Child(kind);
            return container?.Children as IReadOnlyList<LadNode> ?? NoNodes;
        }
    }

    /// <summary>Element-level provenance identifier. Raw XML is fetched by uid on demand.</summary>
    public sealed class LadProvenance
    {
        public string NetworkId { get; set; } = string.Empty;

        public string ElementUid { get; set; } = string.Empty;

        /// <summary>Original schema element name, for example Part / Call / Wire.</summary>
        public string ElementKind { get; set; } = string.Empty;
    }

    /// <summary>LAD view of one logic block or type (FC / FB / OB / GlobalDB / UDT).</summary>
    public sealed class LadBlock
    {
        public string BlockId { get; set; } = string.Empty;

        /// <summary>FC / FB / OB / UDT / GlobalDB / InstanceDB.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Raw block element local name (SW.Blocks.InstanceDB / SW.Types.PlcStruct / ...).</summary>
        public string XmlBlockElement { get; set; } = string.Empty;

        /// <summary>Raw block kind: FC / FB / OB / GlobalDB / InstanceDB / PlcStruct.</summary>
        public string XmlBlockKind { get; set; } = string.Empty;

        /// <summary>True for an instance DB (the block belongs to one FB instance).</summary>
        public bool IsInstanceDb { get; set; }

        /// <summary>Declared FB name of an instance DB (InstanceOfName), empty otherwise.</summary>
        public string InstanceOfName { get; set; } = string.Empty;

        /// <summary>
        /// Block-level AttributeList entries. Scalar values keep their text; complex nodes
        /// (Interface / ...) are marked so read -> write can tell them apart.
        /// </summary>
        public List<BlockAttribute> Attributes { get; set; } = new List<BlockAttribute>();

        public string Name { get; set; } = string.Empty;

        public int Number { get; set; }

        /// <summary>Block title from the block ObjectList (written back as titleZhCn/title).</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Block comment from the block ObjectList.</summary>
        public string Comment { get; set; } = string.Empty;

        /// <summary>LAD / FBD / SCL / STL.</summary>
        public string Language { get; set; } = "LAD";

        /// <summary>Block interface root node (Interface); inside it is a Sections/Member node tree.</summary>
        public LadNode Interface { get; set; }

        public List<LadNetwork> Networks { get; set; } = new List<LadNetwork>();
    }
}
