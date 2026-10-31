using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// Classic (WinCC Basic/Comfort/Advanced) HMI screen XML builder.
    /// Emits the same SIMATIC ML shape that TIA Portal V15.1 exports (Hmi.Screen.Screen,
    /// ScreenLayer, typed screen items, CompositionName markers, one AttributeList per object).
    /// Offline only: the XML must still be imported into a temporary project and read back.
    /// </summary>
    public static class ClassicHmiScreenXmlBuilder
    {
        private const string DefaultCulture = "zh-CN";

        public static JsonObject BuildFromJson(string designJson, string tiaVersion = "V21")
        {
            var design = JsonNode.Parse(designJson) as JsonObject
                ?? throw new ArgumentException("Classic HMI design JSON root must be an object.", nameof(designJson));
            var document = BuildDocument(design, tiaVersion);
            var xml = ToXml(document);
            var analysis = AnalyzeXml(xml);
            return new JsonObject
            {
                ["format"] = "tia-classic-hmi-screen-xml-offline-v1",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["offlineOnly"] = true,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "Offline XML generation only; TIA Portal is not connected.",
                    ["write"] = "No project, reference project, or delivery package content is modified.",
                    ["apply"] = "Import into a temporary Classic/Basic HMI project first, then read back and compile before using a real project."
                },
                ["ok"] = analysis["ok"]?.GetValue<bool>() == true,
                ["analysis"] = analysis,
                ["xml"] = xml
            };
        }

        public static XDocument BuildDocument(JsonObject design, string tiaVersion = "V21")
        {
            var screen = design["Screen"] as JsonObject ?? design["screen"] as JsonObject ?? design;
            var screenName = GetString(screen, "Name", "name", "Classic_Generated_Screen");
            if (string.IsNullOrWhiteSpace(screenName))
                throw new ArgumentException("Classic HMI screen name is required.");

            var width = GetInt(screen, "Width", "width", 640);
            var height = GetInt(screen, "Height", "height", 480);
            var number = GetInt(screen, "Number", "number", 1);
            if (width < 320 || height < 240)
                throw new ArgumentException("Classic HMI screen size must be at least 320x240.");

            var items = (design["Items"] as JsonArray ?? design["items"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .ToArray();
            ValidateItems(items, width, height);

            var id = new IdAllocator();
            var itemElements = items.Select(item => BuildItem(id, item)).ToArray();

            var layer = new XElement("Hmi.Screen.ScreenLayer",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", "Layers"),
                new XElement("AttributeList",
                    new XElement("Index", "0"),
                    new XElement("Name", ""),
                    new XElement("VisibleES", "true")),
                new XElement("ObjectList", itemElements));

            var screenElement = new XElement("Hmi.Screen.Screen",
                new XAttribute("ID", "0"),
                new XElement("AttributeList",
                    new XElement("ActiveLayer", "0"),
                    new XElement("Height", height),
                    new XElement("Name", screenName),
                    new XElement("Number", number),
                    new XElement("Width", width)),
                new XElement("ObjectList",
                    layer));

            var documentElement = new XElement("Document",
                new XElement("Engineering", new XAttribute("version", tiaVersion)),
                new XElement("DocumentInfo",
                    new XElement("Created", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")),
                    new XElement("ExportSetting", "None"),
                    new XElement("InstalledProducts")),
                screenElement);

            return new XDocument(new XDeclaration("1.0", "utf-8", null), documentElement);
        }

        private static XElement BuildItem(IdAllocator id, JsonObject item)
        {
            var type = NormalizeItemType(GetString(item, "Type", "type", "Text"));
            var name = GetString(item, "Name", "name", "");
            var left = GetInt(item, "Left", "left", 0);
            var top = GetInt(item, "Top", "top", 0);
            var width = GetInt(item, "Width", "width", 120);
            var height = GetInt(item, "Height", "height", 40);
            var text = GetText(item);
            var tag = GetString(item, "Tag", "tag",
                GetString(item, "ProcessValueTag", "processValueTag", ""));

            switch (type)
            {
                case "Button":
                    return BuildButton(id, item, name, left, top, width, height, text);
                case "IOField":
                    return BuildIOField(id, item, name, left, top, width, height, tag);
                case "Rectangle":
                    return BuildRectangle(id, name, left, top, width, height);
                case "Text":
                default:
                    return BuildTextField(id, item, name, left, top, width, height, text);
            }
        }

        private static XElement BuildTextField(IdAllocator id, JsonObject item, string name,
            int left, int top, int width, int height, string text)
        {
            var element = new XElement("Hmi.Screen.TextField",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", "ScreenItems"),
                new XElement("AttributeList",
                    new XElement("BackColor", Color(item, "BackColor", "255, 255, 255")),
                    new XElement("BackFillStyle", "Transparent"),
                    new XElement("BorderBackColor", Color(item, "BorderBackColor", "101, 103, 115")),
                    new XElement("BorderColor", Color(item, "BorderColor", "71, 73, 87")),
                    new XElement("BorderWidth", GetInt(item, "BorderWidth", "borderWidth", 1)),
                    new XElement("BottomMargin", "2"),
                    new XElement("CornerRadius", "3"),
                    new XElement("EdgeStyle", "Double"),
                    new XElement("FitToLargest", "false"),
                    new XElement("ForeColor", Color(item, "ForeColor", "49, 52, 74")),
                    new XElement("Height", height),
                    new XElement("HorizontalAlignment", GetString(item, "HorizontalAlignment", "horizontalAlignment", "Left")),
                    new XElement("Left", left),
                    new XElement("LeftMargin", "2"),
                    new XElement("ObjectName", name),
                    new XElement("RightMargin", "2"),
                    new XElement("TextOrientation", "Horizontal"),
                    new XElement("Top", top),
                    new XElement("TopMargin", "2"),
                    new XElement("UseDesignColorSchema", "false"),
                    new XElement("VerticalAlignment", "Middle"),
                    new XElement("Width", width)),
                new XElement("ObjectList",
                    BuildFont(id, item),
                    BuildMultilingualText(id, "Text", text)));
            return element;
        }

        private static XElement BuildButton(IdAllocator id, JsonObject item, string name,
            int left, int top, int width, int height, string text)
        {
            var objectList = new XElement("ObjectList",
                BuildFont(id, item),
                BuildMultilingualText(id, "HelpText", ""),
                BuildMultilingualText(id, "TextOff", text),
                BuildMultilingualText(id, "TextOn", text));

            var actions = BuildEventActions(id, item);
            if (actions.Count > 0)
            {
                foreach (var action in actions) objectList.Add(action);
            }

            return new XElement("Hmi.Screen.Button",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", "ScreenItems"),
                new XElement("AttributeList",
                    new XElement("BackColor", Color(item, "BackColor", "192, 192, 192")),
                    new XElement("BorderBackColor", Color(item, "BorderBackColor", "105, 105, 105")),
                    new XElement("BorderColor", Color(item, "BorderColor", "255, 255, 255")),
                    new XElement("BorderWidth", GetInt(item, "BorderWidth", "borderWidth", 5)),
                    new XElement("CornerRadius", "4"),
                    new XElement("CornerStyle", "Pointed"),
                    new XElement("EdgeStyle", "Style3D"),
                    new XElement("FirstGradientColor", "131, 132, 142"),
                    new XElement("FirstGradientOffset", "15"),
                    new XElement("FocusColor", "148, 182, 231"),
                    new XElement("FocusWidth", "2"),
                    new XElement("Height", height),
                    new XElement("Left", left),
                    new XElement("MiddleGradientColor", "51, 51, 51"),
                    new XElement("ObjectName", name),
                    new XElement("SecondGradientColor", "88, 90, 103"),
                    new XElement("SecondGradientOffset", "15"),
                    new XElement("TabIndex", GetInt(item, "TabIndex", "tabIndex", 0)),
                    new XElement("Top", top),
                    new XElement("UseDesignColorSchema", "false"),
                    new XElement("UseFirstGradient", "false"),
                    new XElement("UseSecondGradient", "false"),
                    new XElement("Width", width)),
                objectList);
        }

        private static XElement BuildIOField(IdAllocator id, JsonObject item, string name,
            int left, int top, int width, int height, string tag)
        {
            var objectList = new XElement("ObjectList",
                BuildFont(id, item),
                BuildMultilingualText(id, "HelpText", ""));
            if (!string.IsNullOrWhiteSpace(tag))
            {
                objectList.Add(BuildProcessValueDynamic(id, tag));
            }

            return new XElement("Hmi.Screen.IOField",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", "ScreenItems"),
                new XElement("AttributeList",
                    new XElement("AboveUpperLimitColor", "237, 88, 97"),
                    new XElement("BackColor", Color(item, "BackColor", "255, 255, 255")),
                    new XElement("BelowLowerLimitColor", "241, 161, 44"),
                    new XElement("BorderBackColor", Color(item, "BorderBackColor", "101, 103, 115")),
                    new XElement("BorderColor", Color(item, "BorderColor", "71, 73, 87")),
                    new XElement("BorderWidth", GetInt(item, "BorderWidth", "borderWidth", 1)),
                    new XElement("BottomMargin", "2"),
                    new XElement("CornerRadius", "3"),
                    new XElement("DataFormat", "Decimal"),
                    new XElement("EdgeStyle", "Double"),
                    new XElement("FieldLength", GetInt(item, "FieldLength", "fieldLength", 1)),
                    new XElement("ForeColor", Color(item, "ForeColor", "49, 52, 74")),
                    new XElement("FormatPattern", GetString(item, "FormatPattern", "formatPattern", "9")),
                    new XElement("Height", height),
                    new XElement("HorizontalAlignment", "Center"),
                    new XElement("Left", left),
                    new XElement("LeftMargin", "2"),
                    new XElement("Mode", GetString(item, "Mode", "mode", "InOutput")),
                    new XElement("ObjectName", name),
                    new XElement("RightMargin", "2"),
                    new XElement("TabIndex", GetInt(item, "TabIndex", "tabIndex", 0)),
                    new XElement("Top", top),
                    new XElement("TopMargin", "2"),
                    new XElement("UseDesignColorSchema", "false"),
                    new XElement("VerticalAlignment", "Middle"),
                    new XElement("Width", width)),
                objectList);
        }

        private static XElement BuildRectangle(IdAllocator id, string name,
            int left, int top, int width, int height)
        {
            return new XElement("Hmi.Screen.Rectangle",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", "ScreenItems"),
                new XElement("AttributeList",
                    new XElement("BackColor", "255, 255, 255"),
                    new XElement("BackFillStyle", "Transparent"),
                    new XElement("BorderColor", "24, 28, 49"),
                    new XElement("BorderWidth", "1"),
                    new XElement("Height", height),
                    new XElement("Left", left),
                    new XElement("ObjectName", name),
                    new XElement("RoundCornerHeight", "0"),
                    new XElement("RoundCornerWidth", "0"),
                    new XElement("Top", top),
                    new XElement("UseDesignColorSchema", "false"),
                    new XElement("Width", width)));
        }

        private static XElement BuildProcessValueDynamic(IdAllocator id, string tag)
        {
            var tagLink = new XElement("Tag",
                new XAttribute("TargetID", "@OpenLink"),
                new XElement("Name", QuoteLinkName(tag)));
            var dynamic = new XElement("Hmi.Dynamic.TagConnectionDynamic",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", "Dynamic"),
                new XElement("AttributeList",
                    new XElement("Indirect", "false")),
                new XElement("LinkList", tagLink));
            return new XElement("Hmi.Screen.Property",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", "Properties"),
                new XElement("AttributeList",
                    new XElement("Name", "ProcessValue")),
                new XElement("ObjectList", dynamic));
        }

        private static List<XElement> BuildEventActions(IdAllocator id, JsonObject item)
        {
            var events = new List<XElement>();
            var actions = item["Actions"] as JsonArray ?? item["actions"] as JsonArray;
            if (actions == null) return events;

            foreach (var node in actions)
            {
                if (node is not JsonObject action) continue;
                var eventName = NormalizeEvent(GetString(action, "Event", "event", ""));
                var functionName = GetString(action, "ActionKind", "actionKind", "");
                var targetTag = GetString(action, "TargetTag", "targetTag", GetString(action, "Tag", "tag", ""));
                var targetScreen = GetString(action, "TargetScreen", "targetScreen", "");
                if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(functionName)) continue;

                XElement parameter;
                if (!string.IsNullOrWhiteSpace(targetScreen))
                {
                    parameter = new XElement("Hmi.Event.FunctionListEntryParameter",
                        new XAttribute("ID", id.Next()),
                        new XAttribute("CompositionName", "Parameters"),
                        new XElement("AttributeList",
                            new XElement("Name", "Screen name")),
                        new XElement("LinkList",
                            new XElement("Value",
                                new XAttribute("TargetID", "@OpenLink"),
                                new XElement("Name", QuoteLinkName(targetScreen)))));
                }
                else if (!string.IsNullOrWhiteSpace(targetTag))
                {
                    parameter = new XElement("Hmi.Event.FunctionListEntryParameter",
                        new XAttribute("ID", id.Next()),
                        new XAttribute("CompositionName", "Parameters"),
                        new XElement("AttributeList",
                            new XElement("Name", "Tag")),
                        new XElement("LinkList",
                            new XElement("Value",
                                new XAttribute("TargetID", "@OpenLink"),
                                new XElement("Name", QuoteLinkName(targetTag)))));
                }
                else
                {
                    continue;
                }

                var entry = new XElement("Hmi.Event.FunctionListEntry",
                    new XAttribute("ID", id.Next()),
                    new XAttribute("CompositionName", "FunctionListEntries"),
                    new XElement("AttributeList",
                        new XElement("Name", functionName),
                        new XElement("Type", "SystemFunction")),
                    new XElement("ObjectList", parameter));
                var handler = new XElement("Hmi.Event.FunctionListEventHandler",
                    new XAttribute("ID", id.Next()),
                    new XAttribute("CompositionName", "EventHandler"),
                    new XElement("ObjectList", entry));
                events.Add(new XElement("Hmi.Event.Event",
                    new XAttribute("ID", id.Next()),
                    new XAttribute("CompositionName", "Events"),
                    new XElement("AttributeList",
                        new XElement("Name", eventName)),
                    new XElement("ObjectList", handler)));
            }

            return events;
        }

        private static XElement BuildFont(IdAllocator id, JsonObject item)
        {
            var family = GetString(item, "FontFamily", "fontFamily", "Tahoma");
            var size = GetInt(item, "FontSize", "fontSize", 12);
            var style = GetString(item, "FontStyle", "fontStyle", "Regular");
            return new XElement("Hmi.Globalization.MultiLingualFont",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", "Font"),
                new XElement("ObjectList",
                    new XElement("Hmi.Globalization.FontItem",
                        new XAttribute("ID", id.Next()),
                        new XAttribute("CompositionName", "Items"),
                        new XElement("AttributeList",
                            new XElement("Culture", GetString(item, "Culture", "culture", DefaultCulture)),
                            new XElement("FontFamily", family),
                            new XElement("FontSize", Math.Max(1, size)),
                            new XElement("FontStyle", style)))));
        }

        private static XElement BuildMultilingualText(IdAllocator id, string compositionName, string text)
        {
            XElement textElement;
            if (string.IsNullOrEmpty(text))
            {
                textElement = new XElement("Text");
            }
            else
            {
                textElement = new XElement("Text",
                    new XElement("body",
                        new XElement("p", text)));
            }

            var item = new XElement("MultilingualTextItem",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", "Items"),
                new XElement("AttributeList",
                    new XElement("Culture", DefaultCulture),
                    textElement));
            return new XElement("MultilingualText",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", compositionName),
                new XElement("ObjectList", item));
        }

        private static JsonObject AnalyzeXml(string xml)
        {
            var errors = new JsonArray();
            var warnings = new JsonArray();
            var result = new JsonObject
            {
                ["ok"] = false,
                ["screenName"] = "",
                ["width"] = 0,
                ["height"] = 0,
                ["itemCount"] = 0,
                ["errors"] = errors,
                ["warnings"] = warnings
            };

            try
            {
                var document = XDocument.Parse(xml);
                var screen = document.Descendants("Hmi.Screen.Screen").FirstOrDefault();
                if (screen == null)
                {
                    errors.Add("missing-screen: Hmi.Screen.Screen element is required.");
                    return result;
                }

                var attributeList = screen.Element("AttributeList");
                result["screenName"] = attributeList?.Element("Name")?.Value ?? "";
                result["width"] = ParseInt(attributeList?.Element("Width")?.Value);
                result["height"] = ParseInt(attributeList?.Element("Height")?.Value);
                var items = document.Descendants()
                    .Where(x => x.Attribute("CompositionName")?.Value == "ScreenItems")
                    .ToArray();
                result["itemCount"] = items.Length;
                if (items.Length == 0) warnings.Add("empty-screen: no screen items were generated.");

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    var name = item.Element("AttributeList")?.Element("ObjectName")?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        warnings.Add("unnamed-item: " + item.Name.LocalName);
                    }
                    else if (!names.Add(name))
                    {
                        errors.Add("duplicate-item-name: " + name);
                    }
                }

                result["ok"] = errors.Count == 0;
                return result;
            }
            catch (Exception ex)
            {
                errors.Add("xml-parse-error: " + ex.Message);
                return result;
            }
        }

        private static void ValidateItems(JsonObject[] items, int screenWidth, int screenHeight)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var name = GetString(item, "Name", "name", "");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("Classic HMI item name is required.");
                if (!names.Add(name))
                    throw new ArgumentException($"Duplicate Classic HMI item name: {name}");

                var left = GetInt(item, "Left", "left", 0);
                var top = GetInt(item, "Top", "top", 0);
                var width = GetInt(item, "Width", "width", 120);
                var height = GetInt(item, "Height", "height", 40);
                if (width <= 0 || height <= 0)
                    throw new ArgumentException($"Classic HMI item '{name}' must have a positive size.");
                if (left < 0 || top < 0 || left + width > screenWidth || top + height > screenHeight)
                    throw new ArgumentException($"Classic HMI item '{name}' exceeds screen bounds.");

                var type = NormalizeItemType(GetString(item, "Type", "type", "Text"));
                if (type != "Text" && type != "Button" && type != "IOField" && type != "Rectangle")
                    throw new ArgumentException($"Unsupported Classic HMI item type: {type}");
            }
        }

        private static string NormalizeItemType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return "Text";
            switch (type.Trim().ToLowerInvariant())
            {
                case "button": return "Button";
                case "iofield":
                case "io":
                case "i/o field": return "IOField";
                case "rectangle":
                case "rect":
                case "lamp": return "Rectangle";
                case "text":
                case "textfield":
                case "label": return "Text";
                default: return type;
            }
        }

        private static string NormalizeEvent(string eventName)
        {
            switch (eventName.Trim().ToLowerInvariant())
            {
                case "press": return "Press";
                case "release": return "Release";
                case "click": return "Click";
                default: return eventName.Trim();
            }
        }

        private static string Color(JsonObject item, string name, string fallback)
        {
            var value = GetString(item, name, char.ToLowerInvariant(name[0]) + name.Substring(1), fallback);
            return NormalizeColor(value);
        }

        private static string NormalizeColor(string color)
        {
            if (string.IsNullOrWhiteSpace(color)) return "255, 255, 255";
            color = color.Trim();
            if (!color.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return color;
            var hex = color.Substring(color.Length == 10 ? 4 : 2);
            if (hex.Length != 6) return color;
            var r = Convert.ToInt32(hex.Substring(0, 2), 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return $"{r}, {g}, {b}";
        }

        /// <summary>
        /// Link targets (tag/screen names) must be quoted when they contain a space, dot or backslash.
        /// </summary>
        private static string QuoteLinkName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (!name.Any(c => char.IsWhiteSpace(c) || c == '.' || c == '\\' || c == '"' || c == ':'))
                return name;
            return "\"" + name.Replace("\"", "\"\"") + "\"";
        }

        private static string GetText(JsonObject item)
        {
            var node = item["Text"] ?? item["text"];
            if (node == null) return "";
            if (node is JsonObject localized)
            {
                var value = localized[DefaultCulture] ?? localized.FirstOrDefault().Value;
                return value?.ToString() ?? "";
            }
            return node.ToString();
        }

        private static string GetString(JsonObject obj, string name, string alias, string fallback)
        {
            return obj[name]?.ToString() ?? obj[alias]?.ToString() ?? fallback;
        }

        private static int GetInt(JsonObject obj, string name, string alias, int fallback)
        {
            foreach (var key in new[] { name, alias })
            {
                var node = obj[key];
                if (node == null) continue;
                if (node is JsonValue value && value.TryGetValue<int>(out var intValue)) return intValue;
                if (int.TryParse(node.ToString(), out var parsed)) return parsed;
            }
            return fallback;
        }

        private static int ParseInt(string text) => int.TryParse(text, out var value) ? value : 0;

        private static string ToXml(XDocument document)
        {
            using var writer = new Utf8StringWriter();
            document.Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }

        private sealed class IdAllocator
        {
            private int _next = 1;
            public string Next() => (_next++).ToString();
        }
    }
}
