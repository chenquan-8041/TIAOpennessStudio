using System.Collections.Generic;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// Version-specific GRAPH schema behavior.
    ///
    /// Structural differences (interface shape, static runtime members, AlarmsSettings defaults,
    /// Pre/Post operations) are carried by a per-version template file under <c>XML/GRAPH_V*.xml</c>.
    /// Differences that cannot be expressed by a template alone (GraphStep/GraphTransition datatype
    /// versions, action row format, default coils) are exposed here and can be overridden by a
    /// version-specific subclass without touching the composer.
    /// </summary>
    public class GraphSchemaProfile
    {
        public GraphSchemaProfile(
            int majorVersion,
            string templateFile,
            int interfaceVersion,
            int graphVersion,
            bool verified,
            string note,
            bool emitNamespaceElement = false,
            string stepDatatype = "GraphStep",
            string stepDatatypeVersion = "1.0",
            string transitionDatatype = "GraphTransition",
            string transitionDatatypeVersion = "1.0",
            bool tokenizedActions = true,
            bool emitTrailingEmptyAction = true,
            bool ensureDefaultSupervisionInterlock = true)
        {
            MajorVersion = majorVersion;
            TemplateFile = templateFile;
            InterfaceVersion = interfaceVersion;
            GraphVersion = graphVersion;
            Verified = verified;
            Note = note;
            EmitNamespaceElement = emitNamespaceElement;
            StepDatatype = stepDatatype;
            StepDatatypeVersion = stepDatatypeVersion;
            TransitionDatatype = transitionDatatype;
            TransitionDatatypeVersion = transitionDatatypeVersion;
            TokenizedActions = tokenizedActions;
            EmitTrailingEmptyAction = emitTrailingEmptyAction;
            EnsureDefaultSupervisionInterlock = ensureDefaultSupervisionInterlock;
        }

        public int MajorVersion { get; }
        public string TemplateFile { get; }
        public int InterfaceVersion { get; }
        public int GraphVersion { get; }

        public XNamespace InterfaceNamespace =>
            "http://www.siemens.com/automation/Openness/SW/Interface/v" + InterfaceVersion;

        public XNamespace GraphNamespace =>
            "http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v" + GraphVersion;

        /// <summary>True only after the template has been validated against a real export of this version.</summary>
        public bool Verified { get; }

        public string Note { get; }
        public bool EmitNamespaceElement { get; }
        public string StepDatatype { get; }
        public string StepDatatypeVersion { get; }
        public string TransitionDatatype { get; }
        public string TransitionDatatypeVersion { get; }
        public bool TokenizedActions { get; }
        public bool EmitTrailingEmptyAction { get; }
        public bool EnsureDefaultSupervisionInterlock { get; }

        /// <summary>
        /// Hook for template-level differences that a future version needs on top of its own XML
        /// template (e.g. adding a GraphVersion element or rewriting a block attribute).
        /// </summary>
        public virtual void CustomizeTemplate(XDocument document, string culture)
        {
        }

        /// <summary>
        /// Hook for sequence-level differences that a future version needs after the common
        /// Sequence is built (e.g. a new action-row element or a different Step child order).
        /// </summary>
        public virtual void CustomizeSequence(XElement sequence, GraphSequenceDefinition definition, string culture)
        {
        }
    }

    /// <summary>Resolved schema + template state for one generation, surfaced to the tool result.</summary>
    public sealed class GraphSchemaInfo
    {
        public GraphSchemaInfo(
            GraphSchemaProfile profile, string templateFile, bool exactTemplate,
            IReadOnlyList<string> warnings)
        {
            Profile = profile;
            TemplateFile = templateFile;
            ExactTemplate = exactTemplate;
            Warnings = warnings ?? new List<string>();
        }

        public GraphSchemaProfile Profile { get; }

        public int MajorVersion => Profile.MajorVersion;
        public string TemplateFile { get; }
        public bool ExactTemplate { get; }

        /// <summary>Verified only when the profile is verified AND its exact template was used.</summary>
        public bool Verified => Profile.Verified && ExactTemplate;

        public bool Provisional => !Verified;
        public IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>Result of a GRAPH compose: the document plus the schema/template resolution used.</summary>
    public sealed class GraphComposeResult
    {
        public GraphComposeResult(XDocument document, GraphSchemaInfo schema)
        {
            Document = document;
            Schema = schema;
        }

        public XDocument Document { get; }
        public GraphSchemaInfo Schema { get; }
    }

    /// <summary>
    /// Registry of GRAPH schema profiles. V15.1 is the verified baseline; V16–V21 are registered as
    /// provisional slots. Adding <c>docs/samples/V&lt;ver&gt;/GRAPH/*.xml</c> to the project as
    /// <c>Templates/GRAPH_V&lt;ver&gt;.xml</c> (see TIAOpennessStudio.csproj) and marking the profile
    /// verified is the only change needed to adopt a new version.
    /// </summary>
    public static class GraphSchemaRegistry
    {
        public const string BaselineTemplateFile = "GRAPH_V15_1.xml";

        /// <summary>
        /// Namespaces come from <see cref="Models.Lad.LadSchemaRegistry"/> (the single version table);
        /// this registry only adds the GRAPH-specific template name and the verified flag.
        /// Unknown versions get a provisional profile named after that version, so template
        /// resolution falls back explicitly and the result is never misreported as verified.
        /// </summary>
        public static GraphSchemaProfile Resolve(int majorVersion)
        {
            var facts = Models.Lad.LadSchemaRegistry.Resolve(majorVersion);
            var isBaseline = facts.MajorVersion == Models.Lad.LadSchemaRegistry.Baseline.MajorVersion;
            var templateFile = isBaseline ? BaselineTemplateFile : "GRAPH_V" + facts.MajorVersion + ".xml";
            var verified = isBaseline && facts.SchemaConfirmed;
            var note = verified
                ? "V15.1 baseline (real export)"
                : "Provisional: add Templates/" + templateFile + " from a real export, then verify";

            return new GraphSchemaProfile(
                facts.MajorVersion,
                templateFile,
                interfaceVersion: facts.InterfaceVersion,
                graphVersion: facts.GraphVersion,
                verified: verified,
                note: note,
                emitNamespaceElement: facts.EmitNamespaceElement);
        }

        public static GraphSchemaProfile Baseline => Resolve(Models.Lad.LadSchemaRegistry.Baseline.MajorVersion);
    }
}
