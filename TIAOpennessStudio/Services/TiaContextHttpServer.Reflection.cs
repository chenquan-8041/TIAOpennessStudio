using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TIAOpennessStudio.Services.Builders;

namespace TIAOpennessStudio.Services
{
    public sealed partial class TiaContextHttpServer
    {
        private static readonly string[] ReflectionKinds =
        {
            "plc_block", "plc_type", "plc_tag_table", "hmi_screen", "hmi_tag_table"
        };

        /// <summary>
        /// reflect_generation: export the reference object(s) from the live project, obtain the
        /// candidate artifact (from an argument, the project, or an HMI builder), and compare
        /// their SIMATIC ML shapes. Read-only on the project; only XML files are written.
        /// </summary>
        private async System.Threading.Tasks.Task<(string ResultText, string ErrorText)> HandleReflectGenerationAsync(JsonElement arguments)
        {
            var kind = (ReadStringArg(arguments, "kind") ?? "").Trim().ToLowerInvariant();
            if (!ReflectionKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
            {
                return (null, "kind must be one of: " + string.Join(", ", ReflectionKinds) + ".");
            }

            var softwarePath = ReadStringArg(arguments, "software_path") ?? "";
            var isHmi = kind == "hmi_screen" || kind == "hmi_tag_table";
            if (isHmi && string.IsNullOrWhiteSpace(softwarePath))
            {
                return (null, "software_path (HMI device name, e.g. 'IA1') is required for HMI reflection.");
            }

            if (isHmi && _hmiBuilder == null)
            {
                return (null, "HMI builder is not available.");
            }

            var outputDir = ReadStringArg(arguments, "output_dir");
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                outputDir = Path.Combine(Path.GetTempPath(), "tia-openness-reflect",
                    DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            }
            Directory.CreateDirectory(outputDir);

            // ── Candidate artifact ────────────────────────────────────────────────
            string candidateXml = null;
            string candidateSource = null;
            var candidateName = ReadStringArg(arguments, "candidate_name");
            var xmlArgument = ReadStringArg(arguments, "xml");
            var xmlPath = ReadStringArg(arguments, "xml_path");
            var jsonArgument = ReadStringArg(arguments, "json");

            if (!string.IsNullOrWhiteSpace(xmlArgument))
            {
                candidateXml = xmlArgument;
                candidateSource = "xml";
            }
            else if (!string.IsNullOrWhiteSpace(xmlPath))
            {
                if (!File.Exists(xmlPath)) return (null, "xml_path does not exist: " + xmlPath);
                candidateXml = File.ReadAllText(xmlPath);
                candidateSource = "xml_path";
            }
            else if (isHmi && !string.IsNullOrWhiteSpace(jsonArgument))
            {
                try
                {
                    var target = ReadStringArg(arguments, "target") ?? "classic";
                    if (!string.Equals(target, "classic", StringComparison.OrdinalIgnoreCase))
                    {
                        return (null, "reflect_generation supports 'classic' HMI composition only; Unified produces an apply JSON, not import XML.");
                    }
                    var version = ResolveHmiEngineeringVersion(ReadStringArg(arguments, "tia_version"));
                    var built = kind == "hmi_screen"
                        ? _hmiBuilder.BuildHmiScreen(jsonArgument, target, version)
                        : _hmiBuilder.BuildHmiTagTable(jsonArgument, target, version);
                    candidateXml = built?["xml"]?.ToString();
                    if (string.IsNullOrWhiteSpace(candidateXml)) return (null, "The HMI builder returned no XML.");
                    candidateSource = "builder";
                }
                catch (Exception ex)
                {
                    return (null, "HMI compose failed: " + ex.Message);
                }
            }

            if (string.IsNullOrWhiteSpace(candidateXml) && string.IsNullOrWhiteSpace(candidateName))
            {
                return (null, "Provide a candidate via candidate_name (export from the project), xml, xml_path, or (HMI only) json.");
            }

            // ── References + candidate export from the project (STA) ─────────────
            var requestedReferences = ReadNameList(arguments, "reference_names", "reference_name");
            if (!isHmi && kind != "plc_tag_table" && requestedReferences.Count == 0)
            {
                return (null, "reference_name is required for " + kind + " (blocks/types cannot be auto-picked).");
            }

            List<(string Name, string Xml)> references = null;
            string projectCandidateXml = null;
            string projectError = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    references = await ExportReferenceObjectsAsync(kind, softwarePath, requestedReferences, outputDir).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(candidateXml))
                    {
                        projectCandidateXml = await ExportSingleObjectAsync(kind, softwarePath, candidateName, outputDir, "candidate").ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    projectError = DescribeException(ex);
                }
            }).ConfigureAwait(false);

            if (projectError != null) return (null, projectError);
            if (references == null || references.Count == 0) return (null, "No reference artifact could be exported for kind " + kind + ".");

            if (string.IsNullOrWhiteSpace(candidateXml))
            {
                candidateXml = projectCandidateXml;
                candidateSource = "project";
                if (string.IsNullOrWhiteSpace(candidateXml)) return (null, "Candidate export produced no XML.");
            }

            var candidatePath = Path.Combine(outputDir, "candidate_" + kind + ".xml");
            File.WriteAllText(candidatePath, candidateXml, System.Text.Encoding.UTF8);

            JsonObject comparison;
            try
            {
                comparison = SimaticMlStructureComparer.Compare(references.Select(r => r.Xml).ToArray(), candidateXml);
            }
            catch (Exception ex)
            {
                return (null, "Structure comparison failed: " + ex.Message);
            }

            var result = new JsonObject
            {
                ["ok"] = comparison["ok"]?.GetValue<bool>() == true,
                ["kind"] = kind,
                ["software"] = softwarePath,
                ["outputDir"] = outputDir,
                ["candidateSource"] = candidateSource,
                ["candidatePath"] = candidatePath,
                ["references"] = new JsonArray(references.Select(r => (JsonNode)new JsonObject
                {
                    ["name"] = r.Name,
                    ["xmlChars"] = r.Xml?.Length ?? 0
                }).ToArray()),
                ["compare"] = comparison
            };
            return (result.ToJsonString(_jsonOptions), null);
        }

        private async System.Threading.Tasks.Task<List<(string Name, string Xml)>> ExportReferenceObjectsAsync(
            string kind, string softwarePath, List<string> requestedNames, string outputDir)
        {
            var results = new List<(string Name, string Xml)>();
            if (kind == "plc_block" || kind == "plc_type")
            {
                foreach (var name in requestedNames)
                {
                    var xml = await _tia.ExportBlockCodeAsync(name).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(xml))
                    {
                        throw new InvalidOperationException($"PLC object '{name}' exported no XML.");
                    }
                    results.Add((name, xml));
                }
                return results;
            }

            var objects = kind == "plc_tag_table"
                ? EnumeratePlcTagTables().ToList()
                : EnumerateHmiObjects(kind, softwarePath).ToList();

            if (requestedNames.Count == 0)
            {
                results.AddRange(objects.Take(3).Select(o => (o.Name, ExportObjectToFile(o.Object, kind, o.Name, outputDir, "reference"))));
                return results;
            }

            foreach (var wanted in requestedNames)
            {
                var match = objects.FirstOrDefault(o => string.Equals(o.Name, wanted, StringComparison.OrdinalIgnoreCase));
                if (match.Object == null)
                {
                    throw new InvalidOperationException($"'{wanted}' was not found for kind {kind}.");
                }
                results.Add((match.Name, ExportObjectToFile(match.Object, kind, match.Name, outputDir, "reference")));
            }
            return results;
        }

        private async System.Threading.Tasks.Task<string> ExportSingleObjectAsync(
            string kind, string softwarePath, string name, string outputDir, string prefix)
        {
            if (kind == "plc_block" || kind == "plc_type")
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("candidate_name is required for " + kind + ".");
                }
                return await _tia.ExportBlockCodeAsync(name).ConfigureAwait(false);
            }

            var objects = kind == "plc_tag_table"
                ? EnumeratePlcTagTables().ToList()
                : EnumerateHmiObjects(kind, softwarePath).ToList();

            var match = string.IsNullOrWhiteSpace(name)
                ? objects.FirstOrDefault()
                : objects.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match.Object == null)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(name)
                        ? $"No object of kind {kind} exists to use as candidate."
                        : $"Candidate '{name}' was not found for kind {kind}.");
            }
            return ExportObjectToFile(match.Object, kind, match.Name, outputDir, prefix);
        }

        private static string ExportObjectToFile(object target, string kind, string name, string outputDir, string prefix)
        {
            var fileName = prefix + "_" + kind + "_" + MakeSafeFileName(name) + ".xml";
            var path = Path.Combine(outputDir, fileName);
            if (File.Exists(path)) File.Delete(path);

            var method = target.GetType().GetMethod("Export",
                new[] { typeof(FileInfo), typeof(ExportOptions) });
            if (method == null)
            {
                throw new InvalidOperationException($"Type '{target.GetType().FullName}' has no Export(FileInfo, ExportOptions) method.");
            }
            method.Invoke(target, new object[] { new FileInfo(path), ExportOptions.None });
            return File.ReadAllText(path);
        }

        private IEnumerable<(string Name, object Object)> EnumeratePlcTagTables()
        {
            var plc = _tia?.PlcSoftware;
            if (plc == null)
            {
                throw new InvalidOperationException("No PLC software is connected.");
            }
            var root = TryGetPropertyValue(plc, "TagTableGroup") ?? plc;
            var results = new List<(string, object)>();
            CollectReflectionObjects(root, "TagTables", "Groups", results);
            return results;
        }

        private IEnumerable<(string Name, object Object)> EnumerateHmiObjects(string kind, string softwarePath)
        {
            var project = _tia?.Project;
            if (project == null)
            {
                throw new InvalidOperationException("No project is open in TIA Portal.");
            }
            var software = FindHmiSoftware(project, softwarePath);
            if (software == null)
            {
                throw new InvalidOperationException($"HMI software '{softwarePath}' was not found in the open project.");
            }

            var results = new List<(string, object)>();
            if (kind == "hmi_screen")
            {
                var folder = TryGetPropertyValue(software, "ScreenFolder") ?? software;
                CollectReflectionObjects(folder, "Screens", "Folders", results);
            }
            else
            {
                var folder = TryGetPropertyValue(software, "TagFolder");
                if (folder != null) CollectReflectionObjects(folder, "TagTables", "Folders", results);
            }
            return results;
        }

        private static void CollectReflectionObjects(object folder, string itemsProperty, string foldersProperty,
            List<(string Name, object Object)> results)
        {
            var items = TryGetPropertyValue(folder, itemsProperty);
            if (items is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    var name = TryGetStringProperty(item, "Name") ?? "";
                    if (!string.IsNullOrWhiteSpace(name)) results.Add((name, item));
                }
            }

            var folders = TryGetPropertyValue(folder, foldersProperty);
            if (folders is System.Collections.IEnumerable folderEnumerable)
            {
                foreach (var child in folderEnumerable)
                {
                    if (child == null) continue;
                    CollectReflectionObjects(child, itemsProperty, foldersProperty, results);
                }
            }
        }

        private static object FindHmiSoftware(Project project, string softwarePath)
        {
            if (project == null) return null;
            foreach (var device in project.Devices)
            {
                foreach (var item in device.DeviceItems)
                {
                    var found = FindHmiSoftwareInItem(item, softwarePath);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static object FindHmiSoftwareInItem(DeviceItem item, string softwarePath)
        {
            SoftwareContainer container = null;
            try { container = item.GetService<SoftwareContainer>(); }
            catch { }

            var software = container?.Software;
            if (software != null)
            {
                var name = TryGetStringProperty(software, "Name") ?? "";
                if (string.Equals(name, softwarePath, StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf(softwarePath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return software;
                }
            }

            foreach (var child in item.DeviceItems)
            {
                var found = FindHmiSoftwareInItem(child, softwarePath);
                if (found != null) return found;
            }
            return null;
        }

        private static object TryGetPropertyValue(object target, string propertyName)
        {
            if (target == null) return null;
            try { return target.GetType().GetProperty(propertyName)?.GetValue(target); }
            catch { return null; }
        }

        private static string TryGetStringProperty(object target, string propertyName)
        {
            if (target == null) return null;
            try { return target.GetType().GetProperty(propertyName)?.GetValue(target)?.ToString(); }
            catch { return null; }
        }

        private static List<string> ReadNameList(JsonElement arguments, params string[] names)
        {
            var result = new List<string>();
            if (arguments.ValueKind != JsonValueKind.Object) return result;
            foreach (var name in names)
            {
                if (!arguments.TryGetProperty(name, out var element)) continue;
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        var value = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                        if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
                    }
                }
                else
                {
                    var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    foreach (var part in text.Split(','))
                    {
                        if (!string.IsNullOrWhiteSpace(part)) result.Add(part.Trim());
                    }
                }
            }
            return result;
        }

        private static string MakeSafeFileName(string name)
        {
            var result = name ?? "";
            foreach (var c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
            return result;
        }

        private static string DescribeException(Exception ex)
        {
            var parts = new List<string>();
            for (var current = ex; current != null; current = current.InnerException)
            {
                parts.Add(current.Message);
            }
            return string.Join(" | ", parts);
        }
    }
}
