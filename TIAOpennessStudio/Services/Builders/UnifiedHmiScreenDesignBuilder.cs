using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// WinCC Unified HMI screen design-JSON offline builder.
    /// Produces the execution design consumed by ApplyUnifiedHmiScreenDesignJson
    /// ({ screen, items[]{name,type,left,top,width,height,text,properties} }); does not touch TIA.
    /// </summary>
    public static class UnifiedHmiScreenDesignBuilder
    {
        public static JsonObject BuildFromJson(string designJson)
        {
            var design = JsonNode.Parse(designJson) as JsonObject
                ?? throw new ArgumentException("Unified HMI screen design JSON root must be an object.", nameof(designJson));

            var screen = design["Screen"] as JsonObject ?? design["screen"] as JsonObject ?? design;
            var screenName = GetString(screen, "Name", "name", "Unified_Generated_Screen");
            var width = GetInt(screen, "Width", "width", 1280);
            var height = GetInt(screen, "Height", "height", 768);

            var items = new JsonArray();
            foreach (var src in (design["Items"] as JsonArray ?? design["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                items.Add(ToItem(src));

            return new JsonObject
            {
                ["format"] = "tia-unified-hmi-screen-design-json-v1",
                ["offlineOnly"] = true,
                ["applyTool"] = "ApplyUnifiedHmiScreenDesignJson",
                ["screen"] = new JsonObject
                {
                    ["name"] = screenName,
                    ["width"] = width,
                    ["height"] = height
                },
                ["items"] = items
            };
        }

        private static JsonObject ToItem(JsonObject src)
        {
            var item = new JsonObject
            {
                ["name"] = GetString(src, "Name", "name", "Item"),
                ["type"] = GetString(src, "Type", "type", "Rectangle"),
                ["left"] = GetInt(src, "Left", "left", 0),
                ["top"] = GetInt(src, "Top", "top", 0),
                ["width"] = GetInt(src, "Width", "width", 120),
                ["height"] = GetInt(src, "Height", "height", 40)
            };

            var text = GetString(src, "Text", "text", null);
            if (!string.IsNullOrWhiteSpace(text)) item["text"] = text;

            var props = src["properties"] as JsonObject ?? src["Properties"] as JsonObject;
            if (props != null) item["properties"] = props.DeepClone();

            return item;
        }

        private static string GetString(JsonObject obj, string name, string alias, string defaultValue)
            => obj[name]?.ToString() ?? obj[alias]?.ToString() ?? defaultValue;

        private static int GetInt(JsonObject obj, string name, string alias, int defaultValue)
        {
            if (obj.TryGetPropertyValue(name, out var node) && node != null && TryInt(node, out var v)) return v;
            if (obj.TryGetPropertyValue(alias, out var anode) && anode != null && TryInt(anode, out var av)) return av;
            return defaultValue;
        }

        private static bool TryInt(JsonNode node, out int value)
        {
            value = 0;
            if (node is JsonValue jv && jv.TryGetValue<int>(out var i)) { value = i; return true; }
            if (int.TryParse(node.ToString(), out var p)) { value = p; return true; }
            return false;
        }
    }
}