using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Adapter for built-in tools — provides the basic tools available to the Agent.
    /// </summary>
    public class BuiltInToolsAdapter
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly ITiaToolService _tiaToolService;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public BuiltInToolsAdapter(ITiaToolService tiaToolService = null)
        {
            _tiaToolService = tiaToolService;
        }

        /// <summary>
        /// Returns all built-in tools.
        /// </summary>
        public List<ToolInfo> GetBuiltInTools()
        {
            var tools = new List<ToolInfo>
            {
                new ToolInfo
                {
                    Name = "web_search",
                    Description = "Search the web for information using a search engine",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "query", Type = "string", Description = "Search query", Required = true },
                        new ToolParameter { Name = "max_results", Type = "integer", Description = "Maximum number of results", Required = false, Default = 5 }
                    }
                },
                new ToolInfo
                {
                    Name = "file_read",
                    Description = "Read content from a file",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "path", Type = "string", Description = "File path to read", Required = true },
                        new ToolParameter { Name = "encoding", Type = "string", Description = "File encoding", Required = false, Default = "UTF-8" }
                    }
                },
                new ToolInfo
                {
                    Name = "file_write",
                    Description = "Write content to a file",
                    IsDangerous = true,
                    PermissionLevel = ToolPermissionLevel.RequiresConfirmation, // Writes to disk: high risk, requires confirmation.
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "path", Type = "string", Description = "File path to write", Required = true },
                        new ToolParameter { Name = "content", Type = "string", Description = "Content to write", Required = true },
                        new ToolParameter { Name = "encoding", Type = "string", Description = "File encoding", Required = false, Default = "UTF-8" }
                    }
                },
                new ToolInfo
                {
                    Name = "file_list",
                    Description = "List files in a directory",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "path", Type = "string", Description = "Directory path", Required = true },
                        new ToolParameter { Name = "pattern", Type = "string", Description = "File pattern (e.g., *.txt)", Required = false },
                        new ToolParameter { Name = "recursive", Type = "boolean", Description = "Search recursively", Required = false, Default = false }
                    }
                },
                new ToolInfo
                {
                    Name = "calculator",
                    Description = "Perform mathematical calculations",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "expression", Type = "string", Description = "Mathematical expression", Required = true }
                    }
                },
                new ToolInfo
                {
                    Name = "execute_command",
                    Description = "Execute a shell command",
                    IsDangerous = true,
                    PermissionLevel = ToolPermissionLevel.RequiresConfirmation, // Executes a command: high risk, requires confirmation.
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "command", Type = "string", Description = "Command to execute", Required = true },
                        new ToolParameter { Name = "working_directory", Type = "string", Description = "Working directory", Required = false }
                    }
                },
                new ToolInfo
                {
                    Name = "get_system_info",
                    Description = "Get system information",
                    InputSchema = new List<ToolParameter>()
                },
                new ToolInfo
                {
                    Name = "json_parser",
                    Description = "Parse and validate JSON",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "json", Type = "string", Description = "JSON string to parse", Required = true }
                    }
                }
            };

            // TIA PLC build tools (require ITiaToolService).
            if (_tiaToolService != null)
            {
                tools.Add(new ToolInfo
                {
                    Name = "plc_build_udt",
                    Description = "Build PLC UDT (User Defined Type) XML from JSON spec. Returns TIA-importable XML.",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "json", Type = "string", Description = "JSON spec with name and members[]", Required = true }
                    }
                });
                tools.Add(new ToolInfo
                {
                    Name = "plc_build_tag_table",
                    Description = "Build PLC tag table XML from JSON spec. Returns TIA-importable XML.",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "json", Type = "string", Description = "JSON spec with tableName and tags[]", Required = true }
                    }
                });
                tools.Add(new ToolInfo
                {
                    Name = "plc_build_global_db",
                    Description = "Build PLC global DB XML from JSON spec. Returns TIA-importable XML.",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "json", Type = "string", Description = "JSON spec with dbName, dbNumber, staticMembers[]", Required = true }
                    }
                });
                tools.Add(new ToolInfo
                {
                    Name = "plc_build_fc",
                    Description = "Build SCL FC block XML from JSON spec. Returns TIA-importable XML.",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "json", Type = "string", Description = "JSON spec with blockName, inputs[], outputs[], structuredText", Required = true }
                    }
                });
                tools.Add(new ToolInfo
                {
                    Name = "plc_build_fb",
                    Description = "Build SCL FB block XML from JSON spec. Returns TIA-importable XML.",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "json", Type = "string", Description = "JSON spec with blockName, inputs[], outputs[], statics[], structuredText", Required = true }
                    }
                });
                tools.Add(new ToolInfo
                {
                    Name = "plc_build_lad_fc",
                    Description = "Build LAD FC block XML with FlgNet networks from JSON spec. Returns TIA-importable XML.",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "json", Type = "string", Description = "JSON spec with blockName, inputs[], outputs[], networks[]", Required = true }
                    }
                });
                tools.Add(new ToolInfo
                {
                    Name = "plc_build_graph_fb",
                    Description = "Build S7-GRAPH FB block XML (step sequence) from JSON spec. Returns TIA-importable XML.",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "json", Type = "string", Description = "JSON spec with blockName, blockNumber, inputs[], outputs[], sequence{steps,transitions,branches,connections}", Required = true }
                    }
                });
                tools.Add(new ToolInfo
                {
                    Name = "plc_build_and_import",
                    Description = "Build PLC block XML from JSON and import into TIA Portal. Supports: udt|tagtable|globaldb|fc|fb|ladfc|ladfb|graphfb",
                    IsDangerous = true,
                    PermissionLevel = ToolPermissionLevel.RequiresConfirmation, // Imports into TIA: write operation, requires confirmation.
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "kind", Type = "string", Description = "Block type: udt|tagtable|globaldb|fc|fb|ladfc|ladfb|graphfb", Required = true },
                        new ToolParameter { Name = "json", Type = "string", Description = "JSON spec for the block", Required = true },
                        new ToolParameter { Name = "group_path", Type = "string", Description = "Target group path (empty=root)", Required = false, Default = "" }
                    }
                });
                tools.Add(new ToolInfo
                {
                    Name = "import_block_code",
                    Description = "Import raw XML or SCL payload into TIA Portal for a block.",
                    IsDangerous = true,
                    PermissionLevel = ToolPermissionLevel.RequiresConfirmation, // Imports into TIA: write operation, requires confirmation.
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "block_name", Type = "string", Description = "Logical block name", Required = true },
                        new ToolParameter { Name = "block_type", Type = "string", Description = "Block type such as FB|FC|DB|OB|UDT", Required = true },
                        new ToolParameter { Name = "code", Type = "string", Description = "Full XML or SCL payload", Required = true },
                        new ToolParameter { Name = "group_path", Type = "string", Description = "Target group path", Required = false, Default = "" }
                    }
                });
                tools.Add(new ToolInfo
                {
                    Name = "resolve_xml_fragment",
                    Description = "Resolve a raw XML fragment by compileUnitId and optional uid from an exported TIA XML block.",
                    InputSchema = new List<ToolParameter>
                    {
                        new ToolParameter { Name = "xml", Type = "string", Description = "Exported TIA XML content", Required = true },
                        new ToolParameter { Name = "compile_unit_id", Type = "string", Description = "CompileUnit ID", Required = false, Default = "" },
                        new ToolParameter { Name = "uid", Type = "string", Description = "Target element UId", Required = false, Default = "" }
                    }
                });
            }

            return tools;
        }

        /// <summary>
        /// Executes a built-in tool.
        /// </summary>
        public async Task<ToolResult> ExecuteToolAsync(string toolName, Dictionary<string, object> parameters)
        {
            var startTime = DateTime.Now;
            var result = new ToolResult();

            try
            {
                switch (toolName.ToLower())
                {
                    case "web_search":
                        result = await WebSearchAsync(parameters);
                        break;
                    case "file_read":
                        result = FileRead(parameters);
                        break;
                    case "file_write":
                        result = FileWrite(parameters);
                        break;
                    case "file_list":
                        result = FileList(parameters);
                        break;
                    case "calculator":
                        result = Calculator(parameters);
                        break;
                    case "execute_command":
                        result = await ExecuteCommandAsync(parameters);
                        break;
                    case "get_system_info":
                        result = GetSystemInfo();
                        break;
                    case "json_parser":
                        result = JsonParser(parameters);
                        break;
                    // TIA PLC build tools.
                    case "plc_build_udt":
                        result = PlcBuildUdt(parameters);
                        break;
                    case "plc_build_tag_table":
                        result = PlcBuildTagTable(parameters);
                        break;
                    case "plc_build_global_db":
                        result = PlcBuildGlobalDb(parameters);
                        break;
                    case "plc_build_fc":
                        result = PlcBuildFc(parameters);
                        break;
                    case "plc_build_fb":
                        result = PlcBuildFb(parameters);
                        break;
                    case "plc_build_lad_fc":
                        result = PlcBuildLadFc(parameters);
                        break;
                    case "plc_build_graph_fb":
                        result = PlcBuildGraphFb(parameters);
                        break;
                    case "plc_build_and_import":
                        result = await PlcBuildAndImportAsync(parameters);
                        break;
                    case "import_block_code":
                        result = await ImportBlockCodeAsync(parameters);
                        break;
                    case "resolve_xml_fragment":
                        result = ResolveXmlFragment(parameters);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown built-in tool: {toolName}");
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            finally
            {
                result.ExecutionTimeMs = (long)(DateTime.Now - startTime).TotalMilliseconds;
            }

            return result;
        }

        #region Tool implementations

        private Task<ToolResult> WebSearchAsync(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();

            try
            {
                var query = parameters.ContainsKey("query") ? parameters["query"].ToString() : "";
                var maxResults = parameters.ContainsKey("max_results") ? Convert.ToInt32(parameters["max_results"]) : 5;

                if (string.IsNullOrEmpty(query))
                {
                    result.Success = false;
                    result.Error = "Query parameter is required";
                    return Task.FromResult(result);
                }

                result.Success = true;
                result.Output = $"Search query: {query}\nMax results: {maxResults}\nSearch URL: https://duckduckgo.com/?q={Uri.EscapeDataString(query)}";
                result.Result = new
                {
                    query,
                    maxResults,
                    message = "Web search executed. Configure a search API for full results."
                };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return Task.FromResult(result);
        }

        private ToolResult FileRead(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();

            try
            {
                var path = parameters.ContainsKey("path") ? parameters["path"].ToString() : "";
                var encoding = parameters.ContainsKey("encoding") ? parameters["encoding"].ToString() : "UTF-8";

                if (string.IsNullOrEmpty(path))
                {
                    result.Success = false;
                    result.Error = "Path parameter is required";
                    return result;
                }

                if (!File.Exists(path))
                {
                    result.Success = false;
                    result.Error = $"File not found: {path}";
                    return result;
                }

                var enc = Encoding.GetEncoding(encoding);
                var content = File.ReadAllText(path, enc);

                result.Success = true;
                result.Output = content;
                result.Result = new { path, length = content.Length };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        private ToolResult FileWrite(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();

            try
            {
                var path = parameters.ContainsKey("path") ? parameters["path"].ToString() : "";
                var content = parameters.ContainsKey("content") ? parameters["content"].ToString() : "";
                var encoding = parameters.ContainsKey("encoding") ? parameters["encoding"].ToString() : "UTF-8";

                if (string.IsNullOrEmpty(path))
                {
                    result.Success = false;
                    result.Error = "Path parameter is required";
                    return result;
                }

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var enc = Encoding.GetEncoding(encoding);
                File.WriteAllText(path, content, enc);

                result.Success = true;
                result.Output = $"File written successfully: {path}";
                result.Result = new { path, bytesWritten = enc.GetByteCount(content) };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        private ToolResult FileList(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();

            try
            {
                var path = parameters.ContainsKey("path") ? parameters["path"].ToString() : ".";
                var pattern = parameters.ContainsKey("pattern") ? parameters["pattern"].ToString() : "*";
                var recursive = parameters.ContainsKey("recursive") && Convert.ToBoolean(parameters["recursive"]);

                if (!Directory.Exists(path))
                {
                    result.Success = false;
                    result.Error = $"Directory not found: {path}";
                    return result;
                }

                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var files = Directory.GetFiles(path, pattern, searchOption);

                var fileInfos = new List<object>();
                foreach (var file in files)
                {
                    var fi = new FileInfo(file);
                    fileInfos.Add(new
                    {
                        name = fi.Name,
                        path = fi.FullName,
                        size = fi.Length,
                        modified = fi.LastWriteTime
                    });
                }

                result.Success = true;
                result.Output = $"Found {fileInfos.Count} files matching pattern '{pattern}'";
                result.Result = new { path, pattern, recursive, files = fileInfos };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        private ToolResult Calculator(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();

            try
            {
                var expression = parameters.ContainsKey("expression") ? parameters["expression"].ToString() : "";

                if (string.IsNullOrEmpty(expression))
                {
                    result.Success = false;
                    result.Error = "Expression parameter is required";
                    return result;
                }

                var dt = new DataTable();
                var computedValue = dt.Compute(expression, null);

                result.Success = true;
                result.Output = $"{expression} = {computedValue}";
                result.Result = new { expression, result = computedValue };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = $"Calculation error: {ex.Message}";
            }

            return result;
        }

        private async Task<ToolResult> ExecuteCommandAsync(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();

            try
            {
                var command = parameters.ContainsKey("command") ? parameters["command"].ToString() : "";
                var workingDirectory = parameters.ContainsKey("working_directory") ? parameters["working_directory"].ToString() : null;

                if (string.IsNullOrEmpty(command))
                {
                    result.Success = false;
                    result.Error = "Command parameter is required";
                    return result;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    psi.WorkingDirectory = workingDirectory;
                }

                using (var process = Process.Start(psi))
                {
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    process.WaitForExit();

                    var output = await outputTask;
                    var error = await errorTask;

                    result.Success = process.ExitCode == 0;
                    result.Output = string.IsNullOrEmpty(error) ? output : $"Output:\n{output}\nError:\n{error}";
                    result.Result = new
                    {
                        exitCode = process.ExitCode,
                        output,
                        error
                    };

                    if (process.ExitCode != 0)
                    {
                        result.Error = error;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        private ToolResult GetSystemInfo()
        {
            var result = new ToolResult();

            try
            {
                var info = new
                {
                    machineName = Environment.MachineName,
                    userName = Environment.UserName,
                    osVersion = Environment.OSVersion.ToString(),
                    processorCount = Environment.ProcessorCount,
                    workingSet = Environment.WorkingSet,
                    currentDirectory = Environment.CurrentDirectory,
                    timestamp = DateTime.Now
                };

                result.Success = true;
                result.Output = $"Machine: {info.machineName}, User: {info.userName}, OS: {info.osVersion}";
                result.Result = info;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        private ToolResult JsonParser(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();

            try
            {
                var json = parameters.ContainsKey("json") ? parameters["json"].ToString() : "";

                if (string.IsNullOrEmpty(json))
                {
                    result.Success = false;
                    result.Error = "JSON parameter is required";
                    return result;
                }

                var parsed = JsonSerializer.Deserialize<JsonElement>(json);

                result.Success = true;
                result.Output = "JSON parsed successfully";
                result.Result = parsed;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = $"JSON parse error: {ex.Message}";
            }

            return result;
        }

        #endregion

        #region TIA PLC build tools

        private ToolResult PlcBuildUdt(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();
            if (!EnsureTiaToolService(result)) return result;

            try
            {
                var json = GetParam(parameters, "json");
                var xml = _tiaToolService.BuildUdtXml(json);
                result.Success = true;
                result.Output = xml;
                result.Result = new { xml, length = xml.Length };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private ToolResult PlcBuildTagTable(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();
            if (!EnsureTiaToolService(result)) return result;

            try
            {
                var json = GetParam(parameters, "json");
                var xml = _tiaToolService.BuildTagTableXml(json);
                result.Success = true;
                result.Output = xml;
                result.Result = new { xml, length = xml.Length };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private ToolResult PlcBuildGlobalDb(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();
            if (!EnsureTiaToolService(result)) return result;

            try
            {
                var json = GetParam(parameters, "json");
                var xml = _tiaToolService.BuildGlobalDbXml(json);
                result.Success = true;
                result.Output = xml;
                result.Result = new { xml, length = xml.Length };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private ToolResult PlcBuildFc(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();
            if (!EnsureTiaToolService(result)) return result;

            try
            {
                var json = GetParam(parameters, "json");
                var xml = _tiaToolService.BuildFcBlockXml(json);
                result.Success = true;
                result.Output = xml;
                result.Result = new { xml, length = xml.Length };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private ToolResult PlcBuildFb(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();
            if (!EnsureTiaToolService(result)) return result;

            try
            {
                var json = GetParam(parameters, "json");
                var xml = _tiaToolService.BuildFbBlockXml(json);
                result.Success = true;
                result.Output = xml;
                result.Result = new { xml, length = xml.Length };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private ToolResult PlcBuildLadFc(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();
            if (!EnsureTiaToolService(result)) return result;

            try
            {
                var json = GetParam(parameters, "json");
                var xml = _tiaToolService.BuildLadFcBlockXml(json);
                result.Success = true;
                result.Output = xml;
                result.Result = new { xml, length = xml.Length };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private ToolResult PlcBuildGraphFb(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();
            if (!EnsureTiaToolService(result)) return result;

            try
            {
                var json = GetParam(parameters, "json");
                var xml = _tiaToolService.BuildGraphFbBlockXml(json);
                result.Success = true;
                result.Output = xml;
                result.Result = new { xml, length = xml.Length };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private async Task<ToolResult> PlcBuildAndImportAsync(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();
            if (!EnsureTiaToolService(result)) return result;

            try
            {
                var kind = GetParam(parameters, "kind");
                var json = GetParam(parameters, "json");
                var groupPath = parameters.ContainsKey("group_path") ? parameters["group_path"].ToString() : "";

                var importResult = await _tiaToolService.BuildAndImportAsync("", kind, json, groupPath);
                result.Success = true;
                result.Output = importResult;
                result.Result = new { kind, importResult };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private async Task<ToolResult> ImportBlockCodeAsync(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();
            try
            {
                var blockName = GetParam(parameters, "block_name");
                var blockType = GetParam(parameters, "block_type");
                var code = GetParam(parameters, "code");
                var groupPath = GetParam(parameters, "group_path");
                if (_tiaToolService == null)
                {
                    result.Success = false;
                    result.Error = "TIA Tool Service not available. Please connect to TIA Portal first.";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(blockName) || string.IsNullOrWhiteSpace(blockType) || string.IsNullOrWhiteSpace(code))
                {
                    result.Success = false;
                    result.Error = "block_name, block_type and code are required";
                    return result;
                }

                var importResult = await _tiaToolService.ImportBlockCodeAsync(blockName, blockType, code, groupPath);
                result.Success = true;
                result.Output = importResult;
                result.Result = new { blockName, blockType, importResult };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private ToolResult ResolveXmlFragment(Dictionary<string, object> parameters)
        {
            var result = new ToolResult();
            try
            {
                var xml = GetParam(parameters, "xml");
                var compileUnitId = GetParam(parameters, "compile_unit_id");
                var uid = GetParam(parameters, "uid");
                if (string.IsNullOrWhiteSpace(xml))
                {
                    result.Success = false;
                    result.Error = "xml is required";
                    return result;
                }

                if (!TiaXmlAgentReferenceBuilder.TryExtractFragment(xml, compileUnitId, uid, out var fragment))
                {
                    result.Success = false;
                    result.Error = "Unable to resolve XML fragment";
                    return result;
                }

                result.Success = true;
                result.Output = fragment;
                result.Result = new { compileUnitId, uid, fragmentLength = fragment?.Length ?? 0 };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private bool EnsureTiaToolService(ToolResult result)
        {
            if (_tiaToolService == null)
            {
                result.Success = false;
                result.Error = "TIA Tool Service not available. Please connect to TIA Portal first.";
                return false;
            }
            return true;
        }

        private static string GetParam(Dictionary<string, object> parameters, string name)
        {
            return parameters.ContainsKey(name) ? parameters[name]?.ToString() ?? "" : "";
        }

        #endregion
    }
}
