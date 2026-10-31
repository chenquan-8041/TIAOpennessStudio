using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TIAOpennessStudio.Models.Blocks;
using TIAOpennessStudio.Models.Lad;

namespace TIAOpennessStudio.Services.Lad
{
    /// <summary>
    /// Canonical JSON for the unified LAD model. This is the single wire format shared by the read
    /// path (XML -> model -> JSON), the write path (JSON -> model -> XML) and RAG storage.
    ///
    /// It is lossless by construction: every node keeps its kind, attributes, text and child order,
    /// so anything the XML contains survives the JSON round trip.
    /// </summary>
    public static class LadJson
    {
        // Default encoder on purpose: it avoids a hard dependency on System.Text.Encodings.Web at
        // type-init time, which some hosting scenarios (dynamic assembly loading) fail to resolve.
        private static readonly JsonSerializerOptions CompactOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        private static readonly JsonSerializerOptions IndentedOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static string Serialize(LadBlock block, bool indented = false)
        {
            if (block == null)
                throw new ArgumentNullException(nameof(block));
            return ToJson(block).ToJsonString(indented ? IndentedOptions : CompactOptions);
        }

        /// <summary>Serialize a single network entry (used as the RAG payload).</summary>
        public static string SerializeNetwork(LadNetwork network, bool indented = false)
        {
            if (network == null)
                throw new ArgumentNullException(nameof(network));
            return NetworkToJson(network).ToJsonString(indented ? IndentedOptions : CompactOptions);
        }

        /// <summary>Canonical node tree to JSON; the same shape <see cref="NodeFromJson"/> accepts.</summary>
        public static JsonObject ToJson(LadNode node) => NodeToJson(node);

        public static JsonObject ToJson(LadBlock block)
        {
            if (block == null)
                throw new ArgumentNullException(nameof(block));

            var json = new JsonObject
            {
                ["blockId"] = block.BlockId,
                ["kind"] = block.Kind,
                ["xmlBlockKind"] = block.XmlBlockKind ?? string.Empty,
                ["isInstanceDb"] = block.IsInstanceDb,
                ["blockName"] = block.Name,
                ["name"] = block.Name,
                ["number"] = block.Number,
                ["language"] = block.Language,
                ["title"] = block.Title ?? string.Empty,
                ["comment"] = block.Comment ?? string.Empty
            };

            if (!string.IsNullOrEmpty(block.InstanceOfName))
                json["instanceOfName"] = block.InstanceOfName;

            var attributes = new JsonObject();
            if (block.Attributes != null)
            {
                foreach (var attribute in block.Attributes)
                {
                    attributes[attribute.Name] = attribute.IsComplex
                        ? "<" + attribute.Name + ":" + attribute.ChildCount + ">"
                        : (attribute.Value ?? string.Empty);
                }
            }
            json["blockAttributes"] = attributes;

            if (block.Interface != null)
                json["interface"] = NodeToJson(block.Interface);

            var networks = new JsonArray();
            foreach (var network in block.Networks)
                networks.Add(NetworkToJson(network));
            json["networks"] = networks;

            return json;
        }

        public static LadBlock FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON is empty.", nameof(json));

            var node = JsonNode.Parse(json);
            if (node == null)
                throw new ArgumentException("JSON has no root object.", nameof(json));

            return FromJson(node);
        }

        public static LadBlock FromJson(JsonNode node)
        {
            if (!(node is JsonObject json))
                throw new ArgumentException("Root must be a JSON object.", nameof(node));

            var block = new LadBlock
            {
                BlockId = ReadString(json, "blockId"),
                Kind = ReadString(json, "kind"),
                XmlBlockKind = ReadString(json, "xmlBlockKind"),
                IsInstanceDb = ReadBool(json, "isInstanceDb"),
                InstanceOfName = ReadString(json, "instanceOfName"),
                Name = ReadString(json, "name"),
                Number = ReadInt(json, "number"),
                Language = string.IsNullOrEmpty(ReadString(json, "language")) ? "LAD" : ReadString(json, "language"),
                Title = ReadString(json, "title"),
                Comment = ReadString(json, "comment"),
                Attributes = ReadAttributes(json["blockAttributes"] as JsonObject)
            };

            if (json["interface"] is JsonObject interfaceJson)
                block.Interface = NodeFromJson(interfaceJson);

            if (json["networks"] is JsonArray networks)
            {
                foreach (var item in networks)
                    block.Networks.Add(NetworkFromJson(item as JsonObject));
            }

            return block;
        }

        private static JsonObject NetworkToJson(LadNetwork network)
        {
            var json = new JsonObject
            {
                ["networkId"] = network.NetworkId,
                ["blockId"] = network.BlockId,
                ["order"] = network.Order,
                ["compileUnitId"] = network.CompileUnitId,
                ["title"] = network.Title,
                ["comment"] = network.Comment,
                ["language"] = network.Language,
                ["sourceProfile"] = network.SourceProfile
            };

            if (network.Body != null)
                json["body"] = NodeToJson(network.Body);

            return json;
        }

        /// <summary>Parse a single network entry (as produced by <see cref="ToJson"/>) into the model.</summary>
        public static LadNetwork NetworkFromJson(JsonObject json)
        {
            if (json == null)
                throw new ArgumentException("Network entry must be a JSON object.", nameof(json));

            return new LadNetwork
            {
                NetworkId = ReadString(json, "networkId"),
                BlockId = ReadString(json, "blockId"),
                Order = ReadInt(json, "order"),
                CompileUnitId = ReadString(json, "compileUnitId"),
                Title = ReadString(json, "title"),
                Comment = ReadString(json, "comment"),
                Language = string.IsNullOrEmpty(ReadString(json, "language")) ? "LAD" : ReadString(json, "language"),
                SourceProfile = ReadString(json, "sourceProfile"),
                Body = json["body"] is JsonObject body ? NodeFromJson(body) : null
            };
        }

        public static JsonObject NodeToJson(LadNode node)
        {
            var json = new JsonObject { ["kind"] = node.Kind };

            if (node.Attributes.Count > 0)
            {
                var attributes = new JsonObject();
                foreach (var pair in node.Attributes)
                    attributes[pair.Key] = pair.Value;
                json["attrs"] = attributes;
            }

            if (node.Text != null)
                json["text"] = node.Text;

            if (node.Children.Count > 0)
            {
                var children = new JsonArray();
                foreach (var child in node.Children)
                    children.Add(NodeToJson(child));
                json["children"] = children;
            }

            return json;
        }

        public static LadNode NodeFromJson(JsonObject json)
        {
            var node = new LadNode { Kind = ReadString(json, "kind") };

            if (json["attrs"] is JsonObject attributes)
            {
                foreach (var pair in attributes)
                    node.Attributes[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
            }

            if (json["text"] is JsonValue text)
                node.Text = text.GetValue<string>();

            if (json["children"] is JsonArray children)
            {
                foreach (var child in children)
                {
                    if (child is JsonObject childJson)
                        node.Children.Add(NodeFromJson(childJson));
                }
            }

            return node;
        }

        private static string ReadString(JsonObject json, string name)
        {
            if (json[name] is JsonValue value)
                return value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
            return string.Empty;
        }

        private static int ReadInt(JsonObject json, string name)
        {
            if (json[name] is JsonValue value && value.TryGetValue<int>(out var number))
                return number;
            return 0;
        }

        private static bool ReadBool(JsonObject json, string name)
        {
            if (json[name] is JsonValue value && value.TryGetValue<bool>(out var flag))
                return flag;
            return false;
        }

        private static List<BlockAttribute> ReadAttributes(JsonObject attributes)
        {
            var result = new List<BlockAttribute>();
            if (attributes == null) return result;

            foreach (var pair in attributes)
                result.Add(ParseAttribute(pair.Key, pair.Value));
            return result;
        }

        private static BlockAttribute ParseAttribute(string name, JsonNode node)
        {
            var text = "";
            if (node is JsonValue value)
            {
                try { text = value.GetValue<string>() ?? ""; }
                catch { text = ""; }
            }

            var prefix = "<" + (name ?? "") + ":";
            var isComplex = !string.IsNullOrEmpty(name)
                            && text.StartsWith(prefix, StringComparison.Ordinal)
                            && text.EndsWith(">", StringComparison.Ordinal);
            var childCount = 0;
            if (isComplex)
            {
                var colon = text.LastIndexOf(':');
                if (colon > 0 && colon < text.Length - 1)
                {
                    int.TryParse(text.Substring(colon + 1, text.Length - colon - 2), out childCount);
                }
            }

            return new BlockAttribute
            {
                Name = name ?? "",
                Value = isComplex ? "" : text,
                IsComplex = isComplex,
                ChildCount = childCount
            };
        }
    }
}
