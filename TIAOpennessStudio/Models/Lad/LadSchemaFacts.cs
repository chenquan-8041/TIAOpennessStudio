using System;
using System.Collections.Generic;
using System.Linq;

namespace TIAOpennessStudio.Models.Lad
{
    /// <summary>Which interface member kinds may carry the ExternalWritable BooleanAttribute.</summary>
    public enum ExternalWritableScope
    {
        None,
        UdtOnly,
        DbOnly,
        Both
    }

    /// <summary>
    /// XML facts for one TIA major version. This is the ONLY place in the project where schema
    /// version numbers and namespaces are declared; block builders, the canonical codec, the RAG
    /// indexer and the GRAPH schema registry all resolve their version behavior from here.
    ///
    /// Sources:
    ///   - Namespace versions: file names under docs/PublicAPI/V*/Schemas/*.xsd (official XSD).
    ///   - Namespace URIs: real exports in docs/samples/V15.1/...
    ///   - SystemDefined default and nested comments: SW.Common_v2.xsd vs v3.xsd line diff.
    /// </summary>
    public sealed class LadSchemaFacts
    {
        private const string SiemensRoot = "http://www.siemens.com/automation/Openness/";

        public LadSchemaFacts(int majorVersion, string displayVersion)
        {
            MajorVersion = majorVersion;
            DisplayVersion = displayVersion;
            // Block-level attributes are import-verified only. Real V15.1 imports reject both
            // <MemoryLayout> ("Missing XML attribute 'ReadOnly' ...") and <SetENOAutomatically>
            // ("not supported at the object"), and TIA's own exports omit them for default values.
            // Until a version is verified by a real import, emit none of them (fail closed).
            BlockAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public int MajorVersion { get; }

        /// <summary>Display name, for example "V15.1" or "V17".</summary>
        public string DisplayVersion { get; }

        /// <summary>Interface namespace suffix version (SW/Interface/vN).</summary>
        public int InterfaceVersion { get; set; }

        /// <summary>FlgNet namespace suffix version (SW/NetworkSource/FlgNet/vN).</summary>
        public int FlgNetVersion { get; set; }

        /// <summary>SCL namespace suffix version (SW/NetworkSource/StructuredText/vN).</summary>
        public int StructuredTextVersion { get; set; }

        /// <summary>STL namespace suffix version; null when no real export has confirmed it yet.</summary>
        public int? StlVersion { get; set; }

        /// <summary>GRAPH namespace suffix version (SW/NetworkSource/Graph/vN).</summary>
        public int GraphVersion { get; set; }

        public string InterfaceNamespace => InterfaceVersion > 0 ? SiemensRoot + "SW/Interface/v" + InterfaceVersion : null;

        public string FlgNetNamespace => FlgNetVersion > 0 ? SiemensRoot + "SW/NetworkSource/FlgNet/v" + FlgNetVersion : null;

        public string SclNamespace => StructuredTextVersion > 0 ? SiemensRoot + "SW/NetworkSource/StructuredText/v" + StructuredTextVersion : null;

        /// <summary>STL namespace URI. Null means "not confirmed" and the codec refuses to emit it.</summary>
        public string StlNamespace => StlVersion.HasValue
            ? SiemensRoot + "SW/NetworkSource/StatementList/v" + StlVersion.Value
            : null;

        public string GraphNamespace => GraphVersion > 0 ? SiemensRoot + "SW/NetworkSource/Graph/v" + GraphVersion : null;

        /// <summary>Default value of the SystemDefined attribute in SW.Common (v2=true, v3=false).</summary>
        public bool SystemDefinedDefault { get; set; }

        /// <summary>Whether Comment may contain nested Comment elements (supported since v3).</summary>
        public bool SupportsNestedComment { get; set; }

        /// <summary>Whether the block skeleton emits a &lt;Namespace&gt; element (true from V18 on).</summary>
        public bool EmitNamespaceElement { get; set; }

        /// <summary>
        /// Which member kinds may carry ExternalWritable. Older versions reject the extra
        /// AttributeList/BooleanAttribute as a whole, so this gates emission (not just the value).
        /// </summary>
        public ExternalWritableScope ExternalWritableScope { get; set; } = ExternalWritableScope.None;

        public bool UdtMembersSupportExternalWritable
            => ExternalWritableScope == ExternalWritableScope.UdtOnly
               || ExternalWritableScope == ExternalWritableScope.Both;

        public bool DbMembersSupportExternalWritable
            => ExternalWritableScope == ExternalWritableScope.DbOnly
               || ExternalWritableScope == ExternalWritableScope.Both;

        /// <summary>
        /// Block-level AttributeList attributes this version accepts on XML import. Empty until a real
        /// import proves a name works for that version; builders ask this instead of hard-coding checks.
        /// </summary>
        public HashSet<string> BlockAttributes { get; set; }

        public bool SupportsBlockAttribute(string name)
            => !string.IsNullOrWhiteSpace(name) && BlockAttributes != null && BlockAttributes.Contains(name);

        /// <summary>True when namespaces were confirmed by official XSD or a real export (not an import test).</summary>
        public bool SchemaConfirmed { get; set; }

        /// <summary>True only after a real TIA import + block compile + re-export comparison succeeded.</summary>
        public bool ImportVerified { get; set; }

        public string Note { get; set; } = string.Empty;

        internal LadSchemaFacts CloneAs(int majorVersion, string displayVersion, string note)
        {
            return new LadSchemaFacts(majorVersion, displayVersion)
            {
                InterfaceVersion = InterfaceVersion,
                FlgNetVersion = FlgNetVersion,
                StructuredTextVersion = StructuredTextVersion,
                StlVersion = StlVersion,
                GraphVersion = GraphVersion,
                SystemDefinedDefault = SystemDefinedDefault,
                SupportsNestedComment = SupportsNestedComment,
                EmitNamespaceElement = EmitNamespaceElement,
                ExternalWritableScope = ExternalWritableScope,
                BlockAttributes = new HashSet<string>(BlockAttributes ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase),
                SchemaConfirmed = false,
                ImportVerified = false,
                Note = note
            };
        }
    }

    /// <summary>
    /// Version facts table: the single source of truth for TIA 15.1–21.
    ///
    /// 15.1 / 16 / 17 are the verified baseline (XSD file names + real V15.1 exports in the repo).
    /// 18–21 are explicit provisional entries: the values follow the same naming pattern but have
    /// not been validated against a real export, so they say so in <see cref="LadSchemaFacts.Note"/>.
    /// Adding a new TIA version means adding exactly one entry here.
    /// </summary>
    public static class LadSchemaRegistry
    {
        /// <summary>V15.1: project baseline. Every namespace is confirmed by a real export in the repo.</summary>
        public static readonly LadSchemaFacts V15_1 = new LadSchemaFacts(15, "V15.1")
        {
            InterfaceVersion = 3,
            FlgNetVersion = 3,
            StructuredTextVersion = 2,
            StlVersion = null,
            GraphVersion = 4,
            SystemDefinedDefault = true,
            SupportsNestedComment = false,
            EmitNamespaceElement = false,
            ExternalWritableScope = ExternalWritableScope.None,
            SchemaConfirmed = true,
            ImportVerified = false,
            Note = "Baseline. Namespaces taken from LAD_V0.1.xml / SCL_V0.1.xml / graph_V0.1.xml exports."
        };

        /// <summary>V16: Interface/v4, FlgNet/v4, StructuredText/v3, Graph/v5 (official XSD file names).</summary>
        public static readonly LadSchemaFacts V16 = new LadSchemaFacts(16, "V16")
        {
            InterfaceVersion = 4,
            FlgNetVersion = 4,
            StructuredTextVersion = 3,
            StlVersion = null,
            GraphVersion = 5,
            SystemDefinedDefault = false,
            SupportsNestedComment = true,
            EmitNamespaceElement = false,
            ExternalWritableScope = ExternalWritableScope.None,
            SchemaConfirmed = true,
            ImportVerified = false,
            Note = "Versions from docs/PublicAPI/V16/Schemas; SCL v3 URI inferred from the naming pattern, pending a real export."
        };

        /// <summary>V17: LAD-related XSD files are byte-identical to V16, so the facts match.</summary>
        public static readonly LadSchemaFacts V17 = new LadSchemaFacts(17, "V17")
        {
            InterfaceVersion = 4,
            FlgNetVersion = 4,
            StructuredTextVersion = 3,
            StlVersion = null,
            GraphVersion = 5,
            SystemDefinedDefault = false,
            SupportsNestedComment = true,
            EmitNamespaceElement = false,
            ExternalWritableScope = ExternalWritableScope.None,
            SchemaConfirmed = true,
            ImportVerified = false,
            Note = "LAD XSD is byte-identical to V16 (md5 42EE38E8...), so the same codec applies."
        };

        /// <summary>V18: provisional; Interface/v5 + FlgNet/v5 + StructuredText/v4, Namespace element appears.</summary>
        public static readonly LadSchemaFacts V18 = new LadSchemaFacts(18, "V18")
        {
            InterfaceVersion = 5,
            FlgNetVersion = 5,
            StructuredTextVersion = 4,
            StlVersion = null,
            GraphVersion = 5,
            SystemDefinedDefault = false,
            SupportsNestedComment = true,
            EmitNamespaceElement = true,
            ExternalWritableScope = ExternalWritableScope.None,
            SchemaConfirmed = false,
            ImportVerified = false,
            Note = "Provisional: values follow the documented naming pattern; no V18 XSD/sample in the repo."
        };

        public static readonly LadSchemaFacts V19 = V18.CloneAs(19, "V19", "Provisional: derived from V18; no V19 XSD/sample in the repo.");

        public static readonly LadSchemaFacts V20 = V18.CloneAs(20, "V20", "Provisional: derived from V18; no V20 XSD/sample in the repo.");

        /// <summary>V21: provisional; UDT members carry ExternalWritable, everything else follows V18.</summary>
        public static readonly LadSchemaFacts V21 = new LadSchemaFacts(21, "V21")
        {
            InterfaceVersion = 5,
            FlgNetVersion = 5,
            StructuredTextVersion = 4,
            StlVersion = null,
            GraphVersion = 5,
            SystemDefinedDefault = false,
            SupportsNestedComment = true,
            EmitNamespaceElement = true,
            ExternalWritableScope = ExternalWritableScope.UdtOnly,
            SchemaConfirmed = false,
            ImportVerified = false,
            Note = "Provisional: ExternalWritable confirmed by V21 exports; other namespaces follow the naming pattern."
        };

        private static readonly Dictionary<int, LadSchemaFacts> Table =
            new Dictionary<int, LadSchemaFacts>
            {
                [15] = V15_1,
                [16] = V16,
                [17] = V17,
                [18] = V18,
                [19] = V19,
                [20] = V20,
                [21] = V21
            };

        /// <summary>Project baseline version (TIA V15.1).</summary>
        public static LadSchemaFacts Baseline => V15_1;

        public static IEnumerable<LadSchemaFacts> All => Table.Values.OrderBy(f => f.MajorVersion);

        public static bool TryGet(int majorVersion, out LadSchemaFacts facts)
        {
            return Table.TryGetValue(majorVersion, out facts);
        }

        /// <summary>
        /// Resolve any major version. Unknown versions get an explicitly marked provisional profile;
        /// an older profile is never applied silently, and a newer profile is never applied to an
        /// older target.
        /// </summary>
        public static LadSchemaFacts Resolve(int majorVersion)
        {
            if (Table.TryGetValue(majorVersion, out var known))
                return known;

            // Version not detected (no TIA installed): fall back to the project baseline explicitly
            // instead of guessing a newer schema.
            if (majorVersion <= 0)
            {
                return V15_1.CloneAs(
                    V15_1.MajorVersion,
                    V15_1.DisplayVersion,
                    "TIA version not detected; using the project baseline (V15.1) profile.");
            }

            // Older than the supported baseline: V14/V15.0 are outside the supported range. Applying
            // the newer (V17) namespace set here would produce XML the target TIA rejects, so keep the
            // baseline profile and mark it unverified instead.
            if (majorVersion < Baseline.MajorVersion)
            {
                return V15_1.CloneAs(
                    majorVersion,
                    "V" + majorVersion,
                    "Older than the supported baseline (V15.1, V16, V17): XML follows the V15.1 profile and is not import-verified.");
            }

            // Unknown newer version: reuse the newest known namespace set, still marked provisional.
            var template = majorVersion >= 18 ? V21 : V17;
            return template.CloneAs(
                majorVersion,
                "V" + majorVersion,
                "Unknown version: read the XSD names under PublicAPI\\V*\\Schemas of that installation and add an entry here.");
        }
    }
}
