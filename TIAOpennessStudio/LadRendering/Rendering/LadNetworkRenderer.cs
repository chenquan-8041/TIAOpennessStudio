using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.LadRendering.Rendering
{
    /// <summary>
    /// Read-only LAD renderer (deterministic geometric layout).
    ///
    /// Design principles:
    /// 1. The renderer fully owns all coordinates — every pin anchor, every element rectangle and
    ///    every wire bend point is computed by this class; element content is drawn on the same
    ///    coordinates, so wires always land exactly on pins and there is no mismatch from
    ///    "XAML auto-layout vs. guessing coordinates by formula".
    /// 2. No information loss — every operand's full symbolic name, every pin name and every
    ///    constant value is displayed; long names are not truncated.
    /// 3. Spacing grows dynamically — block height grows with pin count, column width with the
    ///    widest label, row height with the tallest block, and the canvas expands with content.
    /// 4. Blocks come in sizes — Contact/Coil are compact symbols (variable name as title);
    ///    Call/Box are multi-pin rectangles sized by pin count and pin-name length.
    ///
    /// Data source: FlgNetModel (parsed by TiaXmlParseHelper.ParseFlgNet), semantics:
    ///   Access  = operand (GlobalVariable / LocalVariable / TypedConstant)
    ///   Part/Call = graphical element (with named pins)
    ///   Wire:  Powerrail+NameCon        → left power rail to pin
    ///          IdentCon+NameCon         → variable/constant ↔ pin (direction from pin name)
    ///          NameCon+NameCon          → pin → pin
    /// </summary>
    internal static class LadNetworkRenderer
    {
        // ── Layout constants ─────────────────────────────────────────────
        private const double HeaderH = 26;       // Block title-bar height
        private const double PinRowH = 22;      // Natural pin-row height for Call/Box blocks
        private const double BlockMinW = 130;    // Minimum block width
        private const double BlockPadX = 14;     // Gap between pin name and block edge
        private const double PinGapMid = 24;     // Min gap between left/right pin columns
        private const double ContactSymW = 34;   // Contact/coil symbol width
        private const double ContactH = 52;      // Contact/coil total height (symbol + title)
        private const double ColGap = 46;        // Horizontal gap between columns
        private const double RowGap = 26;        // Vertical gap between rows
        private const double GutterStub = 22;    // Horizontal stub length from operand label to pin
        private const double RailX = 0;          // Left power rail X (starts at 0)
        private const double Col0Gutter = 100;   // Fixed gutter width of column 0 (operand label area)
        private const double CanvasPad = 26;     // Canvas padding
        private const double AccessGapY = 6;     // Min vertical spacing between operands in one gutter
        private const double FontSizePin = 10.5;
        private const double FontSizeTitle = 11.5;
        private const double FontSizeAccess = 10.5;

        private static Func<string, Brush, Brush> _themeResolver;
        private static Action<Elem, UIElement, bool> _onElementHover;
        private static Action<Elem, UIElement> _onElementContext;

        public static void SetThemeResolver(Func<string, Brush, Brush> resolver)
        {
            _themeResolver = resolver;
        }

        /// <summary>
        /// Set the element hover callback: parameters are (element data, triggering UI element, is entering).
        /// </summary>
        internal static void SetHoverHandler(Action<Elem, UIElement, bool> handler)
        {
            _onElementHover = handler;
        }

        /// <summary>
        /// Set the element right-click callback: parameters are (element data, triggering UI element).
        /// Right-click triggers a cross-reference query instead of hover auto-trigger, avoiding lag.
        /// </summary>
        internal static void SetContextHandler(Action<Elem, UIElement> handler)
        {
            _onElementContext = handler;
        }

        /// <summary>
        /// Attach a right-click context menu to interactive elements. Right-click opens the menu and only
        /// when the user clicks "View Cross Reference" is the query actually triggered, avoiding misfires and lag from hover queries.
        /// </summary>
        private static void AttachContextMenu(FrameworkElement host, Elem elem)
        {
            // Variable name/title as copyable text (operand takes priority).
            var copyText = !string.IsNullOrWhiteSpace(elem?.Operand) ? elem.Operand : elem?.Title;

            // With neither a cross-reference service nor copyable text the context menu is pointless — skip it.
            if (_onElementContext == null && string.IsNullOrWhiteSpace(copyText)) return;

            var lm = LanguageManager.Instance;

            host.Cursor = System.Windows.Input.Cursors.Hand;
            host.ToolTip = lm.GetString("Lad.ContextMenuTip");

            host.MouseRightButtonUp += (s, args) =>
            {
                // Rebuild the menu each time and bind the current element to avoid shared-state cross-talk.
                // Menu text is read here in the current language, so the next right-click uses the new language after a switch.
                var menu = new ContextMenu();

                // Copy variable name (purely local, no cross-reference service needed).
                if (!string.IsNullOrWhiteSpace(copyText))
                {
                    var copyItem = new MenuItem { Header = lm.GetString("Lad.CopyVariableName") };
                    copyItem.Click += (mi, me) =>
                    {
                        try { System.Windows.Clipboard.SetText(copyText); }
                        catch { /* clipboard occasionally busy; ignore */ }
                    };
                    menu.Items.Add(copyItem);
                }

                // View cross reference (requires the cross-reference service).
                if (_onElementContext != null)
                {
                    var viewXref = new MenuItem
                    {
                        Header = lm.GetString("Lad.ViewCrossReference"),
                        // Cross-reference query targets a specific operand; disabled when there is none.
                        IsEnabled = !string.IsNullOrWhiteSpace(copyText)
                    };
                    viewXref.Click += (mi, me) => _onElementContext?.Invoke(elem, host);
                    menu.Items.Add(viewXref);
                }

                if (menu.Items.Count == 0) return;

                menu.PlacementTarget = host;
                menu.IsOpen = true;
                args.Handled = true;
            };
        }

        private static Brush Theme(string key, Brush fallback)  
        {
            try { return _themeResolver?.Invoke(key, fallback) ?? fallback; }
            catch { return fallback; }
        }

        // ── Pin direction detection ─────────────────────────────────────────
        private static string NormPin(string pin) => (pin ?? "").Trim();

        private static bool IsOutputPinName(string pin)
        {
            var p = NormPin(pin).ToLowerInvariant();
            if (p.Length == 0) return false;
            if (p == "eno" || p == "q" || p == "cv" || p == "rt" || p == "q0") return true;
            if (p.StartsWith("out")) return true; // out, out1, out2...
            return false;
        }

        /// <summary>
        /// Determine whether a pin is an input.
        /// Prefer Section info from CallParameters; non-Call blocks fall back to name-based detection.
        /// </summary>
        private static bool IsPinInput(Graph g, string uid, string pin)
        {
            var name = NormPin(pin);
            if (g.CallPinSections.TryGetValue(uid, out var sections) && sections.TryGetValue(name, out var section))
            {
                var sec = section.Trim();
                if (string.Equals(sec, "Output", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true; // Input, InOut → both are input side
            }
            return !IsOutputPinName(name);
        }

        private static bool IsContactOrCoil(string name)
        {
            var n = (name ?? "").Trim().ToLowerInvariant();
            return n == "contact" || n == "pcontact" || n == "ncontact" || n == "not_contact" || n == "not"
                || n == "coil" || n == "pcoil" || n == "ncoil"
                || n == "scoil" || n == "rcoil" || n == "set_coil" || n == "reset_coil";
        }

        private static bool IsCoil(string name)
        {
            var n = (name ?? "").Trim().ToLowerInvariant();
            return n == "coil" || n == "pcoil" || n == "ncoil" || n == "scoil"
                || n == "rcoil" || n == "set_coil" || n == "reset_coil";
        }

        // A purely numeric Part Name (e.g. "0","1","2") is a parallel-branch marker, not a real element
        private static bool IsBranchMarker(string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return false;
            foreach (char c in n)
                if (c < '0' || c > '9') return false;
            return true;
        }

        // OR/AND parallel junction point (Kind="O" or "A" in TIA), not a real function block
        private static bool IsJunction(string kind)
        {
            var k = (kind ?? "").Trim().ToLowerInvariant();
            return k == "o" || k == "a";
        }

        /// <summary>Bit-logic/timer/arithmetic blocks: no en/eno; pins come from Wire</summary>
        private static bool IsBoxBlockType(string kind)
        {
            var k = (kind ?? "").Trim().ToLowerInvariant();
            return k == "add" || k == "sub" || k == "mul" || k == "div"
                || k == "mod" || k == "abs" || k == "neg" || k == "inc" || k == "dec"
                || k == "shl" || k == "shr" || k == "rol" || k == "ror"
                || k == "len" || k == "concat" || k == "left" || k == "right" || k == "mid"
                || k == "conv" || k == "convert" || k == "swap"
                || k == "bcd_to_int" || k == "int_to_bcd"
                || k == "timer" || k == "ton" || k == "tof" || k == "tp" || k == "tonr"
                || k == "counter" || k == "ctu" || k == "ctd" || k == "ctud";
        }

        private static string BuildCallTitle(FlgPart part)
        {
            var callName = part.CallName ?? part.DisplayName ?? "Call";
            var instance = (part.Instance ?? "").Trim('"');
            if (!string.IsNullOrWhiteSpace(instance))
                return callName + "\n" + instance;
            return callName;
        }

        private static int TitleLineCount(string title)
        {
            if (string.IsNullOrEmpty(title)) return 1;
            int count = 1;
            foreach (char c in title)
                if (c == '\n') count++;
            return count;
        }

        // ── Internal graph model ───────────────────────────────────────────
        internal sealed class Pin
        {
            public string Name;
            public bool IsInput;
            public double LocalY;      // Y relative to the element's top-left (anchor)
            public Point Anchor;       // Absolute canvas coordinates (filled after layout)
        }

        internal sealed class Elem
        {
            public string Uid;
            public string Kind;        // contact/coil/call/box...
            public bool IsCall;
            public bool IsContactCoil;
            public bool Negated;
            public string Title;       // Call name / element type name
            public string Operand;     // Main variable name of a contact/coil/block (the "operand" pin) → shown above
            public string OperandBelow; // Auxiliary variables such as edge-memory bits (non-"operand" pins) → shown below
            public List<Pin> Inputs = new List<Pin>();
            public List<Pin> Outputs = new List<Pin>();

            public double W, H;        // Size
            public double X, Y;        // Absolute canvas coordinates (top-left)
            public int Layer;          // Topological layer (column)
            public int Order;          // Order within the layer (row)
            public int InputConnCount; // Number of left-side input wires (for dynamic pin spacing)
            public List<string> PinOrder; // Pin order in CallParameters (for sorting)
            public int PartIndex;   // Position in the Parts list (for contact ordering)

            public Pin FindPin(string name, bool asInput)
            {
                var list = asInput ? Inputs : Outputs;
                var p = list.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (p != null) return p;
                // Fall back to the first pin on that side when the name does not match
                return list.FirstOrDefault();
            }
        }

        // Operand (variable/constant) — rendered as a label with a stub linking it to an element's pin
        private sealed class Operand
        {
            public string Uid;
            public string Text;        // Full symbolic name or constant value
            public bool IsConstant;
            public string TargetElemUid;
            public string TargetPin;
            public bool TargetPinIsInput; // true = variable feeds a pin (label left of block); false = pin writes a variable (label right of block)
            public double X, Y, W, H;
            public Point Anchor;       // Connection point on the element-facing side of the label
        }

        private sealed class Conn
        {
            public string SrcUid, SrcPin, DstUid, DstPin;
            public bool FromPowerrail;
        }

        private sealed class Graph
        {
            public Dictionary<string, Elem> Elems = new Dictionary<string, Elem>(StringComparer.OrdinalIgnoreCase);
            public List<Operand> Operands = new List<Operand>();
            public List<Conn> Conns = new List<Conn>();
            public HashSet<string> PowerrailPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "uid|pin"
            /// <summary>Call-block pin Section map: uid → (pinName → Section)</summary>
            public Dictionary<string, Dictionary<string, string>> CallPinSections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        // ── Phase 1: build the graph model from FlgNetModel ─────────────────────
        private static Graph BuildGraph(FlgNetModel net)
        {
            var g = new Graph();

            // Junction UIDs (OR/AND parallel nodes, not rendered as standalone elements)
            var junctionUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Elements (Part / Call)
            int partIndex = 0;
            foreach (var part in net.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Uid)) continue;

                var kind = part.IsCall ? "call" : (part.Name ?? "part").Trim().ToLowerInvariant();

                // Skip parallel-branch markers (purely numeric Part Name, e.g. "0","1","2")
                if (!part.IsCall && IsBranchMarker(kind))
                    continue;

                // Skip OR/AND junctions (not rendered as standalone elements; wires bypass them)
                if (!part.IsCall && IsJunction(kind))
                {
                    junctionUids.Add(part.Uid);
                    continue;
                }

                var elem = new Elem
                {
                    Uid = part.Uid,
                    Kind = kind,
                    IsCall = part.IsCall,
                    IsContactCoil = IsContactOrCoil(kind),
                    Negated = part.Negated,
                    Title = part.IsCall ? BuildCallTitle(part) : (part.DisplayName ?? part.Name ?? kind),
                    PinOrder = part.CallParameters?.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                    PartIndex = partIndex++,
                };
                g.Elems[part.Uid] = elem;
            }

            // Collect Call-block pin Section info (Input/Output/InOut) to correctly determine pin direction
            foreach (var part in net.Parts)
            {
                if (!part.IsCall || string.IsNullOrWhiteSpace(part.Uid)) continue;
                if (part.CallParameters == null || part.CallParameters.Count == 0) continue;
                var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var param in part.CallParameters)
                {
                    if (!string.IsNullOrWhiteSpace(param.Name) && !string.IsNullOrWhiteSpace(param.Section))
                        sections[param.Name] = param.Section;
                }
                if (sections.Count > 0)
                    g.CallPinSections[part.Uid] = sections;
            }

            // Operands (Access) — record text and scope first; assign to pins via Wire later
            var accessText = net.AccessByUid;
            var accessScope = net.ScopeByUid;

            // Wires: parse the three Wire kinds
            foreach (var wire in net.Wires)
            {
                if (wire.NameConnections == null) continue;

                // Powerrail + single NameCon
                if (wire.HasPowerrail && wire.NameConnections.Count >= 1)
                {
                    foreach (var nc in wire.NameConnections)
                    {
                        RegisterPin(g, nc.Uid, nc.Pin);
                        g.PowerrailPins.Add(nc.Uid + "|" + NormPin(nc.Pin));
                    }
                    continue;
                }

                // IdentCon (operand) + NameCon (pin)
                if (!string.IsNullOrWhiteSpace(wire.IdentAccessUid) && wire.NameConnections.Count >= 1)
                {
                    var identUid = wire.IdentAccessUid;
                    foreach (var nc in wire.NameConnections)
                    {
                        RegisterPin(g, nc.Uid, nc.Pin);
                        bool pinIsInput = IsPinInput(g, nc.Uid, nc.Pin);
                        var text = accessText.TryGetValue(identUid, out var t) ? t : identUid;
                        bool isConst = accessScope.TryGetValue(identUid, out var sc)
                                       && string.Equals(sc, "TypedConstant", StringComparison.OrdinalIgnoreCase);
                        g.Operands.Add(new Operand
                        {
                            Uid = identUid,
                            Text = string.IsNullOrWhiteSpace(text) ? "?" : text,
                            IsConstant = isConst,
                            TargetElemUid = nc.Uid,
                            TargetPin = NormPin(nc.Pin),
                            TargetPinIsInput = pinIsInput
                        });
                    }
                    continue;
                }

                // NameCon + NameCon (pin → pin), possibly several
                var ncs = wire.NameConnections;
                if (ncs.Count >= 2)
                {
                    foreach (var nc in ncs) RegisterPin(g, nc.Uid, nc.Pin);
                    var outs = ncs.Where(x => !IsPinInput(g, x.Uid, x.Pin)).ToList();
                    var ins = ncs.Where(x => IsPinInput(g, x.Uid, x.Pin)).ToList();
                    if (outs.Count == 0)
                    {
                        // Cannot determine the output; degrade to first → rest
                        outs = new List<FlgNameConnection> { ncs[0] };
                        ins = ncs.Skip(1).ToList();
                    }
                    foreach (var s in outs)
                        foreach (var d in ins)
                            g.Conns.Add(new Conn { SrcUid = s.Uid, SrcPin = NormPin(s.Pin), DstUid = d.Uid, DstPin = NormPin(d.Pin) });
                }
                else if (ncs.Count == 1)
                {
                    RegisterPin(g, ncs[0].Uid, ncs[0].Pin);
                }
            }

            // ── Junction resolution: skip junctions and connect parallel contacts directly to the next element ──
            if (junctionUids.Count > 0)
            {
                ResolveJunctions(g, junctionUids);
            }

            // Count left-side input wires per block for dynamic pin spacing
            foreach (var c in g.Conns)
            {
                if (g.Elems.TryGetValue(c.DstUid, out var elem) && !elem.IsContactCoil)
                    elem.InputConnCount++;
            }

            EnsureDefaultPins(g);
            HoistContactOperands(g);
            return g;
        }

        /// <summary>
        /// Resolve OR/AND junctions: find all wires through a junction and create direct connections bypassing it.
        /// The junction itself is not rendered — parallel contacts connect directly by wire to the next element.
        /// </summary>
        private static void ResolveJunctions(Graph g, HashSet<string> junctionUids)
        {
            // Collect the input and output edges of each junction
            var jInputs = new Dictionary<string, List<Conn>>(StringComparer.OrdinalIgnoreCase);
            var jOutputs = new Dictionary<string, List<Conn>>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in g.Conns)
            {
                if (junctionUids.Contains(c.DstUid))
                {
                    if (!jInputs.ContainsKey(c.DstUid)) jInputs[c.DstUid] = new List<Conn>();
                    jInputs[c.DstUid].Add(c);
                }
                if (junctionUids.Contains(c.SrcUid))
                {
                    if (!jOutputs.ContainsKey(c.SrcUid)) jOutputs[c.SrcUid] = new List<Conn>();
                    jOutputs[c.SrcUid].Add(c);
                }
            }

            // Remove all Conn entries involving junctions
            g.Conns.RemoveAll(c => junctionUids.Contains(c.SrcUid) || junctionUids.Contains(c.DstUid));

            // For each junction create direct input→output connections
            foreach (var jUid in junctionUids)
            {
                var inputs = jInputs.ContainsKey(jUid) ? jInputs[jUid] : new List<Conn>();
                var outputs = jOutputs.ContainsKey(jUid) ? jOutputs[jUid] : new List<Conn>();

                foreach (var inConn in inputs)
                {
                    foreach (var outConn in outputs)
                    {
                        // Connect the parallel contact's output directly to the element input after the junction
                        g.Conns.Add(new Conn
                        {
                            SrcUid = inConn.SrcUid,
                            SrcPin = inConn.SrcPin,
                            DstUid = outConn.DstUid,
                            DstPin = outConn.DstPin
                        });
                        RegisterPin(g, inConn.SrcUid, inConn.SrcPin);
                        RegisterPin(g, outConn.DstUid, outConn.DstPin);
                    }
                }
            }

            // Handle a Powerrail connection to a junction: forward to the junction's output targets
            var railToRemove = new List<string>();
            var railToAdd = new List<string>();
            foreach (var railKey in g.PowerrailPins)
            {
                var parts = railKey.Split('|');
                if (parts.Length < 2) continue;
                var uid = parts[0];
                var pin = parts[1];
                if (!junctionUids.Contains(uid)) continue;

                railToRemove.Add(railKey);
                if (jOutputs.ContainsKey(uid))
                {
                    foreach (var outConn in jOutputs[uid])
                    {
                        railToAdd.Add(outConn.DstUid + "|" + outConn.DstPin);
                        RegisterPin(g, outConn.DstUid, outConn.DstPin);
                    }
                }
            }
            foreach (var k in railToRemove) g.PowerrailPins.Remove(k);
            foreach (var k in railToAdd) g.PowerrailPins.Add(k);

            // Handle an Operand connection to a junction: forward to the junction's output targets
            var newOperands = new List<Operand>();
            var oldOperands = new List<Operand>();
            foreach (var op in g.Operands)
            {
                if (!junctionUids.Contains(op.TargetElemUid)) continue;

                oldOperands.Add(op);
                if (jOutputs.ContainsKey(op.TargetElemUid))
                {
                    foreach (var outConn in jOutputs[op.TargetElemUid])
                    {
                        newOperands.Add(new Operand
                        {
                            Uid = op.Uid,
                            Text = op.Text,
                            IsConstant = op.IsConstant,
                            TargetElemUid = outConn.DstUid,
                            TargetPin = outConn.DstPin,
                            TargetPinIsInput = op.TargetPinIsInput
                        });
                        RegisterPin(g, outConn.DstUid, outConn.DstPin);
                    }
                }
            }
            foreach (var op in oldOperands) g.Operands.Remove(op);
            foreach (var op in newOperands) g.Operands.Add(op);
        }

        // A contact/coil/block's variable name comes from the operand wired to its pin:
        // "operand" pin → e.Operand (shown above)
        // other input pins (e.g. edge-memory bit) → e.OperandBelow (shown below)
        // For Block/Call types the "operand" pin is likewise hoisted above the block.
        private static void HoistContactOperands(Graph g)
        {
            var consumed = new List<Operand>();
            foreach (var op in g.Operands)
            {
                if (!g.Elems.TryGetValue(op.TargetElemUid, out var e)) continue;

                var pinName = (op.TargetPin ?? "").Trim().ToLowerInvariant();

                // Any element type: a variable wired to the "operand" pin → e.Operand (shown above)
                if (pinName == "operand")
                {
                    if (string.IsNullOrEmpty(e.Operand))
                    {
                        e.Operand = op.Text;
                        consumed.Add(op);
                    }
                    continue;
                }

                // Contact/coil only
                if (e.IsContactCoil)
                {
                    if (pinName == "in" || pinName == "in1")
                    {
                        if (string.IsNullOrEmpty(e.Operand))
                        {
                            e.Operand = op.Text;
                            consumed.Add(op);
                        }
                    }
                    else if (op.TargetPinIsInput)
                    {
                        // Auxiliary variables such as edge-memory bits → below
                        if (string.IsNullOrEmpty(e.OperandBelow))
                        {
                            e.OperandBelow = op.Text;
                            consumed.Add(op);
                        }
                    }
                }
            }
            foreach (var op in consumed) g.Operands.Remove(op);
        }

        /// <summary>
        /// Extract variable references on the current page from FlgNetModel (no TIA API needed, available immediately)
        /// </summary>
        public static Dictionary<string, List<CrossReferenceEntry>> GetLocalReferences(FlgNetModel net)
        {
            return GetLocalReferences(net, null, 0, null);
        }

        /// <summary>
        /// Extract variable references on the current page from FlgNetModel, with block name and Network info.
        /// </summary>
        public static Dictionary<string, List<CrossReferenceEntry>> GetLocalReferences(
            FlgNetModel net, string blockName, int networkIndex, string networkTitle)
        {
            var result = new Dictionary<string, List<CrossReferenceEntry>>(StringComparer.OrdinalIgnoreCase);
            var g = BuildGraph(net);

            foreach (var e in g.Elems.Values)
            {
                var varName = e.Operand;
                if (string.IsNullOrWhiteSpace(varName)) continue;

                var refType = IsCoil(e.Kind) ? "Write" : "Read";
                var kindLabel = e.Kind == "contact" ? (e.Negated ? "NC Contact" : "NO Contact")
                              : e.Kind == "coil" ? "Coil"
                              : e.Kind == "call" ? "Call"
                              : e.Kind;

                if (!result.ContainsKey(varName))
                    result[varName] = new List<CrossReferenceEntry>();

                result[varName].Add(new CrossReferenceEntry
                {
                    VariableName = varName,
                    SourceName = e.Title ?? varName,
                    ReferenceType = refType,
                    ReferenceLocation = kindLabel,
                    LocationName = kindLabel,
                    BlockName = blockName,
                    NetworkIndex = networkIndex,
                    NetworkTitle = networkTitle
                });
            }

            // Fill in Scope info
            var scopeMap = net.ScopeByUid;
            foreach (var kvp in result)
            {
                foreach (var entry in kvp.Value)
                {
                    // Look up the scope in reverse from AccessByUid
                    var uid = net.AccessByUid.FirstOrDefault(a =>
                        string.Equals(a.Value, kvp.Key, StringComparison.OrdinalIgnoreCase)).Key;
                    if (uid != null && scopeMap.TryGetValue(uid, out var scope))
                        entry.Scope = scope;
                }
            }

            return result;
        }

        private static void RegisterPin(Graph g, string uid, string pin)
        {
            if (!g.Elems.TryGetValue(uid, out var elem)) return;
            var name = NormPin(pin);
            if (name.Length == 0) return;

            bool pinIsInput = IsPinInput(g, uid, pin);
            bool isInOut = false;
            if (g.CallPinSections.TryGetValue(uid, out var sections) && sections.TryGetValue(name, out var section))
                isInOut = string.Equals(section.Trim(), "InOut", StringComparison.OrdinalIgnoreCase);

            // InOut pins are added to both the input and output lists
            if (!pinIsInput || isInOut)
            {
                if (!elem.Outputs.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                    elem.Outputs.Add(new Pin { Name = name, IsInput = false });
            }
            if (pinIsInput || isInOut)
            {
                if (!elem.Inputs.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                    elem.Inputs.Add(new Pin { Name = name, IsInput = true });
            }
        }

        // If a contact/coil has no registered pins, add default in/out so it has connection points
        // Bit-logic/timer/arithmetic blocks have no en/eno, so no default pins are added
        private static void EnsureDefaultPins(Graph g)
        {
            foreach (var e in g.Elems.Values)
            {
                if (e.IsContactCoil)
                {
                    if (e.Inputs.Count == 0) e.Inputs.Add(new Pin { Name = "in", IsInput = true });
                    if (!IsCoil(e.Kind) && e.Outputs.Count == 0) e.Outputs.Add(new Pin { Name = "out", IsInput = false });
                }
                else if (IsBoxBlockType(e.Kind))
                {
                    // Bit-logic/timer/arithmetic blocks: no en/eno; pins come from Wire
                    if (e.Inputs.Count == 0) e.Inputs.Add(new Pin { Name = "in", IsInput = true });
                    if (e.Outputs.Count == 0) e.Outputs.Add(new Pin { Name = "out", IsInput = false });
                }
                else
                {
                    if (e.Inputs.Count == 0) e.Inputs.Add(new Pin { Name = "en", IsInput = true });
                    if (e.Outputs.Count == 0) e.Outputs.Add(new Pin { Name = "eno", IsInput = false });

                    // Pin ordering: en/eno first, the rest in CallParameters order
                    if (e.PinOrder != null && e.PinOrder.Count > 0)
                    {
                        var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < e.PinOrder.Count; i++)
                            orderMap[e.PinOrder[i]] = i;
                        e.Inputs = e.Inputs
                            .OrderBy(p => p.Name == "en" ? -1 : orderMap.TryGetValue(p.Name, out var idx) ? idx : int.MaxValue)
                            .ToList();
                        e.Outputs = e.Outputs
                            .OrderBy(p => p.Name == "eno" ? -1 : orderMap.TryGetValue(p.Name, out var idx) ? idx : int.MaxValue)
                            .ToList();
                    }
                    else
                    {
                        e.Inputs = e.Inputs
                            .OrderBy(p => p.Name == "en" ? 0 : 1)
                            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        e.Outputs = e.Outputs
                            .OrderBy(p => p.Name == "eno" ? 0 : 1)
                            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                }
            }
        }

        // ── Text measurement (basis for dynamic spacing)────────────────────────────
        private static readonly Typeface _typeface = new Typeface("Consolas");

        private static Size MeasureText(string text, double fontSize)
        {
            if (string.IsNullOrEmpty(text)) return new Size(0, fontSize + 2);
            var ft = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, fontSize, Brushes.Black,
                new NumberSubstitution(), TextFormattingMode.Display, 1.0);
            return new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
        }

        // ── Phase 2: measure each element's size (no information loss + dynamic growth)──────
        private static void MeasureElems(Graph g)
        {
            foreach (var e in g.Elems.Values)
            {
                if (e.IsContactCoil)
                {
                    // Contact/coil: fixed symbol width, title (variable name) drives height. Width is the larger of symbol and title.
                    // The NOT contact needs extra width to fit the three letters "NOT"
                    bool isNotContact = IsNotContact(e.Kind);
                    double symW = isNotContact ? 50 : ContactSymW;
                    var label = e.Operand ?? "";
                    var tw = MeasureText(label, FontSizeTitle).Width;
                    e.W = Math.Max(symW + 12, tw + 10);
                    e.H = ContactH;
                    // Pin anchors at the symbol's vertical center
                    foreach (var p in e.Inputs) p.LocalY = ContactH / 2 - 10;
                    foreach (var p in e.Outputs) p.LocalY = ContactH / 2 - 10;
                }
                else
                {
                    int rows = Math.Max(e.Inputs.Count, e.Outputs.Count);
                    rows = Math.Max(1, rows);
                    int titleLines = TitleLineCount(e.Title);
                    double titleH = HeaderH + (titleLines - 1) * (FontSizeTitle + 4);

                    // Dynamic pin spacing: natural spacing + number of left input wires × weight, so block height
                    // grows naturally with the number of input elements. When a block's input pins are fed by
                    // contact branches (Conn), the pin row pitch must be ≥ contact height, so each branch can
                    // align “perfectly horizontally” into its pin without overlapping neighboring branches.
                    double pinRowH = PinRowH + e.InputConnCount * 4.0;
                    if (e.InputConnCount > 0)
                        pinRowH = Math.Max(pinRowH, ContactH + RowGap);
                    e.H = titleH + rows * pinRowH + 8;

                    double maxIn = e.Inputs.Count == 0 ? 0 : e.Inputs.Max(p => MeasureText(p.Name, FontSizePin).Width);
                    double maxOut = e.Outputs.Count == 0 ? 0 : e.Outputs.Max(p => MeasureText(p.Name, FontSizePin).Width);
                    double titleW = MeasureText(e.Title, FontSizeTitle).Width;
                    double bodyW = BlockPadX * 2 + maxIn + maxOut + PinGapMid;
                    e.W = Math.Max(Math.Max(BlockMinW, bodyW), titleW + BlockPadX * 2);

                    // Pin anchor Y: laid out row by row below the title bar
                    for (int i = 0; i < e.Inputs.Count; i++)
                        e.Inputs[i].LocalY = titleH + i * pinRowH + pinRowH / 2;
                    for (int i = 0; i < e.Outputs.Count; i++)
                        e.Outputs[i].LocalY = titleH + i * pinRowH + pinRowH / 2;
                }
            }
        }

        // ── Phase 3: layering (columns) + row assignment ────────────────────────────
        // LAD naturally flows from left (power rail/inputs) to right (coils/outputs). Layer by longest
        // path, forcing coils into the rightmost column.
        private static void AssignLayers(Graph g)
        {
            var succ = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var indeg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in g.Elems.Values) { succ[e.Uid] = new List<string>(); indeg[e.Uid] = 0; }
            foreach (var c in g.Conns)
            {
                if (!g.Elems.ContainsKey(c.SrcUid) || !g.Elems.ContainsKey(c.DstUid)) continue;
                succ[c.SrcUid].Add(c.DstUid);
                indeg[c.DstUid] = indeg[c.DstUid] + 1;
            }

            // Longest-path layering
            var layer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var q = new Queue<string>();
            foreach (var e in g.Elems.Values) { layer[e.Uid] = 0; if (indeg[e.Uid] == 0) q.Enqueue(e.Uid); }
            var indegWork = new Dictionary<string, int>(indeg, StringComparer.OrdinalIgnoreCase);
            int guard = 0, guardMax = g.Elems.Count * 4 + 16;
            while (q.Count > 0 && guard++ < guardMax)
            {
                var u = q.Dequeue();
                foreach (var v in succ[u])
                {
                    if (layer[u] + 1 > layer[v]) layer[v] = layer[u] + 1;
                    indegWork[v]--;
                    if (indegWork[v] <= 0) q.Enqueue(v);
                }
            }

            int maxLayer = layer.Count > 0 ? layer.Values.Max() : 0;
            // Force coils into the rightmost column
            foreach (var e in g.Elems.Values)
                if (IsCoil(e.Kind)) layer[e.Uid] = maxLayer;

            foreach (var e in g.Elems.Values) e.Layer = layer[e.Uid];

            // Intra-layer order: first sort by appearance order in the Parts list (a baseline matching XML order)
            var layerGroups = g.Elems.Values.GroupBy(e => e.Layer).OrderBy(gr => gr.Key).ToList();
            foreach (var grp in layerGroups)
            {
                int order = 0;
                foreach (var e in grp.OrderBy(x => x.PartIndex))
                    e.Order = order++;
            }

            // Crossing minimization: reorder upstream branches feeding the same block by “target pin order”.
            // The pin order (from CallParameters) is fixed and authoritative; left-side branches (contacts/series
            // chains) should be ordered top-to-bottom by the pin they finally connect to, so wires enter in
            // parallel without crossing.
            OrderSourcesByTargetPin(g, layerGroups);
        }

        /// <summary>
        /// Reorder upstream branches within a layer by “target pin order”.
        ///
        /// Method: compute a sink key for each element — walk downstream along wires until reaching a
        /// block's input pin; the key is (target block's Order, the pin's row index within the block).
        /// Every contact on a series chain (contact→contact→block) resolves to the same pin at the chain's
        /// end, so the whole chain is sorted as a unit by target pin and never split. Within a column,
        /// Order is re-sorted ascending by this key.
        /// </summary>
        private static void OrderSourcesByTargetPin(Graph g, List<IGrouping<int, Elem>> layerGroups)
        {
            if (layerGroups.Count < 2) return;

            // Each element's downstream edges (Src → Dst/pin)
            var outEdges = new Dictionary<string, List<Conn>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in g.Conns)
            {
                if (!g.Elems.ContainsKey(c.SrcUid) || !g.Elems.ContainsKey(c.DstUid)) continue;
                if (!outEdges.TryGetValue(c.SrcUid, out var list)) { list = new List<Conn>(); outEdges[c.SrcUid] = list; }
                list.Add(c);
            }

            // Walk downstream to the first “block input pin”, returning (block Order, pin row index). Null if none.
            var memo = new Dictionary<string, (int blockOrder, int pinRow)?>(StringComparer.OrdinalIgnoreCase);
            (int, int)? ResolveSinkPin(string uid, HashSet<string> visiting)
            {
                if (memo.TryGetValue(uid, out var cached)) return cached;
                if (!visiting.Add(uid)) return null; // prevent cycles

                (int, int)? best = null;
                if (outEdges.TryGetValue(uid, out var edges))
                {
                    foreach (var c in edges)
                    {
                        if (!g.Elems.TryGetValue(c.DstUid, out var dst)) continue;

                        (int, int)? candidate;
                        if (!dst.IsContactCoil)
                        {
                            // Downstream is a block: directly hit that input pin's row index
                            int pinRow = dst.Inputs.FindIndex(p =>
                                string.Equals(p.Name, c.DstPin, StringComparison.OrdinalIgnoreCase));
                            if (pinRow < 0) pinRow = 0;
                            candidate = (dst.Order, pinRow);
                        }
                        else
                        {
                            // Downstream is still a contact/coil: recurse further downstream (series chain)
                            candidate = ResolveSinkPin(c.DstUid, visiting);
                        }

                        // Take the “topmost” target pin (smallest block Order, smallest pin row)
                        if (candidate.HasValue &&
                            (best == null ||
                             candidate.Value.Item1 < best.Value.Item1 ||
                             (candidate.Value.Item1 == best.Value.Item1 && candidate.Value.Item2 < best.Value.Item2)))
                        {
                            best = candidate;
                        }
                    }
                }

                visiting.Remove(uid);
                memo[uid] = best;
                return best;
            }

            foreach (var grp in layerGroups)
            {
                var col = grp.ToList();
                if (col.Count < 2) continue;

                // Sort key: first by target block Order, then by target pin row index; unresolved ones (e.g. coils) keep their original Order.
                double KeyOf(Elem e)
                {
                    var sink = ResolveSinkPin(e.Uid, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if (sink == null) return double.MaxValue - (1000 - e.Order); // Terminal elements go last but keep their relative order
                    return sink.Value.Item1 * 1000.0 + sink.Value.Item2;
                }

                var reordered = col
                    .OrderBy(KeyOf)
                    .ThenBy(e => e.PartIndex)
                    .ToList();
                for (int i = 0; i < reordered.Count; i++)
                    reordered[i].Order = i;
            }
        }

        // ── Phase 4: absolute coordinate assignment (column width/row height grow dynamically)──────────────
        private static Size PlaceElems(Graph g)
        {
            if (g.Elems.Count == 0) return new Size(200, 100);

            int maxLayer = g.Elems.Values.Max(e => e.Layer);

            // Column width = widest element in that column; column start X accumulates (including reserved operand gutter)
            var colW = new double[maxLayer + 1];
            for (int l = 0; l <= maxLayer; l++)
            {
                var col = g.Elems.Values.Where(e => e.Layer == l).ToList();
                colW[l] = col.Count == 0 ? BlockMinW : col.Max(e => e.W);
            }

            // Whether each column needs a gutter on its left (to hold operand labels feeding that column's input pins)
            // Column 0 always does (variables sit right of the power rail); other columns do if they have IdentCon input operands.
            // The gutter width must fit the column's widest “left input operand label + stub”; otherwise long variable names
            // get pushed left of colX (possibly across the power rail at X=0) and clipped. A contact/coil's operand is a
            // title drawn above its symbol and takes no left gutter; only large blocks like Call/Box need one for input operands.
            var gutterW = new double[maxLayer + 1];
            foreach (var op in g.Operands)
            {
                if (!g.Elems.TryGetValue(op.TargetElemUid, out var te)) continue;
                if (!op.TargetPinIsInput) continue; // Output operands go on the right and take no left gutter
                if (te.IsContactCoil) continue;     // Contact/coil operands are drawn above the symbol and take no gutter
                // Label width (including padding) + stub length = horizontal space this operand needs
                var tw = MeasureText(op.Text, FontSizeAccess).Width + 12;
                gutterW[te.Layer] = Math.Max(gutterW[te.Layer], tw + GutterStub);
            }

            // Column start X. For column 0 the gutter is the larger of the “fixed value” and the “actually required label width”,
            // ensuring even very long variable names sit fully right of the power rail without being clipped.
            var colX = new double[maxLayer + 1];
            double cursorX = RailX + 24;
            for (int l = 0; l <= maxLayer; l++)
            {
                double gutter = (l == 0) ? Math.Max(Col0Gutter, gutterW[0]) : gutterW[l];
                cursorX += gutter;
                colX[l] = cursorX;
                cursorX += colW[l] + ColGap;
            }

            // X is fixed by column
            foreach (var e in g.Elems.Values)
                e.X = colX[e.Layer];

            // ── Whole-chain horizontal alignment layout ─────────────────────────────────────
            // Idea: blocks (multi-pin consumers) keep their natural height and are first positioned by column
            // to establish each input pin's Y; then the whole series chain feeding a pin is aligned to that
            // “target pin Y” horizontal center line, so the chain lies on one line with horizontal wires and
            // there is no step from the chain's end (source) to the block pin.
            double maxBottom = PlaceChainsAlignedToPins(g, maxLayer, colX);

            // Place operand labels (aligned to target pin Y; input type left of the block, output type right)
            PlaceOperands(g);

            double maxRight = 0;
            foreach (var e in g.Elems.Values) maxRight = Math.Max(maxRight, e.X + e.W);
            foreach (var op in g.Operands) { maxRight = Math.Max(maxRight, op.X + op.W); maxBottom = Math.Max(maxBottom, op.Y + op.H); }

            return new Size(maxRight + CanvasPad, maxBottom + CanvasPad);
        }

        /// <summary>
        /// Vertical layout driven by “outputs (coils/terminal block pins)”.
        ///
        /// Idea (the approach confirmed by the user):
        /// 1. First determine all output rows — each coil and each sink element with no downstream occupies one
        ///    output row, ordered by Order.
        /// 2. Each output corresponds to one chain (the output plus all its upstream elements to the left).
        ///    The whole chain is flattened onto that output row's center Y, keeping wires fully horizontal.
        /// 3. Dynamic row pitch: for each output, count the maximum parallel branch count n on its left chain.
        ///    That chain needs vertical space for n rows (parallel branches spread up/down), so
        ///    next row Y = current row Y + n × single-row height, preventing parallel branches from crossing adjacent chains.
        /// 4. The remaining elements of a chain (further-left columns) align to the output row Y they finally feed;
        ///    parallel branches under the same output spread evenly up/down within the reserved n-row space.
        /// Returns the content's bottom Y.
        /// </summary>
        private static double PlaceChainsAlignedToPins(Graph g, int maxLayer, double[] colX)
        {
            double top = CanvasPad + 10;
            double rowStep = ContactH + RowGap; // Single-row height (natural contact/coil height + row gap)

            // Downstream map: Src → wire list; upstream map: Dst → wire list
            var outEdges = new Dictionary<string, List<Conn>>(StringComparer.OrdinalIgnoreCase);
            var inEdges = new Dictionary<string, List<Conn>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in g.Conns)
            {
                if (!g.Elems.ContainsKey(c.SrcUid) || !g.Elems.ContainsKey(c.DstUid)) continue;
                if (!outEdges.TryGetValue(c.SrcUid, out var ol)) { ol = new List<Conn>(); outEdges[c.SrcUid] = ol; }
                ol.Add(c);
                if (!inEdges.TryGetValue(c.DstUid, out var il)) { il = new List<Conn>(); inEdges[c.DstUid] = il; }
                il.Add(c);
            }

            // If the network contains a “block” (a consumer with multiple pins), keep the original
            // “align to block pins” layout so existing block-containing networks don't regress.
            // Pure contact/coil networks use the new “output-driven + dynamic row pitch” layout.
            bool hasBlock = g.Elems.Values.Any(e => !e.IsContactCoil);
            if (hasBlock)
                return PlaceChainsAlignedToBlockPins(g, maxLayer, outEdges, top);

            // ── New layout: output-driven + dynamic row pitch ──────────────────────────

            // 1) Find the output rows: coils + sink elements with no downstream. Sort by Order (intra-layer order).
            var outputs = g.Elems.Values
                .Where(e => IsCoil(e.Kind) || !outEdges.ContainsKey(e.Uid) || outEdges[e.Uid].Count == 0)
                .OrderBy(e => e.Order)
                .ThenBy(e => e.PartIndex)
                .ToList();
            var outputSet = new HashSet<string>(outputs.Select(o => o.Uid), StringComparer.OrdinalIgnoreCase);

            // 2) Build the “element → output it feeds into” map: walk downstream to the first output (coil/sink).
            var sinkMemo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string SinkOutput(string uid, HashSet<string> visiting)
            {
                if (sinkMemo.TryGetValue(uid, out var cached)) return cached;
                if (outputSet.Contains(uid)) return uid; // itself is an output
                if (!visiting.Add(uid)) return null;
                string best = null;
                if (outEdges.TryGetValue(uid, out var edges))
                {
                    foreach (var c in edges)
                    {
                        var s = SinkOutput(c.DstUid, visiting);
                        if (s != null && (best == null ||
                            g.Elems[s].Order < g.Elems[best].Order)) best = s;
                    }
                }
                visiting.Remove(uid);
                sinkMemo[uid] = best;
                return best;
            }

            // Assign each non-output element to the output it feeds into, and record its column.
            var membersByOutput = new Dictionary<string, List<Elem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in g.Elems.Values)
            {
                if (outputSet.Contains(e.Uid)) continue;
                var sink = SinkOutput(e.Uid, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                if (sink == null) continue;
                if (!membersByOutput.TryGetValue(sink, out var list)) { list = new List<Elem>(); membersByOutput[sink] = list; }
                list.Add(e);
            }

            // 3) The number of rows n an output actually needs = the max over columns of “elements feeding that output”.
            //    This matches the later “spread same-column same-output downward” logic exactly, so it never overestimates.
            int ParallelWidthOf(string outputUid)
            {
                if (!membersByOutput.TryGetValue(outputUid, out var members) || members.Count == 0)
                    return 1;
                int maxPerCol = members.GroupBy(m => m.Layer).Max(gr => gr.Count());
                return Math.Max(1, maxPerCol);
            }

            // 4) Assign a “main line Y” to each output row (= the horizontal line of the chain's topmost branch).
            //    The extra (n-1) rows of parallel space are filled below the main line, i.e. between the current
            //    and next rows, without accumulating. Next main line Y = current main line Y + n × single-row height
            //    (this row occupies 1 main line + (n-1) parallel spread). The output itself aligns to the “main line”
            //    (topmost branch); the parallel section only expands downward, so the net row spacing is always
            //    “1 row + this row's parallel compensation”, consistent for equal parallel counts and non-accumulating.
            var rowTopY = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double cursorTop = top + ContactH / 2; // center Y of the first main line
            for (int i = 0; i < outputs.Count; i++)
            {
                var outp = outputs[i];
                int n = ParallelWidthOf(outp.Uid);
                rowTopY[outp.Uid] = cursorTop;
                cursorTop += n * rowStep;
            }

            // 5) Place each output element on its “main line Y” (aligned to the topmost branch, deriving element Y from the input pin's LocalY).
            foreach (var outp in outputs)
            {
                double anchorLocalY = outp.Inputs.Count > 0 ? outp.Inputs[0].LocalY : outp.H / 2;
                outp.Y = rowTopY[outp.Uid] - anchorLocalY;
            }
            RefreshAnchors(g);

            // 6) Upstream element alignment: within one output and one column, multiple parallel branches spread row by row
            //    downward from the “main line Y” (branch 0 on the main line, the rest below), so the next-level wire
            //    naturally follows the topmost branch's horizontal line.
            // Go column by column from right to left, grouping upstream elements by the output they feed into and
            // spreading them downward from the main line Y.
            for (int l = maxLayer - 1; l >= 0; l--)
            {
                var col = g.Elems.Values
                    .Where(e => e.Layer == l && !rowTopY.ContainsKey(e.Uid))
                    .OrderBy(e => e.Order).ThenBy(e => e.PartIndex)
                    .ToList();
                if (col.Count == 0) continue;

                // Group by the output they feed into
                var byOutput = col
                    .Select(e => (elem: e, sink: SinkOutput(e.Uid, new HashSet<string>(StringComparer.OrdinalIgnoreCase))))
                    .Where(t => t.sink != null)
                    .GroupBy(t => t.sink);

                foreach (var grp in byOutput)
                {
                    var members = grp.OrderBy(t => t.elem.Order).ThenBy(t => t.elem.PartIndex).ToList();
                    double topLineY = rowTopY[grp.Key]; // main line = topmost branch
                    int cnt = members.Count;
                    // Row by row downward from the main line: branch 0 on the main line, the rest below.
                    for (int i = 0; i < cnt; i++)
                    {
                        var e = members[i].elem;
                        double branchCenter = topLineY + i * rowStep;
                        double outLocalY = e.Outputs.Count > 0 ? e.Outputs[0].LocalY : e.H / 2;
                        e.Y = branchCenter - outLocalY;
                    }
                }
                RefreshAnchors(g);
            }

            double maxBottom = top;
            foreach (var e in g.Elems.Values)
                maxBottom = Math.Max(maxBottom, e.Y + e.H);
            return maxBottom;
        }

        /// <summary>
        /// Original layout: block-containing networks — blocks stack by natural height within a column to establish pin Y,
        /// and upstream chains align to the block pin Y.
        /// Split out from the existing block-network behavior so PlaceChainsAlignedToPins can call it when a block is detected.
        /// </summary>
        private static double PlaceChainsAlignedToBlockPins(
            Graph g, int maxLayer,
            Dictionary<string, List<Conn>> outEdges, double top)
        {
            // 1) Stack each column by Order using natural heights — establishes the pin Y baseline.
            for (int l = 0; l <= maxLayer; l++)
            {
                var col = g.Elems.Values.Where(e => e.Layer == l).OrderBy(e => e.Order).ToList();
                double cursorY = top;
                foreach (var e in col)
                {
                    e.Y = cursorY;
                    cursorY += e.H + RowGap;
                }
            }
            RefreshAnchors(g);

            // 2) Walk to the sink pin: follow downstream to the first “block input pin” and record that pin's absolute Y.
            var memo = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            double? SinkPinY(string uid, HashSet<string> visiting)
            {
                if (memo.TryGetValue(uid, out var cached)) return cached;
                if (!visiting.Add(uid)) return null;

                double? best = null;
                if (outEdges.TryGetValue(uid, out var edges))
                {
                    foreach (var c in edges)
                    {
                        if (!g.Elems.TryGetValue(c.DstUid, out var dst)) continue;
                        double? cand;
                        if (!dst.IsContactCoil)
                        {
                            var pin = dst.FindPin(c.DstPin, true);
                            cand = pin?.Anchor.Y;
                        }
                        else
                        {
                            cand = SinkPinY(c.DstUid, visiting);
                        }
                        if (cand.HasValue && (best == null || cand.Value < best.Value))
                            best = cand;
                    }
                }
                visiting.Remove(uid);
                memo[uid] = best;
                return best;
            }

            // 3) Align column by column to the sink pin, from right to left.
            for (int l = maxLayer - 1; l >= 0; l--)
            {
                var col = g.Elems.Values.Where(e => e.Layer == l).OrderBy(e => e.Order).ToList();
                if (col.Count == 0) continue;

                memo.Clear();

                bool anyAligned = false;
                foreach (var e in col)
                {
                    var sinkY = SinkPinY(e.Uid, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if (sinkY.HasValue)
                    {
                        double outLocalY = e.Outputs.Count > 0 ? e.Outputs[0].LocalY : e.H / 2;
                        e.Y = sinkY.Value - outLocalY;
                        anyAligned = true;
                    }
                }
                if (!anyAligned) continue;

                for (int i = 1; i < col.Count; i++)
                {
                    double minY = col[i - 1].Y + col[i - 1].H + RowGap;
                    if (col[i].Y < minY) col[i].Y = minY;
                }
                double shift = top - col[0].Y;
                if (shift > 0) foreach (var e in col) e.Y += shift;

                RefreshAnchors(g);
            }

            double maxBottom = top;
            foreach (var e in g.Elems.Values)
                maxBottom = Math.Max(maxBottom, e.Y + e.H);
            return maxBottom;
        }

        // Recompute the absolute coordinates of all pin anchors from each element's current X/Y.
        private static void RefreshAnchors(Graph g)
        {
            foreach (var e in g.Elems.Values)
            {
                foreach (var p in e.Inputs) p.Anchor = new Point(e.X, e.Y + p.LocalY);
                foreach (var p in e.Outputs) p.Anchor = new Point(e.X + e.W, e.Y + p.LocalY);
            }
        }

        private static void PlaceOperands(Graph g)
        {
            foreach (var op in g.Operands)
            {
                if (!g.Elems.TryGetValue(op.TargetElemUid, out var te)) continue;
                var pin = te.FindPin(op.TargetPin, op.TargetPinIsInput);
                if (pin == null) continue;

                var sz = MeasureText(op.Text, FontSizeAccess);
                op.W = sz.Width + 12;
                op.H = Math.Max(18, sz.Height + 6);
                double y = pin.Anchor.Y - op.H / 2;
                if (op.TargetPinIsInput)
                {
                    // Label left of the block, right edge GutterStub from the pin
                    op.X = pin.Anchor.X - GutterStub - op.W;
                    op.Anchor = new Point(op.X + op.W, pin.Anchor.Y);
                }
                else
                {
                    // Label right of the block
                    op.X = pin.Anchor.X + GutterStub;
                    op.Anchor = new Point(op.X, pin.Anchor.Y);
                }
                op.Y = y;
            }
        }

        // ── Phase 5: draw to Canvas ─────────────────────────────────
        /// <summary>
        /// Render a single network (FlgNet). Returns a size-adaptive Canvas.
        /// </summary>
        public static FrameworkElement RenderNetwork(FlgNetModel net)
        {
            var g = BuildGraph(net);
            MeasureElems(g);
            AssignLayers(g);
            var size = PlaceElems(g);

            var wire = Theme("LadWireBrush", new SolidColorBrush(Color.FromRgb(120, 200, 255)));
            var rail = Theme("LadRailBrush", new SolidColorBrush(Color.FromRgb(30, 150, 255)));
            var sym = Theme("LadSymbolBrush", Brushes.White);
            var txt = Theme("TextBrush", Brushes.White);
            var muted = Theme("MutedTextBrush", Brushes.Silver);
            var nodeBg = Theme("LadNodeBackgroundBrush", new SolidColorBrush(Color.FromRgb(38, 38, 42)));
            var nodeBd = Theme("LadNodeBorderBrush", new SolidColorBrush(Color.FromRgb(90, 90, 96)));

            var canvas = new Canvas
            {
                Width = size.Width,
                Height = size.Height,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            // Left power rail (vertical line)
            double railTop = CanvasPad, railBottom = size.Height - CanvasPad;
            AddLine(canvas, RailX, railTop, RailX, railBottom, rail, 2.5);

            // Wires: power rail → input pins (powerrail pins)
            foreach (var e in g.Elems.Values)
                foreach (var p in e.Inputs)
                    if (g.PowerrailPins.Contains(e.Uid + "|" + p.Name))
                        AddOrthWire(canvas, new Point(RailX, p.Anchor.Y), p.Anchor, wire);

            // Wires: pin → pin (parallel wires merged into a shared vertical line to avoid overlap)
            var connGroups = g.Conns
                .Where(c => g.Elems.ContainsKey(c.SrcUid) && g.Elems.ContainsKey(c.DstUid))
                .GroupBy(c => c.DstUid + "|" + c.DstPin, StringComparer.OrdinalIgnoreCase);
            foreach (var grp in connGroups)
            {
                var conns = grp.ToList();
                if (conns.Count == 1)
                {
                    var c = conns[0];
                    var sp = g.Elems[c.SrcUid].FindPin(c.SrcPin, false);
                    var dp = g.Elems[c.DstUid].FindPin(c.DstPin, true);
                    if (sp != null && dp != null)
                        AddOrthWire(canvas, sp.Anchor, dp.Anchor, wire);
                }
                else
                {
                    // Parallel: merge multiple lines into a shared vertical line plus individual horizontal branches
                    var dp = g.Elems[conns[0].DstUid].FindPin(conns[0].DstPin, true);
                    if (dp == null) continue;
                    double minY = double.MaxValue, maxY = double.MinValue;
                    double maxSrcX = double.MinValue;
                    var branches = new List<(double srcX, double srcY)>();
                    foreach (var c in conns)
                    {
                        var sp = g.Elems[c.SrcUid].FindPin(c.SrcPin, false);
                        if (sp == null) continue;
                        branches.Add((sp.Anchor.X, sp.Anchor.Y));
                        minY = Math.Min(minY, sp.Anchor.Y);
                        maxY = Math.Max(maxY, sp.Anchor.Y);
                        maxSrcX = Math.Max(maxSrcX, sp.Anchor.X);
                    }
                    if (branches.Count == 0) continue;

                    // Shared vertical line X: placed between the “rightmost branch output” and the “target pin”, closer to the branch
                    // output, so each branch's horizontal run is as short as possible and the vertical line only joins at branch ends.
                    double midX = (maxSrcX + dp.Anchor.X) / 2;

                    // The next-level horizontal line follows the “topmost branch” line (topmost), not the midpoint.
                    // The layout already aligns outputs/next-level pins to the topmost branch Y; minY is taken explicitly here for consistency.
                    double trunkY = minY;

                    // Individual branch horizontal lines → shared vertical line
                    foreach (var (sx, sy) in branches)
                        AddLine(canvas, sx, sy, midX, sy, wire, 2);

                    // Shared vertical line: from the topmost branch to the bottommost branch (covering all branches)
                    AddLine(canvas, midX, minY, midX, maxY, wire, 2);

                    // Next-level horizontal line: from the shared vertical line along the topmost branch line to the target pin.
                    // If the target pin Y deviates slightly from trunkY, fall back to an orthogonal polyline.
                    if (Math.Abs(dp.Anchor.Y - trunkY) < 0.5)
                        AddLine(canvas, midX, trunkY, dp.Anchor.X, trunkY, wire, 2);
                    else
                        AddOrthWire(canvas, new Point(midX, trunkY), dp.Anchor, wire);
                }
            }

            // Wires: operand ↔ pin
            foreach (var op in g.Operands)
            {
                if (!g.Elems.TryGetValue(op.TargetElemUid, out var te)) continue;
                var pin = te.FindPin(op.TargetPin, op.TargetPinIsInput);
                if (pin == null) continue;
                AddOrthWire(canvas, op.Anchor, pin.Anchor, wire);
            }

            // Operand labels
            foreach (var op in g.Operands)
                DrawOperand(canvas, op, muted, txt);

            // Elements
            foreach (var e in g.Elems.Values)
            {
                if (e.IsContactCoil) DrawContactCoil(canvas, e, sym, txt, wire);
                else DrawBlock(canvas, e, nodeBg, nodeBd, txt, muted, sym);
            }

            return canvas;
        }

        // ── Drawing helpers ─────────────────────────────────────────────
        private static void AddLine(Canvas c, double x1, double y1, double x2, double y2, Brush b, double th)
        {
            c.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = b, StrokeThickness = th, SnapsToDevicePixels = true });
        }

        // Orthogonal wire: horizontal-vertical-horizontal, all right angles (LAD style)
        private static void AddOrthWire(Canvas c, Point from, Point to, Brush b)
        {
            const double th = 2.0;
            if (Math.Abs(from.Y - to.Y) < 0.5)
            {
                AddLine(c, from.X, from.Y, to.X, to.Y, b, th);
                return;
            }
            double midX = (from.X + to.X) / 2;
            AddLine(c, from.X, from.Y, midX, from.Y, b, th);
            AddLine(c, midX, from.Y, midX, to.Y, b, th);
            AddLine(c, midX, to.Y, to.X, to.Y, b, th);
        }

        private static void DrawOperand(Canvas c, Operand op, Brush border, Brush txt)
        {
            var tb = new TextBlock
            {
                Text = op.Text,
                Foreground = txt,
                FontFamily = _typeface.FontFamily,
                FontSize = FontSizeAccess,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var bd = new Border
            {
                Child = tb,
                Width = op.W,
                Height = op.H,
                Padding = new Thickness(4, 2, 4, 2),
                Background = Brushes.Transparent
            };
            Canvas.SetLeft(bd, op.X);
            Canvas.SetTop(bd, op.Y);
            c.Children.Add(bd);
        }

        private static void DrawContactCoil(Canvas c, Elem e, Brush sym, Brush txt, Brush wire)
        {
            double cx = e.X + e.W / 2;
            double symY = e.Y + 8;
            double midY = symY + 8;

            if (IsCoil(e.Kind))
            {
                // Coil -( )-  → wires connect to the outside of the arcs, arcs drawn on top so wires don't pierce them
                double r = 8;

                // 1. Horizontal lines connect to the outside of the arcs (cx-2r / cx+2r), not entering the arc span
                AddLine(c, e.X, midY, cx - 2 * r, midY, wire, 2);
                AddLine(c, cx + 2 * r, midY, e.X + e.W, midY, wire, 2);

                // 2. Arcs drawn on top of the wires (left arc + right arc)
                var leftArc = new Path
                {
                    Stroke = sym, StrokeThickness = 2,
                    Data = new PathGeometry(new[] { new PathFigure(
                        new Point(cx - r, midY - r),
                        new[] { new ArcSegment(new Point(cx - r, midY + r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true) },
                        false) })
                };
                c.Children.Add(leftArc);

                var rightArc = new Path
                {
                    Stroke = sym, StrokeThickness = 2,
                    Data = new PathGeometry(new[] { new PathFigure(
                        new Point(cx + r, midY - r),
                        new[] { new ArcSegment(new Point(cx + r, midY + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true) },
                        false) })
                };
                c.Children.Add(rightArc);

                var letter = CoilLetter(e.Kind);
                if (!string.IsNullOrEmpty(letter))
                    AddCentifiedText(c, letter, cx, midY, sym, FontSizePin, true);
            }
            else
            {
                // Contact -| |-  or NC -|/|-  or -|P|- / -|N|- / -|NOT|-
                double half = IsNotContact(e.Kind) ? 18 : 8;
                AddLine(c, e.X, midY, cx - half, midY, wire, 2);
                AddLine(c, cx + half, midY, e.X + e.W, midY, wire, 2);
                AddLine(c, cx - half, midY - 9, cx - half, midY + 9, sym, 2);
                AddLine(c, cx + half, midY - 9, cx + half, midY + 9, sym, 2);
                if (e.Negated)
                    AddLine(c, cx - half + 2, midY + 8, cx + half - 2, midY - 8, sym, 2);

                // Special contact types: P / N / NOT text
                var contactLetter = ContactLetter(e.Kind);
                if (!string.IsNullOrEmpty(contactLetter))
                    AddCentifiedText(c, contactLetter, cx, midY, sym, FontSizePin, true);
            }

            // Main variable name (operand pin) → centered directly above the symbol
            if (!string.IsNullOrWhiteSpace(e.Operand))
            {
                var tb = new TextBlock
                {
                    Text = e.Operand,
                    Foreground = txt,
                    FontFamily = _typeface.FontFamily,
                    FontSize = FontSizeTitle,
                    TextAlignment = TextAlignment.Center,
                    Width = Math.Max(e.W, 60)
                };
                Canvas.SetLeft(tb, e.X + (e.W - Math.Max(e.W, 60)) / 2);
                Canvas.SetTop(tb, e.Y - FontSizeTitle - 2);
                c.Children.Add(tb);
            }

            // Auxiliary variables (edge-memory bits etc.) → centered directly below the symbol
            if (!string.IsNullOrWhiteSpace(e.OperandBelow))
            {
                var tb = new TextBlock
                {
                    Text = e.OperandBelow,
                    Foreground = txt,
                    FontFamily = _typeface.FontFamily,
                    FontSize = FontSizeTitle,
                    TextAlignment = TextAlignment.Center
                };
                // No width limit, so it displays in full
                var sz = MeasureText(e.OperandBelow, FontSizeTitle);
                double labelW = Math.Max(sz.Width + 4, e.W);
                tb.Width = labelW;
                Canvas.SetLeft(tb, e.X + (e.W - labelW) / 2);
                Canvas.SetTop(tb, e.Y + e.H - 15);
                c.Children.Add(tb);
            }

            // Transparent hit area for right-click cross-reference interaction
            if (_onElementContext != null)
            {
                var hitArea = new Border
                {
                    Width = e.W,
                    Height = e.H,
                    Background = Brushes.Transparent,
                    Tag = e
                };
                Canvas.SetLeft(hitArea, e.X);
                Canvas.SetTop(hitArea, e.Y);
                AttachContextMenu(hitArea, e);
                c.Children.Add(hitArea);
            }
        }

        private static string CoilLetter(string kind)
        {
            var k = (kind ?? "").ToLowerInvariant();
            if (k == "scoil" || k == "set_coil") return "S";
            if (k == "rcoil" || k == "reset_coil") return "R";
            if (k == "pcoil") return "P";
            if (k == "ncoil") return "N";
            return "";
        }

        private static string ContactLetter(string kind)
        {
            var k = (kind ?? "").ToLowerInvariant();
            if (k == "pcontact") return "P";
            if (k == "ncontact") return "N";
            if (k == "not_contact" || k == "not") return "NOT";
            return "";
        }

        private static bool IsNotContact(string kind)
        {
            var k = (kind ?? "").Trim().ToLowerInvariant();
            return k == "not_contact" || k == "not";
        }

        private static void DrawBlock(Canvas c, Elem e, Brush bg, Brush border, Brush txt, Brush muted, Brush sym)
        {
            // Show the operand variable name above the block (e.g. an RS block's instance variable)
            if (!string.IsNullOrWhiteSpace(e.Operand))
            {
                var opTb = new TextBlock
                {
                    Text = e.Operand,
                    Foreground = txt,
                    FontFamily = _typeface.FontFamily,
                    FontSize = FontSizeTitle,
                    TextAlignment = TextAlignment.Center,
                    Width = Math.Max(e.W, 60)
                };
                Canvas.SetLeft(opTb, e.X + (e.W - Math.Max(e.W, 60)) / 2);
                Canvas.SetTop(opTb, e.Y - FontSizeTitle - 2);
                c.Children.Add(opTb);
            }

            var rect = new Border
            {
                Width = e.W,
                Height = e.H,
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Tag = e
            };
            AttachContextMenu(rect, e);
            Canvas.SetLeft(rect, e.X);
            Canvas.SetTop(rect, e.Y);
            c.Children.Add(rect);

            // Title bar
            int titleLines = TitleLineCount(e.Title);
            double titleH = HeaderH + (titleLines - 1) * (FontSizeTitle + 4);
            var title = new TextBlock
            {
                Text = e.Title,
                Foreground = txt,
                FontFamily = _typeface.FontFamily,
                FontSize = FontSizeTitle,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Width = e.W
            };
            Canvas.SetLeft(title, e.X);
            Canvas.SetTop(title, e.Y + 5);
            c.Children.Add(title);
            AddLine(c, e.X, e.Y + titleH - 2, e.X + e.W, e.Y + titleH - 2, border, 1);

            // Pin names + pin stubs (the "operand" pin is not rendered at all)
            foreach (var p in e.Inputs)
            {
                if (string.Equals(p.Name, "operand", StringComparison.OrdinalIgnoreCase))
                    continue;
                var tb = new TextBlock { Text = p.Name, Foreground = muted, FontFamily = _typeface.FontFamily, FontSize = FontSizePin };
                Canvas.SetLeft(tb, e.X + BlockPadX * 0.5);
                Canvas.SetTop(tb, e.Y + p.LocalY - FontSizePin);
                c.Children.Add(tb);
                AddLine(c, p.Anchor.X - 6, p.Anchor.Y, p.Anchor.X, p.Anchor.Y, sym, 1.5);
            }
            foreach (var p in e.Outputs)
            {
                if (string.Equals(p.Name, "operand", StringComparison.OrdinalIgnoreCase))
                    continue;
                var w = MeasureText(p.Name, FontSizePin).Width;
                var tb = new TextBlock { Text = p.Name, Foreground = muted, FontFamily = _typeface.FontFamily, FontSize = FontSizePin };
                Canvas.SetLeft(tb, e.X + e.W - BlockPadX * 0.5 - w);
                Canvas.SetTop(tb, e.Y + p.LocalY - FontSizePin);
                c.Children.Add(tb);
                AddLine(c, p.Anchor.X, p.Anchor.Y, p.Anchor.X + 6, p.Anchor.Y, sym, 1.5);
            }
        }

        private static void AddCentifiedText(Canvas c, string text, double cx, double cy, Brush b, double fs, bool center)
        {
            var sz = MeasureText(text, fs);
            var tb = new TextBlock { Text = text, Foreground = b, FontFamily = _typeface.FontFamily, FontSize = fs };
            Canvas.SetLeft(tb, cx - sz.Width / 2);
            Canvas.SetTop(tb, cy - sz.Height / 2);
            c.Children.Add(tb);
        }
    }
}
