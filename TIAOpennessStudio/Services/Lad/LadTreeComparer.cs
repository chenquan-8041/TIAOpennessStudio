using System;
using System.Linq;
using TIAOpennessStudio.Models.Lad;

namespace TIAOpennessStudio.Services.Lad
{
    /// <summary>
    /// Semantic comparison of generic node trees, used to verify the "parse, emit, parse again"
    /// invariant. Compares element name, attributes, text and child order. Namespaces are not part
    /// of the model, so they never take part in the comparison.
    /// </summary>
    public static class LadTreeComparer
    {
        public static bool TryCompare(LadNode left, LadNode right, out string difference)
        {
            difference = null;
            return Compare(left, right, "$", ref difference);
        }

        private static bool Compare(LadNode left, LadNode right, string path, ref string difference)
        {
            if (left == null && right == null)
                return true;

            if (left == null || right == null)
            {
                difference = path + ": one side is null (left=" + (left == null ? "null" : left.Kind)
                             + ", right=" + (right == null ? "null" : right.Kind) + ")";
                return false;
            }

            if (!string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase))
            {
                difference = path + ": element name differs (" + left.Kind + " vs " + right.Kind + ")";
                return false;
            }

            if (!string.Equals(left.Text ?? string.Empty, right.Text ?? string.Empty, StringComparison.Ordinal))
            {
                difference = path + "/" + left.Kind + ": text differs";
                return false;
            }

            if (left.Attributes.Count != right.Attributes.Count)
            {
                difference = path + "/" + left.Kind + ": attribute count differs ("
                             + left.Attributes.Count + " vs " + right.Attributes.Count + ")";
                return false;
            }

            foreach (var pair in left.Attributes)
            {
                if (!right.Attributes.TryGetValue(pair.Key, out var value)
                    || !string.Equals(value, pair.Value, StringComparison.Ordinal))
                {
                    difference = path + "/" + left.Kind + ": attribute " + pair.Key + " differs";
                    return false;
                }
            }

            if (left.Children.Count != right.Children.Count)
            {
                difference = path + "/" + left.Kind + ": child count differs ("
                             + left.Children.Count + " vs " + right.Children.Count + ")";
                return false;
            }

            for (var i = 0; i < left.Children.Count; i++)
            {
                if (!Compare(left.Children[i], right.Children[i], path + "/" + left.Kind + "[" + i + "]", ref difference))
                    return false;
            }

            return true;
        }

        public static string Describe(LadNode node)
        {
            if (node == null)
                return "(null)";
            var attributes = string.Join(", ", node.Attributes.Select(p => p.Key + "=" + p.Value));
            return node.Kind + (attributes.Length > 0 ? " [" + attributes + "]" : string.Empty)
                   + " children=" + node.Children.Count;
        }
    }
}
