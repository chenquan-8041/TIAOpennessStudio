using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TIAOpennessStudio.Services.Blocks
{
    /// <summary>
    /// Fail-closed guards for the build_* write path.
    ///
    /// Rebuilding a block is lossy by nature (only the fields carried by the spec survive). These
    /// guards stop the two ways that loss becomes silent data damage:
    ///   - a truncated IR (paged read) must never be rebuilt, or the missing networks disappear;
    ///   - an instance DB must never go through build_global_db, or it silently becomes a global DB.
    /// </summary>
    public static class BlockWriteGuards
    {
        /// <summary>
        /// True when the IR is a partial page (fidelity.truncated or returned &lt; total). Rebuilding
        /// from it would drop every network that was not returned.
        /// </summary>
        public static bool TryRejectTruncatedIr(JsonObject root, out string error)
        {
            error = null;
            var document = Unwrap(root);
            var fidelity = FindObject(document, "fidelity");
            if (fidelity == null)
                return false;

            var truncated = ReadBool(fidelity, "truncated");
            var returned = ReadInt(fidelity, "returnedNetworks");
            var total = ReadInt(fidelity, "totalNetworks");
            var partial = truncated || (total > 0 && returned > 0 && returned < total);
            if (!partial)
                return false;

            error = "The IR page is truncated (" + returned + "/" + total + " networks). "
                    + "Rebuilding from a partial IR would delete the missing networks. "
                    + "Read the whole block first (get_block_content format=ir with network_start/network_count), "
                    + "or change only one network with patch_block_network.";
            return true;
        }

        /// <summary>
        /// True when the spec describes an instance DB. build_global_db always emits SW.Blocks.GlobalDB,
        /// so an instance DB would be silently converted; patch_block_interface preserves it instead.
        /// </summary>
        public static bool TryRejectInstanceDb(JsonObject root, out string error)
        {
            error = null;
            foreach (var candidate in new[] { Unwrap(root), root })
            {
                if (candidate == null) continue;

                var kind = ReadString(candidate, "kind", "xmlBlockKind", "xml_block_kind", "blockType", "block_type");
                var isInstance = ReadBool(candidate, "isInstanceDb", "is_instance_db");
                var instanceOf = ReadString(candidate, "instanceOfName", "instance_of_name", "instanceOf");

                if (isInstance
                    || string.Equals(kind, "InstanceDB", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(instanceOf))
                {
                    error = "This block is an instance DB (FB instance"
                            + (string.IsNullOrWhiteSpace(instanceOf) ? "" : " of '" + instanceOf + "'") + "). "
                            + "build_global_db would silently turn it into a global DB. "
                            + "Use patch_block_interface for the instance DB, or edit the owning FB.";
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolve the MemoryLayout for a DB rebuild: explicit argument &gt; IR blockAttributes &gt; Standard.
        /// Invalid explicit values are rejected instead of being silently replaced.
        /// </summary>
        public static bool TryResolveMemoryLayout(
            JsonObject root, string requested, out string layout, out string error)
        {
            layout = null;
            error = null;

            if (!string.IsNullOrWhiteSpace(requested))
            {
                var explicitLayout = NormalizeLayout(requested);
                if (explicitLayout == null)
                {
                    error = "memory_layout must be 'Standard' or 'Optimized' (got '" + requested + "').";
                    return false;
                }
                layout = explicitLayout;
                return true;
            }

            var document = Unwrap(root);
            var attributes = FindObject(document, "blockAttributes");
            if (attributes != null)
            {
                var fromIr = NormalizeLayout(ReadString(attributes, "MemoryLayout", "memoryLayout"));
                if (fromIr != null)
                {
                    layout = fromIr;
                    return true;
                }
            }

            layout = "Standard";
            return true;
        }

        private static string NormalizeLayout(string value)
        {
            var text = (value ?? "").Trim();
            if (string.Equals(text, "Standard", StringComparison.OrdinalIgnoreCase)) return "Standard";
            if (string.Equals(text, "Optimized", StringComparison.OrdinalIgnoreCase)) return "Optimized";
            return null;
        }

        /// <summary>Accept the get_block_content envelope ({content}) and the build payload ({ir}).</summary>
        private static JsonObject Unwrap(JsonObject root)
        {
            if (root == null) return null;
            var content = FindObject(root, "content");
            if (content != null && (Has(content, "fidelity") || Has(content, "networks"))) return content;
            var ir = FindObject(root, "ir");
            if (ir != null) return ir;
            return root;
        }

        private static bool Has(JsonObject obj, string name)
        {
            foreach (var pair in obj)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static JsonObject FindObject(JsonObject obj, string name)
        {
            if (obj == null) return null;
            foreach (var pair in obj)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    return pair.Value as JsonObject;
            }
            return null;
        }

        private static string ReadString(JsonObject obj, params string[] names)
        {
            if (obj == null) return "";
            foreach (var name in names)
            {
                foreach (var pair in obj)
                {
                    if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (pair.Value is JsonValue value)
                    {
                        try { return value.GetValue<string>() ?? ""; }
                        catch { return ""; }
                    }
                }
            }
            return "";
        }

        private static bool ReadBool(JsonObject obj, params string[] names)
        {
            if (obj == null) return false;
            foreach (var name in names)
            {
                foreach (var pair in obj)
                {
                    if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (pair.Value is JsonValue value)
                    {
                        try { return value.GetValue<bool>(); }
                        catch { return false; }
                    }
                }
            }
            return false;
        }

        private static int ReadInt(JsonObject obj, string name)
        {
            if (obj == null) return 0;
            foreach (var pair in obj)
            {
                if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Value is JsonValue value)
                {
                    try { return value.GetValue<int>(); }
                    catch { return 0; }
                }
            }
            return 0;
        }
    }
}
