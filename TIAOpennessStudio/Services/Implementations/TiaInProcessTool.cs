#if TIA_V18
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Portal;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Wraps a single Portal class method as an IAgentTool, enabling in-process
    /// TIA tool invocation. All executions are marshaled to the STA thread for
    /// thread safety.
    /// </summary>
    public sealed class TiaInProcessTool : IAgentTool
    {
        private readonly TiaToolDefinition _definition;
        private readonly Portal _portal;
        private readonly Func<Func<object>, Task<object>> _runOnSta;

        public TiaInProcessTool(
            TiaToolDefinition definition,
            Portal portal,
            Func<Func<object>, Task<object>> runOnSta)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _portal = portal ?? throw new ArgumentNullException(nameof(portal));
            _runOnSta = runOnSta ?? throw new ArgumentNullException(nameof(runOnSta));
        }

        public string Name => _definition.Name;
        public string Description => _definition.Description;
        public string InputSchema => _definition.InputSchema;
        public ToolPermissionLevel Permission => _definition.Permission;
        public bool SupportsParallel => false; // All Portal calls are STA-bound

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                // Version guard
                int currentVersion = TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion;
                if (currentVersion > 0 && currentVersion < _definition.MinVersion)
                {
                    return AgentToolResult.Fail(
                        $"Tool '{_definition.Name}' requires TIA Portal V{_definition.MinVersion}+, " +
                        $"but connected version is V{currentVersion}.");
                }

                // Parse parameters
                var args = ParseParameters(parameters);

                // Marshal to the STA thread for execution
                var result = await _runOnSta(() => DispatchCall(args)).ConfigureAwait(false);

                // Serialize the result
                return SerializeResult(result);
            }
            catch (OperationCanceledException)
            {
                return AgentToolResult.Fail($"Tool '{_definition.Name}' was cancelled.");
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"Tool '{_definition.Name}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Routes to the concrete Portal method call based on PortalMethodName.
        /// </summary>
        private object DispatchCall(Dictionary<string, string> args)
        {
            string softwarePath = args.GetValueOrDefault("software_path", "");

            switch (_definition.PortalMethodName)
            {
                // ── Online ──
                case "GetOnlineState":
                    return _portal.GetOnlineState(softwarePath);

                case "GoOnline":
                    return _portal.GoOnline(
                        softwarePath,
                        args.GetValueOrDefault("ip_address"),
                        args.GetValueOrDefault("password"));

                case "GoOffline":
                    return _portal.GoOffline(softwarePath);

                case "CompareSoftwareToOnline":
                    int maxDepth = int.TryParse(args.GetValueOrDefault("max_depth", "4"), out var d) ? d : 4;
                    int maxEntries = int.TryParse(args.GetValueOrDefault("max_entries", "200"), out var e) ? e : 200;
                    return _portal.CompareSoftwareToOnline(softwarePath, maxDepth, maxEntries);

                // ── Download ──
                case "DownloadToPlc":
                    return _portal.DownloadToPlc(
                        softwarePath,
                        ParseBool(args.GetValueOrDefault("consistent_blocks_only"), true),
                        ParseBool(args.GetValueOrDefault("keep_actual_values"), true),
                        ParseBool(args.GetValueOrDefault("start_after_download"), true),
                        ParseBool(args.GetValueOrDefault("stop_before_download"), true),
                        args.GetValueOrDefault("password"));

                case "CheckDownloadReadiness":
                    return _portal.CheckDownloadReadiness(softwarePath);

                // ── Watch Tables ──
                case "GetPlcWatchTables":
                    return _portal.GetPlcWatchTables(softwarePath);

                case "EnsureWatchTableEntry":
                    return _portal.EnsureWatchTableEntry(
                        softwarePath,
                        args.GetValueOrDefault("table_name", ""),
                        args.GetValueOrDefault("address", ""),
                        args.GetValueOrDefault("display_format", ""));

                // ── Cross References ──
                case "GetCrossReferences":
                    return _portal.GetCrossReferences(
                        softwarePath,
                        args.GetValueOrDefault("object_path", ""),
                        args.GetValueOrDefault("object_kind", "Block"),
                        args.GetValueOrDefault("filter", "AllObjects"));

                // ── Alarm ──
                case "ExportAlarmClasses":
                    return _portal.ExportAlarmClasses(
                        softwarePath,
                        args.GetValueOrDefault("export_path", ""));

                // ── Read-only inspection (batch 1) ──
                case "GetPlcTagTables":
                    return _portal.GetPlcTagTables(softwarePath);

                case "GetPlcForceTables":
                    return _portal.GetPlcForceTables(softwarePath);

                case "GetTechnologyObjects":
                    return _portal.GetTechnologyObjects(softwarePath);

                case "GetHmiProgramInfo":
                    return _portal.GetHmiProgramInfo(softwarePath);

                case "GetPlcExternalSources":
                    return _portal.GetPlcExternalSources(softwarePath);

                case "GetOpcUaConfig":
                    return _portal.GetOpcUaConfig(softwarePath);

                case "GetProjectTree":
                    return _portal.GetProjectTree();

                case "SearchHardwareCatalog":
                    int hwLimit = int.TryParse(args.GetValueOrDefault("limit", "50"), out var hl) ? hl : 50;
                    return _portal.SearchHardwareCatalog(args.GetValueOrDefault("keyword", ""), hwLimit);

                case "GetDeviceItemTree":
                    int diDepth = int.TryParse(args.GetValueOrDefault("max_depth", "4"), out var dd) ? dd : 4;
                    return _portal.GetDeviceItemTree(args.GetValueOrDefault("device_item_path", ""), diDepth);

                default:
                    throw new NotSupportedException(
                        $"Portal method '{_definition.PortalMethodName}' is not mapped in TiaInProcessTool. " +
                        $"Add a case in DispatchCall.");
            }
        }

        private static Dictionary<string, string> ParseParameters(JsonElement parameters)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (parameters.ValueKind != JsonValueKind.Object)
                return dict;

            foreach (var prop in parameters.EnumerateObject())
            {
                var value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText()
                };
                if (value != null)
                    dict[prop.Name] = value;
            }
            return dict;
        }

        private static AgentToolResult SerializeResult(object result)
        {
            if (result == null)
                return AgentToolResult.Ok("(no result)");

            if (result is string s)
                return AgentToolResult.Ok(s);

            if (result is List<string> list)
                return AgentToolResult.Ok(string.Join("\n", list));

            // For complex objects, fall back to JSON serialization
            try
            {
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                return AgentToolResult.Ok(json);
            }
            catch
            {
                return AgentToolResult.Ok(result.ToString());
            }
        }

        private static bool ParseBool(string value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            if (bool.TryParse(value, out var b)) return b;
            return defaultValue;
        }
    }
}
#endif
