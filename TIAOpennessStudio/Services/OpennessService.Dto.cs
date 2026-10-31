using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// DTO-facade partial of OpennessService: exposes TIA project data as plain-data
    /// DTOs (never COM objects), with every COM access marshaled onto the dedicated STA
    /// host thread via <see cref="OnSta{T}"/>. The UI/ViewModel layers consume these
    /// DTOs and hold opaque string handles instead of live COM objects, so the WPF UI
    /// thread never touches Openness COM and never triggers the STA cross-thread error.
    ///
    /// Handles: each COM object the UI may later act on (block/type/group/software) is
    /// registered in <see cref="_handleMap"/> and referenced by a stable string handle.
    /// The map is cleared whenever the project tree is re-read.
    /// </summary>
    public partial class OpennessService
    {
        // handle -> live COM object (resolved only on the STA thread).
        private readonly ConcurrentDictionary<string, object> _handleMap =
            new ConcurrentDictionary<string, object>();
        private long _handleSeq;

        private string RegisterHandle(object comObject)
        {
            if (comObject == null) return null;
            var handle = "h" + System.Threading.Interlocked.Increment(ref _handleSeq);
            _handleMap[handle] = comObject;
            return handle;
        }

        /// <summary>Resolve a handle back to its COM object. MUST be called on the STA thread.</summary>
        public T ResolveHandle<T>(string handle) where T : class
        {
            if (string.IsNullOrEmpty(handle)) return null;
            return _handleMap.TryGetValue(handle, out var obj) ? obj as T : null;
        }

        private void ClearHandles() => _handleMap.Clear();

        // ── Project-level DTO queries ──────────────────────────────────────

        /// <summary>Project summary as plain data. Safe to call from any thread.</summary>
        public Task<ProjectInfoDto> GetProjectInfoAsync()
            => OnSta(() => Task.FromResult(GetProjectInfoCore()));

        private ProjectInfoDto GetProjectInfoCore()
        {
            var dto = new ProjectInfoDto { IsOpen = false };
            try
            {
                if (Project == null) return dto;
                dto.Name = Project.Name ?? "";
                try { dto.Path = Project.Path?.FullName ?? ""; } catch { dto.Path = ""; }
                try { dto.IsModified = Project.IsModified; } catch { dto.IsModified = false; }
                dto.IsOpen = true;
            }
            catch (ObjectDisposedException) { dto.IsOpen = false; }
            catch (Exception ex) { AppLogger.Log($"[Openness] GetProjectInfo failed: {ex.Message}"); }
            return dto;
        }

        // ── Device tree DTO ────────────────────────────────────────────────

        /// <summary>
        /// Enumerate devices/PLCs as DTOs. Registers each PlcSoftware under a handle so the
        /// UI can later select one by handle. Clears previously issued handles first.
        /// </summary>
        public Task<List<DeviceNodeDto>> GetDeviceTreeAsync()
            => OnSta(() => Task.FromResult(GetDeviceTreeCore()));

        private List<DeviceNodeDto> GetDeviceTreeCore()
        {
            ClearHandles();
            var result = new List<DeviceNodeDto>();

            if (Project == null && IsConnected && TiaPortal.Projects.Count > 0)
                Project = TiaPortal.Projects[0];
            if (Project == null) return result;

            foreach (Device device in Project.Devices)
            {
                var devDto = new DeviceNodeDto { Name = device.Name, IsPlc = false };
                if (device.DeviceItems != null)
                    CollectPlcItems(device.DeviceItems, devDto, new HashSet<int>());
                result.Add(devDto);
            }
            return result;
        }

        private void CollectPlcItems(DeviceItemComposition items, DeviceNodeDto parent, HashSet<int> visited)
        {
            foreach (DeviceItem item in items)
            {
                if (!visited.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item))) continue;

                var container = ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
                if (container?.Software is PlcSoftware plc)
                {
                    parent.Children.Add(new DeviceNodeDto
                    {
                        Name = item.Name,
                        IsPlc = true,
                        PlcHandle = RegisterHandle(plc),
                    });
                }
                else if (container?.Software is HmiTarget hmi)
                {
                    // Classic WinCC HMI (V15.1 and newer). HmiTarget is part of the V15.1
                    // compile baseline, so this branch compiles and runs on the target version.
                    parent.Children.Add(new DeviceNodeDto
                    {
                        Name = item.Name,
                        IsHmi = true,
                        HmiHandle = RegisterHandle(hmi),
                        HmiKind = "Classic",
                    });
                }
                else if (container?.Software != null && IsUnifiedHmiSoftware(container.Software))
                {
                    // Unified HMI (HmiSoftware) only exists on newer portals; detect it by
                    // runtime type name so the V15.1 compile baseline stays untouched.
                    parent.Children.Add(new DeviceNodeDto
                    {
                        Name = item.Name,
                        IsHmi = true,
                        HmiHandle = RegisterHandle(container.Software),
                        HmiKind = "Unified",
                    });
                }

                if (item.DeviceItems != null && item.DeviceItems.Count > 0)
                    CollectPlcItems(item.DeviceItems, parent, visited);
            }
        }

        private static bool IsUnifiedHmiSoftware(object software)
        {
            var type = software.GetType();
            return string.Equals(type.Name, "HmiSoftware", StringComparison.Ordinal)
                || (type.FullName != null && type.FullName.IndexOf("HmiUnified", StringComparison.Ordinal) >= 0);
        }

        // ── HMI screens / tag tables DTO tree ──────────────────────────────

        /// <summary>
        /// Builds the HMI software tree (root → Screens folder → screens, root → tag tables).
        /// Uses reflection for the collection members so classic (V15.1) and unified HMI share
        /// one implementation without version-specific compile symbols.
        /// </summary>
        public Task<PlcTreeNodeDto> GetHmiTreeAsync(string hmiHandle)
            => OnSta(() => Task.FromResult(GetHmiTreeCore(hmiHandle)));

        private PlcTreeNodeDto GetHmiTreeCore(string hmiHandle)
        {
            var hmi = ResolveHandle<object>(hmiHandle);
            if (hmi == null) return null;
            // Guard against being handed a PLC/other software handle by mistake.
            if (!(hmi is HmiTarget) && !IsUnifiedHmiSoftware(hmi)) return null;

            var name = GetMemberString(hmi, "Name");
            if (string.IsNullOrWhiteSpace(name)) name = "HMI";

            var root = new PlcTreeNodeDto
            {
                Name = name,
                DisplayText = name,
                Icon = "FolderClosed_16x.png",
                Kind = PlcTreeNodeKind.HmiRoot,
                Handle = RegisterHandle(hmi),
            };

            var screens = GetHmiScreensCollection(hmi);
            if (screens != null)
            {
                var group = new PlcTreeNodeDto
                {
                    Name = "Screens",
                    DisplayText = "Screens",
                    Icon = "FolderClosed_16x.png",
                    Kind = PlcTreeNodeKind.HmiScreenGroup,
                    Handle = RegisterHandle(screens),
                };
                foreach (var screen in EnumerateObjects(screens))
                {
                    var screenName = GetMemberString(screen, "Name");
                    if (string.IsNullOrWhiteSpace(screenName)) continue;
                    group.Children.Add(new PlcTreeNodeDto
                    {
                        Name = screenName,
                        DisplayText = screenName,
                        Icon = "Document_16x.png",
                        Kind = PlcTreeNodeKind.HmiScreen,
                        Handle = RegisterHandle(screen),
                    });
                }
                root.Children.Add(group);
            }

            // Tag tables: walk the tag folder recursively. Classic V15.1 exposes
            // HmiTarget.TagFolder (TagSystemFolder) with TagTables + Folders
            // (TagUserFolder, e.g. "SiVArc_ARG"); listing only the root TagTables
            // silently drops every table inside those user folders.
            var tagFolder = GetHmiTagFolder(hmi);
            if (tagFolder != null)
            {
                var group = new PlcTreeNodeDto
                {
                    Name = "HMI Tag Tables",
                    DisplayText = "HMI Tag Tables",
                    Icon = "FolderClosed_16x.png",
                    Kind = PlcTreeNodeKind.HmiTagTableGroup,
                    Handle = RegisterHandle(tagFolder),
                };
                BuildHmiTagTableNodes(tagFolder, group, new HashSet<int>());
                if (group.Children.Count > 0)
                    root.Children.Add(group);
            }

            return root;
        }

        private void BuildHmiTagTableNodes(object folder, PlcTreeNodeDto parent, HashSet<int> visited)
        {
            if (folder == null) return;
            if (!visited.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(folder))) return;

            foreach (var table in EnumerateObjects(TryGetMember(folder, "TagTables", "HmiTagTables", "Tables")))
            {
                var tableName = GetMemberString(table, "Name");
                if (string.IsNullOrWhiteSpace(tableName)) continue;

                // TIA shows the variable count next to the table name (e.g. "Users [4]").
                var count = CountObjects(TryGetMember(table, "Tags"));
                parent.Children.Add(new PlcTreeNodeDto
                {
                    Name = tableName,
                    DisplayText = count < 0 ? tableName : $"{tableName} [{count}]",
                    Icon = "Table_16x.png",
                    Kind = PlcTreeNodeKind.HmiTagTable,
                    Handle = RegisterHandle(table),
                });
            }

            foreach (var subFolder in EnumerateObjects(TryGetMember(folder, "Folders", "Groups")))
            {
                var folderName = GetMemberString(subFolder, "Name");
                if (string.IsNullOrWhiteSpace(folderName)) continue;

                var subNode = new PlcTreeNodeDto
                {
                    Name = folderName,
                    DisplayText = folderName,
                    Icon = "FolderClosed_16x.png",
                    Kind = PlcTreeNodeKind.HmiTagTableGroup,
                    Handle = RegisterHandle(subFolder),
                };
                BuildHmiTagTableNodes(subFolder, subNode, visited);
                parent.Children.Add(subNode);
            }
        }

        private static object GetHmiTagFolder(object hmi)
        {
            return TryGetMember(hmi,
                       "TagFolder", "TagTableFolder", "HmiTagTableFolder", "HmiTagFolder",
                       "TagTableGroup", "HmiTagTableGroup")
                   ?? hmi;
        }

        private static int CountObjects(object collection)
        {
            if (collection == null) return -1;

            var count = TryGetMember(collection, "Count");
            if (count != null && int.TryParse(count.ToString(), out var parsed)) return parsed;

            int n = 0;
            foreach (var _ in EnumerateObjects(collection)) n++;
            return n;
        }

        private static object GetHmiScreensCollection(object hmi)
        {
            var screens = TryGetMember(hmi, "Screens");
            if (screens != null) return screens;
            var folder = TryGetMember(hmi, "ScreenFolder");
            return folder == null ? null : TryGetMember(folder, "Screens");
        }

        private static object TryGetMember(object obj, params string[] names)
        {
            if (obj == null) return null;
            foreach (var name in names)
            {
                try
                {
                    var prop = obj.GetType().GetProperty(name);
                    var value = prop?.GetValue(obj);
                    if (value != null) return value;
                }
                catch { /* member missing on this version/type */ }
            }
            return null;
        }

        private static string GetMemberString(object obj, params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGetMember(obj, name);
                if (value != null) return value.ToString() ?? "";
            }
            return "";
        }

        private static IEnumerable<object> EnumerateObjects(object collection)
        {
            if (collection is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null) yield return item;
                }
            }
        }

        /// <summary>
        /// Extract the variables of an HMI tag table (by tree handle) on the STA thread,
        /// streaming batches to the UI. Mirrors the PLC tag-table contract so the same
        /// DataGrid control can render both.
        /// </summary>
        public Task<TagTableContentDto> GetHmiTagTableContentAsync(string handle, IProgress<IReadOnlyList<TagEntryDto>> progress)
            => OnSta(() => Task.FromResult(GetHmiTagTableContentCore(handle, progress)));

        private TagTableContentDto GetHmiTagTableContentCore(string handle, IProgress<IReadOnlyList<TagEntryDto>> progress)
        {
            var dto = new TagTableContentDto();
            var table = ResolveHandle<object>(handle);
            if (table == null) { dto.Error = "Handle not found."; return dto; }

            try
            {
                var name = GetMemberString(table, "Name");
                dto.Name = string.IsNullOrWhiteSpace(name) ? "HMI Tag Table" : name;

                // Classic V15.1 exposes only Name for Tag objects; the TIA variable-table
                // columns (data type / connection / PLC tag) are LinkList entries.
                // TagTable.Export yields the same SIMATIC ML the TIA UI is built on, so one
                // export reconstructs the whole table without per-tag attribute probing.
                var entries = TryReadHmiTagsFromExport(table);
                if (entries == null)
                    entries = ReadHmiTagsFromAttributes(table);
                if (entries == null)
                {
                    dto.Error = $"Tags collection not found on {table.GetType().FullName}";
                    return dto;
                }

                // PLC name lives on the HMI connection the tag links to, not on the tag.
                var connectionNames = BuildHmiConnectionNameMap(table);
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrEmpty(entry.Connection))
                        entry.PlcName = connectionNames.TryGetValue(entry.Connection, out var plcName) ? plcName : "";
                }

                dto.TagCount = entries.Count;

                const int batchSize = 200;
                for (int offset = 0; offset < entries.Count; offset += batchSize)
                {
                    progress?.Report(entries.Skip(offset).Take(batchSize).ToList());
                }
            }
            catch (Exception ex)
            {
                dto.Error = ex.Message;
            }
            return dto;
        }

        /// <summary>
        /// Export the tag table to a temporary SIMATIC ML file and read each tag's
        /// AttributeList/Name plus LinkList (DataType / Connection / ControllerTag).
        /// Returns null when the export is unavailable, so the caller falls back.
        /// </summary>
        private static List<TagEntryDto> TryReadHmiTagsFromExport(object table)
        {
            string tempFile = null;
            try
            {
                var export = table.GetType().GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) });
                if (export == null) return null;

                var dir = Path.Combine(Path.GetTempPath(), "TiaOpennessHmiTags");
                Directory.CreateDirectory(dir);
                tempFile = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".xml");

                export.Invoke(table, new object[] { new FileInfo(tempFile), ExportOptions.None });
                if (!File.Exists(tempFile)) return null;

                var doc = XDocument.Load(tempFile);
                var tags = doc.Descendants()
                    .Where(e => e.Name.LocalName == "Tag"
                                && e.Elements().Any(c => c.Name.LocalName == "AttributeList"))
                    .ToList();

                var result = new List<TagEntryDto>(tags.Count);
                foreach (var tag in tags)
                {
                    var tagName = ReadElementText(tag, "AttributeList", "Name");
                    if (string.IsNullOrWhiteSpace(tagName)) continue;

                    result.Add(new TagEntryDto
                    {
                        Name = tagName,
                        DataType = ReadFirstLinkText(tag, "DataType", "HmiDataType"),
                        Connection = ReadLinkText(tag, "Connection"),
                        PlcTag = ReadFirstLinkText(tag, "ControllerTag", "PlcTag", "PLCTag"),
                        LogicalAddress = ReadLinkText(tag, "Address"),
                        Comment = ReadAttributeComment(tag),
                    });
                }

                return result.Count > 0 ? result : null;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] HMI tag table export failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (tempFile != null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        /// <summary>Attribute-based fallback (Unified HMI exposes these as CLR properties).</summary>
        private static List<TagEntryDto> ReadHmiTagsFromAttributes(object table)
        {
            var tags = TryGetMember(table, "Tags");
            if (tags == null) return null;

            var result = new List<TagEntryDto>();
            foreach (var tag in EnumerateObjects(tags))
            {
                result.Add(new TagEntryDto
                {
                    Name = GetMemberString(tag, "Name"),
                    DataType = GetTagMemberOrAttribute(tag, "DataType", "DataTypeName"),
                    Connection = GetTagMemberOrAttribute(tag, "Connection"),
                    PlcName = GetTagMemberOrAttribute(tag, "PlcName", "PLCName", "ControllerName"),
                    PlcTag = GetTagMemberOrAttribute(tag, "PlcTag", "PLCTag", "PlcTagName"),
                    LogicalAddress = GetTagMemberOrAttribute(tag, "Address", "LogicalAddress"),
                    Comment = GetHmiTagComment(tag),
                });
            }
            return result;
        }

        private static string ReadElementText(XElement parent, string containerName, string childName)
        {
            var container = parent.Elements().FirstOrDefault(e => e.Name.LocalName == containerName);
            var child = container?.Elements().FirstOrDefault(e => e.Name.LocalName == childName);
            return NormalizeLinkValue(child?.Value);
        }

        private static string ReadLinkText(XElement tag, string linkName)
        {
            var linkList = tag.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            var link = linkList?.Elements().FirstOrDefault(e => e.Name.LocalName == linkName);
            var nameElement = link?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            return NormalizeLinkValue(nameElement?.Value);
        }

        private static string ReadFirstLinkText(XElement tag, params string[] linkNames)
        {
            foreach (var name in linkNames)
            {
                var value = ReadLinkText(tag, name);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static string NormalizeLinkValue(string value)
            => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Trim('"');

        private static string ReadAttributeComment(XElement tag)
        {
            var attributeList = tag.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            var comment = attributeList?.Elements().FirstOrDefault(e => e.Name.LocalName == "Comment");
            if (comment == null) return "";
            foreach (var text in comment.Descendants().Where(e => e.Name.LocalName == "Text"))
            {
                if (!string.IsNullOrWhiteSpace(text.Value)) return text.Value.Trim();
            }
            return "";
        }

        /// <summary>Map HMI connection name → the PLC/partner name shown in the TIA variable table.</summary>
        private static Dictionary<string, string> BuildHmiConnectionNameMap(object table)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var hmi = FindAncestorWithConnections(table);
                foreach (var connection in EnumerateObjects(TryGetMember(hmi, "Connections")))
                {
                    var connectionName = GetMemberString(connection, "Name");
                    if (string.IsNullOrWhiteSpace(connectionName)) continue;

                    var plcName = GetTagMemberOrAttribute(connection,
                        "PlcName", "PLCName", "PartnerName", "Partner", "Station", "Node", "ControllerName");
                    map[connectionName] = plcName;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] HMI connection map failed: {ex.Message}");
            }
            return map;
        }

        private static object FindAncestorWithConnections(object obj)
        {
            var current = obj;
            for (int depth = 0; depth < 6 && current != null; depth++)
            {
                var parent = TryGetMember(current, "Parent");
                if (parent == null) return null;
                if (TryGetMember(parent, "Connections") != null) return parent;
                current = parent;
            }
            return null;
        }

        /// <summary>Classic HMI tags expose Comment as a MultilingualText; take the first non-empty text.</summary>
        private static string GetHmiTagComment(object tag)
        {
            var comment = TryGetMember(tag, "Comment");
            if (comment == null) return "";
            var items = TryGetMember(comment, "Items");
            if (items != null)
            {
                foreach (var item in EnumerateObjects(items))
                {
                    var text = GetMemberString(item, "Text");
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            return "";
        }

        // Engineering attribute names per CLR type (filled on first read). Classic HMI tags
        // publish their editable properties only through IEngineeringObject.GetAttribute.
        private static readonly ConcurrentDictionary<string, HashSet<string>> _engAttributeNameCache =
            new ConcurrentDictionary<string, HashSet<string>>();

        private static string GetTagMemberOrAttribute(object tag, params string[] names)
        {
            foreach (var name in names)
            {
                // Direct CLR property first (Unified HMI exposes Connection/PlcName/PlcTag/...).
                var direct = TryGetMember(tag, name);
                if (direct != null) return direct.ToString() ?? "";

                var attribute = TryGetEngineeringAttribute(tag, name);
                if (attribute != null) return attribute.ToString() ?? "";
            }
            return "";
        }

        private static object TryGetEngineeringAttribute(object obj, string name)
        {
            if (obj == null) return null;

            var type = obj.GetType();
            var attributeNames = GetEngineeringAttributeNames(obj, type);
            if (attributeNames == null || !attributeNames.Contains(name)) return null;

            try
            {
                var engineInterface = GetEngineeringObjectInterface(type);
                var getAttribute = engineInterface?.GetMethod("GetAttribute", new[] { typeof(string) });
                return getAttribute?.Invoke(obj, new object[] { name });
            }
            catch { /* attribute missing / not readable on this version */ }
            return null;
        }

        private static System.Type GetEngineeringObjectInterface(System.Type type)
        {
            foreach (var iface in type.GetInterfaces())
            {
                if (string.Equals(iface.Name, "IEngineeringObject", StringComparison.Ordinal)) return iface;
            }
            return null;
        }

        private static HashSet<string> GetEngineeringAttributeNames(object obj, System.Type type)
        {
            var key = type.FullName ?? type.Name;
            if (_engAttributeNameCache.TryGetValue(key, out var cached)) return cached;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var engineInterface = GetEngineeringObjectInterface(type);
                var infos = engineInterface?.GetMethod("GetAttributeInfos")?.Invoke(obj, null);
                if (infos is System.Collections.IEnumerable enumerable)
                {
                    foreach (var info in enumerable)
                    {
                        var name = info?.GetType().GetProperty("Name")?.GetValue(info)?.ToString()
                                   ?? info?.ToString();
                        if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                    }
                }
            }
            catch { /* version without attribute metadata: leave empty */ }

            _engAttributeNameCache[key] = names;
            if (names.Count > 0)
                AppLogger.Log($"[Openness] HMI tag attributes ({key}): {string.Join(", ", names)}");
            return names;
        }

        /// <summary>
        /// The project's only PlcSoftware, or null when none / more than one exists.
        /// Deliberately does NOT register handles (unlike GetDeviceTreeCore, which calls ClearHandles),
        /// so an agent reading the program directory cannot invalidate handles the UI is holding.
        /// </summary>
        private PlcSoftware FindUniquePlcSoftware()
        {
            try
            {
                if (Project == null && IsConnected && TiaPortal != null && TiaPortal.Projects.Count > 0)
                    Project = TiaPortal.Projects[0];
                if (Project == null) return null;

                var found = new List<PlcSoftware>();
                foreach (Device device in Project.Devices)
                {
                    if (device?.DeviceItems == null) continue;
                    CollectPlcSoftwares(device.DeviceItems, found);
                }
                return found.Count == 1 ? found[0] : null;
            }
            catch (Exception ex)
            {
                AppLogger.Log("[Openness] FindUniquePlcSoftware failed: " + ex.Message);
                return null;
            }
        }

        private static void CollectPlcSoftwares(DeviceItemComposition items, List<PlcSoftware> found)
        {
            if (items == null) return;
            foreach (DeviceItem item in items)
            {
                try
                {
                    var container = ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
                    if (container?.Software is PlcSoftware plc && !found.Contains(plc))
                        found.Add(plc);
                }
                catch { /* a device item without software is normal */ }

                if (item.DeviceItems != null && item.DeviceItems.Count > 0)
                    CollectPlcSoftwares(item.DeviceItems, found);
            }
        }

        /// <summary>Select the active PlcSoftware by handle (sets PlcSoftware). STA-safe.</summary>
        public Task<bool> SelectPlcByHandleAsync(string plcHandle)
            => OnSta(() => Task.FromResult(SelectPlcByHandleCore(plcHandle)));

        private bool SelectPlcByHandleCore(string plcHandle)
        {
            var plc = ResolveHandle<PlcSoftware>(plcHandle);
            if (plc == null) return false;
            PlcSoftware = plc;
            return true;
        }

        // ── Block / type counts DTO ────────────────────────────────────────

        public Task<BlockCountsDto> GetBlockCountsAsync(string plcHandle = null)
            => OnSta(() => Task.FromResult(GetBlockCountsCore(plcHandle)));

        private BlockCountsDto GetBlockCountsCore(string plcHandle)
        {
            var dto = new BlockCountsDto();
            var sw = string.IsNullOrEmpty(plcHandle) ? PlcSoftware : ResolveHandle<PlcSoftware>(plcHandle);
            if (sw == null) return dto;

            int ob = 0, fb = 0, fc = 0, db = 0, type = 0;
            CountBlocksRec(sw.BlockGroup, ref ob, ref fb, ref fc, ref db, new HashSet<int>());
            CountTypesRec(sw.TypeGroup, ref type, new HashSet<int>());
            dto.Ob = ob; dto.Fb = fb; dto.Fc = fc; dto.Db = db; dto.Type = type;
            return dto;
        }

        private void CountBlocksRec(PlcBlockGroup group, ref int ob, ref int fb, ref int fc, ref int db, HashSet<int> visited)
        {
            if (group == null) return;
            if (!visited.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group))) return;
            foreach (PlcBlock block in group.Blocks)
            {
                if (block is OB) ob++;
                else if (block is FB) fb++;
                else if (block is FC) fc++;
                else if (block is GlobalDB || block is InstanceDB) db++;
            }
            foreach (PlcBlockUserGroup ug in group.Groups)
                CountBlocksRec(ug, ref ob, ref fb, ref fc, ref db, visited);
        }

        private void CountBlocksRec(PlcBlockSystemGroup group, ref int ob, ref int fb, ref int fc, ref int db, HashSet<int> visited)
        {
            if (group == null) return;
            if (!visited.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group))) return;
            foreach (PlcBlock block in group.Blocks)
            {
                if (block is OB) ob++;
                else if (block is FB) fb++;
                else if (block is FC) fc++;
                else if (block is GlobalDB || block is InstanceDB) db++;
            }
            foreach (PlcBlockUserGroup ug in group.Groups)
                CountBlocksRec(ug, ref ob, ref fb, ref fc, ref db, visited);
        }

        private void CountTypesRec(PlcTypeGroup group, ref int types, HashSet<int> visited)
        {
            if (group == null) return;
            if (!visited.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group))) return;
            types += group.Types.Count;
            foreach (PlcTypeUserGroup ug in group.Groups)
                CountTypesRec(ug, ref types, visited);
        }

        private void CountTypesRec(PlcTypeSystemGroup group, ref int types, HashSet<int> visited)
        {
            if (group == null) return;
            if (!visited.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group))) return;
            types += group.Types.Count;
            foreach (PlcTypeUserGroup ug in group.Groups)
                CountTypesRec(ug, ref types, visited);
        }

        // ── Full block/type/tag tree as DTO ────────────────────────────────

        /// <summary>
        /// Build the entire PLC tree (root → Program Blocks / PLC Data types / PLC Tags)
        /// as plain-data DTOs on the STA host thread. Each block/type/group/table is
        /// registered under a handle so the UI can act on it later without ever holding
        /// a COM object. Call GetDeviceTreeAsync/SelectPlcByHandleAsync first (or pass a
        /// plcHandle). Returns null when no PLC is selected.
        /// </summary>
        public Task<PlcTreeNodeDto> GetBlockTreeAsync(string plcHandle = null)
            => OnSta(() => Task.FromResult(GetBlockTreeCore(plcHandle)));

        private PlcTreeNodeDto GetBlockTreeCore(string plcHandle)
        {
            var sw = string.IsNullOrEmpty(plcHandle) ? PlcSoftware : ResolveHandle<PlcSoftware>(plcHandle);
            // Agent-driven runs (no UI selection yet): fall back to the project's only PLC. This does
            // not register handles or change the UI selection, so it cannot invalidate what the UI holds.
            if (sw == null && string.IsNullOrEmpty(plcHandle))
                sw = FindUniquePlcSoftware();
            if (sw == null) return null;

            var root = new PlcTreeNodeDto
            {
                Name = sw.Name,
                DisplayText = sw.Name,
                Icon = "FolderClosed_16x.png",
                Kind = PlcTreeNodeKind.Root,
                Handle = RegisterHandle(sw),
            };

            if (sw.BlockGroup != null)
            {
                var progBlocks = new PlcTreeNodeDto
                {
                    Name = "Program Blocks",
                    DisplayText = "Program Blocks",
                    Icon = "FolderClosed_16x.png",
                    Kind = PlcTreeNodeKind.BlockGroup,
                    GroupPath = "",
                    Handle = RegisterHandle(sw.BlockGroup),
                };
                BuildBlockNodes(sw.BlockGroup, progBlocks, "", new HashSet<int>());
                root.Children.Add(progBlocks);
            }

            if (sw.TypeGroup != null)
            {
                var dataTypes = new PlcTreeNodeDto
                {
                    Name = "PLC Data types",
                    DisplayText = "PLC Data types",
                    Icon = "FolderClosed_16x.png",
                    Kind = PlcTreeNodeKind.TypeGroup,
                    GroupPath = "",
                    Handle = RegisterHandle(sw.TypeGroup),
                };
                BuildTypeNodes(sw.TypeGroup, dataTypes, "", new HashSet<int>());
                root.Children.Add(dataTypes);
            }

            // PLC Tag Tables (dynamic — some SW versions expose TagTableGroup)
            try
            {
                dynamic plcDyn = sw;
                object tagTableGroup = null;
                try { tagTableGroup = plcDyn.TagTableGroup; } catch { }
                if (tagTableGroup != null)
                {
                    var tagsNode = new PlcTreeNodeDto
                    {
                        Name = "PLC Tags",
                        DisplayText = "PLC Tags",
                        Icon = "FolderClosed_16x.png",
                        Kind = PlcTreeNodeKind.TagTableGroup,
                        Handle = RegisterHandle(tagTableGroup),
                    };
                    BuildTagTableNodes(tagTableGroup, tagsNode);
                    root.Children.Add(tagsNode);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] GetBlockTree TagTableGroup failed: {ex.Message}");
            }

            return root;
        }

        private void BuildBlockNodes(PlcBlockGroup grp, PlcTreeNodeDto parent, string parentPath, HashSet<int> visited)
        {
            if (grp == null) return;
            if (!visited.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(grp))) return;

            foreach (PlcBlock b in grp.Blocks)
            {
                string icon = "Document_16x.png";
                string text = b.Name;
                string bt = "";
                if (b is OB ob)
                {
                    text += " [OB" + b.Number + "]"; icon = "OB.png"; bt = "OB";
                    try { if (ob.SecondaryType.Contains("Safe")) icon = "safeOB.png"; } catch { }
                }
                else if (b is FB fb)
                {
                    text += " [FB" + b.Number + "]"; icon = "FB.png"; bt = "FB";
                    try { if (fb.ProgrammingLanguage == ProgrammingLanguage.F_LAD || fb.ProgrammingLanguage == ProgrammingLanguage.F_FBD) icon = "safeFB.png"; } catch { }
                }
                else if (b is FC)
                {
                    text += " [FC" + b.Number + "]"; icon = "FC.png"; bt = "FC";
                }
                else if (b is GlobalDB gdb)
                {
                    text += " [DB" + b.Number + "]"; icon = "DB.png"; bt = "DB";
                    try { if (gdb.ProgrammingLanguage == ProgrammingLanguage.F_DB) icon = "safeDB.png"; } catch { }
                }
                else if (b is InstanceDB idb)
                {
                    text += " [DB" + b.Number + "]"; icon = "DB.png"; bt = "DB";
                    try { if (idb.ProgrammingLanguage == ProgrammingLanguage.F_DB) icon = "safeDB.png"; } catch { }
                }
                else if (b is ArrayDB adb)
                {
                    text += " [DB" + b.Number + "]"; icon = "DB.png"; bt = "DB";
                    try { if (adb.ProgrammingLanguage == ProgrammingLanguage.F_DB) icon = "safeDB.png"; } catch { }
                }

                string lang = "";
                try
                {
                    string rawLang = b.ProgrammingLanguage.ToString();
                    lang = rawLang == "F_LAD" ? "LAD" : rawLang == "F_FBD" ? "FBD" : rawLang;
                }
                catch { /* language is optional in the directory */ }

                parent.Children.Add(new PlcTreeNodeDto
                {
                    Name = b.Name,
                    DisplayText = text,
                    Icon = icon,
                    Kind = PlcTreeNodeKind.Block,
                    BlockType = bt,
                    Number = b.Number,
                    Language = lang,
                    GroupPath = parentPath,
                    Handle = RegisterHandle(b),
                });
            }

            foreach (PlcBlockUserGroup ug in grp.Groups)
            {
                string path = string.IsNullOrEmpty(parentPath) ? ug.Name : (parentPath + "\\" + ug.Name);
                var gNode = new PlcTreeNodeDto
                {
                    Name = ug.Name,
                    DisplayText = ug.Name,
                    Icon = "FolderClosed_16x.png",
                    Kind = PlcTreeNodeKind.BlockGroup,
                    GroupPath = path,
                    Handle = RegisterHandle(ug),
                };
                BuildBlockNodes(ug, gNode, path, visited);
                parent.Children.Add(gNode);
            }
        }

        private void BuildTypeNodes(PlcTypeGroup grp, PlcTreeNodeDto parent, string parentPath, HashSet<int> visited)
        {
            if (grp == null) return;
            if (!visited.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(grp))) return;

            foreach (PlcType ty in grp.Types)
            {
                parent.Children.Add(new PlcTreeNodeDto
                {
                    Name = ty.Name,
                    DisplayText = ty.Name,
                    Icon = "DataType.png",
                    Kind = PlcTreeNodeKind.Type,
                    BlockType = "UDT",
                    GroupPath = parentPath,
                    Handle = RegisterHandle(ty),
                });
            }

            foreach (PlcTypeUserGroup ug in grp.Groups)
            {
                string path = string.IsNullOrEmpty(parentPath) ? ug.Name : (parentPath + "\\" + ug.Name);
                var gNode = new PlcTreeNodeDto
                {
                    Name = ug.Name,
                    DisplayText = ug.Name,
                    Icon = "FolderClosed_16x.png",
                    Kind = PlcTreeNodeKind.TypeGroup,
                    GroupPath = path,
                    Handle = RegisterHandle(ug),
                };
                BuildTypeNodes(ug, gNode, path, visited);
                parent.Children.Add(gNode);
            }
        }

        // ── Target group trees (write-target chip) ─────────────────────────

        /// <summary>
        /// Build the Blocks group tree as DTOs on the STA host thread. Mirrors
        /// PlcSoftware.BlockGroup.Groups (recursive). Returns an empty list when no PLC is
        /// selected. STA-safe: the whole COM traversal runs on the STA thread, so the UI only
        /// ever sees plain-data nodes.
        /// </summary>
        public Task<List<TargetGroupNodeDto>> GetBlockGroupTreeAsync()
            => OnSta(() => Task.FromResult(GetBlockGroupTreeCore()));

        private List<TargetGroupNodeDto> GetBlockGroupTreeCore()
        {
            var result = new List<TargetGroupNodeDto>();
            if (PlcSoftware?.BlockGroup == null) return result;
            foreach (PlcBlockGroup g in PlcSoftware.BlockGroup.Groups)
                result.Add(BuildBlockGroupDto(g, ""));
            return result;
        }

        private static TargetGroupNodeDto BuildBlockGroupDto(PlcBlockGroup group, string parentPath)
        {
            string path = string.IsNullOrEmpty(parentPath) ? group.Name : (parentPath + "\\" + group.Name);
            var node = new TargetGroupNodeDto { Name = group.Name, Path = path, IsRoot = false };
            try
            {
                foreach (PlcBlockGroup child in group.Groups)
                    node.Children.Add(BuildBlockGroupDto(child, path));
            }
            catch { /* some groups throw on Groups access; skip */ }
            return node;
        }

        /// <summary>
        /// Build the Types (UDT) group tree as DTOs on the STA host thread. Mirrors
        /// PlcSoftware.TypeGroup.Groups (recursive). Returns an empty list when no PLC is selected.
        /// </summary>
        public Task<List<TargetGroupNodeDto>> GetTypeGroupTreeAsync()
            => OnSta(() => Task.FromResult(GetTypeGroupTreeCore()));

        private List<TargetGroupNodeDto> GetTypeGroupTreeCore()
        {
            var result = new List<TargetGroupNodeDto>();
            if (PlcSoftware?.TypeGroup == null) return result;
            foreach (PlcTypeUserGroup g in PlcSoftware.TypeGroup.Groups)
                result.Add(BuildTypeGroupDto(g, ""));
            return result;
        }

        private static TargetGroupNodeDto BuildTypeGroupDto(PlcTypeUserGroup group, string parentPath)
        {
            string path = string.IsNullOrEmpty(parentPath) ? group.Name : (parentPath + "\\" + group.Name);
            var node = new TargetGroupNodeDto { Name = group.Name, Path = path, IsRoot = false };
            try
            {
                foreach (PlcTypeUserGroup child in group.Groups)
                    node.Children.Add(BuildTypeGroupDto(child, path));
            }
            catch { }
            return node;
        }

        // ── Block content for the code viewer (export on STA, render on UI) ─

        /// <summary>
        /// Export a block/type (by tree handle) to XML + metadata on the STA thread.
        /// The UI then parses/renders the returned XML (no COM touched on the UI thread).
        /// </summary>
        public Task<BlockContentDto> GetBlockContentAsync(string handle)
            => OnSta(() => Task.FromResult(GetBlockContentCore(handle)));

        private BlockContentDto GetBlockContentCore(string handle)
        {
            var dto = new BlockContentDto();
            var obj = ResolveHandle<object>(handle);
            if (obj == null) { dto.Error = "Handle not found."; return dto; }

            try
            {
                if (obj is PlcBlock block)
                {
                    dto.Name = block.Name;
                    dto.Number = block.Number;
                    dto.IsType = false;

                    string rawLang = "";
                    try { dynamic d = block; rawLang = d.ProgrammingLanguage.ToString(); } catch { }
                    dto.IsSafety = rawLang == "F_LAD" || rawLang == "F_FBD" || rawLang == "F_DB";
                    dto.Language = rawLang == "F_LAD" ? "LAD" : rawLang == "F_FBD" ? "FBD" : rawLang;

                    dto.Xml = ExportBlockXmlAsync(block).GetAwaiter().GetResult();
                }
                else if (obj is PlcType type)
                {
                    dto.Name = type.Name;
                    dto.IsType = true;
                    dto.Language = "UDT";
                    dto.Xml = ExportTypeCodeAsync(type).GetAwaiter().GetResult();
                }
                else
                {
                    dto.Error = "Unsupported node for content view.";
                }
            }
            catch (Exception ex)
            {
                dto.Error = ex.Message;
            }
            return dto;
        }

        // ── Project save (run on STA thread) ───────────────────────────────

        /// <summary>
        /// Save the current project if it has unsaved changes. The IsModified check and Save call
        /// run on the STA host thread, so the UI thread never touches the Project COM object.
        /// Returns true when a save was performed.
        /// </summary>
        public Task<bool> SaveProjectAsync()
            => OnSta(() => Task.FromResult(SaveProjectCore()));

        private bool SaveProjectCore()
        {
            if (Project == null) return false;
            try
            {
                if (Project.IsModified)
                {
                    Project.Save();
                    return true;
                }
            }
            catch (ObjectDisposedException ex)
            {
                // The TIA project was closed underneath us (e.g. the user closed it directly
                // in TIA Portal). There is nothing left to save — skip instead of surfacing an error.
                AppLogger.Log($"[Openness] SaveProject skipped: project already closed ({ex.Message})");
                return false;
            }
            catch (EngineeringTargetInvocationException ex)
            {
                // Openness reports a dead/closed project access through this wrapper.
                AppLogger.Log($"[Openness] SaveProject skipped: target unavailable ({ex.Message})");
                return false;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                AppLogger.Log($"[Openness] SaveProject skipped: COM object unavailable ({ex.Message})");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] SaveProject failed: {ex.Message}");
                throw;
            }
            return false;
        }

        // ── Handle-addressed export (run on STA thread) ────────────────────

        /// <summary>
        /// Export a block/type (by tree handle) to an XML file under <paramref name="targetDir"/>.
        /// Consistency is checked and the COM Export call runs on the STA host thread, so the UI
        /// thread never touches a COM object. The file name mirrors the legacy UI naming scheme.
        /// </summary>
        public Task<ExportResultDto> ExportByHandleAsync(string handle, string targetDir)
            => OnSta(() => Task.FromResult(ExportByHandleCore(handle, targetDir)));

        private ExportResultDto ExportByHandleCore(string handle, string targetDir)
        {
            var result = new ExportResultDto();
            var obj = ResolveHandle<object>(handle);
            if (obj == null) { result.Error = "Handle not found."; return result; }

            try
            {
                if (obj is PlcBlock block)
                {
                    result.Name = block.Name;
                    if (!block.IsConsistent)
                    {
                        result.NotConsistent = true;
                        return result;
                    }

                    string fPath = System.IO.Path.Combine(targetDir,
                        block.ProgrammingLanguage.ToString() + "_" + block.Name + "_" +
                        "V" + block.HeaderVersion.ToString() + ".xml");
                    fPath = NextFreeFileName(fPath);

                    block.Export(new System.IO.FileInfo(fPath), ExportOptions.None);
                    result.Success = true;
                    result.FilePath = fPath;
                    return result;
                }

                if (obj is PlcStruct structBlock)
                {
                    result.Name = structBlock.Name;
                    if (!structBlock.IsConsistent)
                    {
                        result.NotConsistent = true;
                        return result;
                    }

                    string fPath = System.IO.Path.Combine(targetDir, "plcType_" +
                        structBlock.Name + "_" +
                        structBlock.ModifiedDate.ToString("yyyyMMdd") + ".xml");
                    fPath = NextFreeFileName(fPath);

                    structBlock.Export(new System.IO.FileInfo(fPath), ExportOptions.None);
                    result.Success = true;
                    result.FilePath = fPath;
                    return result;
                }

                if (obj is PlcType type)
                {
                    result.Name = type.Name;
                    string fPath = System.IO.Path.Combine(targetDir, "plcType_" +
                        type.Name + "_" +
                        DateTime.Now.ToString("yyyyMMdd") + ".xml");
                    fPath = NextFreeFileName(fPath);

                    type.Export(new System.IO.FileInfo(fPath), ExportOptions.None);
                    result.Success = true;
                    result.FilePath = fPath;
                    return result;
                }

                result.Error = "Please select a block or data type to export.";
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] ExportByHandle failed: {ex.Message}");
                result.Error = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Create a subgroup named <paramref name="name"/> under the block/type group identified
        /// by <paramref name="parentHandle"/>. Runs on the STA host thread. Returns AlreadyExists
        /// when a sibling group with the same name is present.
        /// </summary>
        public Task<GroupCreateResultDto> CreateGroupByHandleAsync(string parentHandle, string name)
            => OnSta(() => Task.FromResult(CreateGroupByHandleCore(parentHandle, name)));

        private GroupCreateResultDto CreateGroupByHandleCore(string parentHandle, string name)
        {
            var result = new GroupCreateResultDto();
            var parent = ResolveHandle<object>(parentHandle);
            if (parent == null) { result.Error = "Handle not found."; return result; }
            if (string.IsNullOrWhiteSpace(name)) { result.Error = "Group name is empty."; return result; }

            try
            {
                switch (parent)
                {
                    case PlcBlockSystemGroup sysGrp:
                        return CreateBlockUserGroup(sysGrp.Groups, name, result);
                    case PlcBlockGroup blkGrp:
                        return CreateBlockUserGroup(blkGrp.Groups, name, result);
                    case PlcTypeSystemGroup tSysGrp:
                        return CreateTypeUserGroup(tSysGrp.Groups, name, result);
                    case PlcTypeUserGroup tUserGrp:
                        return CreateTypeUserGroup(tUserGrp.Groups, name, result);
                    default:
                        result.Error = "Selected node is not a group.";
                        return result;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] CreateGroupByHandle failed: {ex.Message}");
                result.Error = ex.Message;
                return result;
            }
        }

        private static GroupCreateResultDto CreateBlockUserGroup(
            PlcBlockUserGroupComposition groups, string name, GroupCreateResultDto result)
        {
            foreach (PlcBlockUserGroup g in groups)
            {
                if (g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    result.AlreadyExists = true;
                    return result;
                }
            }
            groups.Create(name);
            result.Success = true;
            return result;
        }

        private static GroupCreateResultDto CreateTypeUserGroup(
            PlcTypeUserGroupComposition groups, string name, GroupCreateResultDto result)
        {
            foreach (PlcTypeUserGroup g in groups)
            {
                if (g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    result.AlreadyExists = true;
                    return result;
                }
            }
            groups.Create(name);
            result.Success = true;
            return result;
        }

        // ── Handle-addressed import (run on STA thread) ────────────────────

        /// <summary>
        /// Import a block XML file into the block group identified by <paramref name="groupHandle"/>.
        /// Runs on the STA host thread. When the block already exists and <paramref name="overwrite"/>
        /// is false, nothing is imported and AlreadyExists is returned so the UI can confirm.
        /// </summary>
        public Task<ImportResultDto> ImportBlockFromFileAsync(string groupHandle, string filePath, bool overwrite)
            => OnSta(() => Task.FromResult(ImportBlockFromFileCore(groupHandle, filePath, overwrite)));

        private ImportResultDto ImportBlockFromFileCore(string groupHandle, string filePath, bool overwrite)
        {
            var result = new ImportResultDto();
            var group = ResolveHandle<PlcBlockGroup>(groupHandle);
            if (group == null) { result.Error = "Block group handle not found."; return result; }

            try
            {
                var f = new cImportBlock(filePath);
                if (string.IsNullOrEmpty(f.BlockName))
                {
                    result.InvalidFile = true;
                    return result;
                }
                result.Name = f.BlockName;

                if (!overwrite && NameExistsCore(f.BlockName))
                {
                    result.AlreadyExists = true;
                    return result;
                }

                group.Blocks.Import(f.XmlFileInfo, overwrite ? ImportOptions.Override : ImportOptions.None);
                result.Success = true;
                RaiseBlockChanged(f.BlockName);
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] ImportBlockFromFile failed: {ex.Message}");
                result.Error = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Import a PLC data type (UDT) XML file into the current PLC's TypeGroup. Runs on the STA
        /// host thread. Returns AlreadyExists (without importing) when the type/block name already
        /// exists and overwrite is false.
        /// </summary>
        public Task<ImportResultDto> ImportTypeFromFileAsync(string filePath, bool overwrite)
            => OnSta(() => Task.FromResult(ImportTypeFromFileCore(filePath, overwrite)));

        private ImportResultDto ImportTypeFromFileCore(string filePath, bool overwrite)
        {
            var result = new ImportResultDto();
            if (PlcSoftware == null) { result.Error = "No PLC Software connected."; return result; }

            try
            {
                var xmlDoc = new System.Xml.XmlDocument();
                xmlDoc.Load(filePath);

                var dataType = xmlDoc.SelectSingleNode("//Document//SW.Types.PlcStruct");
                if (dataType == null)
                {
                    result.InvalidFile = true;
                    return result;
                }

                var nameNode = xmlDoc.SelectSingleNode("//Document//SW.Types.PlcStruct//AttributeList//Name");
                string name = nameNode?.InnerText ?? "";
                result.Name = name;

                if (!overwrite && NameExistsCore(name))
                {
                    result.AlreadyExists = true;
                    return result;
                }

                PlcSoftware.TypeGroup.Types.Import(new System.IO.FileInfo(filePath),
                    overwrite ? ImportOptions.Override : ImportOptions.None);
                result.Success = true;
                RaiseBlockChanged(name);
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] ImportTypeFromFile failed: {ex.Message}");
                result.Error = ex.Message;
                return result;
            }
        }

        // ── Full-PLC export (run on STA thread) ────────────────────────────

        /// <summary>
        /// Export every program block, PLC data type and tag table of the current PLC under
        /// <paramref name="targetDir"/>, preserving the group directory structure. Runs entirely
        /// on the STA host thread; inconsistent (uncompiled) blocks/types are skipped.
        /// </summary>
        public Task<ExportAllResultDto> ExportAllAsync(string targetDir)
            => OnSta(() => Task.FromResult(ExportAllCore(targetDir)));

        private ExportAllResultDto ExportAllCore(string targetDir)
        {
            var result = new ExportAllResultDto();
            if (PlcSoftware == null) { result.Error = "No PLC selected."; return result; }

            try
            {
                result.PlcName = PlcSoftware.Name;
                string safePlc = string.Join("_", PlcSoftware.Name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
                string rootDir = System.IO.Path.Combine(targetDir, safePlc);
                string blocksDir = System.IO.Path.Combine(rootDir, "Blocks");
                string typesDir = System.IO.Path.Combine(rootDir, "PlcDataTypes");
                string tagsDir = System.IO.Path.Combine(rootDir, "TagTables");
                System.IO.Directory.CreateDirectory(blocksDir);
                System.IO.Directory.CreateDirectory(typesDir);
                System.IO.Directory.CreateDirectory(tagsDir);
                result.OutputDir = rootDir;

                int blkExp = 0, blkSkip = 0, typeExp = 0, typeSkip = 0;
                ExportBlockGroupRecursive(PlcSoftware.BlockGroup, blocksDir, ref blkExp, ref blkSkip, result.Failures);
                if (PlcSoftware.TypeGroup != null)
                    ExportTypeGroupRecursive(PlcSoftware.TypeGroup, typesDir, ref typeExp, ref typeSkip, result.Failures);
                int tagExp = 0;
                if (PlcSoftware.TagTableGroup != null)
                    tagExp = ExportTagTableGroupRecursive(PlcSoftware.TagTableGroup, tagsDir, result.Failures);

                result.BlocksExported = blkExp;
                result.BlocksSkipped = blkSkip;
                result.TypesExported = typeExp;
                result.TypesSkipped = typeSkip;
                result.TagTablesExported = tagExp;
                result.Success = true;
                AppLogger.Log($"[ExportAll] {result.PlcName}: blocks={blkExp}/skip{blkSkip}, types={typeExp}/skip{typeSkip}, tags={tagExp}, failed={result.Failures.Count}");
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ExportAll] ERROR: {ex.Message}");
                result.Error = ex.Message;
                return result;
            }
        }

        private static string SafeFileName(string name)
            => string.Join("_", (name ?? "").Split(System.IO.Path.GetInvalidFileNameChars())).Trim();

        private void ExportBlockGroupRecursive(PlcBlockGroup group, string dir, ref int exported, ref int skipped, List<string> failures)
        {
            if (group == null) return;
            foreach (PlcBlock block in group.Blocks)
            {
                try
                {
                    if (!block.IsConsistent) { skipped++; continue; }
                    string path = System.IO.Path.Combine(dir, SafeFileName(block.Name) + ".xml");
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    block.Export(new System.IO.FileInfo(path), ExportOptions.None);
                    exported++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{block.Name}: {ex.Message}");
                }
            }
            foreach (PlcBlockUserGroup subGroup in group.Groups)
            {
                string subDir = System.IO.Path.Combine(dir, SafeFileName(subGroup.Name));
                System.IO.Directory.CreateDirectory(subDir);
                ExportBlockGroupRecursive(subGroup, subDir, ref exported, ref skipped, failures);
            }
        }

        private void ExportTypeGroupRecursive(PlcTypeGroup group, string dir, ref int exported, ref int skipped, List<string> failures)
        {
            if (group == null) return;
            foreach (PlcType type in group.Types)
            {
                try
                {
                    if (!type.IsConsistent) { skipped++; continue; }
                    string path = System.IO.Path.Combine(dir, SafeFileName(type.Name) + ".xml");
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    type.Export(new System.IO.FileInfo(path), ExportOptions.None);
                    exported++;
                }
                catch (Exception ex)
                {
                    failures.Add($"UDT {type.Name}: {ex.Message}");
                }
            }
            foreach (PlcTypeGroup subGroup in group.Groups)
            {
                string subDir = System.IO.Path.Combine(dir, SafeFileName(subGroup.Name));
                System.IO.Directory.CreateDirectory(subDir);
                ExportTypeGroupRecursive(subGroup, subDir, ref exported, ref skipped, failures);
            }
        }

        private int ExportTagTableGroupRecursive(dynamic group, string dir, List<string> failures)
        {
            if (group == null) return 0;
            int exported = 0;
            foreach (var table in group.TagTables)
            {
                string tableName = (string)(table.Name ?? "TagTable");
                try
                {
                    string path = System.IO.Path.Combine(dir, SafeFileName(tableName) + ".xml");
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    table.Export(new System.IO.FileInfo(path), ExportOptions.None);
                    exported++;
                }
                catch (Exception ex)
                {
                    failures.Add($"TagTable {tableName}: {ex.Message}");
                }
            }
            foreach (var subGroup in group.Groups)
            {
                string subName = (string)(subGroup.Name ?? "Group");
                string subDir = System.IO.Path.Combine(dir, SafeFileName(subName));
                System.IO.Directory.CreateDirectory(subDir);
                exported += ExportTagTableGroupRecursive(subGroup, subDir, failures);
            }
            return exported;
        }

        /// <summary>Whether a block/type with the given name already exists in the current PLC.</summary>
        private bool NameExistsCore(string name)
        {
            if (PlcSoftware == null || string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                if (PlcSoftware.BlockGroup.Blocks.Find(name) != null) return true;
                if (PlcSoftware.TypeGroup.Types.Find(name) != null) return true;
            }
            catch { }
            return false;
        }

        private static string NextFreeFileName(string filePath)
        {
            string dir = System.IO.Path.GetDirectoryName(filePath);
            string name = System.IO.Path.GetFileNameWithoutExtension(filePath);
            string ext = System.IO.Path.GetExtension(filePath);
            int i = 1;
            while (System.IO.File.Exists(filePath))
            {
                filePath = System.IO.Path.Combine(dir, $"{name}_{i}{ext}");
                i++;
            }
            return filePath;
        }

        // ── Handle-addressed mutations (run on STA thread) ─────────────────

        /// <summary>Delete a block/type by its tree handle. Returns true on success.</summary>
        public Task<bool> DeleteByHandleAsync(string handle)
            => OnSta(() => Task.FromResult(DeleteByHandleCore(handle)));

        private bool DeleteByHandleCore(string handle)
        {
            var obj = ResolveHandle<object>(handle);
            if (obj == null) return false;
            try
            {
                switch (obj)
                {
                    case PlcBlock b: b.Delete(); break;
                    case PlcStruct s: s.Delete(); break;
                    case PlcType t: t.Delete(); break;
                    case IEngineeringObject eo: eo.Invoke("Delete", new Dictionary<Type, object>()); break;
                    default: return false;
                }
                _handleMap.TryRemove(handle, out _);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] DeleteByHandle failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>Whether a group handle has children/blocks that block deletion (checked on STA).</summary>
        public Task<bool> GroupHasChildrenAsync(string handle)
            => OnSta(() => Task.FromResult(GroupHasChildrenCore(handle)));

        private bool GroupHasChildrenCore(string handle)
        {
            var obj = ResolveHandle<object>(handle);
            try
            {
                if (obj is PlcBlockUserGroup bg) return bg.Blocks.Count > 0 || bg.Groups.Count > 0;
                if (obj is PlcTypeUserGroup tg) return tg.Types.Count > 0 || tg.Groups.Count > 0;
            }
            catch { }
            return false;
        }

        private void BuildTagTableNodes(object tagTableGroup, PlcTreeNodeDto parent)
        {
            try
            {
                dynamic group = tagTableGroup;
                foreach (var table in group.TagTables)
                {
                    string tableName = (string)(table.Name ?? "Unknown");
                    parent.Children.Add(new PlcTreeNodeDto
                    {
                        Name = tableName,
                        DisplayText = tableName,
                        Icon = "Table_16x.png",
                        Kind = PlcTreeNodeKind.TagTable,
                        Handle = RegisterHandle((object)table),
                    });
                }
                try
                {
                    foreach (var subGroup in group.Groups)
                    {
                        string subName = (string)(subGroup.Name ?? "SubGroup");
                        var subNode = new PlcTreeNodeDto
                        {
                            Name = subName,
                            DisplayText = subName,
                            Icon = "FolderClosed_16x.png",
                            Kind = PlcTreeNodeKind.TagTableGroup,
                            Handle = RegisterHandle((object)subGroup),
                        };
                        BuildTagTableNodes(subGroup, subNode);
                        parent.Children.Add(subNode);
                    }
                }
                catch { /* no sub-groups */ }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] BuildTagTableNodes failed: {ex.Message}");
            }
        }

        // ── Tag table content for the DataGrid viewer (extract on STA, render on UI) ─

        /// <summary>
        /// Extract all tags from a tag table (by tree handle) on the STA thread, streaming
        /// batches through <paramref name="progress"/> so the UI can render progressively.
        /// Returns only metadata (name / total count / error); tag data arrives via progress.
        /// </summary>
        public Task<TagTableContentDto> GetTagTableContentAsync(string handle, IProgress<IReadOnlyList<TagEntryDto>> progress)
            => OnSta(() => Task.FromResult(GetTagTableContentCore(handle, progress)));

        private TagTableContentDto GetTagTableContentCore(string handle, IProgress<IReadOnlyList<TagEntryDto>> progress)
        {
            var dto = new TagTableContentDto();
            var obj = ResolveHandle<object>(handle);
            if (obj == null) { dto.Error = "Handle not found."; return dto; }

            try
            {
                dynamic table = obj;
                dto.Name = (string)(table.Name ?? "Tag Table");

                const int batchSize = 200;
                var batch = new List<TagEntryDto>(batchSize);

                foreach (var tag in table.Tags)
                {
                    var entry = new TagEntryDto();
                    try { entry.Name = (string)(tag.Name ?? ""); } catch { entry.Name = "?"; }
                    try { entry.DataType = (string)(tag.DataTypeName ?? ""); } catch { entry.DataType = ""; }
                    try { entry.LogicalAddress = (string)(tag.LogicalAddress ?? ""); } catch { entry.LogicalAddress = ""; }
                    try { entry.Comment = GetTagComment(tag); } catch { entry.Comment = ""; }

                    batch.Add(entry);
                    dto.TagCount++;

                    if (batch.Count == batchSize)
                    {
                        progress?.Report(batch);
                        batch = new List<TagEntryDto>(batchSize);
                    }
                }

                if (batch.Count > 0)
                    progress?.Report(batch);
            }
            catch (Exception ex)
            {
                dto.Error = ex.Message;
            }
            return dto;
        }

        private static string GetTagComment(dynamic tag)
        {
            try
            {
                dynamic comment = tag.Comment;
                if (comment == null) return "";
                foreach (var item in comment.Items)
                {
                    string text = (string)(item.Text ?? "");
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
            catch { }
            return "";
        }
    }
}
