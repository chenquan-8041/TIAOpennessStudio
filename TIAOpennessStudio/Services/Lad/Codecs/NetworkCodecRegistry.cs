using System.Xml.Linq;
using TIAOpennessStudio.Models.Lad;

namespace TIAOpennessStudio.Services.Lad.Codecs
{
    /// <summary>
    /// Codec factory. New versions only need an entry in <see cref="LadSchemaRegistry"/>;
    /// new languages only need the two mappings in <see cref="NetworkBodyCodec"/>.
    /// </summary>
    public static class NetworkCodecRegistry
    {
        public static INetworkCodec ForLanguage(string language, LadSchemaFacts facts)
        {
            var resolved = facts ?? LadSchemaRegistry.Baseline;
            var lang = string.IsNullOrEmpty(language) ? "LAD" : language.ToUpperInvariant();
            return new NetworkBodyCodec(resolved, lang, NetworkBodyCodec.BodyElementOf(lang));
        }

        public static INetworkCodec ForVersion(int majorVersion, string language)
        {
            return ForLanguage(language, LadSchemaRegistry.Resolve(majorVersion));
        }

        /// <summary>Select by network body root: FlgNet to LAD, StructuredText to SCL, StatementList to STL.</summary>
        public static INetworkCodec ForXml(XElement body, LadSchemaFacts facts)
        {
            var name = body?.Name.LocalName ?? "FlgNet";
            return new NetworkBodyCodec(facts ?? LadSchemaRegistry.Baseline, NetworkBodyCodec.LanguageOf(name), name);
        }
    }
}
