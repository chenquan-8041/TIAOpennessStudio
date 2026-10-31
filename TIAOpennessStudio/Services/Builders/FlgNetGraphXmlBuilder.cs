using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// Generic FlgNet graph serializer: Parts + Accesses + Wires.
    ///
    /// The same XML shape is used by LAD networks and by GRAPH transition / supervision / interlock
    /// conditions. GRAPH nests FlgNet under the Graph namespace, so the namespace is a parameter
    /// instead of being taken from <see cref="PlcBlockXmlHelpers.FlgNetNs"/>.
    /// </summary>
    public static class FlgNetGraphXmlBuilder
    {
        /// <summary>Serialize a Parts/Accesses/Wires graph into a namespace-bound FlgNet element.</summary>
        public static XElement BuildFlgNet(XNamespace ns, GraphFlgNetDefinition definition)
        {
            if (ns == null) throw new ArgumentNullException(nameof(ns));
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (definition.Parts.Count == 0)
                throw new ArgumentException("FlgNet requires at least one Part.");

            var used = new HashSet<int>();
            var next = 21;

            foreach (var access in definition.Accesses)
                access.UId = AssignUId(access.UId, used, ref next);
            foreach (var part in definition.Parts)
                part.UId = AssignUId(part.UId, used, ref next);
            foreach (var wire in definition.Wires)
                wire.UId = AssignUId(wire.UId, used, ref next);

            var partIds = new HashSet<int>(definition.Parts.Select(p => p.UId));
            var accessIds = new HashSet<int>(definition.Accesses.Select(a => a.UId));

            var partsElement = new XElement(ns + "Parts");
            foreach (var access in definition.Accesses)
                partsElement.Add(BuildAccess(ns, access));
            foreach (var part in definition.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Name))
                    throw new ArgumentException("FlgNet Part name is required.");
                partsElement.Add(new XElement(ns + "Part",
                    new XAttribute("Name", part.Name),
                    new XAttribute("UId", part.UId)));
            }

            var wiresElement = new XElement(ns + "Wires");
            foreach (var wire in definition.Wires)
            {
                ValidateNode(wire.From, partIds, accessIds);
                ValidateNode(wire.To, partIds, accessIds);
                wiresElement.Add(new XElement(ns + "Wire",
                    new XAttribute("UId", wire.UId),
                    BuildNode(ns, wire.From),
                    BuildNode(ns, wire.To)));
            }

            return new XElement(ns + "FlgNet", partsElement, wiresElement);
        }

        /// <summary>
        /// Minimal condition used by generated transitions / supervisions / interlocks: a single coil
        /// (TrCoil / SvCoil / IlCoil) fed from the power rail.
        /// </summary>
        public static GraphFlgNetDefinition CreateCoilCondition(string coilName, int coilUId = 21, int wireUId = 22)
        {
            var coil = new GraphFlgNetPart { Name = coilName, UId = coilUId };
            return new GraphFlgNetDefinition
            {
                Parts = new List<GraphFlgNetPart> { coil },
                Wires = new List<GraphFlgNetWire>
                {
                    new GraphFlgNetWire
                    {
                        UId = wireUId,
                        From = new GraphFlgNetNode { Kind = "Powerrail" },
                        To = new GraphFlgNetNode { Kind = "NameCon", UId = coilUId, Pin = "in" }
                    }
                }
            };
        }

        private static int AssignUId(int uid, HashSet<int> used, ref int next)
        {
            if (uid > 0)
            {
                if (!used.Add(uid))
                    throw new ArgumentException("Duplicate FlgNet UId: " + uid);
                return uid;
            }

            while (used.Contains(next)) next++;
            used.Add(next);
            return next++;
        }

        private static void ValidateNode(GraphFlgNetNode node, HashSet<int> partIds, HashSet<int> accessIds)
        {
            if (node == null) throw new ArgumentException("FlgNet wire endpoint is required.");
            var kind = (node.Kind ?? "").Trim();
            switch (kind.ToLowerInvariant())
            {
                case "powerrail":
                    return;
                case "namecon":
                    if (!partIds.Contains(node.UId))
                        throw new ArgumentException($"FlgNet NameCon references a missing Part UId: {node.UId}");
                    if (string.IsNullOrWhiteSpace(node.Pin))
                        throw new ArgumentException("FlgNet NameCon is missing a pin name.");
                    return;
                case "identcon":
                case "opencon":
                    if (!partIds.Contains(node.UId) && !accessIds.Contains(node.UId))
                        throw new ArgumentException($"FlgNet {kind} references a missing UId: {node.UId}");
                    return;
                default:
                    throw new ArgumentException("Unsupported FlgNet wire endpoint kind: " + kind);
            }
        }

        private static XElement BuildNode(XNamespace ns, GraphFlgNetNode node)
        {
            switch ((node.Kind ?? "").Trim().ToLowerInvariant())
            {
                case "powerrail":
                    return new XElement(ns + "Powerrail");
                case "namecon":
                    return new XElement(ns + "NameCon",
                        new XAttribute("UId", node.UId),
                        new XAttribute("Name", node.Pin));
                case "identcon":
                    return new XElement(ns + "IdentCon", new XAttribute("UId", node.UId));
                case "opencon":
                    return new XElement(ns + "OpenCon", new XAttribute("UId", node.UId));
                default:
                    throw new ArgumentException("Unsupported FlgNet wire endpoint kind: " + node.Kind);
            }
        }

        private static XElement BuildAccess(XNamespace ns, GraphFlgNetAccess access)
        {
            var scope = string.IsNullOrWhiteSpace(access.Scope) ? "GlobalVariable" : access.Scope.Trim();
            var element = new XElement(ns + "Access",
                new XAttribute("Scope", scope),
                new XAttribute("UId", access.UId));

            if (string.Equals(scope, "LiteralConstant", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scope, "TypedConstant", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(access.ConstantValue))
                    throw new ArgumentException("FlgNet constant Access is missing ConstantValue.");
                element.Add(new XElement(ns + "Constant",
                    new XElement(ns + "ConstantType", access.ConstantType ?? "Bool"),
                    new XElement(ns + "ConstantValue", access.ConstantValue)));
                return element;
            }

            if (access.SymbolPath == null || access.SymbolPath.Count == 0)
                throw new ArgumentException("FlgNet global-variable Access is missing SymbolPath.");
            element.Add(new XElement(ns + "Symbol",
                access.SymbolPath
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => new XElement(ns + "Component", new XAttribute("Name", x)))));
            return element;
        }
    }
}
