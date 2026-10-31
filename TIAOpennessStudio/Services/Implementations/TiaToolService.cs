using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Builders;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// TIA Portal tool service implementation: wraps the offline builders and
    /// the TIA import capability.
    /// </summary>
    public class TiaToolService : ITiaToolService
    {
        private readonly ITiaOpennessService _tiaService;
        private readonly IUIService _uiService;

        public TiaToolService(ITiaOpennessService tiaService, IUIService uiService)
        {
            _tiaService = tiaService;
            _uiService = uiService;
        }

        // ===== Offline build (no TIA connection) =====

        public string BuildUdtXml(string json)
        {
            var result = PlcBuilderToolJson.BuildUdt(json);
            return ExtractXml(result);
        }

        public string BuildTagTableXml(string json)
        {
            var result = PlcBuilderToolJson.BuildTagTable(json);
            return ExtractXml(result);
        }

        public string BuildGlobalDbXml(string json)
        {
            var result = PlcBuilderToolJson.BuildGlobalDb(json);
            return ExtractXml(result);
        }

        public string BuildFcBlockXml(string json)
        {
            var result = PlcBuilderToolJson.ComposeFcBlock(json);
            return ExtractXml(result);
        }

        public string BuildFbBlockXml(string json)
        {
            var result = PlcBuilderToolJson.ComposeFbBlock(json);
            return ExtractXml(result);
        }

        public string BuildLadFcBlockXml(string json)
        {
            var result = PlcBuilderToolJson.ComposeLadFcBlock(json);
            return ExtractXml(result);
        }

        public string BuildLadFbBlockXml(string json)
        {
            var result = PlcBuilderToolJson.ComposeLadFbBlock(json);
            return ExtractXml(result);
        }

        public string BuildGraphFbBlockXml(string json)
        {
            var result = GraphBlockToolJson.ComposeGraphFbBlock(json);
            return ExtractXml(result);
        }

        public string BuildFlgNetCallXml(string json)
        {
            var result = PlcBuilderToolJson.BuildFlgNetCall(json);
            return ExtractXml(result);
        }

        public string BuildStructuredTextXml(string json)
        {
            var result = PlcBuilderToolJson.BuildStructuredText(json);
            return ExtractXml(result);
        }

        // ===== Build + import (connected to TIA) =====

        public async Task<string> BuildAndImportAsync(string softwarePath, string kind, string json, string groupPath = "")
        {
            if (_tiaService?.PlcSoftware == null)
                throw new InvalidOperationException("Not connected to TIA Portal. Open a project first.");

            // Resolve the MultilingualText culture from the live project (avoids emitting a culture the project lacks).
            string culture = "zh-CN";
            try { culture = await _tiaService.ResolveProjectCultureAsync().ConfigureAwait(false); }
            catch { /* keep the default culture when unavailable */ }

            // 1. Build XML offline
            string xml = kind.ToLowerInvariant() switch
            {
                "udt" => ExtractXml(PlcBuilderToolJson.BuildUdt(json, culture)),
                "tagtable" => BuildTagTableXml(json),
                "globaldb" => BuildGlobalDbXml(json),
                "fc" => ExtractXml(PlcBuilderToolJson.ComposeFcBlock(json, culture)),
                "fb" => ExtractXml(PlcBuilderToolJson.ComposeFbBlock(json, culture)),
                "ladfc" => ExtractXml(PlcBuilderToolJson.ComposeLadFcBlock(json, culture)),
                "ladfb" => ExtractXml(PlcBuilderToolJson.ComposeLadFbBlock(json, culture)),
                "graphfb" => ExtractXml(GraphBlockToolJson.ComposeGraphFbBlock(json, culture)),
                _ => throw new ArgumentException($"Unsupported block kind: {kind}. Supported: udt|tagtable|globaldb|fc|fb|ladfc|ladfb|graphfb")
            };

            if (string.IsNullOrWhiteSpace(xml))
                throw new InvalidOperationException("Offline build returned empty XML.");

            // 2. Extract the block name from the JSON
            string blockName = ExtractBlockName(json, kind);

            // 3. Import into TIA Portal
            string result = await _tiaService.ImportBlockCodeAsync(blockName, kind.ToUpperInvariant(), xml, groupPath);

            // GRAPH XML import has no compile gate of its own; compile the block explicitly so a
            // structurally bad sequence is not reported as a successful generation.
            if (string.Equals(kind, "graphfb", StringComparison.OrdinalIgnoreCase))
            {
                var compile = await _tiaService.CompileBlockAsync(blockName).ConfigureAwait(false);
                if (compile != null && !compile.Success)
                {
                    var messages = (compile.Errors ?? new System.Collections.Generic.List<CompilationError>())
                        .Select(e => e?.Message ?? "")
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Take(5)
                        .ToArray();
                    throw new InvalidOperationException(
                        "GRAPH block was imported but block compilation failed: " +
                        (messages.Length > 0 ? string.Join("; ", messages) : (compile.ErrorMessage ?? "unknown error")));
                }
                return $"[Offline build] {kind} '{blockName}' built.\n[TIA import] {result}\n[Block compile] ok";
            }

            return $"[Offline build] {kind} '{blockName}' succeeded.\n[TIA import] {result}";
        }

        public async Task<string> ImportBlockCodeAsync(string blockName, string blockType, string code, string groupPath = "")
        {
            if (_tiaService?.PlcSoftware == null)
                throw new InvalidOperationException("Not connected to TIA Portal. Open a project first.");
            return await _tiaService.ImportBlockCodeAsync(blockName, blockType, code, groupPath);
        }

        // ===== Project scaffolding =====

        public async Task<string> ScaffoldProjectAsync(string specJson, bool dryRun = false)
        {
            var root = JsonNode.Parse(specJson) as JsonObject
                ?? throw new ArgumentException("Invalid project spec JSON.");

            var results = new System.Text.StringBuilder();
            int successCount = 0;
            int failCount = 0;

            // Handle UDT
            if (root["udt"] is JsonArray udtArray)
            {
                foreach (var item in udtArray)
                {
                    var json = item!.ToJsonString();
                    try
                    {
                        if (dryRun)
                        {
                            var xml = BuildUdtXml(json);
                            results.AppendLine($"[dryRun] UDT built successfully: {xml.Length} characters");
                        }
                        else
                        {
                            var r = await BuildAndImportAsync("", "udt", json);
                            results.AppendLine(r);
                        }
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        results.AppendLine($"[Failed] UDT: {ex.Message}");
                        failCount++;
                    }
                }
            }

            // Handle TagTable
            if (root["tagTable"] is JsonArray tagTableArray)
            {
                foreach (var item in tagTableArray)
                {
                    var json = item!.ToJsonString();
                    try
                    {
                        if (dryRun)
                        {
                            var xml = BuildTagTableXml(json);
                            results.AppendLine($"[dryRun] TagTable built successfully: {xml.Length} characters");
                        }
                        else
                        {
                            var r = await BuildAndImportAsync("", "tagtable", json);
                            results.AppendLine(r);
                        }
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        results.AppendLine($"[Failed] TagTable: {ex.Message}");
                        failCount++;
                    }
                }
            }

            // Handle GlobalDB
            if (root["globalDb"] is JsonArray globalDbArray)
            {
                foreach (var item in globalDbArray)
                {
                    var json = item!.ToJsonString();
                    try
                    {
                        if (dryRun)
                        {
                            var xml = BuildGlobalDbXml(json);
                            results.AppendLine($"[dryRun] GlobalDB built successfully: {xml.Length} characters");
                        }
                        else
                        {
                            var r = await BuildAndImportAsync("", "globaldb", json);
                            results.AppendLine(r);
                        }
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        results.AppendLine($"[Failed] GlobalDB: {ex.Message}");
                        failCount++;
                    }
                }
            }

            // Handle FC
            if (root["fc"] is JsonArray fcArray)
            {
                foreach (var item in fcArray)
                {
                    var json = item!.ToJsonString();
                    try
                    {
                        if (dryRun)
                        {
                            var xml = BuildFcBlockXml(json);
                            results.AppendLine($"[dryRun] FC built successfully: {xml.Length} characters");
                        }
                        else
                        {
                            var r = await BuildAndImportAsync("", "fc", json);
                            results.AppendLine(r);
                        }
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        results.AppendLine($"[Failed] FC: {ex.Message}");
                        failCount++;
                    }
                }
            }

            // Handle FB
            if (root["fb"] is JsonArray fbArray)
            {
                foreach (var item in fbArray)
                {
                    var json = item!.ToJsonString();
                    try
                    {
                        if (dryRun)
                        {
                            var xml = BuildFbBlockXml(json);
                            results.AppendLine($"[dryRun] FB built successfully: {xml.Length} characters");
                        }
                        else
                        {
                            var r = await BuildAndImportAsync("", "fb", json);
                            results.AppendLine(r);
                        }
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        results.AppendLine($"[Failed] FB: {ex.Message}");
                        failCount++;
                    }
                }
            }

            // Handle LAD FC
            if (root["ladFc"] is JsonArray ladFcArray)
            {
                foreach (var item in ladFcArray)
                {
                    var json = item!.ToJsonString();
                    try
                    {
                        if (dryRun)
                        {
                            var xml = BuildLadFcBlockXml(json);
                            results.AppendLine($"[dryRun] LAD FC built successfully: {xml.Length} characters");
                        }
                        else
                        {
                            var r = await BuildAndImportAsync("", "ladfc", json);
                            results.AppendLine(r);
                        }
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        results.AppendLine($"[Failed] LAD FC: {ex.Message}");
                        failCount++;
                    }
                }
            }

            string summary = dryRun
                ? $"[dryRun] Validation complete: {successCount} succeeded, {failCount} failed"
                : $"Scaffold complete: {successCount} succeeded, {failCount} failed";

            return results.ToString() + "\n" + summary;
        }

        // ===== Private helpers =====

        private static string ExtractXml(JsonObject result)
        {
            if (result["ok"]?.GetValue<bool>() != true)
            {
                var error = result["error"]?.ToString() ?? "unknown error";
                throw new InvalidOperationException($"Offline build failed: {error}");
            }
            return result["xml"]?.ToString() ?? throw new InvalidOperationException("Build result does not contain an xml field.");
        }

        private static string ExtractBlockName(string json, string kind)
        {
            var root = JsonNode.Parse(json) as JsonObject;
            return kind.ToLowerInvariant() switch
            {
                "udt" => root?["name"]?.ToString() ?? root?["udtName"]?.ToString() ?? "UDT_Generated",
                "tagtable" => root?["tableName"]?.ToString() ?? root?["name"]?.ToString() ?? "TagTable_Generated",
                "globaldb" => root?["dbName"]?.ToString() ?? root?["name"]?.ToString() ?? "DB_Generated",
                "fc" => root?["blockName"]?.ToString() ?? root?["name"]?.ToString() ?? "FC_Generated",
                "fb" => root?["blockName"]?.ToString() ?? root?["name"]?.ToString() ?? "FB_Generated",
                "ladfc" => root?["blockName"]?.ToString() ?? root?["name"]?.ToString() ?? "FC_LAD_Generated",
                "ladfb" => root?["blockName"]?.ToString() ?? root?["name"]?.ToString() ?? "FB_LAD_Generated",
                "graphfb" => root?["blockName"]?.ToString() ?? root?["name"]?.ToString() ?? "FB_GRAPH_Generated",
                _ => "Block_Generated"
            };
        }
    }
}
