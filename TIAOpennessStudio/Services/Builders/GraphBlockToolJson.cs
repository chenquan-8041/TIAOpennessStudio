using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TIAOpennessStudio.LadRendering.Parser;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// JSON adapter for the GRAPH FB builder tool: validation, model parsing, compose and the
    /// generate→re-read self check. Kept separate from <see cref="PlcBuilderToolJson"/> because the
    /// GRAPH contract (nodes + connections) is structurally different from the LAD network contract.
    /// </summary>
    public static class GraphBlockToolJson
    {
        private static readonly HashSet<string> BranchTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AltBegin", "AltEnd", "SimBegin", "SimEnd" };

        private static readonly HashSet<string> Qualifiers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "", "CD", "CR", "CS", "CU", "D", "L", "N", "ON", "OFF", "R", "S", "TD", "TF", "TL", "TR"
            };

        private static readonly HashSet<string> Events =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "", "A1", "L0", "L1", "R1", "S0", "S1", "V0", "V1"
            };

        public static JsonObject ComposeGraphFbBlock(string json, string culture = "zh-CN")
        {
            var root = ParseObject(json, "$");
            var definition = ReadDefinition(root);
            ValidateDefinition(definition);

            var composed = GraphBlockXmlComposer.Compose(definition, culture);
            var xml = GraphBlockXmlComposer.ToXml(composed.Document);
            var selfCheck = RunSelfCheck(definition, xml);

            return new JsonObject
            {
                ["ok"] = true,
                ["mode"] = "plc-compose-graph-fb-block-xml",
                ["offlineOnly"] = true,
                ["xmlParseOk"] = true,
                ["error"] = null,
                ["xml"] = xml,
                ["summary"] = new JsonObject
                {
                    ["blockName"] = definition.BlockName,
                    ["blockNumber"] = definition.BlockNumber,
                    ["stepCount"] = definition.Sequence.Steps.Count,
                    ["transitionCount"] = definition.Sequence.Transitions.Count,
                    ["branchCount"] = definition.Sequence.Branches.Count,
                    ["connectionCount"] = definition.Sequence.Connections.Count,
                    ["inputCount"] = definition.Inputs.Count,
                    ["outputCount"] = definition.Outputs.Count,
                    ["selfCheck"] = selfCheck
                },
                ["schema"] = BuildSchemaJson(composed.Schema),
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "Offline XML generation: no TIA Portal connection, no project opened, no PLC object imported.",
                    ["write"] = "This tool only returns the XML string; importing is performed by build_graph_fb after write approval."
                }
            };
        }

        private static GraphBlockDefinition ReadDefinition(JsonObject root)
        {
            var definition = new GraphBlockDefinition
            {
                BlockName = ReadString(root, "$.blockName", "blockName", "name"),
                BlockNumber = ReadInt(root, "$.blockNumber", "blockNumber", "number"),
                TitleZhCn = ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title"),
                CommentZhCn = ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment"),
                Inputs = ReadMembers(root, "inputs", "$.inputs").ToList(),
                Outputs = ReadMembers(root, "outputs", "$.outputs").ToList(),
                InOuts = ReadMembers(root, "inouts", "$.inouts", "inOuts", "inOut").ToList(),
                Statics = ReadMembers(root, "statics", "$.statics", "staticMembers", "static").ToList(),
                Temps = ReadMembers(root, "temps", "$.temps", "tempMembers", "temp").ToList()
            };

            var sequenceNode = root["sequence"] as JsonObject
                ?? throw new ArgumentException("Missing required JSON object: $.sequence");
            var sequence = new GraphSequenceDefinition
            {
                TitleZhCn = ReadOptionalString(sequenceNode, "titleZhCn", "title"),
                CommentZhCn = ReadOptionalString(sequenceNode, "commentZhCn", "comment")
            };

            var steps = ReadArray(sequenceNode, "steps", "$.sequence.steps");
            for (var i = 0; i < steps.Count; i++)
                sequence.Steps.Add(ReadStep(AsObject(steps[i], "$.sequence.steps[" + i + "]"), i));

            var transitions = sequenceNode["transitions"] as JsonArray;
            if (transitions != null)
            {
                for (var i = 0; i < transitions.Count; i++)
                    sequence.Transitions.Add(ReadTransition(AsObject(transitions[i], "$.sequence.transitions[" + i + "]"), i));
            }

            var branches = sequenceNode["branches"] as JsonArray;
            if (branches != null)
            {
                for (var i = 0; i < branches.Count; i++)
                    sequence.Branches.Add(ReadBranch(AsObject(branches[i], "$.sequence.branches[" + i + "]"), i));
            }

            var connections = sequenceNode["connections"] as JsonArray;
            if (connections != null)
            {
                for (var i = 0; i < connections.Count; i++)
                    sequence.Connections.Add(ReadConnection(AsObject(connections[i], "$.sequence.connections[" + i + "]"), i));
            }

            definition.Sequence = sequence;
            return definition;
        }

        private static GraphStepDefinition ReadStep(JsonObject step, int index)
        {
            var path = "$.sequence.steps[" + index + "]";
            var definition = new GraphStepDefinition
            {
                Number = ReadInt(step, path + ".number", "number"),
                Name = ReadOptionalString(step, "name"),
                Init = ReadBool(step, "init", false),
                MaximumStepTime = ReadOptionalString(step, "maximumStepTime", "maxStepTime", "maximumTime"),
                WarningTime = ReadOptionalString(step, "warningTime"),
                CommentZhCn = ReadOptionalString(step, "commentZhCn", "comment"),
                ActionsTitleZhCn = ReadOptionalString(step, "actionsTitleZhCn", "actionsTitle")
            };

            if (step["actions"] is JsonArray actions)
            {
                for (var i = 0; i < actions.Count; i++)
                    definition.Actions.Add(ReadAction(AsObject(actions[i], path + ".actions[" + i + "]"), path + ".actions[" + i + "]"));
            }

            if (step["supervision"] is JsonObject supervision)
                definition.Supervision = ReadCondition(supervision, path + ".supervision");
            if (step["interlock"] is JsonObject interlock)
                definition.Interlock = ReadCondition(interlock, path + ".interlock");

            return definition;
        }

        private static GraphActionDefinition ReadAction(JsonObject action, string path)
        {
            return new GraphActionDefinition
            {
                Qualifier = ReadOptionalString(action, "qualifier", "q").Trim(),
                Event = ReadOptionalString(action, "event").Trim(),
                Interlock = ReadBool(action, "interlock", false),
                Operand = ReadOptionalString(action, "operand", "symbol", "address"),
                Parameter = ReadOptionalString(action, "parameter", "param", "value")
            };
        }

        private static GraphTransitionDefinition ReadTransition(JsonObject transition, int index)
        {
            var path = "$.sequence.transitions[" + index + "]";
            var definition = new GraphTransitionDefinition
            {
                Number = ReadInt(transition, path + ".number", "number"),
                Name = ReadOptionalString(transition, "name"),
                IsMissing = ReadBool(transition, "isMissing", false),
                ProgrammingLanguage = ReadOptionalString(transition, "language", "programmingLanguage"),
                CommentZhCn = ReadOptionalString(transition, "commentZhCn", "comment")
            };

            var condition = transition["condition"] as JsonObject
                ?? transition["flgNet"] as JsonObject
                ?? transition["flgnet"] as JsonObject;
            if (condition != null)
                definition.Condition = ReadCondition(condition, path + ".condition");

            return definition;
        }

        private static GraphBranchDefinition ReadBranch(JsonObject branch, int index)
        {
            var path = "$.sequence.branches[" + index + "]";
            return new GraphBranchDefinition
            {
                Number = ReadInt(branch, path + ".number", "number"),
                Type = ReadOptionalString(branch, "type").Trim(),
                Cardinality = ReadOptionalInt(branch, "cardinality") ?? 2
            };
        }

        private static GraphConnectionDefinition ReadConnection(JsonObject connection, int index)
        {
            var path = "$.sequence.connections[" + index + "]";
            var from = connection["from"] as JsonObject
                ?? throw new ArgumentException("Missing required JSON object: " + path + ".from");
            var to = connection["to"] as JsonObject
                ?? throw new ArgumentException("Missing required JSON object: " + path + ".to");

            return new GraphConnectionDefinition
            {
                From = ReadNodeReference(from, path + ".from"),
                To = ReadNodeReference(to, path + ".to"),
                LinkType = ReadOptionalString(connection, "linkType", "link").Trim()
            };
        }

        private static GraphNodeReference ReadNodeReference(JsonObject node, string path)
        {
            return new GraphNodeReference
            {
                Kind = ReadString(node, path + ".kind", "kind", "type"),
                Number = ReadOptionalInt(node, "number", "no") ?? 0,
                In = ReadOptionalInt(node, "in"),
                Out = ReadOptionalInt(node, "out")
            };
        }

        private static GraphFlgNetDefinition ReadCondition(JsonObject condition, string path)
        {
            var definition = new GraphFlgNetDefinition();

            if (condition["parts"] is JsonArray parts)
            {
                for (var i = 0; i < parts.Count; i++)
                {
                    var part = AsObject(parts[i], path + ".parts[" + i + "]");
                    definition.Parts.Add(new GraphFlgNetPart
                    {
                        Name = ReadString(part, path + ".parts[" + i + "].name", "name"),
                        UId = ReadOptionalInt(part, "uid", "uId") ?? 0
                    });
                }
            }

            if (condition["accesses"] is JsonArray accesses)
            {
                for (var i = 0; i < accesses.Count; i++)
                {
                    var access = AsObject(accesses[i], path + ".accesses[" + i + "]");
                    var symbolPath = new List<string>();
                    if (access["symbolPath"] is JsonArray pathArray)
                        symbolPath.AddRange(pathArray.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)));
                    else
                    {
                        var symbol = ReadOptionalString(access, "symbol", "path", "plcTag");
                        if (!string.IsNullOrWhiteSpace(symbol))
                            symbolPath.AddRange(symbol.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries));
                    }

                    var scope = ReadOptionalString(access, "scope");
                    definition.Accesses.Add(new GraphFlgNetAccess
                    {
                        UId = ReadOptionalInt(access, "uid", "uId") ?? 0,
                        Scope = string.IsNullOrWhiteSpace(scope) ? "GlobalVariable" : scope,
                        SymbolPath = symbolPath,
                        ConstantType = ReadOptionalString(access, "constantType", "dataType", "datatype"),
                        ConstantValue = ReadOptionalString(access, "constantValue", "value")
                    });
                }
            }

            if (condition["wires"] is JsonArray wires)
            {
                for (var i = 0; i < wires.Count; i++)
                {
                    var wire = AsObject(wires[i], path + ".wires[" + i + "]");
                    var from = wire["from"] as JsonObject
                        ?? throw new ArgumentException("Missing required JSON object: " + path + ".wires[" + i + "].from");
                    var to = wire["to"] as JsonObject
                        ?? throw new ArgumentException("Missing required JSON object: " + path + ".wires[" + i + "].to");

                    definition.Wires.Add(new GraphFlgNetWire
                    {
                        UId = ReadOptionalInt(wire, "uid", "uId") ?? 0,
                        From = ReadFlgNetNode(from, path + ".wires[" + i + "].from"),
                        To = ReadFlgNetNode(to, path + ".wires[" + i + "].to")
                    });
                }
            }

            return definition;
        }

        private static GraphFlgNetNode ReadFlgNetNode(JsonObject node, string path)
        {
            return new GraphFlgNetNode
            {
                Kind = ReadString(node, path + ".kind", "kind", "type"),
                UId = ReadOptionalInt(node, "uid", "uId") ?? 0,
                Pin = ReadOptionalString(node, "pin", "name", "port")
            };
        }

        private static JsonObject BuildSchemaJson(GraphSchemaInfo schema)
        {
            var warnings = new JsonArray();
            foreach (var warning in schema.Warnings)
                warnings.Add(JsonValue.Create(warning));

            return new JsonObject
            {
                ["tiaMajor"] = schema.MajorVersion,
                ["template"] = schema.TemplateFile,
                ["exactTemplate"] = schema.ExactTemplate,
                ["verified"] = schema.Verified,
                ["provisional"] = schema.Provisional,
                ["warnings"] = warnings
            };
        }

        private static void ValidateDefinition(GraphBlockDefinition definition)
        {
            var sequence = definition.Sequence;
            if (sequence.Steps.Count == 0)
                throw new ArgumentException("GRAPH sequence requires at least one Step.");
            if (sequence.Steps.Count(s => s.Init) != 1)
                throw new ArgumentException("GRAPH sequence must contain exactly one Step with init=true.");
            if (sequence.Connections.Count == 0)
                throw new ArgumentException("GRAPH sequence requires at least one Connection.");

            EnsureUnique(sequence.Steps.Select(s => s.Number), "Step");
            EnsureUnique(sequence.Transitions.Select(t => t.Number), "Transition");
            EnsureUnique(sequence.Branches.Select(b => b.Number), "Branch");

            var stepNumbers = new HashSet<int>(sequence.Steps.Select(s => s.Number));
            var transitionNumbers = new HashSet<int>(sequence.Transitions.Select(t => t.Number));
            var branches = sequence.Branches.ToDictionary(b => b.Number);

            foreach (var step in sequence.Steps)
            {
                if (step.Number <= 0) throw new ArgumentException("GRAPH Step number must be greater than 0.");
                if (string.IsNullOrWhiteSpace(step.Name)) step.Name = "Step" + step.Number;
            }

            foreach (var transition in sequence.Transitions)
            {
                if (transition.Number <= 0) throw new ArgumentException("GRAPH Transition number must be greater than 0.");
                if (string.IsNullOrWhiteSpace(transition.Name)) transition.Name = "Trans" + transition.Number;

                var language = (transition.ProgrammingLanguage ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(language)) language = "LAD";
                if (language != "LAD" && language != "FBD")
                    throw new ArgumentException("GRAPH Transition supports LAD/FBD conditions only: " + transition.Name);
                transition.ProgrammingLanguage = language;

                // A transition without a condition makes the step advance unconditionally, which is
                // a generation error for a control sequence (the old auto TrCoil/Powerrail default
                // silently produced an always-true transition). Reject it instead.
                if (transition.Condition == null || transition.Condition.Parts.Count == 0)
                    throw new ArgumentException(
                        "GRAPH transition " + transition.Number
                        + " has no execution condition. Every transition must declare a condition "
                        + "(Contact / compare / TrCoil wired to real operands).");
            }

            foreach (var branch in sequence.Branches)
            {
                if (branch.Number <= 0) throw new ArgumentException("GRAPH Branch number must be greater than 0.");
                if (string.IsNullOrWhiteSpace(branch.Type)) branch.Type = "AltBegin";
                if (!BranchTypes.Contains(branch.Type))
                    throw new ArgumentException("Unsupported GRAPH Branch type: " + branch.Type);
                if (branch.Cardinality <= 0)
                    throw new ArgumentException("GRAPH Branch cardinality must be greater than 0: " + branch.Number);
            }

            // An empty sequence (no action anywhere) cannot drive anything; a wait step may stay
            // empty, but the sequence as a whole must contain at least one action.
            if (sequence.Steps.All(s => s.Actions.Count == 0))
                throw new ArgumentException(
                    "GRAPH sequence has no action. At least one step must run an action "
                    + "(N/S/R/D/L... on a real operand) while it is active.");

            foreach (var step in sequence.Steps)
            {
                ValidateActions(step);
                ValidateCondition(step.Supervision, "Step " + step.Number + " supervision");
                ValidateCondition(step.Interlock, "Step " + step.Number + " interlock");
            }
            foreach (var transition in sequence.Transitions)
                ValidateCondition(transition.Condition, "Transition " + transition.Number);

            foreach (var connection in sequence.Connections)
            {
                ValidateNodeReference(connection.From, stepNumbers, transitionNumbers, branches, "from", allowEnd: false);
                ValidateNodeReference(connection.To, stepNumbers, transitionNumbers, branches, "to", allowEnd: true);
                var link = (connection.LinkType ?? "").Trim();
                if (string.IsNullOrEmpty(link)) link = "Direct";
                if (link != "Direct" && link != "Jump")
                    throw new ArgumentException("GRAPH Connection LinkType supports Direct/Jump only: " + link);
                connection.LinkType = link;
            }

            ValidateReachability(sequence);
        }

        private static void ValidateActions(GraphStepDefinition step)
        {
            foreach (var action in step.Actions)
            {
                if (!Qualifiers.Contains(action.Qualifier ?? ""))
                    throw new ArgumentException("Unsupported GRAPH Action qualifier: " + action.Qualifier);
                if (!Events.Contains(action.Event ?? ""))
                    throw new ArgumentException("Unsupported GRAPH Action event: " + action.Event);
            }
        }

        private static void ValidateCondition(GraphFlgNetDefinition condition, string label)
        {
            if (condition == null || condition.Parts.Count == 0) return;
            // UId uniqueness / dangling wire checks happen in the FlgNet serializer.
            _ = FlgNetGraphXmlBuilder.BuildFlgNet(XNamespace.None, condition);
        }

        private static void ValidateNodeReference(
            GraphNodeReference node, HashSet<int> stepNumbers, HashSet<int> transitionNumbers,
            Dictionary<int, GraphBranchDefinition> branches, string side, bool allowEnd)
        {
            var kind = (node.Kind ?? "").Trim().ToLowerInvariant();
            switch (kind)
            {
                case "step":
                    if (!stepNumbers.Contains(node.Number))
                        throw new ArgumentException($"GRAPH connection {side} references a missing Step: {node.Number}");
                    return;
                case "transition":
                    if (!transitionNumbers.Contains(node.Number))
                        throw new ArgumentException($"GRAPH connection {side} references a missing Transition: {node.Number}");
                    return;
                case "branch":
                    if (!branches.TryGetValue(node.Number, out var branch))
                        throw new ArgumentException($"GRAPH connection {side} references a missing Branch: {node.Number}");
                    if (side == "from")
                    {
                        if (!node.Out.HasValue)
                            throw new ArgumentException("GRAPH branch as a source requires 'out'.");
                        if (node.Out.Value < 0 || node.Out.Value >= branch.Cardinality)
                            throw new ArgumentException($"GRAPH branch {branch.Number} 'out' is out of range: {node.Out}");
                    }
                    else
                    {
                        if (!node.In.HasValue)
                            throw new ArgumentException("GRAPH branch as a target requires 'in'.");
                        if (node.In.Value != 0)
                            throw new ArgumentException("GRAPH branch 'in' currently supports 0 only.");
                    }
                    return;
                case "end":
                    if (!allowEnd)
                        throw new ArgumentException("GRAPH End is allowed only on the 'to' side of a connection.");
                    return;
                default:
                    throw new ArgumentException("Unsupported GRAPH connection endpoint kind: " + node.Kind);
            }
        }

        private static void ValidateReachability(GraphSequenceDefinition sequence)
        {
            var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void AddEdge(string from, string to)
            {
                if (!adjacency.TryGetValue(from, out var list))
                {
                    list = new List<string>();
                    adjacency[from] = list;
                }
                list.Add(to);
            }

            foreach (var connection in sequence.Connections)
                AddEdge(NodeKey(connection.From), NodeKey(connection.To));

            var init = sequence.Steps.First(s => s.Init);
            var visited = new HashSet<string>(StringComparer.Ordinal) { NodeKey(new GraphNodeReference { Kind = "step", Number = init.Number }) };
            var queue = new Queue<string>(visited);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out var next)) continue;
                foreach (var node in next)
                {
                    if (visited.Add(node)) queue.Enqueue(node);
                }
            }

            foreach (var step in sequence.Steps)
            {
                if (!visited.Contains(NodeKey(new GraphNodeReference { Kind = "step", Number = step.Number })))
                    throw new ArgumentException("GRAPH Step is unreachable from the Init step: " + step.Number);
            }
        }

        private static string NodeKey(GraphNodeReference node)
        {
            var kind = (node.Kind ?? "").Trim().ToLowerInvariant();
            if (kind == "end") return "end";
            return kind + ":" + node.Number;
        }

        private static void EnsureUnique(IEnumerable<int> numbers, string label)
        {
            var duplicates = numbers.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (duplicates.Length > 0)
                throw new ArgumentException($"Duplicate GRAPH {label} number: {string.Join(", ", duplicates)}");
        }

        private static JsonObject RunSelfCheck(GraphBlockDefinition definition, string xml)
        {
            var parsed = GraphXmlParser.Parse(xml);
            var initCount = parsed.Steps.Count(s => s.Init);

            if (parsed.Steps.Count != definition.Sequence.Steps.Count ||
                parsed.Transitions.Count != definition.Sequence.Transitions.Count ||
                parsed.Branches.Count != definition.Sequence.Branches.Count ||
                parsed.Connections.Count != definition.Sequence.Connections.Count ||
                initCount != 1)
            {
                throw new InvalidOperationException(
                    $"GRAPH re-read mismatch after generation: steps {parsed.Steps.Count}/{definition.Sequence.Steps.Count}, " +
                    $"transitions {parsed.Transitions.Count}/{definition.Sequence.Transitions.Count}, " +
                    $"branches {parsed.Branches.Count}/{definition.Sequence.Branches.Count}, " +
                    $"connections {parsed.Connections.Count}/{definition.Sequence.Connections.Count}, init={initCount}.");
            }

            return new JsonObject
            {
                ["parser"] = "GraphXmlParser",
                ["steps"] = parsed.Steps.Count,
                ["transitions"] = parsed.Transitions.Count,
                ["branches"] = parsed.Branches.Count,
                ["connections"] = parsed.Connections.Count,
                ["ok"] = true
            };
        }

        private static PlcBlockMemberDefinition[] ReadMembers(JsonObject root, string defaultArrayName, string defaultPath, params string[] aliases)
        {
            var names = new[] { defaultArrayName }.Concat(aliases ?? Array.Empty<string>()).ToArray();
            var foundName = names.FirstOrDefault(name => root[name] is JsonArray);
            if (foundName == null) return Array.Empty<PlcBlockMemberDefinition>();

            var array = (JsonArray)root[foundName];
            var path = "$." + foundName;
            return array
                .Select((x, i) =>
                {
                    var member = AsObject(x, path + "[" + i + "]");
                    return new PlcBlockMemberDefinition(
                        ReadString(member, path + "[" + i + "].name", "name"),
                        ReadString(member, path + "[" + i + "].datatype", "datatype", "dataType"),
                        ReadOptionalString(member, "commentZhCn", "comment", "commentZh"));
                })
                .ToArray();
        }

        private static JsonObject ParseObject(string json, string path)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("Missing required JSON object: " + path);

            return JsonNode.Parse(json) as JsonObject
                ?? throw new ArgumentException("Expected JSON object at " + path);
        }

        private static JsonObject AsObject(JsonNode node, string path)
        {
            return node as JsonObject ?? throw new ArgumentException("Expected JSON object at " + path);
        }

        private static JsonArray ReadArray(JsonObject root, string name, string path)
        {
            return root[name] as JsonArray ?? throw new ArgumentException("Missing required JSON array: " + path);
        }

        private static string ReadString(JsonObject root, string path, params string[] names)
        {
            var value = ReadOptionalString(root, names);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Missing required JSON string: " + path);
            return value;
        }

        private static string ReadOptionalString(JsonObject root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetPropertyValue(name, out var node) && node != null)
                    return node.ToString();
            }
            return "";
        }

        private static int ReadInt(JsonObject root, string path, params string[] names)
        {
            var value = ReadOptionalInt(root, names);
            if (!value.HasValue)
                throw new ArgumentException("Missing required JSON integer: " + path);
            return value.Value;
        }

        private static int? ReadOptionalInt(JsonObject root, params string[] names)
        {
            foreach (var name in names)
            {
                if (!root.TryGetPropertyValue(name, out var node) || node == null) continue;
                if (node is JsonValue value && value.TryGetValue<int>(out var intValue)) return intValue;
                if (int.TryParse(node.ToString(), out var parsed)) return parsed;
            }
            return null;
        }

        private static bool ReadBool(JsonObject root, string name, bool defaultValue)
        {
            if (!root.TryGetPropertyValue(name, out var node) || node == null) return defaultValue;
            if (node is JsonValue value && value.TryGetValue<bool>(out var boolValue)) return boolValue;
            return bool.TryParse(node.ToString(), out var parsed) ? parsed : defaultValue;
        }
    }
}
