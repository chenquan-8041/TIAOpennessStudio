using System.Collections.Generic;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services
{
    internal interface IXmlAstVisitor
    {
        bool Enter(XElement node, int depth);
        void Exit(XElement node, int depth);
    }

    internal static class XmlAstDfsWalker
    {
        public static void Walk(XElement root, IXmlAstVisitor visitor)
        {
            if (root == null || visitor == null) return;
            WalkCore(root, 0, visitor);
        }

        private static void WalkCore(XElement node, int depth, IXmlAstVisitor visitor)
        {
            if (node == null) return;
            bool goChildren = visitor.Enter(node, depth);
            if (goChildren)
            {
                foreach (var child in node.Elements())
                {
                    WalkCore(child, depth + 1, visitor);
                }
            }
            visitor.Exit(node, depth);
        }
    }

    internal sealed class LocalNameCollectVisitor : IXmlAstVisitor
    {
        private readonly HashSet<string> _localNames;
        private readonly List<XElement> _collected;

        public LocalNameCollectVisitor(IEnumerable<string> localNames, List<XElement> collected)
        {
            _localNames = new HashSet<string>(localNames ?? new string[0], System.StringComparer.OrdinalIgnoreCase);
            _collected = collected ?? new List<XElement>();
        }

        public bool Enter(XElement node, int depth)
        {
            if (_localNames.Contains(node.Name.LocalName))
            {
                _collected.Add(node);
            }
            return true;
        }

        public void Exit(XElement node, int depth)
        {
        }
    }
}
