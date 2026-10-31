using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace TIAOpennessStudio.LadRendering.Rendering
{
    /// <summary>
    /// LAD layout engine — based on topology matrix + spatial compaction + orthogonal routing
    /// 
    /// References:
    /// [1] Tamassia, R. (1987). "On Embedding a Graph in the Grid with the Minimum Number of Bends"
    ///     - Topology-Shape-Metric methodology
    /// [2] Sugiyama, K. et al. (1981). "Methods for Visual Understanding of Hierarchical System Structures"
    ///     - foundational layered-layout algorithm
    /// [3] Battista, G.D. et al. (1998). "Graph Drawing: Algorithms for the Visualization of Graphs"
    ///     - orthogonal layout and compaction algorithms
    /// [4] Freivalds, K. & Glagoļevs, J. (2014). "Graph Compact Orthogonal Layout Algorithm"
    ///     - compact orthogonal layout compaction
    /// [5] Lengauer, T. (1990). "Combinatorial Algorithms for Integrated Circuit Layout"
    ///     - topological compaction in integrated-circuit layout
    /// </summary>
    public class LadLayoutEngine
    {
        #region Configuration parameters

        /// <summary>Minimum horizontal rung spacing (between contacts)</summary>
        public double ContactHSpacing { get; set; } = 40.0;
        /// <summary>Minimum horizontal spacing of function blocks</summary>
        public double BlockHSpacing { get; set; } = 60.0;
        /// <summary>Minimum vertical rung spacing</summary>
        public double RungVSpacing { get; set; } = 50.0;
        /// <summary>Width of the left power rail</summary>
        public double PowerRailWidth { get; set; } = 30.0;
        /// <summary>Radius of a pin port</summary>
        public double PortRadius { get; set; } = 5.0;
        /// <summary>Minimum edge channel width</summary>
        public double MinChannelWidth { get; set; } = 20.0;
        /// <summary>Maximum number of iterations</summary>
        public int MaxIterations { get; set; } = 32;

        #endregion

        #region Data structures

        /// <summary>
        /// Node semantic category (determines layout priority)
        /// </summary>
        public enum NodeCategory
        {
            /// <summary>Contact (NO/NC/comparison)</summary>
            Contact,
            /// <summary>Function block (Call/arithmetic/timer/counter)</summary>
            Block,
            /// <summary>Coil (output/set/reset)</summary>
            Coil
        }

        /// <summary>
        /// Layout node
        /// </summary>
        public class LayoutNode
        {
            public string Uid { get; set; }
            public string Type { get; set; }
            public string Label { get; set; }
            public NodeCategory Category { get; set; }
            public bool IsNegated { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }

            // Layout results
            public double X { get; set; }
            public double Y { get; set; }
            public int Layer { get; set; }
            public int OrderInLayer { get; set; }

            // Pin info
            public List<LayoutPort> InputPorts { get; set; } = new List<LayoutPort>();
            public List<LayoutPort> OutputPorts { get; set; } = new List<LayoutPort>();
        }

        /// <summary>
        /// Pin port
        /// </summary>
        public class LayoutPort
        {
            public string Name { get; set; }
            public bool IsInput { get; set; }
            /// <summary>Port Y coordinate relative to the node (between 0 and 1)</summary>
            public double RelativeY { get; set; }
            /// <summary>Port absolute coordinates (computed after layout)</summary>
            public double AbsoluteX { get; set; }
            public double AbsoluteY { get; set; }
        }

        /// <summary>
        /// Layout edge
        /// </summary>
        public class LayoutEdge
        {
            public string SourceUid { get; set; }
            public string TargetUid { get; set; }
            public string SourcePort { get; set; }
            public string TargetPort { get; set; }

            // Routing results
            public List<Point> RoutePoints { get; set; } = new List<Point>();
            public int BendCount { get; set; }
        }

        /// <summary>
        /// Topology matrix — matrix representation of adjacency
        /// </summary>
        public class TopologyMatrix
        {
            /// <summary>Node index map (uid -> index)</summary>
            public Dictionary<string, int> IndexMap { get; set; }
            /// <summary>Reverse map (index -> uid)</summary>
            public string[] UidByIndex { get; set; }
            /// <summary>Adjacency matrix; [i,j] = true means an edge i -> j</summary>
            public bool[,] AdjMatrix { get; set; }
            /// <summary>Reachability matrix; [i,j] = true means i can reach j</summary>
            public bool[,] ReachMatrix { get; set; }
            /// <summary>In-degree array</summary>
            public int[] InDegree { get; set; }
            /// <summary>Out-degree array</summary>
            public int[] OutDegree { get; set; }
            /// <summary>Node count</summary>
            public int NodeCount { get; set; }
        }

        /// <summary>
        /// Constraint-graph node (for spatial compaction)
        /// </summary>
        private class ConstraintNode
        {
            public string Id { get; set; }
            public double Value { get; set; }
            public double MinSize { get; set; }
        }

        /// <summary>
        /// Constraint-graph edge (for spatial compaction)
        /// </summary>
        private class ConstraintEdge
        {
            public string From { get; set; }
            public string To { get; set; }
            public double MinDistance { get; set; }
        }

        /// <summary>
        /// Rung info
        /// </summary>
        public class RungInfo
        {
            public int RungId { get; set; }
            public List<string> NodeUids { get; set; } = new List<string>();
            public string SourceUid { get; set; }
            public List<string> SinkUids { get; set; } = new List<string>();
        }

        /// <summary>
        /// Layout result
        /// </summary>
        public class LayoutResult
        {
            public Dictionary<string, LayoutNode> Nodes { get; set; }
            public List<LayoutEdge> Edges { get; set; }
            public List<RungInfo> Rungs { get; set; }
            public TopologyMatrix Topology { get; set; }
            public double TotalWidth { get; set; }
            public double TotalHeight { get; set; }
            public double PowerRailHeight { get; set; }
        }

        #endregion

        #region Public interface

        /// <summary>
        /// Run the full layout computation
        /// </summary>
        public LayoutResult Layout(
            Dictionary<string, (string type, string label, bool isNegated, double width, double height)> nodeDefs,
            List<(string src, string dst, string srcPin, string dstPin)> edgeDefs,
            Dictionary<string, List<(string pin, bool isInput)>> pinDefs = null)
        {
            // ===== Phase 1: topology matrix generation =====
            var nodes = BuildNodes(nodeDefs, pinDefs);
            var edges = BuildEdges(edgeDefs);
            var topology = BuildTopologyMatrix(nodes, edges);
            var layers = AssignLayersFromTopology(topology, nodes);
            var rungs = DetectRungs(topology, nodes, edges);

            // ===== Phase 2: spatial compaction =====
            // 2a: vertical compaction — determine the vertical position of nodes within each layer
            var verticalPositions = CompactVertical(layers, nodes, rungs);
            // 2b: horizontal compaction — determine the horizontal position of each layer
            var horizontalPositions = CompactHorizontal(layers, nodes, topology);

            ApplyPositions(nodes, verticalPositions, horizontalPositions);

            // ===== Phase 3: orthogonal routing =====
            // 3a: pin port assignment
            AssignPorts(nodes, edges);
            // 3b: orthogonal routing (with bend minimization)
            var routedEdges = RouteEdgesOrthogonal(nodes, edges, topology);

            // Compute the total size
            double totalWidth = 0, totalHeight = 0;
            foreach (var node in nodes.Values)
            {
                totalWidth = Math.Max(totalWidth, node.X + node.Width);
                totalHeight = Math.Max(totalHeight, node.Y + node.Height);
            }

            return new LayoutResult
            {
                Nodes = nodes,
                Edges = routedEdges,
                Rungs = rungs,
                Topology = topology,
                TotalWidth = totalWidth + PowerRailWidth + 40,
                TotalHeight = totalHeight + 60,
                PowerRailHeight = totalHeight
            };
        }

        #endregion

        #region Phase 1: topology matrix generation

        /// <summary>
        /// Build the layout node set
        /// </summary>
        private Dictionary<string, LayoutNode> BuildNodes(
            Dictionary<string, (string type, string label, bool isNegated, double width, double height)> nodeDefs,
            Dictionary<string, List<(string pin, bool isInput)>> pinDefs)
        {
            var nodes = new Dictionary<string, LayoutNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in nodeDefs)
            {
                var (type, label, isNegated, width, height) = kvp.Value;
                var node = new LayoutNode
                {
                    Uid = kvp.Key,
                    Type = type,
                    Label = label,
                    Category = ClassifyNode(type),
                    IsNegated = isNegated,
                    Width = width,
                    Height = height
                };

                // Assign pins
                if (pinDefs != null && pinDefs.ContainsKey(kvp.Key))
                {
                    foreach (var (pin, isInput) in pinDefs[kvp.Key])
                    {
                        var port = new LayoutPort { Name = pin, IsInput = isInput };
                        if (isInput) node.InputPorts.Add(port);
                        else node.OutputPorts.Add(port);
                    }
                }

                // Ensure at least one input and one output port
                if (node.InputPorts.Count == 0)
                    node.InputPorts.Add(new LayoutPort { Name = "in", IsInput = true });
                if (node.OutputPorts.Count == 0)
                    node.OutputPorts.Add(new LayoutPort { Name = "out", IsInput = false });

                // Pin ordering: en/eno first, the rest sorted by name
                node.InputPorts = node.InputPorts
                    .OrderBy(p => p.Name == "en" ? 0 : 1)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                node.OutputPorts = node.OutputPorts
                    .OrderBy(p => p.Name == "eno" ? 0 : 1)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                nodes[kvp.Key] = node;
            }
            return nodes;
        }

        /// <summary>
        /// Build layout edges
        /// </summary>
        private List<LayoutEdge> BuildEdges(List<(string src, string dst, string srcPin, string dstPin)> edgeDefs)
        {
            return edgeDefs.Select(e => new LayoutEdge
            {
                SourceUid = e.src,
                TargetUid = e.dst,
                SourcePort = e.srcPin,
                TargetPort = e.dstPin
            }).ToList();
        }

        /// <summary>
        /// Classify a node
        /// </summary>
        private NodeCategory ClassifyNode(string type)
        {
            var t = (type ?? "").Trim().ToLowerInvariant();
            switch (t)
            {
                case "contact":
                case "pcontact":
                case "ncontact":
                case "not_contact":
                case "not":
                    return NodeCategory.Contact;
                case "ne": case "eq": case "gt": case "ge": case "lt": case "le":
                    return NodeCategory.Contact;
                case "coil": case "scoil": case "rcoil":
                case "pcoil": case "ncoil":
                    return NodeCategory.Coil;
                default:
                    return NodeCategory.Block;
            }
        }

        /// <summary>
        /// Build the topology matrix
        /// Adjacency matrix + reachability matrix (Warshall's algorithm)
        /// </summary>
        private TopologyMatrix BuildTopologyMatrix(
            Dictionary<string, LayoutNode> nodes,
            List<LayoutEdge> edges)
        {
            int n = nodes.Count;
            var indexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var uidByIndex = new string[n];
            int idx = 0;
            foreach (var uid in nodes.Keys)
            {
                indexMap[uid] = idx;
                uidByIndex[idx] = uid;
                idx++;
            }

            var adjMatrix = new bool[n, n];
            var inDegree = new int[n];
            var outDegree = new int[n];

            foreach (var edge in edges)
            {
                if (!indexMap.ContainsKey(edge.SourceUid) || !indexMap.ContainsKey(edge.TargetUid))
                    continue;
                int si = indexMap[edge.SourceUid];
                int ti = indexMap[edge.TargetUid];
                if (si != ti && !adjMatrix[si, ti])
                {
                    adjMatrix[si, ti] = true;
                    outDegree[si]++;
                    inDegree[ti]++;
                }
            }

            // Compute the reachability matrix with Warshall's algorithm
            var reachMatrix = (bool[,])adjMatrix.Clone();
            for (int k = 0; k < n; k++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (reachMatrix[i, k])
                    {
                        for (int j = 0; j < n; j++)
                        {
                            if (reachMatrix[k, j])
                                reachMatrix[i, j] = true;
                        }
                    }
                }
            }

            return new TopologyMatrix
            {
                IndexMap = indexMap,
                UidByIndex = uidByIndex,
                AdjMatrix = adjMatrix,
                ReachMatrix = reachMatrix,
                InDegree = inDegree,
                OutDegree = outDegree,
                NodeCount = n
            };
        }

        /// <summary>
        /// Layer assignment based on the topology matrix
        /// Uses the longest-path algorithm + semantic correction (contacts shift left, coils shift right)
        /// </summary>
        private List<List<string>> AssignLayersFromTopology(
            TopologyMatrix topology,
            Dictionary<string, LayoutNode> nodes)
        {
            int n = topology.NodeCount;
            var layerOf = new int[n];
            var inDeg = (int[])topology.InDegree.Clone();

            // Longest-path layer assignment
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (inDeg[i] == 0)
                {
                    queue.Enqueue(i);
                    layerOf[i] = 0;
                }
            }

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                for (int j = 0; j < n; j++)
                {
                    if (topology.AdjMatrix[cur, j])
                    {
                        int newLayer = layerOf[cur] + 1;
                        if (newLayer > layerOf[j])
                            layerOf[j] = newLayer;
                        inDeg[j]--;
                        if (inDeg[j] == 0)
                            queue.Enqueue(j);
                    }
                }
            }

            // Semantic correction: force coils into the rightmost layer
            int maxLayer = layerOf.Length > 0 ? layerOf.Max() : 0;
            for (int i = 0; i < n; i++)
            {
                var uid = topology.UidByIndex[i];
                if (nodes.ContainsKey(uid) && nodes[uid].Category == NodeCategory.Coil)
                {
                    // Compute the coil's required layer: max(current layer, max predecessor layer + 1, maxLayer)
                    int predMaxLayer = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (topology.AdjMatrix[j, i])
                            predMaxLayer = Math.Max(predMaxLayer, layerOf[j] + 1);
                    }
                    layerOf[i] = Math.Max(Math.Max(layerOf[i], predMaxLayer), maxLayer);
                }
            }

            // Correction: ensure all edge directions are correct (src < dst)
            for (int iter = 0; iter < 10; iter++)
            {
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        if (topology.AdjMatrix[i, j] && layerOf[i] >= layerOf[j])
                        {
                            layerOf[j] = layerOf[i] + 1;
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
            }

            // Ensure coils are in the rightmost layer again
            maxLayer = layerOf.Max();
            for (int i = 0; i < n; i++)
            {
                var uid = topology.UidByIndex[i];
                if (nodes.ContainsKey(uid) && nodes[uid].Category == NodeCategory.Coil)
                    layerOf[i] = maxLayer;
            }

            for (int i = 0; i < n; i++)
            {
                var uid = topology.UidByIndex[i];
                if (nodes.ContainsKey(uid))
                    nodes[uid].Layer = layerOf[i];
            }

            // Group by layer and return
            int layerCount = maxLayer + 1;
            var layers = new List<List<string>>(layerCount);
            for (int l = 0; l < layerCount; l++)
                layers.Add(new List<string>());

            for (int i = 0; i < n; i++)
                layers[layerOf[i]].Add(topology.UidByIndex[i]);

            return layers;
        }

        /// <summary>
        /// Rung detection
        /// Start from nodes with in-degree 0, BFS along edges, and identify independent rung paths
        /// </summary>
        private List<RungInfo> DetectRungs(
            TopologyMatrix topology,
            Dictionary<string, LayoutNode> nodes,
            List<LayoutEdge> edges)
        {
            var rungs = new List<RungInfo>();
            int n = topology.NodeCount;
            var assigned = new bool[n];

            // Build the successor map (using string uids)
            var succMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var predMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var uid in nodes.Keys)
            {
                succMap[uid] = new List<string>();
                predMap[uid] = new List<string>();
            }
            foreach (var edge in edges)
            {
                if (succMap.ContainsKey(edge.SourceUid) && predMap.ContainsKey(edge.TargetUid))
                {
                    succMap[edge.SourceUid].Add(edge.TargetUid);
                    predMap[edge.TargetUid].Add(edge.SourceUid);
                }
            }

            // Find all source nodes (in-degree 0, non-TypedConstant)
            var sources = nodes.Keys
                .Where(uid => !predMap.ContainsKey(uid) || predMap[uid].Count == 0)
                .Where(uid => !string.Equals(nodes[uid].Type, "Access", StringComparison.OrdinalIgnoreCase))
                .ToList();

            int rungId = 0;
            foreach (var source in sources)
            {
                // BFS starting from the source node
                var bfsQueue = new Queue<string>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bfsQueue.Enqueue(source);
                visited.Add(source);

                var rung = new RungInfo { RungId = rungId++, SourceUid = source };

                while (bfsQueue.Count > 0)
                {
                    var cur = bfsQueue.Dequeue();
                    rung.NodeUids.Add(cur);

                    if (!succMap.ContainsKey(cur)) continue;
                    foreach (var succ in succMap[cur])
                    {
                        if (!visited.Contains(succ))
                        {
                            visited.Add(succ);
                            bfsQueue.Enqueue(succ);
                        }
                    }
                }

                // Find the rung's sink nodes (nodes with out-degree 0)
                rung.SinkUids = rung.NodeUids
                    .Where(uid => !succMap.ContainsKey(uid) || succMap[uid].Count == 0)
                    .ToList();

                rungs.Add(rung);
            }

            // Handle nodes that were not assigned (possibly in a cycle)
            foreach (var uid in nodes.Keys)
            {
                if (!rungs.Any(r => r.NodeUids.Contains(uid, StringComparer.OrdinalIgnoreCase)))
                {
                    rungs.Add(new RungInfo
                    {
                        RungId = rungId++,
                        SourceUid = uid,
                        NodeUids = new List<string> { uid },
                        SinkUids = new List<string> { uid }
                    });
                }
            }

            return rungs;
        }

        #endregion

        #region Phase 2: spatial compaction

        /// <summary>
        /// Vertical compaction — constraint-graph longest-path method
        /// Ensure nodes within the same layer do not overlap and minimize vertical space
        /// </summary>
        private Dictionary<string, double> CompactVertical(
            List<List<string>> layers,
            Dictionary<string, LayoutNode> nodes,
            List<RungInfo> rungs)
        {
            var yPositions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // Build a vertical constraint graph per rung
            // Nodes of the same rung are aligned to the same vertical position when possible
            // Minimum-spacing constraints must hold between different rungs

            // Build the rung membership map
            var rungOfUid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int r = 0; r < rungs.Count; r++)
            {
                foreach (var uid in rungs[r].NodeUids)
                {
                    if (!rungOfUid.ContainsKey(uid))
                        rungOfUid[uid] = r;
                }
            }

            // Process each layer's vertical position independently
            foreach (var layer in layers)
            {
                if (layer.Count == 0) continue;

                // Sort by rung ID (nodes of the same rung grouped together)
                var sorted = layer.OrderBy(uid =>
                    rungOfUid.ContainsKey(uid) ? rungOfUid[uid] : int.MaxValue
                ).ToList();

                // Use the constraint graph to determine vertical positions
                // Constraint: nodes of the same rung align to the same Y when possible
                // Constraint: spacing between adjacent nodes >= nodeHeight + spacing

                // Compute each rung's preferred Y position within this layer
                var rungPreferredY = new Dictionary<int, double>();
                double currentY = 0;
                int lastRungId = -1;

                for (int i = 0; i < sorted.Count; i++)
                {
                    var uid = sorted[i];
                    var node = nodes[uid];
                    int rungId = rungOfUid.ContainsKey(uid) ? rungOfUid[uid] : -1;

                    if (rungId != lastRungId && lastRungId != -1)
                    {
                        // Add spacing between different rungs
                        currentY += RungVSpacing;
                    }

                    if (!rungPreferredY.ContainsKey(rungId))
                        rungPreferredY[rungId] = currentY;

                    // Nodes of the same rung share the Y position
                    yPositions[uid] = rungPreferredY[rungId];

                    if (rungId != lastRungId)
                    {
                        currentY = rungPreferredY[rungId] + node.Height;
                        lastRungId = rungId;
                    }
                    else
                    {
                        // Multiple nodes in the same rung: accumulate height
                        currentY = Math.Max(currentY, rungPreferredY[rungId] + node.Height);
                    }
                }
            }

            // Center alignment: compute the total height and center all nodes
            if (yPositions.Count > 0)
            {
                double minY = yPositions.Values.Min();
                foreach (var uid in yPositions.Keys.ToList())
                {
                    yPositions[uid] -= minY;
                }
            }

            return yPositions;
        }

        /// <summary>
        /// Horizontal compaction — constraint-graph longest-path method
        /// Ensure nodes in adjacent layers do not overlap and minimize horizontal space
        /// 
        /// Constraint graph construction:
        /// - Nodes: each layout node maps to two constraint nodes (left, right)
        /// - Edges: right -> left' means left'.x >= right.x + width + spacing
        /// - The longest path gives the minimum feasible coordinates
        /// </summary>
        private Dictionary<string, double> CompactHorizontal(
            List<List<string>> layers,
            Dictionary<string, LayoutNode> nodes,
            TopologyMatrix topology)
        {
            var xPositions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // Build the horizontal constraint graph
            // Constraint type 1: between nodes of the same layer (usually unnecessary, since same-layer nodes share a horizontal position)
            // Constraint type 2: between nodes of adjacent layers: x[next] >= x[current] + width + spacing
            // Constraint type 3: semantic constraint: contact layer spacing < function block layer spacing

            // Compute each layer's X coordinate using the longest-path method
            var layerX = new double[layers.Count];
            double currentX = PowerRailWidth;

            for (int l = 0; l < layers.Count; l++)
            {
                layerX[l] = currentX;

                // Compute the layer's maximum node width
                double maxWidth = 0;
                foreach (var uid in layers[l])
                {
                    if (nodes.ContainsKey(uid))
                        maxWidth = Math.Max(maxWidth, nodes[uid].Width);
                }

                // Decide spacing based on the layer's dominant node type
                double spacing = GetLayerHSpacing(layers[l], nodes);

                // Total distance to the next layer
                if (l < layers.Count - 1)
                    currentX += maxWidth + spacing;
            }

            // Apply X coordinates
            for (int l = 0; l < layers.Count; l++)
            {
                foreach (var uid in layers[l])
                {
                    if (nodes.ContainsKey(uid))
                        xPositions[uid] = layerX[l];
                }
            }

            return xPositions;
        }

        /// <summary>
        /// Return horizontal spacing based on the layer's dominant node type
        /// </summary>
        private double GetLayerHSpacing(List<string> layerUids, Dictionary<string, LayoutNode> nodes)
        {
            int contacts = 0, blocks = 0, coils = 0;
            foreach (var uid in layerUids)
            {
                if (!nodes.ContainsKey(uid)) continue;
                switch (nodes[uid].Category)
                {
                    case NodeCategory.Contact: contacts++; break;
                    case NodeCategory.Block: blocks++; break;
                    case NodeCategory.Coil: coils++; break;
                }
            }

            if (contacts >= blocks && contacts >= coils) return ContactHSpacing;
            if (coils >= contacts && coils >= blocks) return ContactHSpacing;
            return BlockHSpacing;
        }

        /// <summary>
        /// Apply coordinates to nodes
        /// </summary>
        private void ApplyPositions(
            Dictionary<string, LayoutNode> nodes,
            Dictionary<string, double> verticalPositions,
            Dictionary<string, double> horizontalPositions)
        {
            foreach (var node in nodes.Values)
            {
                node.X = horizontalPositions.ContainsKey(node.Uid) ? horizontalPositions[node.Uid] : 0;
                node.Y = verticalPositions.ContainsKey(node.Uid) ? verticalPositions[node.Uid] : 0;
            }
        }

        #endregion

        #region Phase 3: orthogonal routing

        /// <summary>
        /// Pin port assignment
        /// Compute each pin's absolute coordinates on its node
        /// </summary>
        private void AssignPorts(
            Dictionary<string, LayoutNode> nodes,
            List<LayoutEdge> edges)
        {
            foreach (var node in nodes.Values)
            {
                // Input ports: evenly distributed vertically on the left side
                for (int i = 0; i < node.InputPorts.Count; i++)
                {
                    var port = node.InputPorts[i];
                    port.RelativeY = (i + 1.0) / (node.InputPorts.Count + 1.0);
                    port.AbsoluteX = node.X;
                    port.AbsoluteY = node.Y + node.Height * port.RelativeY;
                }

                // Output ports: evenly distributed vertically on the right side
                for (int i = 0; i < node.OutputPorts.Count; i++)
                {
                    var port = node.OutputPorts[i];
                    port.RelativeY = (i + 1.0) / (node.OutputPorts.Count + 1.0);
                    port.AbsoluteX = node.X + node.Width;
                    port.AbsoluteY = node.Y + node.Height * port.RelativeY;
                }
            }
        }

        /// <summary>
        /// Orthogonal routing — with bend minimization
        /// 
        /// Algorithm:
        /// 1. Find the source and target ports
        /// 2. Compute the relay X coordinate (at the layer boundary midway between source and target)
        /// 3. Generate an orthogonal polyline path
        /// 4. Bend minimization: use as few corners as possible
        /// 
        /// Reference: Tamassia (1987), the topology-shape-metric method
        /// </summary>
        private List<LayoutEdge> RouteEdgesOrthogonal(
            Dictionary<string, LayoutNode> nodes,
            List<LayoutEdge> edges,
            TopologyMatrix topology)
        {
            foreach (var edge in edges)
            {
                var srcNode = nodes.ContainsKey(edge.SourceUid) ? nodes[edge.SourceUid] : null;
                var dstNode = nodes.ContainsKey(edge.TargetUid) ? nodes[edge.TargetUid] : null;
                if (srcNode == null || dstNode == null) continue;

                // Find the corresponding ports
                var srcPort = FindPort(srcNode, edge.SourcePort, false);
                var dstPort = FindPort(dstNode, edge.TargetPort, true);

                // Compute the routing path
                edge.RoutePoints = ComputeOrthogonalRoute(
                    srcPort.AbsoluteX, srcPort.AbsoluteY,
                    dstPort.AbsoluteX, dstPort.AbsoluteY,
                    srcNode, dstNode);

                edge.BendCount = Math.Max(0, edge.RoutePoints.Count - 2);
            }

            return edges;
        }

        /// <summary>
        /// Find the port with the specified name
        /// </summary>
        private LayoutPort FindPort(LayoutNode node, string portName, bool isInput)
        {
            var ports = isInput ? node.InputPorts : node.OutputPorts;
            var normalized = (portName ?? "").Trim().ToLowerInvariant();

            // Exact match
            var found = ports.FirstOrDefault(p =>
                string.Equals(p.Name, normalized, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;

            // Default to the first one
            return ports.FirstOrDefault() ?? new LayoutPort
            {
                Name = isInput ? "in" : "out",
                IsInput = isInput,
                AbsoluteX = isInput ? node.X : node.X + node.Width,
                AbsoluteY = node.Y + node.Height / 2
            };
        }

        /// <summary>
        /// Compute the orthogonal routing path
        /// 
        /// Strategy:
        /// - Adjacent-layer direct: source right -> relay -> target left
        /// - Cross-layer connection: source right -> multiple relay points -> target left
        /// - Bend minimization: use the fewest right-angle corners
        /// </summary>
        private List<Point> ComputeOrthogonalRoute(
            double srcX, double srcY,
            double dstX, double dstY,
            LayoutNode srcNode, LayoutNode dstNode)
        {
            var points = new List<Point>();

            // Source point (the node's right-side output port)
            points.Add(new Point(srcX, srcY));

            double dx = dstX - srcX;
            double dy = dstY - srcY;

            if (Math.Abs(dy) < 1.0)
            {
                // Straight horizontal connection (same Y)
                points.Add(new Point(dstX, dstY));
                return points;
            }

            if (dx > 0)
            {
                // Normal direction (left to right)
                // Use the mid X coordinate as the relay point
                double midX = srcX + dx / 2;

                if (Math.Abs(dx) < 5.0)
                {
                    // Nearly the same X, so an S-shaped route is needed
                    double offsetX = Math.Max(srcNode?.Width ?? 40, dstNode?.Width ?? 40) / 2 + 15;
                    midX = srcX + offsetX;
                    points.Add(new Point(midX, srcY));
                    points.Add(new Point(midX, dstY));
                }
                else
                {
                    // Standard Z- or C-shaped route
                    points.Add(new Point(midX, srcY));
                    points.Add(new Point(midX, dstY));
                }
            }
            else
            {
                // Reverse connection (right to left, e.g. a feedback loop)
                // A detour is needed
                double detourX = Math.Max(srcX, dstX) + 40;
                double midY = (srcY + dstY) / 2;
                points.Add(new Point(detourX, srcY));
                points.Add(new Point(detourX, midY));
                points.Add(new Point(dstX - 20, midY));
                points.Add(new Point(dstX - 20, dstY));
            }

            // Target point (the node's left-side input port)
            points.Add(new Point(dstX, dstY));

            return points;
        }

        #endregion
    }
}
