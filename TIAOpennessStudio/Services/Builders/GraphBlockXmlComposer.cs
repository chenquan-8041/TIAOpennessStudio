using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// S7-GRAPH FB composer.
    ///
    /// Structure is based on the real V15.1 GRAPH FB export
    /// <c>docs/samples/V15.1/GRAPH/graph_V0.1.xml</c>, embedded as <c>Templates/GRAPH_V15_1.xml</c>
    /// (no GRAPH_BASE section, per-step/per-transition Static members, Graph/v4 namespace,
    /// full AlarmsSettings). The Interface/Graph namespace, Engineering header and culture are
    /// rewritten per detected TIA version (V15.1 baseline, 15.1–21 supported), the Sequence is
    /// replaced, and the GraphStep/GraphTransition static members are regenerated from the IR.
    /// </summary>
    public static class GraphBlockXmlComposer
    {
        /// <summary>Compose a complete GRAPH FB document plus the schema/template resolution used.</summary>
        public static GraphComposeResult Compose(GraphBlockDefinition definition, string culture = "zh-CN")
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrWhiteSpace(definition.BlockName))
                throw new ArgumentException("GRAPH block name is required.");
            if (definition.BlockNumber <= 0)
                throw new ArgumentException("GRAPH block number must be greater than 0.");
            if (definition.Sequence == null || definition.Sequence.Steps.Count == 0)
                throw new ArgumentException("GRAPH requires at least one Step.");

            culture = string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture;

            // Version architecture: exact template when available, otherwise the verified V15.1
            // baseline plus explicit warnings. A version-specific profile can add template/sequence
            // customization hooks without touching this composer.
            var profile = GraphSchemaRegistry.Resolve(TiaVersionAdapter.CurrentMajorVersion);
            var warnings = new List<string>();
            var templateFile = profile.TemplateFile;
            var document = TryLoadTemplate(templateFile);
            var exactTemplate = document != null;
            if (document == null)
            {
                templateFile = GraphSchemaRegistry.BaselineTemplateFile;
                warnings.Add($"No GRAPH template '{profile.TemplateFile}' for TIA V{profile.MajorVersion}; falling back to {templateFile}.");
                document = LoadTemplate(templateFile);
            }
            if (!profile.Verified)
                warnings.Add($"GRAPH schema profile for TIA V{profile.MajorVersion} is provisional: {profile.Note}");
            var block = document.Root?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "SW.Blocks.FB")
                ?? throw new InvalidOperationException("GRAPH template is missing SW.Blocks.FB.");

            var attributeList = block.Element("AttributeList")
                ?? throw new InvalidOperationException("GRAPH template is missing AttributeList.");
            var interfaceElement = attributeList.Element("Interface")
                ?? throw new InvalidOperationException("GRAPH template is missing Interface.");

            // 1. Engineering header follows the detected TIA version ("V15.1", "V16", ...).
            document.Root?.Element("Engineering")?
                .SetAttributeValue("version", TiaVersionAdapter.CurrentEngineeringVersion);

            // 2. Interface namespace: the V15.1 template is Interface/v3; rewrite to the detected version.
            var oldSections = interfaceElement
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Sections" && !e.Name.Namespace.Equals(XNamespace.None))
                ?? throw new InvalidOperationException("GRAPH template is missing Interface Sections.");
            var oldInterfaceNs = oldSections.Name.Namespace;
            var newSections = RewriteNamespace(oldSections, oldInterfaceNs, profile.InterfaceNamespace);
            oldSections.ReplaceWith(newSections);

            // 3. All multilingual texts must use a culture the live project actually has.
            RewriteCulture(document, culture);

            // 4. Header identity.
            attributeList.Element("Name")?.SetValue(definition.BlockName);
            attributeList.Element("Number")?.SetValue(definition.BlockNumber.ToString());

            // V18+ Software Unit namespace element (V15.1 has none).
            if (profile.EmitNamespaceElement &&
                !attributeList.Elements().Any(e => e.Name.LocalName == "Namespace"))
            {
                var nameElement = attributeList.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                if (nameElement != null) nameElement.AddAfterSelf(new XElement("Namespace"));
                else attributeList.Add(new XElement("Namespace"));
            }

            // 5. User interface members are appended after the GRAPH system members.
            //    Static is handled after the GraphStep/GraphTransition members are regenerated.
            AppendMembers(newSections, "Input", definition.Inputs, culture);
            AppendMembers(newSections, "Output", definition.Outputs, culture);
            AppendMembers(newSections, "InOut", definition.InOuts, culture);
            AppendMembers(newSections, "Temp", definition.Temps, culture);
            RebuildGraphStaticMembers(newSections, definition.Sequence, definition.Statics, culture, profile);
            AppendMembers(newSections, "Static", definition.Statics, culture);

            // 6. Replace the template sequence with the generated one. The template already carries
            //    the V15.1 AlarmsSettings defaults (including the v4/v5 subcategory elements).
            var graphElement = block
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Graph");
            if (graphElement == null)
                throw new InvalidOperationException("GRAPH template is missing NetworkSource/Graph.");

            var targetGraphNs = profile.GraphNamespace;
            if (graphElement.Name.Namespace != targetGraphNs)
            {
                var rewrittenGraph = RewriteNamespace(graphElement, graphElement.Name.Namespace, targetGraphNs);
                graphElement.ReplaceWith(rewrittenGraph);
                graphElement = rewrittenGraph;
            }

            var graphNamespace = graphElement.Name.Namespace;
            var oldSequence = graphElement.Elements().FirstOrDefault(e => e.Name.LocalName == "Sequence");
            var newSequence = BuildSequence(definition.Sequence, graphNamespace, culture, profile);
            profile.CustomizeSequence(newSequence, definition.Sequence, culture);
            if (oldSequence != null) oldSequence.ReplaceWith(newSequence);
            else graphElement.Add(newSequence);

            // 7. Block / network texts (ObjectList).
            SetObjectListText(block, "Comment", definition.CommentZhCn ?? "");
            SetObjectListText(block, "Title", definition.TitleZhCn ?? "");
            var compileUnit = block.Elements().FirstOrDefault(e => e.Name.LocalName == "SW.Blocks.CompileUnit");
            if (compileUnit != null)
            {
                SetObjectListText(compileUnit, "Comment", definition.Sequence.CommentZhCn ?? "");
                SetObjectListText(compileUnit, "Title", definition.Sequence.TitleZhCn ?? "");
            }

            profile.CustomizeTemplate(document, culture);
            return new GraphComposeResult(document, new GraphSchemaInfo(profile, templateFile, exactTemplate, warnings));
        }

        /// <summary>Compose to an XML string (schema resolution is discarded; use Compose for metadata).</summary>
        public static string ComposeXml(GraphBlockDefinition definition, string culture = "zh-CN")
        {
            return ToXml(Compose(definition, culture).Document);
        }

        // GRAPH system interface members owned by the template. Classification lives in
        // GraphSystemMembers so the knowledge worker can link the read-side rule without
        // pulling in this composer.
        /// <summary>True when the member is part of the GRAPH system interface (not user-declared).</summary>
        public static bool IsSystemMember(string sectionName, string name, string datatype)
            => GraphSystemMembers.IsSystemMember(sectionName, name, datatype);

        /// <summary>Serialize a composed document (UTF-8, no BOM).</summary>
        public static string ToXml(XDocument document)
        {
            using var writer = new Utf8StringWriter();
            document.Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        private static XDocument TryLoadTemplate(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            var assembly = typeof(GraphBlockXmlComposer).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return null;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            return stream == null ? null : XDocument.Load(stream);
        }

        private static XDocument LoadTemplate(string fileName)
        {
            return TryLoadTemplate(fileName)
                ?? throw new InvalidOperationException("Embedded GRAPH template resource was not found: " + fileName);
        }

        private static void AppendMembers(
            XElement sections, string sectionName,
            IEnumerable<PlcBlockMemberDefinition> members, string culture)
        {
            var list = members?.Where(m => m != null).ToArray() ?? Array.Empty<PlcBlockMemberDefinition>();
            if (list.Length == 0) return;

            var ns = sections.Name.Namespace;
            var section = sections.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Section" &&
                                     string.Equals((string)e.Attribute("Name"), sectionName, StringComparison.OrdinalIgnoreCase));
            if (section == null)
                throw new InvalidOperationException("GRAPH template is missing interface section: " + sectionName);

            foreach (var member in list)
            {
                if (string.IsNullOrWhiteSpace(member.Name))
                    throw new ArgumentException("GRAPH interface member name is required.");
                if (string.IsNullOrWhiteSpace(member.Datatype))
                    throw new ArgumentException("GRAPH interface member datatype is required: " + member.Name);
                if (section.Elements().Any(e => e.Name.LocalName == "Member" &&
                                                string.Equals((string)e.Attribute("Name"), member.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Duplicate GRAPH interface member in section {sectionName}: {member.Name}");

                var memberElement = new XElement(ns + "Member",
                    new XAttribute("Name", member.Name),
                    new XAttribute("Datatype", member.Datatype));
                if (!string.IsNullOrWhiteSpace(member.CommentZhCn))
                {
                    memberElement.Add(new XElement(ns + "Comment",
                        new XElement(ns + "MultiLanguageText",
                            new XAttribute("Lang", culture),
                            member.CommentZhCn)));
                }
                section.Add(memberElement);
            }
        }

        /// <summary>
        /// Rebuild the per-step / per-transition Static members from the IR. The V15.1 GRAPH export
        /// stores one GraphStep / GraphTransition structure per sequence node (SNO / TNO start value),
        /// so a template exported from another sequence cannot be reused as-is.
        /// </summary>
        private static void RebuildGraphStaticMembers(
            XElement sections, GraphSequenceDefinition sequence,
            IEnumerable<PlcBlockMemberDefinition> userStatics, string culture,
            GraphSchemaProfile profile)
        {
            var ns = sections.Name.Namespace;
            var staticSection = sections.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Section" &&
                                     string.Equals((string)e.Attribute("Name"), "Static", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("GRAPH template is missing the Static section.");

            foreach (var member in staticSection.Elements()
                .Where(e => e.Name.LocalName == "Member" && IsGraphStepOrTransitionMember(e))
                .ToList())
            {
                member.Remove();
            }

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in staticSection.Elements().Where(e => e.Name.LocalName == "Member"))
            {
                var name = (string)member.Attribute("Name");
                if (!string.IsNullOrWhiteSpace(name)) reserved.Add(name);
            }
            foreach (var member in userStatics ?? Enumerable.Empty<PlcBlockMemberDefinition>())
            {
                if (!string.IsNullOrWhiteSpace(member.Name)) reserved.Add(member.Name);
            }

            NormalizeGraphNames(sequence, reserved);

            var generated = new List<XElement>();
            foreach (var transition in sequence.Transitions)
            {
                generated.Add(BuildGraphStaticMember(
                    ns, profile.TransitionDatatype, profile.TransitionDatatypeVersion,
                    transition.Name, "TNO", transition.Number, culture, "Transition structure"));
            }
            foreach (var step in sequence.Steps)
            {
                generated.Add(BuildGraphStaticMember(
                    ns, profile.StepDatatype, profile.StepDatatypeVersion,
                    step.Name, "SNO", step.Number, culture, "Step structure"));
            }

            var first = staticSection.Elements().FirstOrDefault();
            foreach (var member in generated)
            {
                if (first != null) first.AddBeforeSelf(member);
                else staticSection.Add(member);
            }
        }

        private static bool IsGraphStepOrTransitionMember(XElement member)
        {
            var datatype = (string)member.Attribute("Datatype");
            return string.Equals(datatype, "GraphStep", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(datatype, "GraphTransition", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// GRAPH step/transition names double as Static member names, so they must be unique ASCII
        /// identifiers. Invalid or colliding names fall back to Step{n} / Trans{n}.
        /// </summary>
        private static void NormalizeGraphNames(GraphSequenceDefinition sequence, HashSet<string> used)
        {
            foreach (var transition in sequence.Transitions)
                transition.Name = MakeUniqueGraphName(transition.Name, "Trans" + transition.Number, used);
            foreach (var step in sequence.Steps)
                step.Name = MakeUniqueGraphName(step.Name, "Step" + step.Number, used);
        }

        private static string MakeUniqueGraphName(string proposed, string fallback, HashSet<string> used)
        {
            var name = IsAsciiIdentifier(proposed) ? proposed.Trim() : fallback;
            if (used.Add(name)) return name;

            var index = 2;
            var candidate = name + "_" + index;
            while (!used.Add(candidate))
            {
                index++;
                candidate = name + "_" + index;
            }
            return candidate;
        }

        private static bool IsAsciiIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim();
            if (!(IsAsciiLetter(text[0]) || text[0] == '_')) return false;
            for (var i = 1; i < text.Length; i++)
            {
                if (!(IsAsciiLetter(text[i]) || (text[i] >= '0' && text[i] <= '9') || text[i] == '_'))
                    return false;
            }
            return true;
        }

        private static bool IsAsciiLetter(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static XElement BuildGraphStaticMember(
            XNamespace ns, string datatype, string datatypeVersion,
            string name, string numberMember, int number,
            string culture, string comment)
        {
            return new XElement(ns + "Member",
                new XAttribute("Name", name),
                new XAttribute("Datatype", datatype),
                new XAttribute("Version", datatypeVersion),
                new XElement(ns + "Comment", new XAttribute("Informative", "true"),
                    new XElement(ns + "MultiLanguageText", new XAttribute("Lang", culture), comment)),
                new XElement(ns + "Sections",
                    new XElement(ns + "Section", new XAttribute("Name", "None"),
                        new XElement(ns + "Member",
                            new XAttribute("Name", numberMember),
                            new XAttribute("Datatype", "Int"),
                            new XElement(ns + "StartValue", new XAttribute("Informative", "true"), number)))));
        }

        private static XElement BuildSequence(
            GraphSequenceDefinition sequence, XNamespace ns, string culture, GraphSchemaProfile profile)
        {
            var steps = new XElement(ns + "Steps");
            foreach (var step in sequence.Steps) steps.Add(BuildStep(step, ns, culture, profile));

            var transitions = new XElement(ns + "Transitions");
            foreach (var transition in sequence.Transitions) transitions.Add(BuildTransition(transition, ns, culture));

            var branches = new XElement(ns + "Branches");
            foreach (var branch in sequence.Branches)
            {
                branches.Add(new XElement(ns + "Branch",
                    new XAttribute("Number", branch.Number),
                    new XAttribute("Type", branch.Type),
                    new XAttribute("Cardinality", branch.Cardinality)));
            }

            var connections = new XElement(ns + "Connections");
            foreach (var connection in sequence.Connections) connections.Add(BuildConnection(connection, ns));

            // TIA exports Title/Comment even when empty; emit them unconditionally to keep the
            // sequence shape identical to a real GRAPH export.
            var element = new XElement(ns + "Sequence");
            element.Add(BuildMultilingualText(ns, "Title", sequence.TitleZhCn ?? "", culture));
            element.Add(BuildMultilingualText(ns, "Comment", sequence.CommentZhCn ?? "", culture));
            element.Add(steps, transitions, branches, connections);
            return element;
        }

        private static XElement BuildStep(
            GraphStepDefinition step, XNamespace ns, string culture, GraphSchemaProfile profile)
        {
            var element = new XElement(ns + "Step",
                new XAttribute("Number", step.Number),
                new XAttribute("Init", step.Init ? "true" : "false"));

            if (!string.IsNullOrWhiteSpace(step.Name))
                element.SetAttributeValue("Name", step.Name);
            if (!string.IsNullOrWhiteSpace(step.MaximumStepTime))
                element.SetAttributeValue("MaximumStepTime", step.MaximumStepTime);
            if (!string.IsNullOrWhiteSpace(step.WarningTime))
                element.SetAttributeValue("WarningTime", step.WarningTime);

            if (!string.IsNullOrWhiteSpace(step.CommentZhCn))
                element.Add(BuildMultilingualText(ns, "Comment", step.CommentZhCn, culture));

            // TIA GRAPH stores one action row per line with tokenized text, and always keeps a blank
            // trailing row. The operand is the TIA text form (quoted tag/address, e.g. "Q0.0").
            var actions = new XElement(ns + "Actions");
            if (!string.IsNullOrWhiteSpace(step.ActionsTitleZhCn))
                actions.Add(BuildMultilingualText(ns, "Title", step.ActionsTitleZhCn, culture));
            foreach (var action in step.Actions)
            {
                if (string.IsNullOrWhiteSpace(action.Operand)) continue;

                var actionElement = new XElement(ns + "Action",
                    new XAttribute("Qualifier", string.IsNullOrWhiteSpace(action.Qualifier) ? "N" : action.Qualifier));
                if (!string.IsNullOrWhiteSpace(action.Event))
                    actionElement.SetAttributeValue("Event", action.Event);
                if (action.Interlock)
                    actionElement.SetAttributeValue("Interlock", "true");

                if (profile.TokenizedActions)
                {
                    actionElement.Add(BuildActionToken(ns, action.Operand));
                    if (!string.IsNullOrWhiteSpace(action.Parameter))
                    {
                        actionElement.Add(BuildActionToken(ns, ","));
                        actionElement.Add(BuildActionToken(ns, " "));
                        actionElement.Add(BuildActionToken(ns, action.Parameter));
                    }
                    actionElement.Add(BuildActionToken(ns, "\n"));
                }
                else
                {
                    var text = action.Operand;
                    if (!string.IsNullOrWhiteSpace(action.Parameter))
                        text += ", " + action.Parameter;
                    actionElement.Add(BuildActionToken(ns, text));
                }
                actions.Add(actionElement);
            }
            if (profile.EmitTrailingEmptyAction)
                actions.Add(new XElement(ns + "Action"));
            element.Add(actions);

            // Supervisions_T / Interlocks_T require at least one entry; TIA exports a default coil
            // even when the step has no supervision/interlock logic.
            var supervision = step.Supervision;
            if ((supervision == null || supervision.Parts.Count == 0) && profile.EnsureDefaultSupervisionInterlock)
                supervision = FlgNetGraphXmlBuilder.CreateCoilCondition("SvCoil");
            var interlock = step.Interlock;
            if ((interlock == null || interlock.Parts.Count == 0) && profile.EnsureDefaultSupervisionInterlock)
                interlock = FlgNetGraphXmlBuilder.CreateCoilCondition("IlCoil");

            element.Add(BuildLogicContainer("Supervisions", "Supervision", supervision, ns));
            element.Add(BuildLogicContainer("Interlocks", "Interlock", interlock, ns));
            return element;
        }

        private static XElement BuildActionToken(XNamespace ns, string text) =>
            new XElement(ns + "Token", new XAttribute("Text", text ?? ""));

        private static XElement BuildLogicContainer(
            string containerName, string itemName, GraphFlgNetDefinition condition, XNamespace ns)
        {
            var container = new XElement(ns + containerName);
            if (condition != null && condition.Parts.Count > 0)
            {
                container.Add(new XElement(ns + itemName,
                    new XAttribute("ProgrammingLanguage", "LAD"),
                    FlgNetGraphXmlBuilder.BuildFlgNet(ns, condition)));
            }
            return container;
        }

        private static XElement BuildTransition(GraphTransitionDefinition transition, XNamespace ns, string culture)
        {
            var element = new XElement(ns + "Transition",
                new XAttribute("IsMissing", transition.IsMissing ? "true" : "false"),
                new XAttribute("Name", string.IsNullOrWhiteSpace(transition.Name)
                    ? "Trans" + transition.Number
                    : transition.Name),
                new XAttribute("Number", transition.Number),
                new XAttribute("ProgrammingLanguage",
                    string.IsNullOrWhiteSpace(transition.ProgrammingLanguage) ? "LAD" : transition.ProgrammingLanguage));

            if (!string.IsNullOrWhiteSpace(transition.CommentZhCn))
                element.Add(BuildMultilingualText(ns, "Comment", transition.CommentZhCn, culture));

            var condition = transition.Condition;
            if (condition == null || condition.Parts.Count == 0)
                condition = FlgNetGraphXmlBuilder.CreateCoilCondition("TrCoil");

            element.Add(FlgNetGraphXmlBuilder.BuildFlgNet(ns, condition));
            return element;
        }

        private static XElement BuildConnection(GraphConnectionDefinition connection, XNamespace ns)
        {
            return new XElement(ns + "Connection",
                new XElement(ns + "NodeFrom", BuildNodeReference(connection.From, ns, isFrom: true)),
                new XElement(ns + "NodeTo", BuildNodeReference(connection.To, ns, isFrom: false)),
                new XElement(ns + "LinkType", string.IsNullOrWhiteSpace(connection.LinkType) ? "Direct" : connection.LinkType));
        }

        private static XElement BuildNodeReference(GraphNodeReference node, XNamespace ns, bool isFrom)
        {
            if (node == null) throw new ArgumentException("GRAPH connection endpoint is required.");
            var kind = (node.Kind ?? "").Trim();
            switch (kind.ToLowerInvariant())
            {
                case "step":
                    return new XElement(ns + "StepRef", new XAttribute("Number", node.Number));
                case "transition":
                    return new XElement(ns + "TransitionRef", new XAttribute("Number", node.Number));
                case "branch":
                    var branchRef = new XElement(ns + "BranchRef", new XAttribute("Number", node.Number));
                    if (isFrom)
                    {
                        if (!node.Out.HasValue)
                            throw new ArgumentException("GRAPH branch as a source requires 'out'.");
                        branchRef.SetAttributeValue("Out", node.Out.Value);
                    }
                    else
                    {
                        if (!node.In.HasValue)
                            throw new ArgumentException("GRAPH branch as a target requires 'in'.");
                        branchRef.SetAttributeValue("In", node.In.Value);
                    }
                    return branchRef;
                case "end":
                    if (isFrom) throw new ArgumentException("GRAPH End cannot be a connection source.");
                    return new XElement(ns + "EndConnection");
                default:
                    throw new ArgumentException("Unsupported GRAPH connection endpoint kind: " + kind);
            }
        }

        private static XElement BuildMultilingualText(XNamespace ns, string elementName, string text, string culture)
        {
            return new XElement(ns + elementName,
                new XElement(ns + "MultiLanguageText",
                    new XAttribute("Lang", culture),
                    text ?? ""));
        }

        private static void SetObjectListText(XElement owner, string compositionName, string text)
        {
            var objectList = owner.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
            if (objectList == null) return;

            var multilingualText = objectList.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "MultilingualText" &&
                                     string.Equals((string)e.Attribute("CompositionName"), compositionName, StringComparison.OrdinalIgnoreCase));
            var textElement = multilingualText?
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Text");
            if (textElement != null) textElement.Value = text ?? "";
        }

        private static void RewriteCulture(XDocument document, string culture)
        {
            foreach (var element in document.Descendants())
            {
                if (element.Name.LocalName == "Culture")
                    element.Value = culture;
                else if (element.Name.LocalName == "MultiLanguageText")
                    element.SetAttributeValue("Lang", culture);
            }
        }

        private static XElement RewriteNamespace(XElement element, XNamespace oldNs, XNamespace newNs)
        {
            var name = element.Name.Namespace == oldNs
                ? newNs + element.Name.LocalName
                : element.Name;
            var result = new XElement(name);

            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                result.Add(new XAttribute(attribute.Name, attribute.Value));
            }
            foreach (var node in element.Nodes())
                result.Add(RewriteNode(node, oldNs, newNs));
            return result;
        }

        private static XNode RewriteNode(XNode node, XNamespace oldNs, XNamespace newNs)
        {
            if (node is XElement element) return RewriteNamespace(element, oldNs, newNs);
            if (node is XText text) return new XText(text.Value);
            if (node is XCData cdata) return new XCData(cdata.Value);
            if (node is XComment comment) return new XComment(comment.Value);
            return node;
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        }
    }
}
