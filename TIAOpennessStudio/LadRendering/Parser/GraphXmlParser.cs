using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace TIAOpennessStudio.LadRendering.Parser
{
    /// <summary>
    /// Parses TIA Portal GRAPH (S7-GRAPH) exported XML and extracts the step, transition, branch, and connection structure
    /// </summary>
    public class GraphSequence
    {
        public string Title { get; set; } = "";
        public string Comment { get; set; } = "";
        public List<GraphStep> Steps { get; set; } = new();
        public List<GraphTransition> Transitions { get; set; } = new();
        public List<GraphBranch> Branches { get; set; } = new();
        public List<GraphConnection> Connections { get; set; } = new();
    }

    public class GraphStep
    {
        public int Number { get; set; }
        public string Name { get; set; } = "";
        public bool Init { get; set; }
        public string MaxStepTime { get; set; } = "";
        public string WarningTime { get; set; } = "";
        public string ActionsTitle { get; set; } = "";
        /// <summary>Step action rows (parsed from Actions/FlgNet)</summary>
        public List<GraphActionRow> Actions { get; set; } = new();
        public List<GraphLogicBlock> Supervisions { get; set; } = new();
        public List<GraphLogicBlock> Interlocks { get; set; } = new();
    }

    /// <summary>
    /// One action row in a step (corresponds to a row in TIA Portal GRAPH: interlock | event | qualifier | action | symbol | address)
    /// </summary>
    public class GraphActionRow
    {
        /// <summary>Qualifier: N (non-stored), D (delay), S (set), R (reset), L (time-limited), P (positive pulse), etc.</summary>
        public string Qualifier { get; set; } = "";
        /// <summary>Action operand / symbol name</summary>
        public string Operand { get; set; } = "";
        /// <summary>Absolute operand address (e.g. %Q0.3, %M130.1)</summary>
        public string Address { get; set; } = "";
        /// <summary>Timer/parameter value (e.g. 5000 for a delay)</summary>
        public string Parameter { get; set; } = "";
        /// <summary>Event condition (if any)</summary>
        public string Event { get; set; } = "";
        /// <summary>Whether an interlock condition exists</summary>
        public bool HasInterlock { get; set; }
    }

    public class GraphTransition
    {
        public int Number { get; set; }
        public string Name { get; set; } = "";
        public bool IsMissing { get; set; }
        public string ProgrammingLanguage { get; set; } = "";
        public string FlgNetXml { get; set; } = "";
    }

    public class GraphBranch
    {
        public int Number { get; set; }
        public string Type { get; set; } = "";        // AltBegin, AltEnd, ParBegin, ParEnd
        public int Cardinality { get; set; }
    }

    public class GraphConnection
    {
        public string FromType { get; set; } = "";    // StepRef, TransitionRef, BranchRef
        public int FromNumber { get; set; }
        public int FromIn { get; set; } = -1;          // BranchRef In index
        public int FromOut { get; set; } = -1;         // BranchRef Out index
        public string ToType { get; set; } = "";      // StepRef, TransitionRef, BranchRef
        public int ToNumber { get; set; }
        public int ToIn { get; set; } = -1;            // BranchRef In index
        public int ToOut { get; set; } = -1;           // BranchRef Out index
        public string LinkType { get; set; } = "Direct";
    }

    public class GraphLogicBlock
    {
        public string ProgrammingLanguage { get; set; } = "";
        public string FlgNetXml { get; set; } = "";
    }

    public static class GraphXmlParser
    {
        public static GraphSequence Parse(string xmlContent)
        {
            var seq = new GraphSequence();
            if (string.IsNullOrWhiteSpace(xmlContent)) return seq;

            var doc = XDocument.Parse(xmlContent);
            var sequence = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Sequence");
            if (sequence == null) return seq;

            // Get the Sequence namespace (Graph namespace)
            var ns = sequence.Name.Namespace;

            // Title and comment
            seq.Title = GetMultiLangText(sequence.Element(ns + "Title"));
            seq.Comment = GetMultiLangText(sequence.Element(ns + "Comment"));

            // Parse Steps
            var stepsEl = sequence.Element(ns + "Steps");
            if (stepsEl != null)
            {
                foreach (var stepEl in stepsEl.Elements(ns + "Step"))
                {
                    var step = new GraphStep
                    {
                        Number = ParseInt(stepEl.Attribute("Number")?.Value),
                        Name = stepEl.Attribute("Name")?.Value ?? "",
                        Init = ParseBool(stepEl.Attribute("Init")?.Value),
                        MaxStepTime = stepEl.Attribute("MaximumStepTime")?.Value ?? "",
                        WarningTime = stepEl.Attribute("WarningTime")?.Value ?? ""
                    };

                    // Action title + action row parsing
                    var actions = stepEl.Element(ns + "Actions");
                    if (actions != null)
                    {
                        step.ActionsTitle = GetMultiLangText(actions.Element(ns + "Title"));

                        // Parse the <Action Qualifier="..."><Token Text="..." />...</Action> format
                        foreach (var actionEl in actions.Elements(ns + "Action"))
                        {
                            var qualifier = actionEl.Attribute("Qualifier")?.Value ?? "N";
                            var tokens = actionEl.Elements(ns + "Token")
                                .Select(t => t.Attribute("Text")?.Value ?? "")
                                .ToList();

                            // Concatenate Token text, stripping line breaks
                            var fullText = string.Join("", tokens).Replace("\n", "").Replace("\r", "").Trim();
                            if (fullText.Length == 0) continue;

                            // Parse the Token sequence into operand and parameter
                            // Typical formats: "variable", parameterValue  or  "variable"
                            var row = new GraphActionRow { Qualifier = qualifier };
                            ParseActionTokens(fullText, row);
                            step.Actions.Add(row);
                        }

                        // If there is no <Action> child element, try parsing from FlgNet
                        if (step.Actions.Count == 0)
                        {
                            var actFlgNet = actions.Element(ns + "FlgNet");
                            if (actFlgNet != null)
                            {
                                step.Actions = ParseActionRows(actFlgNet);
                            }
                        }
                    }

                    // Supervision conditions
                    var supervisions = stepEl.Element(ns + "Supervisions");
                    if (supervisions != null)
                    {
                        foreach (var sv in supervisions.Elements(ns + "Supervision"))
                        {
                            var flgNet = sv.Element(ns + "FlgNet");
                            step.Supervisions.Add(new GraphLogicBlock
                            {
                                ProgrammingLanguage = sv.Attribute("ProgrammingLanguage")?.Value ?? "LAD",
                                FlgNetXml = flgNet?.ToString() ?? ""
                            });
                        }
                    }

                    // Interlock conditions
                    var interlocks = stepEl.Element(ns + "Interlocks");
                    if (interlocks != null)
                    {
                        foreach (var il in interlocks.Elements(ns + "Interlock"))
                        {
                            var flgNet = il.Element(ns + "FlgNet");
                            step.Interlocks.Add(new GraphLogicBlock
                            {
                                ProgrammingLanguage = il.Attribute("ProgrammingLanguage")?.Value ?? "LAD",
                                FlgNetXml = flgNet?.ToString() ?? ""
                            });
                        }
                    }

                    seq.Steps.Add(step);
                }
            }

            // Parse Transitions
            var transitionsEl = sequence.Element(ns + "Transitions");
            if (transitionsEl != null)
            {
                foreach (var trEl in transitionsEl.Elements(ns + "Transition"))
                {
                    var flgNet = trEl.Element(ns + "FlgNet");
                    seq.Transitions.Add(new GraphTransition
                    {
                        Number = ParseInt(trEl.Attribute("Number")?.Value),
                        Name = trEl.Attribute("Name")?.Value ?? "",
                        IsMissing = ParseBool(trEl.Attribute("IsMissing")?.Value),
                        ProgrammingLanguage = trEl.Attribute("ProgrammingLanguage")?.Value ?? "LAD",
                        FlgNetXml = flgNet?.ToString() ?? ""
                    });
                }
            }

            // Parse Branches
            var branchesEl = sequence.Element(ns + "Branches");
            if (branchesEl != null)
            {
                foreach (var brEl in branchesEl.Elements(ns + "Branch"))
                {
                    seq.Branches.Add(new GraphBranch
                    {
                        Number = ParseInt(brEl.Attribute("Number")?.Value),
                        Type = brEl.Attribute("Type")?.Value ?? "",
                        Cardinality = ParseInt(brEl.Attribute("Cardinality")?.Value)
                    });
                }
            }

            // Parse Connections
            var connectionsEl = sequence.Element(ns + "Connections");
            if (connectionsEl != null)
            {
                foreach (var connEl in connectionsEl.Elements(ns + "Connection"))
                {
                    var fromNode = connEl.Element(ns + "NodeFrom");
                    var toNode = connEl.Element(ns + "NodeTo");
                    if (fromNode == null || toNode == null) continue;

                    var conn = new GraphConnection
                    {
                        LinkType = connEl.Element(ns + "LinkType")?.Value ?? "Direct"
                    };

                    ParseNodeRef(fromNode, conn, ns, isFrom: true);
                    ParseNodeRef(toNode, conn, ns, isFrom: false);

                    seq.Connections.Add(conn);
                }
            }

            return seq;
        }

        /// <summary>
        /// Parses action Token text into operand and parameter.
        /// Typical formats:
        ///   "variable"              → Operand="variable"
        ///   "variable", 5000        → Operand="variable", Parameter="5000"
        ///   "variable"."submember"  → Operand="variable.submember"
        /// </summary>
        private static void ParseActionTokens(string text, GraphActionRow row)
        {
            // Full text with the outer quotes removed
            var parts = text.Split(new[] { ',' }, 2);

            // First part: the operand (possibly quoted)
            var operand = parts[0].Trim().Trim('"', '"', '"');
            row.Operand = operand;

            // Second part (if present): parameter/timer value
            if (parts.Length > 1)
            {
                row.Parameter = parts[1].Trim();
            }
        }

        /// <summary>
        /// Parses action rows from FlgNet: coil type→qualifier, Access→operand/address
        /// </summary>
        private static List<GraphActionRow> ParseActionRows(XElement flgNet)
        {
            var rows = new List<GraphActionRow>();
            if (flgNet == null) return rows;

            var fns = flgNet.Name.Namespace;

            // Collect Parts (coils and function blocks)
            var parts = flgNet.Element(fns + "Parts");
            if (parts == null) return rows;

            // Collect Access (operands)
            var accessMap = new Dictionary<string, (string Symbol, string Address, string Constant)>();
            foreach (var acc in parts.Elements(fns + "Access"))
            {
                var uid = acc.Attribute("UId")?.Value ?? "";
                var scope = acc.Attribute("Scope")?.Value ?? "";
                string symbol = "";
                string address = "";
                string constant = "";

                if (scope == "TypedConstant" || scope == "LiteralConstant")
                {
                    constant = acc.Element(fns + "Constant")?.Element(fns + "ConstantValue")?.Value ?? "";
                }
                else
                {
                    // GlobalVariable / LocalVariable
                    var symEl = acc.Element(fns + "Symbol");
                    if (symEl != null)
                    {
                        var components = symEl.Elements(fns + "Component")
                            .Select(c => c.Attribute("Name")?.Value ?? "")
                            .Where(n => n.Length > 0);
                        symbol = string.Join(".", components);
                    }
                    address = acc.Element(fns + "Address")?.Attribute("BitOffset") != null
                        ? FormatAddress(acc.Element(fns + "Address"))
                        : "";
                }

                if (uid.Length > 0)
                    accessMap[uid] = (symbol, address, constant);
            }

            // Collect Wires (connection info)
            var wires = flgNet.Element(fns + "Wires");
            var wireConnections = new Dictionary<string, List<(string TargetUid, string TargetPin)>>();
            if (wires != null)
            {
                foreach (var wire in wires.Elements(fns + "Wire"))
                {
                    var conns = wire.Elements().Where(e => e.Name.LocalName == "NameCon" || e.Name.LocalName == "IdentCon").ToList();
                    // IdentCon represents an Access→Part pin connection
                    foreach (var identCon in wire.Elements(fns + "IdentCon"))
                    {
                        var accUid = identCon.Attribute("UId")?.Value ?? "";
                        foreach (var nameCon in wire.Elements(fns + "NameCon"))
                        {
                            var partUid = nameCon.Attribute("UId")?.Value ?? "";
                            var pin = nameCon.Attribute("Name")?.Value ?? "";
                            if (accUid.Length > 0 && partUid.Length > 0)
                            {
                                if (!wireConnections.ContainsKey(partUid))
                                    wireConnections[partUid] = new List<(string, string)>();
                                wireConnections[partUid].Add((accUid, pin));
                            }
                        }
                    }
                }
            }

            // Parse each Part as one action row
            foreach (var part in parts.Elements(fns + "Part"))
            {
                var partName = part.Attribute("Name")?.Value ?? "";
                var partUid = part.Attribute("UId")?.Value ?? "";

                // Only handle coil types (action outputs)
                string qualifier = MapPartToQualifier(partName);
                if (qualifier.Length == 0) continue;

                var row = new GraphActionRow { Qualifier = qualifier };

                // Find the operand connected to this coil
                if (wireConnections.TryGetValue(partUid, out var connectedAccesses))
                {
                    foreach (var (accUid, pin) in connectedAccesses)
                    {
                        if (accessMap.TryGetValue(accUid, out var acc))
                        {
                            if (pin.Equals("operand", StringComparison.OrdinalIgnoreCase) ||
                                pin.Equals("in", StringComparison.OrdinalIgnoreCase) ||
                                pin.Equals("in1", StringComparison.OrdinalIgnoreCase))
                            {
                                if (acc.Symbol.Length > 0)
                                    row.Operand = acc.Symbol;
                                if (acc.Address.Length > 0)
                                    row.Address = acc.Address;
                            }
                            else if (pin.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
                                     pin.Equals("duration", StringComparison.OrdinalIgnoreCase) ||
                                     pin.Equals("pt", StringComparison.OrdinalIgnoreCase))
                            {
                                row.Parameter = acc.Constant.Length > 0 ? acc.Constant : acc.Symbol;
                            }
                        }
                    }
                }

                // If not found via wire, try extracting from the Part's TemplateValue
                if (row.Operand.Length == 0)
                {
                    var templateVal = part.Element(fns + "TemplateValue")?.Value ??
                                      part.Attribute("Operand")?.Value ?? "";
                    if (templateVal.Length > 0)
                        row.Operand = templateVal;
                }

                rows.Add(row);
            }

            // If no rows were parsed but there are Access operands, create simple rows
            if (rows.Count == 0 && accessMap.Count > 0)
            {
                foreach (var kvp in accessMap)
                {
                    var (sym, addr, constant) = kvp.Value;
                    if (sym.Length > 0 || constant.Length > 0)
                    {
                        rows.Add(new GraphActionRow
                        {
                            Qualifier = "N",
                            Operand = sym.Length > 0 ? sym : constant,
                            Address = addr
                        });
                    }
                }
            }

            return rows;
        }

        /// <summary>Maps a Part Name to an IEC 61131-3 action qualifier</summary>
        private static string MapPartToQualifier(string partName)
        {
            var name = (partName ?? "").ToLowerInvariant();
            if (name == "coil" || name == "ncoil") return "N";
            if (name == "scoil" || name == "set_coil") return "S";
            if (name == "rcoil" || name == "reset_coil") return "R";
            if (name == "dcoil" || name == "delay_coil") return "D";
            if (name == "lcoil" || name == "time_limited") return "L";
            if (name == "pcoil" || name == "pulse_coil") return "P";
            if (name == "sdcoil") return "SD";
            if (name == "dscoil") return "DS";
            if (name == "slcoil") return "SL";
            if (name == "svcoil") return "SV";  // Supervision coil
            if (name == "ilcoil") return "IL";  // Interlock coil
            // Call blocks (FC/FB) as actions
            if (name.StartsWith("call") || name.Contains("call")) return "N";
            return "";
        }

        /// <summary>Formats an address as %Qx.y / %Mx.y</summary>
        private static string FormatAddress(XElement addrEl)
        {
            if (addrEl == null) return "";
            var area = addrEl.Attribute("Area")?.Value ?? "";
            var bitOffset = addrEl.Attribute("BitOffset")?.Value ?? "";
            if (area.Length == 0 || bitOffset.Length == 0) return "";

            if (int.TryParse(bitOffset, out int bit))
            {
                int byteNum = bit / 8;
                int bitNum = bit % 8;
                string prefix = area switch
                {
                    "Output" => "%Q",
                    "Input" => "%I",
                    "Merker" => "%M",
                    "Memory" => "%M",
                    _ => "%" + area[0]
                };
                return $"{prefix}{byteNum}.{bitNum}";
            }
            return "";
        }

        private static void ParseNodeRef(XElement node, GraphConnection conn, XNamespace ns, bool isFrom)
        {
            var stepRef = node.Element(ns + "StepRef");
            var trRef = node.Element(ns + "TransitionRef");
            var brRef = node.Element(ns + "BranchRef");
            var endRef = node.Element(ns + "EndConnection");

            if (endRef != null)
            {
                // Sequence end: only meaningful as a target. The generator treats "End" as a sink.
                if (!isFrom) { conn.ToType = "EndConnection"; conn.ToNumber = 0; }
            }
            else if (stepRef != null)
            {
                if (isFrom) { conn.FromType = "StepRef"; conn.FromNumber = ParseInt(stepRef.Attribute("Number")?.Value); }
                else { conn.ToType = "StepRef"; conn.ToNumber = ParseInt(stepRef.Attribute("Number")?.Value); }
            }
            else if (trRef != null)
            {
                if (isFrom) { conn.FromType = "TransitionRef"; conn.FromNumber = ParseInt(trRef.Attribute("Number")?.Value); }
                else { conn.ToType = "TransitionRef"; conn.ToNumber = ParseInt(trRef.Attribute("Number")?.Value); }
            }
            else if (brRef != null)
            {
                if (isFrom)
                {
                    conn.FromType = "BranchRef";
                    conn.FromNumber = ParseInt(brRef.Attribute("Number")?.Value);
                    conn.FromIn = ParseInt(brRef.Attribute("In")?.Value, -1);
                    conn.FromOut = ParseInt(brRef.Attribute("Out")?.Value, -1);
                }
                else
                {
                    conn.ToType = "BranchRef";
                    conn.ToNumber = ParseInt(brRef.Attribute("Number")?.Value);
                    conn.ToIn = ParseInt(brRef.Attribute("In")?.Value, -1);
                    conn.ToOut = ParseInt(brRef.Attribute("Out")?.Value, -1);
                }
            }
        }

        private static string GetMultiLangText(XElement parent)
        {
            if (parent == null) return "";
            var ns = parent.Name.Namespace;
            var mt = parent.Element(ns + "MultiLanguageText");
            return mt?.Value ?? "";
        }

        private static int ParseInt(string s, int defaultValue = 0)
        {
            return int.TryParse(s, out var n) ? n : defaultValue;
        }

        private static bool ParseBool(string s)
        {
            return bool.TryParse(s, out var b) && b;
        }
    }
}