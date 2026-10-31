using System.Text.Json;
using System.Text.Json.Nodes;

#nullable enable

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// JsonNode extension methods providing the DeepClone functionality missing from
    /// System.Text.Json 6.0.0. DeepClone was only introduced in System.Text.Json 7.0.0
    /// (.NET 7+).
    /// </summary>
    internal static class JsonNodeExtensions
    {
        /// <summary>
        /// Deep-clones a JsonNode via a serialize/deserialize round-trip.
        /// </summary>
        public static JsonNode? DeepClone(this JsonNode? node)
        {
            if (node == null) return null;
            var json = node.ToJsonString(new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            return JsonNode.Parse(json);
        }
    }
}