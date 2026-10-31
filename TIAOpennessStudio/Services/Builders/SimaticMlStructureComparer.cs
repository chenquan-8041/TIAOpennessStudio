using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// Shape-only comparison between a TIA-exported SIMATIC ML document (reference) and a
    /// generated one. Works for PLC blocks/types/tag tables and HMI screens/tag tables:
    /// values are ignored on purpose, because TIA rejects unknown object classes, missing
    /// CompositionName markers and repeated AttributeList nodes before it looks at values.
    /// </summary>
    public static class SimaticMlStructureComparer
    {
        private sealed class Shape
        {
            public int Count;
            public readonly HashSet<string> Attributes = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Links = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> CompositionNames = new HashSet<string>(StringComparer.Ordinal);
        }

        public static JsonObject Compare(string referenceXml, string generatedXml, int maxDetails = 200)
            => Compare(new[] { referenceXml }, generatedXml, maxDetails);

        /// <summary>
        /// Compare against one or more exported references. Multiple references widen the accepted
        /// element/attribute vocabulary, which matters when no single project screen contains every
        /// control type used by the design.
        /// </summary>
        public static JsonObject Compare(IEnumerable<string> referenceXmls, string generatedXml, int maxDetails = 200)
        {
            var differences = new JsonArray();
            var result = new JsonObject
            {
                ["ok"] = false,
                ["blockingCount"] = 0,
                ["warningCount"] = 0,
                ["differences"] = differences
            };

            void Add(string severity, string code, string detail)
            {
                if (differences.Count >= maxDetails) return;
                differences.Add(new JsonObject
                {
                    ["severity"] = severity,
                    ["code"] = code,
                    ["detail"] = detail
                });
            }

            try
            {
                var referenceDocuments = (referenceXmls ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(XDocument.Parse)
                    .ToArray();
                if (referenceDocuments.Length == 0)
                {
                    Add("blocking", "reference-missing", "no exported reference XML was provided");
                    result["blockingCount"] = differences.Count;
                    return result;
                }

                var generated = XDocument.Parse(generatedXml);
                var referenceShapes = new Dictionary<string, Shape>(StringComparer.Ordinal);
                foreach (var document in referenceDocuments)
                {
                    foreach (var pair in ExtractShapes(document))
                    {
                        if (!referenceShapes.TryGetValue(pair.Key, out var merged))
                        {
                            merged = pair.Value;
                            referenceShapes[pair.Key] = merged;
                        }
                        else
                        {
                            merged.Count += pair.Value.Count;
                            foreach (var value in pair.Value.Attributes) merged.Attributes.Add(value);
                            foreach (var value in pair.Value.Links) merged.Links.Add(value);
                            foreach (var value in pair.Value.CompositionNames) merged.CompositionNames.Add(value);
                        }
                    }
                }

                var generatedShapes = ExtractShapes(generated);
                result["referenceProfile"] = BuildProfile(referenceShapes);
                result["generatedProfile"] = BuildProfile(generatedShapes);

                var primaryNames = referenceDocuments
                    .Select(PrimaryObjectName)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var generatedPrimary = PrimaryObjectName(generated);
                if (primaryNames.Length > 0 && !primaryNames.Contains(generatedPrimary, StringComparer.Ordinal))
                {
                    Add("blocking", "root-object-mismatch",
                        $"generated object is '{generatedPrimary}' but the reference exports '{string.Join("', '", primaryNames)}'");
                }

                foreach (var element in generated.Descendants())
                {
                    var attributeLists = element.Elements().Count(e => e.Name.LocalName == "AttributeList");
                    if (attributeLists > 1)
                    {
                        Add("blocking", "duplicate-attribute-list",
                            $"'{element.Name.LocalName}' contains {attributeLists} AttributeList nodes; exactly one is allowed");
                    }
                }

                foreach (var pair in generatedShapes)
                {
                    var generatedName = pair.Key;
                    var generatedShape = pair.Value;
                    if (!referenceShapes.TryGetValue(generatedName, out var referenceShape))
                    {
                        Add("blocking", "unsupported-element",
                            $"element '{generatedName}' does not exist in any exported reference");
                        continue;
                    }

                    // An object type that declares CompositionName in the reference must declare at
                    // least one; individual missing values (e.g. MultilingualText "TagValue") are only warnings.
                    if (referenceShape.CompositionNames.Count > 0 && generatedShape.CompositionNames.Count == 0)
                    {
                        Add("blocking", "composition-name-missing",
                            $"'{generatedName}' has no CompositionName (reference uses: {string.Join(", ", referenceShape.CompositionNames.OrderBy(x => x))})");
                    }
                    else
                    {
                        foreach (var compositionName in referenceShape.CompositionNames)
                        {
                            if (!generatedShape.CompositionNames.Contains(compositionName))
                            {
                                Add("warning", "composition-name-unused",
                                    $"'{generatedName}' does not use CompositionName=\"{compositionName}\"");
                            }
                        }
                    }

                    var missingAttributes = referenceShape.Attributes
                        .Where(a => !generatedShape.Attributes.Contains(a)).OrderBy(a => a).ToArray();
                    if (missingAttributes.Length > 0)
                    {
                        Add("warning", "attribute-missing",
                            $"'{generatedName}' does not use attributes present in the reference: {string.Join(", ", missingAttributes)}");
                    }

                    var extraAttributes = generatedShape.Attributes
                        .Where(a => !referenceShape.Attributes.Contains(a)).OrderBy(a => a).ToArray();
                    if (extraAttributes.Length > 0)
                    {
                        Add("warning", "attribute-unknown",
                            $"'{generatedName}' uses attributes absent from the reference: {string.Join(", ", extraAttributes)}");
                    }

                    var missingLinks = referenceShape.Links
                        .Where(l => !generatedShape.Links.Contains(l)).OrderBy(l => l).ToArray();
                    if (missingLinks.Length > 0)
                    {
                        Add("warning", "link-missing",
                            $"'{generatedName}' does not use LinkList entries present in the reference: {string.Join(", ", missingLinks)}");
                    }
                }

                var blockingCount = differences.OfType<JsonObject>()
                    .Count(d => string.Equals(d["severity"]?.ToString(), "blocking", StringComparison.Ordinal));
                var warningCount = differences.OfType<JsonObject>()
                    .Count(d => string.Equals(d["severity"]?.ToString(), "warning", StringComparison.Ordinal));
                result["blockingCount"] = blockingCount;
                result["warningCount"] = warningCount;
                result["ok"] = blockingCount == 0;
                return result;
            }
            catch (Exception ex)
            {
                Add("blocking", "xml-parse-error", ex.Message);
                result["blockingCount"] = differences.Count;
                return result;
            }
        }

        private static Dictionary<string, Shape> ExtractShapes(XDocument document)
        {
            var shapes = new Dictionary<string, Shape>(StringComparer.Ordinal);
            var objectElements = new List<XElement>();

            // Object elements are the primary object under <Document> plus every child of an
            // <ObjectList>. Attribute-value elements (children of AttributeList) and link targets
            // are NOT engineering object classes and must not be compared as such.
            var root = document.Root;
            if (root != null)
            {
                foreach (var child in root.Elements())
                {
                    var name = child.Name.LocalName;
                    if (name != "Engineering" && name != "DocumentInfo")
                    {
                        objectElements.Add(child);
                    }
                }
            }

            foreach (var element in document.Descendants())
            {
                if (element.Parent?.Name.LocalName == "ObjectList")
                {
                    objectElements.Add(element);
                }
            }

            foreach (var element in objectElements)
            {
                var name = element.Name.LocalName;
                if (!shapes.TryGetValue(name, out var shape))
                {
                    shape = new Shape();
                    shapes[name] = shape;
                }

                shape.Count++;
                var compositionName = element.Attribute("CompositionName")?.Value;
                if (!string.IsNullOrEmpty(compositionName))
                {
                    shape.CompositionNames.Add(compositionName);
                }

                foreach (var child in element.Elements())
                {
                    switch (child.Name.LocalName)
                    {
                        case "AttributeList":
                            foreach (var attribute in child.Elements())
                            {
                                shape.Attributes.Add(attribute.Name.LocalName);
                            }
                            break;
                        case "LinkList":
                            foreach (var link in child.Elements())
                            {
                                shape.Links.Add(link.Name.LocalName);
                            }
                            break;
                    }
                }
            }

            return shapes;
        }

        private static string PrimaryObjectName(XDocument document)
        {
            var root = document.Root;
            if (root == null) return "";
            foreach (var child in root.Elements())
            {
                var name = child.Name.LocalName;
                if (name != "Engineering" && name != "DocumentInfo")
                {
                    return name;
                }
            }
            return "";
        }

        private static JsonObject BuildProfile(Dictionary<string, Shape> shapes)
        {
            var profile = new JsonObject();
            foreach (var pair in shapes.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (pair.Key == "AttributeList" || pair.Key == "LinkList" || pair.Key == "ObjectList")
                {
                    continue;
                }

                var shape = pair.Value;
                profile[pair.Key] = new JsonObject
                {
                    ["count"] = shape.Count,
                    ["compositionNames"] = new JsonArray(shape.CompositionNames.OrderBy(x => x, StringComparer.Ordinal)
                        .Select(x => JsonValue.Create(x)).ToArray()),
                    ["attributes"] = new JsonArray(shape.Attributes.OrderBy(x => x, StringComparer.Ordinal)
                        .Select(x => JsonValue.Create(x)).ToArray()),
                    ["links"] = new JsonArray(shape.Links.OrderBy(x => x, StringComparer.Ordinal)
                        .Select(x => JsonValue.Create(x)).ToArray())
                };
            }
            return profile;
        }
    }
}
