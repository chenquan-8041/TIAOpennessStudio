using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// Unified HMI theme/layout execution-JSON builder.
    /// Only produces JSON consumable by ApplyUnifiedHmiScreenDesignJson; it does not connect
    /// to TIA or modify the project.
    /// </summary>
    public static class HmiUnifiedThemeLayoutBuilder
    {
        public static JsonObject BuildThemeDesign(JsonObject theme)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            var palette = theme["palette"] as JsonObject ?? theme["Palette"] as JsonObject ?? theme;
            var screen = new JsonObject();

            CopyColor(palette, screen, "Page", "BackColor");
            CopyColor(palette, screen, "Background", "BackColor");

            var itemDefaults = new JsonObject();
            CopyColor(palette, itemDefaults, "Surface", "BackColor");
            CopyColor(palette, itemDefaults, "Text", "ForeColor");
            CopyColor(palette, itemDefaults, "Border", "BorderColor");

            return new JsonObject
            {
                ["screen"] = screen,
                ["items"] = new JsonArray(),
                ["theme"] = new JsonObject
                {
                    ["name"] = theme["name"]?.ToString() ?? theme["Name"]?.ToString() ?? "UnifiedTheme",
                    ["palette"] = CloneObject(palette),
                    ["defaultItemProperties"] = itemDefaults
                },
                ["offlineOnly"] = true,
                ["applyTool"] = "ApplyUnifiedHmiScreenDesignJson"
            };
        }

        public static JsonObject BuildLayoutDesign(JsonObject layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            var grid = GetInt(layout, "grid", "Grid", 8);
            var left = GetInt(layout, "left", "Left", "x", 0);
            var top = GetInt(layout, "top", "Top", "y", 0);
            var gap = GetInt(layout, "gap", "Gap", 8);
            var columns = Math.Max(1, GetInt(layout, "columns", "Columns", "cols", 1));
            var cellWidth = Math.Max(1, GetInt(layout, "cellWidth", "CellWidth", "width", 160));
            var cellHeight = Math.Max(1, GetInt(layout, "cellHeight", "CellHeight", "height", 80));
            var items = layout["items"] as JsonArray ?? layout["Items"] as JsonArray
                ?? throw new ArgumentException("layoutJson.items must be an array.");

            var outputItems = new JsonArray();
            for (var i = 0; i < items.Count; i++)
            {
                var src = items[i] as JsonObject ?? throw new ArgumentException("layoutJson.items[" + i + "] must be an object.");
                var name = src["name"]?.ToString() ?? src["Name"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("layoutJson.items[" + i + "].name is required.");

                var col = GetInt(src, "col", "Col", -1);
                var row = GetInt(src, "row", "Row", -1);
                if (col < 0) col = i % columns;
                if (row < 0) row = i / columns;
                var colSpan = Math.Max(1, GetInt(src, "colSpan", "ColSpan", 1));
                var rowSpan = Math.Max(1, GetInt(src, "rowSpan", "RowSpan", 1));

                var item = new JsonObject
                {
                    ["name"] = name,
                    ["type"] = src["type"]?.DeepClone() ?? src["Type"]?.DeepClone() ?? JsonValue.Create("Rectangle"),
                    ["left"] = Snap(left + col * (cellWidth + gap), grid),
                    ["top"] = Snap(top + row * (cellHeight + gap), grid),
                    ["width"] = Snap(cellWidth * colSpan + gap * (colSpan - 1), grid),
                    ["height"] = Snap(cellHeight * rowSpan + gap * (rowSpan - 1), grid)
                };

                CopyOptionalString(src, item, "text", "Text");
                CopyOptionalString(src, item, "culture", "Culture");
                if (src["properties"] is JsonObject || src["Properties"] is JsonObject)
                {
                    item["properties"] = (src["properties"] as JsonObject ?? src["Properties"] as JsonObject)!.DeepClone();
                }

                outputItems.Add(item);
            }

            return new JsonObject
            {
                ["screen"] = new JsonObject(),
                ["items"] = outputItems,
                ["offlineOnly"] = true,
                ["applyTool"] = "ApplyUnifiedHmiScreenDesignJson"
            };
        }

        private static void CopyColor(JsonObject source, JsonObject target, string paletteKey, string targetKey)
        {
            var value = source[paletteKey]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                target[targetKey] = value;
        }

        private static JsonObject CloneObject(JsonObject source)
        {
            return source.DeepClone() as JsonObject ?? new JsonObject();
        }

        private static int GetInt(JsonObject obj, string name, string alias, int defaultValue)
        {
            foreach (var n in new[] { name, alias })
            {
                if (obj.TryGetPropertyValue(n, out var node) && node != null)
                {
                    if (node is JsonValue value && value.TryGetValue<int>(out var intValue))
                        return intValue;
                    if (int.TryParse(node.ToString(), out var parsed))
                        return parsed;
                }
            }
            return defaultValue;
        }

        private static int GetInt(JsonObject obj, string name, string alias1, string alias2, int defaultValue)
        {
            foreach (var n in new[] { name, alias1, alias2 })
            {
                if (obj.TryGetPropertyValue(n, out var node) && node != null)
                {
                    if (node is JsonValue value && value.TryGetValue<int>(out var intValue))
                        return intValue;
                    if (int.TryParse(node.ToString(), out var parsed))
                        return parsed;
                }
            }
            return defaultValue;
        }

        private static int Snap(int value, int grid)
        {
            return (int)(Math.Round((double)value / grid) * grid);
        }

        private static void CopyOptionalString(JsonObject source, JsonObject target, string sourceKey, string targetKey)
        {
            var value = source[sourceKey]?.ToString() ?? source[targetKey]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                target[targetKey] = value;
        }
    }
}
