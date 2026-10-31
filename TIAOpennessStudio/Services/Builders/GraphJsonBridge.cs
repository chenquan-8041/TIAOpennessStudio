using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TIAOpennessStudio.LadRendering.Parser;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// The single GRAPH JSON contract shared by all three paths:
    ///   - read:   TiaXmlAgentReferenceBuilder renders an exported block through this shape,
    ///   - write:  GraphBlockToolJson consumes exactly this shape (build_graph_fb),
    ///   - RAG:    PlcNetworkFactExtractor stores this shape as the network canonical JSON.
    ///
    /// Deliberately independent of WPF so the knowledge worker can link it.
    /// </summary>
    public static class GraphJsonBridge
    {
        /// <summary>Sequence object accepted by build_graph_fb (steps / transitions / branches / connections).</summary>
        public static JsonObject SequenceToJson(GraphSequence sequence)
        {
            if (sequence == null)
                return new JsonObject();

            var steps = new JsonArray();
            foreach (var step in sequence.Steps)
            {
                var json = new JsonObject
                {
                    ["number"] = step.Number,
                    ["name"] = step.Name ?? "",
                    ["init"] = step.Init
                };
                if (!string.IsNullOrWhiteSpace(step.MaxStepTime)) json["maxStepTime"] = step.MaxStepTime;
                if (!string.IsNullOrWhiteSpace(step.WarningTime)) json["warningTime"] = step.WarningTime;
                if (!string.IsNullOrWhiteSpace(step.ActionsTitle)) json["actionsTitleZhCn"] = step.ActionsTitle;

                var actions = new JsonArray();
                foreach (var action in step.Actions)
                    actions.Add(ActionToJson(action));
                json["actions"] = actions;

                var supervision = ConditionToJson(step.Supervisions.FirstOrDefault()?.FlgNetXml);
                if (supervision != null) json["supervision"] = supervision;

                var interlock = ConditionToJson(step.Interlocks.FirstOrDefault()?.FlgNetXml);
                if (interlock != null) json["interlock"] = interlock;

                steps.Add(json);
            }

            var transitions = new JsonArray();
            foreach (var transition in sequence.Transitions)
            {
                var json = new JsonObject
                {
                    ["number"] = transition.Number,
                    ["name"] = transition.Name ?? "",
                    ["isMissing"] = transition.IsMissing,
                    ["language"] = string.IsNullOrWhiteSpace(transition.ProgrammingLanguage)
                        ? "LAD"
                        : transition.ProgrammingLanguage
                };
                var condition = ConditionToJson(transition.FlgNetXml);
                if (condition != null) json["condition"] = condition;
                transitions.Add(json);
            }

            var branches = new JsonArray();
            foreach (var branch in sequence.Branches)
            {
                branches.Add(new JsonObject
                {
                    ["number"] = branch.Number,
                    ["type"] = string.IsNullOrWhiteSpace(branch.Type) ? "AltBegin" : branch.Type,
                    ["cardinality"] = branch.Cardinality
                });
            }

            var connections = new JsonArray();
            foreach (var connection in sequence.Connections)
            {
                var from = NodeToJson(connection.FromType, connection.FromNumber, connection.FromIn, connection.FromOut);
                if (from == null) continue;
                var to = NodeToJson(connection.ToType, connection.ToNumber, connection.ToIn, connection.ToOut);
                if (to == null) continue;

                connections.Add(new JsonObject
                {
                    ["from"] = from,
                    ["to"] = to,
                    ["linkType"] = string.IsNullOrWhiteSpace(connection.LinkType) ? "Direct" : connection.LinkType
                });
            }

            return new JsonObject
            {
                ["titleZhCn"] = sequence.Title ?? "",
                ["commentZhCn"] = sequence.Comment ?? "",
                ["steps"] = steps,
                ["transitions"] = transitions,
                ["branches"] = branches,
                ["connections"] = connections
            };
        }

        /// <summary>
        /// FlgNet XML (transition / supervision / interlock condition) to the write contract
        /// { parts[], accesses[], wires[] }. Returns null when the fragment is empty or unparsable.
        /// </summary>
        public static JsonObject ConditionToJson(string flgNetXml)
        {
            if (string.IsNullOrWhiteSpace(flgNetXml))
                return null;

            XElement root;
            try { root = XElement.Parse(flgNetXml); }
            catch { return null; }

            var partsContainer = Child(root, "Parts");
            if (partsContainer == null) return null;

            var parts = new JsonArray();
            var accesses = new JsonArray();
            foreach (var element in partsContainer.Elements())
            {
                var local = element.Name.LocalName;
                var uid = IntAttr(element, "UId");
                if (string.Equals(local, "Access", StringComparison.OrdinalIgnoreCase))
                {
                    accesses.Add(AccessToJson(element, uid));
                    continue;
                }

                // Part and Call both travel as a named part; the write side re-emits a <Part>.
                var name = (string)element.Attribute("Name") ?? (string)element.Attribute("CallType") ?? local;
                parts.Add(new JsonObject
                {
                    ["name"] = name ?? "",
                    ["uid"] = uid
                });
            }

            var wiresContainer = Child(root, "Wires");
            var wires = new JsonArray();
            if (wiresContainer != null)
            {
                foreach (var wire in wiresContainer.Elements())
                {
                    var endpoints = wire.Elements().ToList();
                    if (endpoints.Count < 2) continue;
                    var from = EndpointToJson(endpoints[0]);
                    var to = EndpointToJson(endpoints[1]);
                    if (from == null || to == null) continue;

                    wires.Add(new JsonObject
                    {
                        ["uid"] = IntAttr(wire, "UId"),
                        ["from"] = from,
                        ["to"] = to
                    });
                }
            }

            return new JsonObject
            {
                ["parts"] = parts,
                ["accesses"] = accesses,
                ["wires"] = wires
            };
        }

        /// <summary>Action row: operand is re-quoted because build_graph_fb expects the TIA text form.</summary>
        public static JsonObject ActionToJson(GraphActionRow action)
        {
            var operand = action?.Operand ?? "";
            var json = new JsonObject
            {
                ["qualifier"] = string.IsNullOrWhiteSpace(action?.Qualifier) ? "N" : action.Qualifier,
                ["event"] = action?.Event ?? "",
                ["interlock"] = action?.HasInterlock ?? false,
                ["operand"] = QuoteOperand(operand),
                ["parameter"] = action?.Parameter ?? ""
            };
            if (!string.IsNullOrWhiteSpace(action?.Address))
                json["address"] = action.Address; // informational; build_graph_fb ignores it
            return json;
        }

        /// <summary>Variable names referenced by a condition (used by RAG to index GRAPH tags).</summary>
        public static IReadOnlyList<string> ConditionVariables(string flgNetXml)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(flgNetXml)) return result;
            XElement root;
            try { root = XElement.Parse(flgNetXml); }
            catch { return result; }

            foreach (var component in root.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "Component", StringComparison.OrdinalIgnoreCase)))
            {
                var name = (string)component.Attribute("Name");
                if (!string.IsNullOrWhiteSpace(name) && !result.Contains(name))
                    result.Add(name);
            }
            return result;
        }

        private static JsonObject AccessToJson(XElement access, int uid)
        {
            var scope = (string)access.Attribute("Scope") ?? "GlobalVariable";
            var json = new JsonObject
            {
                ["uid"] = uid,
                ["scope"] = scope
            };

            if (string.Equals(scope, "LiteralConstant", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scope, "TypedConstant", StringComparison.OrdinalIgnoreCase))
            {
                var constant = Child(access, "Constant");
                if (constant != null)
                {
                    json["constantType"] = Child(constant, "ConstantType")?.Value ?? "Bool";
                    json["constantValue"] = Child(constant, "ConstantValue")?.Value ?? "";
                }
                return json;
            }

            var symbol = Child(access, "Symbol");
            var path = new JsonArray();
            if (symbol != null)
            {
                foreach (var component in symbol.Elements()
                    .Where(e => string.Equals(e.Name.LocalName, "Component", StringComparison.OrdinalIgnoreCase)))
                {
                    var name = (string)component.Attribute("Name");
                    if (!string.IsNullOrWhiteSpace(name)) path.Add(name);
                }
            }
            if (path.Count > 0) json["symbolPath"] = path;

            var address = Child(access, "Address");
            if (address != null)
                json["address"] = address.ToString(SaveOptions.DisableFormatting);
            return json;
        }

        private static JsonObject EndpointToJson(XElement element)
        {
            var local = element?.Name.LocalName ?? "";
            switch (local.ToLowerInvariant())
            {
                case "powerrail":
                    return new JsonObject { ["kind"] = "Powerrail" };
                case "namecon":
                    return new JsonObject
                    {
                        ["kind"] = "NameCon",
                        ["uid"] = IntAttr(element, "UId"),
                        ["pin"] = (string)element.Attribute("Name") ?? ""
                    };
                case "identcon":
                    return new JsonObject { ["kind"] = "IdentCon", ["uid"] = IntAttr(element, "UId") };
                case "opencon":
                    return new JsonObject { ["kind"] = "OpenCon", ["uid"] = IntAttr(element, "UId") };
                default:
                    return null;
            }
        }

        private static JsonObject NodeToJson(string type, int number, int inIndex, int outIndex)
        {
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "stepref":
                case "step":
                {
                    var node = new JsonObject { ["kind"] = "Step", ["number"] = number };
                    return node;
                }
                case "transitionref":
                case "transition":
                {
                    var node = new JsonObject { ["kind"] = "Transition", ["number"] = number };
                    return node;
                }
                case "branchref":
                case "branch":
                {
                    var node = new JsonObject { ["kind"] = "Branch", ["number"] = number };
                    if (inIndex >= 0) node["in"] = inIndex;
                    if (outIndex >= 0) node["out"] = outIndex;
                    return node;
                }
                case "endconnection":
                case "end":
                    return new JsonObject { ["kind"] = "End" };
                default:
                    return null;
            }
        }

        private static string QuoteOperand(string operand)
        {
            var value = (operand ?? "").Trim();
            if (value.Length == 0) return "";
            if (value.StartsWith("\"", StringComparison.Ordinal)) return value;
            // Absolute addresses (%I0.0/%Q0.0/%M10.0) are already valid TIA text without quotes.
            if (value.StartsWith("%", StringComparison.Ordinal)) return value;
            return "\"" + value + "\"";
        }

        private static XElement Child(XElement parent, string localName)
        {
            return parent?.Elements().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        }

        private static int IntAttr(XElement element, string name)
        {
            var value = (string)element?.Attribute(name);
            return int.TryParse(value, out var number) ? number : 0;
        }
    }
}
