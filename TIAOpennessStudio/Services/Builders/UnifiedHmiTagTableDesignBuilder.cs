using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// WinCC Unified HMI tag-table design-JSON offline builder.
    /// Produces a self-describing tag plan consumed by the Unified tag ensure steps
    /// (EnsureUnifiedHmiTagTable + EnsureUnifiedHmiTag); does not touch TIA.
    /// </summary>
    public static class UnifiedHmiTagTableDesignBuilder
    {
        public static JsonObject BuildFromJson(string tableJson)
        {
            var root = JsonNode.Parse(tableJson) as JsonObject
                ?? throw new ArgumentException("Unified HMI tag table JSON root must be an object.", nameof(tableJson));

            var tableName = GetString(root, "Name", "name", GetString(root, "TableName", "tableName", "Unified_HMI_Tags"));

            var tags = new JsonArray();
            foreach (var src in (root["Tags"] as JsonArray ?? root["tags"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                tags.Add(ToTag(src));

            return new JsonObject
            {
                ["format"] = "tia-unified-hmi-tag-table-design-json-v1",
                ["offlineOnly"] = true,
                ["applyTool"] = "EnsureUnifiedHmiTag",
                ["tagTable"] = tableName,
                ["tags"] = tags
            };
        }

        private static JsonObject ToTag(JsonObject src)
        {
            var tag = new JsonObject
            {
                ["name"] = GetString(src, "Name", "name", ""),
                ["dataType"] = GetString(src, "DataType", "dataType", "Bool")
            };

            var connection = GetString(src, "Connection", "connection", null);
            if (!string.IsNullOrWhiteSpace(connection)) tag["connection"] = connection;

            var controllerTag = GetString(src, "ControllerTag", "controllerTag", null);
            if (!string.IsNullOrWhiteSpace(controllerTag)) tag["controllerTag"] = controllerTag;

            var address = GetString(src, "Address", "address", null);
            if (!string.IsNullOrWhiteSpace(address)) tag["address"] = address;

            return tag;
        }

        private static string GetString(JsonObject obj, string name, string alias, string defaultValue)
            => obj[name]?.ToString() ?? obj[alias]?.ToString() ?? defaultValue;
    }
}