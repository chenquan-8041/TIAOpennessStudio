using System.Xml.Linq;
using TIAOpennessStudio.Models.Lad;

namespace TIAOpennessStudio.Services.Lad.Codecs
{
    /// <summary>
    /// Network body codec: the only conversion point between <see cref="LadNetwork"/> and the XML
    /// of a given language and version. Version differences (namespaces, SystemDefined default)
    /// may only live in <see cref="Facts"/>; language differences may only live in
    /// <see cref="BodyElementName"/> and its namespace.
    /// </summary>
    public interface INetworkCodec
    {
        LadSchemaFacts Facts { get; }

        /// <summary>LAD / FBD / SCL / STL.</summary>
        string Language { get; }

        /// <summary>Network body root element name: FlgNet / StructuredText / StatementList.</summary>
        string BodyElementName { get; }

        bool CanParse(XElement body);

        /// <summary>XML to canonical. Unmodelled elements are preserved in the Body subtree.</summary>
        LadNetwork Parse(XElement body, LadParseContext context);

        /// <summary>Canonical to XML, using the target namespace from <paramref name="context"/>.</summary>
        XElement Emit(LadNetwork network, LadEmitContext context);
    }

    /// <summary>Parse context: network identity and display data supplied by the block parser.</summary>
    public sealed class LadParseContext
    {
        public string NetworkId { get; set; } = string.Empty;

        public string BlockId { get; set; } = string.Empty;

        public int Order { get; set; }

        public string CompileUnitId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Comment { get; set; } = string.Empty;

        public string Language { get; set; }
    }

    /// <summary>Emit context: target version and whether to renumber UIds.</summary>
    public sealed class LadEmitContext
    {
        /// <summary>Target version facts; falls back to the codec's own Facts when null.</summary>
        public LadSchemaFacts Target { get; set; }

        /// <summary>When true, UIds are renumbered to 1..N while keeping references consistent.</summary>
        public bool RenumberUids { get; set; }

        public static LadEmitContext For(LadSchemaFacts target)
        {
            return new LadEmitContext { Target = target };
        }
    }
}
