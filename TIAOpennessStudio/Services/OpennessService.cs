using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Blocks;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    public partial class OpennessService : ITiaOpennessService
    {
        private readonly IUIService _uiService;

        public bool IsConnected => TiaPortal != null;
        public TiaPortal TiaPortal { get; private set; }
        public Project Project { get; private set; }
        public PlcSoftware PlcSoftware { get; private set; }

        public event EventHandler<bool> ConnectionStatusChanged;

        /// <summary>Raised after a block is imported/modified via Openness (block name).
        /// Consumers (cache daemon) use this to mark the index dirty and auto-refresh.</summary>
        public event EventHandler<string> BlockChanged;

        /// <summary>
        /// Raised after a successful block write-back. Carries the XML snapshot that was
        /// captured immediately before the import so callers can store it for undo.
        /// Args: blockName, blockType, snapshotXml (null when the block is brand-new).
        /// </summary>
        public event EventHandler<BlockWrittenEventArgs> BlockWritten;

        private void RaiseBlockChanged(string blockName)
        {
            try { BlockChanged?.Invoke(this, blockName ?? string.Empty); } catch { }
        }

        private void RaiseBlockWritten(string blockName, string blockType, string snapshotXml)
        {
            try { BlockWritten?.Invoke(this, new BlockWrittenEventArgs(blockName, blockType, snapshotXml)); } catch { }
        }

        public OpennessService(IUIService uiService)
        {
            _uiService = uiService;
        }

        /// <summary>
        /// Run an Openness operation on the dedicated STA host thread, re-entrancy safe.
        /// If the caller is already on the STA host thread (e.g. invoked via
        /// TiaContextHttpServer._runOnTiaSta or AgentOrchestrator.RunOnTiaSta), the work
        /// runs inline to avoid a redundant marshal / self-deadlock. Otherwise it is
        /// marshaled onto the STA host thread so the WPF UI thread stays responsive.
        /// </summary>
        private static Task<T> OnSta<T>(Func<Task<T>> work)
        {
            if (TiaStaHost.Instance.CheckAccess())
                return work();
            return TiaStaHost.Instance.RunAsync(work);
        }

        public void SetTiaPortal(TiaPortal portal)
        {
            TiaPortal = portal;
            // Notify the connection status change so MainViewModel is aware.
            ConnectionStatusChanged?.Invoke(this, IsConnected);
        }
        public void SetProject(Project project) => Project = project;
        public void SetPlcSoftware(PlcSoftware software) => PlcSoftware = software;

        public Task<bool> ConnectAsync(string version = null)
        {
            // Marshal to the dedicated TIA STA thread so the COM objects (TiaPortal,
            // PlcSoftware) are created and used on the same STA thread throughout their
            // entire lifetime.  Creating them on the WPF UI thread and then calling them
            // from other threads triggers "Cross-thread operation is not valid in Openness
            // within STA" because Openness disables COM apartment-proxy marshaling.
            return TiaStaHost.Instance.RunAsync(() => ConnectToTiaPortalCore(allowLaunch: true));
        }

        public Task<bool> AttachToRunningTiaPortalAsync()
        {
            return TiaStaHost.Instance.RunAsync(() => ConnectToTiaPortalCore(allowLaunch: false));
        }

        public bool ConnectToTiaPortal()
        {
            // Force onto the STA host thread so the TiaPortal COM object is created there,
            // regardless of which thread the caller is on. Blocking-wait is acceptable here
            // because these synchronous entry points are legacy callers; the async variants
            // (ConnectAsync) are preferred. If already on the STA thread, runs inline.
            return RunOnStaBlocking(() => ConnectToTiaPortalCore(allowLaunch: true));
        }

        public bool AttachToRunningTiaPortal()
        {
            return RunOnStaBlocking(() => ConnectToTiaPortalCore(allowLaunch: false));
        }

        /// <summary>
        /// Run a synchronous Openness function on the STA host thread. If already on that
        /// thread, runs inline (no deadlock). Otherwise marshals and blocks for the result.
        /// </summary>
        private static T RunOnStaBlocking<T>(Func<T> func)
        {
            if (TiaStaHost.Instance.CheckAccess())
                return func();
            return TiaStaHost.Instance.RunAsync(func).GetAwaiter().GetResult();
        }

        private bool ConnectToTiaPortalCore(bool allowLaunch)
        {
            try
            {
                if (TiaPortal != null) return true;
                AppLogger.Log("[Openness] Connecting to TIA Portal... allowLaunch=" + allowLaunch);
                IList<TiaPortalProcess> processes = Siemens.Engineering.TiaPortal.GetProcesses();
                AppLogger.Log("[Openness] TIA Portal process count: " + processes.Count);
                if (processes.Count > 0)
                {
                    Siemens.Engineering.TiaPortal firstAttachable = null;
                    foreach (TiaPortalProcess proc in processes)
                    {
                        Siemens.Engineering.TiaPortal candidate = null;
                        try
                        {
                            AppLogger.Log("[Openness] Trying attach to PID=" + proc.Id + "...");
                            candidate = AttachWithTimeout(proc, 30000);
                            if (candidate == null) { AppLogger.Log("[Openness] Attach timed out for PID=" + proc.Id); continue; }
                            AppLogger.Log("[Openness] Attach succeeded for PID=" + proc.Id);
                            if (firstAttachable == null) firstAttachable = candidate;
                            bool hasProject = false;
                            try { hasProject = candidate.Projects.Count > 0; } catch (Exception ex) { AppLogger.Log($"[Openness] Project count check failed for PID={proc.Id}: {ex.Message}"); }
                            AppLogger.Log("[Openness] PID=" + proc.Id + ": hasProject=" + hasProject);
                            if (hasProject)
                            {
                                TiaPortal = candidate;
                                AppLogger.Log("[Openness] Selected PID=" + proc.Id);
                                ConnectionStatusChanged?.Invoke(this, IsConnected);
                                return true;
                            }
                        }
                        catch (Exception attachEx)
                        {
                            AppLogger.Log("[Openness] Attach to PID=" + proc.Id + " failed: " + attachEx.Message);
                            if (candidate != null && candidate != firstAttachable) try { candidate.Dispose(); } catch { }
                        }
                    }
                    if (firstAttachable != null)
                    {
                        TiaPortal = firstAttachable;
                        AppLogger.Log("[Openness] Fallback: using first attachable instance");
                        ConnectionStatusChanged?.Invoke(this, IsConnected);
                        return true;
                    }
                }
                if (!allowLaunch)
                {
                    AppLogger.Log("[Openness] No running TIA Portal found. AttachOnly mode will not start a new instance.");
                    return false;
                }

                AppLogger.Log("[Openness] No running TIA Portal found, starting new instance...");
                TiaPortal = new Siemens.Engineering.TiaPortal(Siemens.Engineering.TiaPortalMode.WithUserInterface);
                ConnectionStatusChanged?.Invoke(this, IsConnected);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log("[Openness] ConnectToTiaPortal failed: " + ex.Message);
                if (allowLaunch)
                {
                    _uiService.ShowNotification("Error", "Error connecting to TIA Portal: " + ex.Message, NotificationType.Error);
                }
                return false;
            }
        }

        private Siemens.Engineering.TiaPortal AttachWithTimeout(TiaPortalProcess proc, int timeoutMs)
        {
            // Phase 1 (probe): attach on a throwaway STA thread purely to verify the target
            // instance is responsive (a modal dialog in TIA blocks Attach indefinitely).
            // The probe object is DISCARDED: Openness COM objects are thread-affine, so an
            // object created on the probe thread cannot be used from the TiaStaHost thread
            // (previously the probe result was returned directly, and every later access
            // threw "Cross-thread operation is not valid in Openness within STA").
            Exception error = null;
            var probe = new System.Threading.Thread(() =>
            {
                try { proc.Attach(); } // result intentionally discarded (see above)
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            probe.SetApartmentState(System.Threading.ApartmentState.STA);
            probe.Start();
            if (!probe.Join(timeoutMs))
            {
                AppLogger.Log("[Openness] Attach to PID=" + proc.Id + " exceeded " + timeoutMs + "ms, skipping");
                return null;
            }
            if (error != null) throw error;

            // Phase 2: probe succeeded => the instance is responsive, so this attach is
            // sub-second and needs no timeout. Run it directly on THIS thread (TiaStaHost's
            // STA thread) so the returned object is affine to the thread that will use it.
            return proc.Attach();
        }

        public Task<List<DeviceItem>> GetProjectTreeAsync()
            // Marshal the COM traversal onto the STA host thread. Callers that are already on
            // the STA thread run inline (OnSta short-circuits via CheckAccess). NOTE: the returned
            // DeviceItem objects are COM-affine — reading their members off the STA thread still
            // throws. Prefer GetProjectTreeNamesAsync for cross-thread callers (agent tools, HTTP).
            => OnSta(() => Task.FromResult(GetProjectTreeCore()));

        private List<DeviceItem> GetProjectTreeCore()
        {
            if (Project == null && IsConnected)
            {
                if (TiaPortal.Projects.Count > 0) Project = TiaPortal.Projects[0];
            }

            var list = new List<DeviceItem>();
            if (Project == null) return list;

            foreach (Device device in Project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    list.Add(item);
                }
            }
            return list;
        }

        /// <summary>
        /// STA-safe device-name list. The COM traversal and the Name extraction both run on the
        /// STA host thread, so the returned plain strings can be consumed from any thread
        /// (agent tools, HTTP handlers) without a cross-thread COM access.
        /// </summary>
        public Task<List<string>> GetProjectTreeNamesAsync()
            => OnSta(() => Task.FromResult(GetProjectTreeCore().Select(di => di.Name).ToList()));

        public Task<string> AddDeviceAsync(string typeIdentifier, string deviceName)
            => OnSta(() => Task.FromResult(AddDeviceCore(typeIdentifier, deviceName)));

        private string AddDeviceCore(string typeIdentifier, string deviceName)
        {
            if (string.IsNullOrWhiteSpace(typeIdentifier))
                throw new ArgumentException("typeIdentifier is required (e.g. OrderNumber:...).", nameof(typeIdentifier));
            if (string.IsNullOrWhiteSpace(deviceName))
                throw new ArgumentException("deviceName is required.", nameof(deviceName));

            if (Project == null && IsConnected && TiaPortal.Projects.Count > 0)
                Project = TiaPortal.Projects[0];
            if (Project == null)
                throw new InvalidOperationException("No open TIA project.");

            var device = Project.Devices.CreateWithItem(typeIdentifier, deviceName, deviceName);
            return $"Device '{deviceName}' created (TypeIdentifier {typeIdentifier}).";
        }

        public Task<string> SetDeviceItemAttributeAsync(string deviceItemName, string attributeName, string value)
            => OnSta(() => Task.FromResult(SetDeviceItemAttributeCore(deviceItemName, attributeName, value)));

        private string SetDeviceItemAttributeCore(string deviceItemName, string attributeName, string value)
        {
            var di = FindDeviceItemByName(deviceItemName)
                ?? throw new InvalidOperationException($"Device item '{deviceItemName}' not found.");
            var info = di.GetAttributeInfos()
                .FirstOrDefault(x => string.Equals(x.Name, attributeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Attribute '{attributeName}' not found on '{deviceItemName}'.");

            object current = null;
            try { current = di.GetAttribute(info.Name); } catch { }
            object typed = CoerceAttributeValue(value, current);

            try { di.SetAttribute(info.Name, typed); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SetAttribute('{attributeName}') on '{deviceItemName}' failed: {ex.Message}");
            }
            return $"Set '{attributeName}' = '{value}' on device item '{deviceItemName}'.";
        }

        private static object CoerceAttributeValue(string text, object current)
        {
            if (current == null) return text;
            switch (current)
            {
                case bool: if (bool.TryParse(text, out var b)) return b; break;
                case int: if (int.TryParse(text, out var i32)) return i32; break;
                case long: if (long.TryParse(text, out var i64)) return i64; break;
                case short: if (short.TryParse(text, out var i16)) return i16; break;
                case byte: if (byte.TryParse(text, out var u8)) return u8; break;
                case double: if (double.TryParse(text, out var f64)) return f64; break;
                case float: if (float.TryParse(text, out var f32)) return f32; break;
                default:
                    var t = current.GetType();
                    if (t.IsEnum) return CoerceEnumValue(t, text);
                    break;
            }
            return text; // fallback: keep raw string
        }

        private static object CoerceEnumValue(Type enumType, string text)
        {
            var names = System.Enum.GetNames(enumType);
            foreach (var n in names)
                if (string.Equals(n, text, StringComparison.OrdinalIgnoreCase))
                    return System.Enum.Parse(enumType, n);
            foreach (var n in names)
                if (n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    return System.Enum.Parse(enumType, n);
            throw new InvalidOperationException($"'{text}' is not a valid {enumType.Name} value ({string.Join(", ", names)}).");
        }

        public Task<string> AddModuleAsync(string parentDeviceItemName, string moduleName, string orderNumber)
            => OnSta(() => Task.FromResult(AddModuleCore(parentDeviceItemName, moduleName, orderNumber)));

        private string AddModuleCore(string parentDeviceItemName, string moduleName, string orderNumber)
        {
            var parent = FindDeviceItemByName(parentDeviceItemName)
                ?? throw new InvalidOperationException($"Parent device item '{parentDeviceItemName}' not found.");
            CreateModuleReflective(parent.DeviceItems, moduleName, "OrderNumber:" + orderNumber, orderNumber);
            return $"Module '{moduleName}' added under '{parentDeviceItemName}'.";
        }

        private static object CreateModuleReflective(object itemsComposition, string name, string typeIdentifier, string orderNumber)
        {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            var creates = itemsComposition.GetType().GetMethods(flags).Where(m => m.Name == "Create").ToList();
            var create3 = creates.FirstOrDefault(m => m.GetParameters().Length == 3 && m.GetParameters().All(p => p.ParameterType == typeof(string)));
            if (create3 != null) return create3.Invoke(itemsComposition, new object[] { name, typeIdentifier, orderNumber });
            var create2 = creates.FirstOrDefault(m => m.GetParameters().Length == 2 && m.GetParameters().All(p => p.ParameterType == typeof(string)));
            if (create2 != null) return create2.Invoke(itemsComposition, new object[] { name, typeIdentifier });
            throw new InvalidOperationException("No string-typed DeviceItems.Create overload found. Available: " + string.Join(" | ", creates.Select(m => m.ToString())));
        }

        public Task<string> ConfigureSubnetAsync(string deviceItemName, string subnetName, string subnetType)
            => OnSta(() => Task.FromResult(ConfigureSubnetCore(deviceItemName, subnetName, subnetType)));

        private string ConfigureSubnetCore(string deviceItemName, string subnetName, string subnetType)
        {
            if (Project == null && IsConnected && TiaPortal.Projects.Count > 0) Project = TiaPortal.Projects[0];
            if (Project == null) throw new InvalidOperationException("No open TIA project.");
            var di = FindDeviceItemByName(deviceItemName)
                ?? throw new InvalidOperationException($"Device item '{deviceItemName}' not found.");

            var subnet = CreateSubnetReflective(Project, subnetType, subnetName);
            var connected = ConnectToSubnetReflective(di, subnet);
            var via = !string.Equals(connected.Name, deviceItemName, StringComparison.OrdinalIgnoreCase)
                ? $" (via interface '{connected.Name}')" : "";
            return $"Device item '{deviceItemName}' connected to subnet '{subnetName}' ({subnetType}){via}.";
        }

        private DeviceItem FindDeviceItemByName(string name)
        {
            if (Project == null) return null;
            foreach (Device device in Project.Devices)
            {
                foreach (DeviceItem root in device.DeviceItems)
                {
                    var found = FindDeviceItemRecursive(root, name);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static DeviceItem FindDeviceItemRecursive(DeviceItem item, string name)
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) return item;
            if (item.DeviceItems != null)
            {
                foreach (DeviceItem child in item.DeviceItems)
                {
                    var r = FindDeviceItemRecursive(child, name);
                    if (r != null) return r;
                }
            }
            return null;
        }

        private static object CreateSubnetReflective(Project project, string subnetType, string subnetName)
        {
            var subnets = project.GetType().GetProperty("Subnets")?.GetValue(project)
                ?? throw new InvalidOperationException("Project.Subnets not found.");
            var create = subnets.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string));
            if (create == null) throw new InvalidOperationException("Subnets.Create(type, name) overload not found.");

            var typeParam = create.GetParameters()[0].ParameterType;
            object typeValue = typeParam.IsEnum ? CoerceEnumValue(typeParam, subnetType) : subnetType;
            return create.Invoke(subnets, new[] { typeValue, subnetName });
        }

        private static DeviceItem ConnectToSubnetReflective(DeviceItem item, object subnet)
        {
            // Prefer the item itself when it is already a network interface / station exposing ConnectToSubnet.
            var direct = FindConnectToSubnet(item);
            if (direct != null) { direct.Invoke(item, new[] { subnet }); return item; }

            // Otherwise descend into child device items to find the PN/DP interface and connect that.
            var iface = FindConnectableDescendant(item);
            if (iface == null)
                throw new InvalidOperationException($"No device item under '{item.Name}' exposes ConnectToSubnet; connect a network interface instead.");

            FindConnectToSubnet(iface)!.Invoke(iface, new[] { subnet });
            return iface;
        }

        private static System.Reflection.MethodInfo FindConnectToSubnet(DeviceItem item)
        {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            return item.GetType().GetMethods(flags)
                .FirstOrDefault(x => x.Name == "ConnectToSubnet" && x.GetParameters().Length == 1);
        }

        private static DeviceItem FindConnectableDescendant(DeviceItem item)
        {
            if (item.DeviceItems == null) return null;
            foreach (DeviceItem child in item.DeviceItems)
            {
                if (FindConnectToSubnet(child) != null) return child;
                var deeper = FindConnectableDescendant(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        public async Task<string> ExportBlockXmlAsync(PlcBlock block)
        {
            // TIA Openness requires the STA thread; Task.Run must not be used.
            await Task.CompletedTask;
            if (block == null) return null;

            // Guard: auto-generated blocks (e.g. safety program F-OB, system OB) are often in an
            // inconsistent / uncompiled or know-how-protected state. Calling Export() on such a
            // block would repeatedly block/retry inside the STA COM context (manifesting as many
            // FunctionFromSTA threads created and torn down), eventually wedging the whole STA
            // thread. TIA Portal never exports inconsistent blocks, so intercept here.
            if (!IsBlockExportable(block, out string skipReason))
            {
                AppLogger.Log($"[Openness] Skip export for block '{block.Name}': {skipReason}");
                throw new InvalidOperationException(
                    $"Block '{block.Name}' cannot be exported: {skipReason}");
            }

            string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".xml");
            block.Export(new FileInfo(tempPath), ExportOptions.None);
            string content = File.ReadAllText(tempPath);
            try { File.Delete(tempPath); }
            catch (Exception delEx) { AppLogger.Log($"[Openness] Delete temp file failed: {delEx.Message}"); }
            return content;
        }

        /// <summary>
        /// Checks whether a block can be safely exported. Returns false with a reason when it
        /// cannot (inconsistent / know-how protected), to avoid calling Export() on an
        /// unexportable block and blocking the STA thread. Property reads are wrapped in
        /// try/catch to tolerate different TIA Openness API revisions.
        /// </summary>
        private static bool IsBlockExportable(PlcBlock block, out string reason)
        {
            reason = null;

            // 1) Consistency check — TIA Portal does not export inconsistent (uncompiled) blocks
            try
            {
                if (!block.IsConsistent)
                {
                    reason = "block is inconsistent (needs compile); TIA Portal does not export inconsistent blocks";
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Being unable to read consistency is itself a sign this is an abnormal/generated block; skip conservatively.
                reason = $"unable to read consistency state ({ex.Message})";
                return false;
            }

            // 2) Know-how protection check — protected blocks are not allowed to export their content
            try
            {
                dynamic dyn = block;
                bool isProtected = dyn.IsKnowHowProtected;
                if (isProtected)
                {
                    reason = "block is know-how protected";
                    return false;
                }
            }
            catch
            {
                // This API version doesn't expose IsKnowHowProtected; ignore this check.
            }

            return true;
        }

        public async Task<bool> ImportSclAsync(PlcBlock target, string sclCode)
        {
            if (target == null || string.IsNullOrWhiteSpace(sclCode))
            {
                AppLogger.Log("[Openness] ImportSclAsync: target or sclCode is null/empty");
                return false;
            }
            if (PlcSoftware == null)
            {
                AppLogger.Log("[Openness] ImportSclAsync: no PLC Software connected");
                return false;
            }

            try
            {
                // Delegate to ImportBlockCodeAsync which handles SCL external source generation
                // and the full snapshot/rollback/compile pipeline.
                // Empty blockType triggers the SCL-source path (non-XML, non-UDT).
                await ImportBlockCodeAsync(target.Name, "", sclCode).ConfigureAwait(false);
                AppLogger.Log($"[Openness] ImportSclAsync succeeded for '{target.Name}'");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] ImportSclAsync failed for '{target.Name}': {ex.Message}");
                return false;
            }
        }

        public Task<string> ExportBlockCodeAsync(string blockName)
            => OnSta(() => ExportBlockCodeCoreAsync(blockName));

        /// <inheritdoc/>
        public Task<string> GetBlockGroupPathAsync(string blockName)
            => OnSta(() => Task.FromResult(GetBlockGroupPathCore(blockName)));

        private string GetBlockGroupPathCore(string blockName)
        {
            if (PlcSoftware == null || string.IsNullOrWhiteSpace(blockName)) return null;
            try
            {
                return FindBlockGroupPath(PlcSoftware.BlockGroup, blockName.Trim(), "");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] GetBlockGroupPath failed for '{blockName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Recursive group-path search. Returns "" for the root group, null when absent.</summary>
        private static string FindBlockGroupPath(PlcBlockGroup group, string name, string path)
        {
            if (group == null) return null;
            try
            {
                var block = group.Blocks.Find(name);
                if (block != null) return path;
                foreach (PlcBlockUserGroup child in group.Groups)
                {
                    var childPath = string.IsNullOrEmpty(path) ? child.Name : path + "\\" + child.Name;
                    var found = FindBlockGroupPath(child, name, childPath);
                    if (found != null) return found;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] FindBlockGroupPath error in group: {ex.Message}");
            }
            return null;
        }

        /// <inheritdoc/>
        public Task<string> GetTypeGroupPathAsync(string typeName)
            => OnSta(() => Task.FromResult(GetTypeGroupPathCore(typeName)));

        private string GetTypeGroupPathCore(string typeName)
        {
            if (PlcSoftware == null || string.IsNullOrWhiteSpace(typeName)) return null;
            try
            {
                return FindTypeGroupPath(PlcSoftware.TypeGroup, typeName.Trim(), "");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] GetTypeGroupPath failed for '{typeName}': {ex.Message}");
                return null;
            }
        }

        private static string FindTypeGroupPath(PlcTypeGroup group, string name, string path)
        {
            if (group == null) return null;
            try
            {
                var type = group.Types.Find(name);
                if (type != null) return path;
                foreach (PlcTypeUserGroup child in group.Groups)
                {
                    var childPath = string.IsNullOrEmpty(path) ? child.Name : path + "\\" + child.Name;
                    var found = FindTypeGroupPath(child, name, childPath);
                    if (found != null) return found;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] FindTypeGroupPath error in group: {ex.Message}");
            }
            return null;
        }

        private async Task<string> ExportBlockCodeCoreAsync(string blockName)
        {
            // Runs on the STA host thread (guaranteed by OnSta wrapper).
            if (PlcSoftware == null)
                throw new InvalidOperationException("No PLC Software connected. Please open a project first.");

            // Search recursively: Blocks.Find only looks at the root group, so blocks living in
            // user sub-groups were reported as "not found" to the agent tools.
            var block = FindBlockRecursive(PlcSoftware.BlockGroup, blockName);
            if (block != null)
            {
                return await ExportBlockXmlAsync(block);
            }

            var type = PlcSoftware.TypeGroup.Types.Find(blockName);
            if (type == null)
                type = FindTypeRecursive(PlcSoftware.TypeGroup, blockName);
            if (type != null)
            {
                return await ExportTypeCodeAsync(type);
            }

            throw new InvalidOperationException($"Block '{blockName}' not found in project.");
        }

        public async Task<string> ExportTypeCodeAsync(PlcType type)
        {
            // TIA Openness requires the STA thread; Task.Run must not be used.
            await Task.CompletedTask;
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            // Consistent with block export: TIA won't export an inconsistent type, so skip to avoid blocking STA.
            try
            {
                if (!type.IsConsistent)
                {
                    AppLogger.Log($"[Openness] Skip export for type '{type.Name}': inconsistent");
                    throw new InvalidOperationException(
                        $"Type '{type.Name}' cannot be exported: type is inconsistent (needs compile)");
                }
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] Skip export for type '{type.Name}': unable to read consistency ({ex.Message})");
                throw new InvalidOperationException(
                    $"Type '{type.Name}' cannot be exported: unable to read consistency state ({ex.Message})");
            }

            string typeTempFile = Path.Combine(Path.GetTempPath(), MakeSafeFileName(type.Name) + ".udt");
            if (File.Exists(typeTempFile)) File.Delete(typeTempFile);
            type.Export(new FileInfo(typeTempFile), ExportOptions.None);
            string typeContent = File.ReadAllText(typeTempFile);
            try { File.Delete(typeTempFile); }
            catch (Exception delEx) { AppLogger.Log($"[Openness] Delete temp file failed: {delEx.Message}"); }
            return typeContent;
        }

        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "udt";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        public async Task<string> FindVariableTypeAsync(string variable, string block)
        {
            // TIA Openness requires the STA thread; the caller marshals, this only awaits as a placeholder.
            await Task.CompletedTask;
            if (string.IsNullOrWhiteSpace(variable)) return null;
            if (PlcSoftware == null) return null;

            // 1) Recursively search in TagTableGroup (including subgroups)
            try
            {
                if (PlcSoftware.TagTableGroup != null)
                {
                    string fromTags = SearchTagInTagTableGroup(PlcSoftware.TagTableGroup, variable);
                    if (!string.IsNullOrEmpty(fromTags)) return fromTags;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OpennessService] FindVariableTypeAsync TagTable search failed: {ex.Message}");
            }

            // 2) Search the specified block's interface (via exported XML Member nodes)
            if (!string.IsNullOrWhiteSpace(block))
            {
                try
                {
                    var plcBlock = PlcSoftware.BlockGroup.Blocks.Find(block);
                    if (plcBlock != null)
                    {
                        string xml = await ExportBlockXmlAsync(plcBlock).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(xml))
                        {
                            string fromInterface = ExtractMemberTypeFromBlockXml(xml, variable);
                            if (!string.IsNullOrEmpty(fromInterface)) return fromInterface;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[OpennessService] FindVariableTypeAsync interface search failed: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Recursively walks TagTableSystemGroup (and its subgroups) looking for a matching Tag.
        /// Returns the DataTypeName or null.
        /// </summary>
        private static string SearchTagInTagTableGroup(object tagTableRoot, string variable)
        {
            // dynamic accommodates TagTableSystemGroup / PlcTagTableUserGroup (both expose .TagTables and .Groups)
            try
            {
                dynamic group = tagTableRoot;
                if (group == null) return null;

                foreach (var table in group.TagTables)
                {
                    foreach (var tag in table.Tags)
                    {
                        string name = tag.Name as string;
                        if (!string.IsNullOrEmpty(name) &&
                            string.Equals(name, variable, StringComparison.OrdinalIgnoreCase))
                        {
                            return tag.DataTypeName as string;
                        }
                    }
                }

                foreach (var sub in group.Groups)
                {
                    string found = SearchTagInTagTableGroup(sub, variable);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            catch
            {
                // the node lacks an expected member; skip
            }
            return null;
        }

        /// <summary>
        /// Finds the Member node with the matching name in the exported PlcBlock XML and
        /// returns its Datatype attribute.
        /// </summary>
        private static string ExtractMemberTypeFromBlockXml(string xml, string variable)
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                if (doc.Root == null) return null;
                var members = doc.Root.Descendants()
                    .Where(e => e.Name.LocalName == "Member");
                foreach (var m in members)
                {
                    string name = m.Attribute("Name")?.Value;
                    if (!string.IsNullOrEmpty(name) &&
                        string.Equals(name, variable, StringComparison.OrdinalIgnoreCase))
                    {
                        return m.Attribute("Datatype")?.Value;
                    }
                }
            }
            catch
            {
                // XML parse failed -> treated as not found
            }
            return null;
        }

        /// <inheritdoc/>
        public async Task<IList<string>> ListTagTablesAsync(int limit = 500)
        {
            await Task.CompletedTask;
            var result = new List<string>();
            if (PlcSoftware == null) return result;
            try
            {
                CollectTagsFromGroup(PlcSoftware.TagTableGroup, result, limit);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OpennessService] ListTagTablesAsync failed: {ex.Message}");
            }
            return result;
        }

        private static void CollectTagsFromGroup(object tagTableRoot, IList<string> result, int limit)
        {
            try
            {
                dynamic group = tagTableRoot;
                if (group == null) return;

                foreach (var table in group.TagTables)
                {
                    string tableName = (table.Name as string) ?? "?";
                    foreach (var tag in table.Tags)
                    {
                        if (result.Count >= limit) return;
                        string name = (tag.Name as string) ?? "";
                        string dtype = (tag.DataTypeName as string) ?? "";
                        string addr = "";
                        try { addr = (tag.LogicalAddress as string) ?? ""; }
                        catch { /* LogicalAddress may not be accessible for some tag types */ }
                        result.Add($"{tableName}.{name}: {dtype}" + (string.IsNullOrEmpty(addr) ? "" : $" @ {addr}"));
                    }
                }

                foreach (var sub in group.Groups)
                {
                    if (result.Count >= limit) return;
                    CollectTagsFromGroup(sub, result, limit);
                }
            }
            catch
            {
                // the node lacks an expected member; skip
            }
        }

        /// <inheritdoc/>
        public async Task<IList<string>> GetDbStructureAsync(string dbName)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(dbName) || PlcSoftware == null) return result;
            try
            {
                // First look for the DB block in BlockGroup
                var block = FindBlockRecursive(PlcSoftware.BlockGroup, dbName);
                if (block == null)
                {
                    AppLogger.Log($"[OpennessService] GetDbStructureAsync: DB '{dbName}' not found.");
                    return result;
                }

                string xml = await ExportBlockXmlAsync(block).ConfigureAwait(false);
                if (string.IsNullOrEmpty(xml)) return result;

                // Parse the Interface/Sections/Section/Member nodes
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                var members = doc.Root?.Descendants()
                    .Where(e => e.Name.LocalName == "Member") ?? Enumerable.Empty<System.Xml.Linq.XElement>();

                foreach (var m in members)
                {
                    string name = m.Attribute("Name")?.Value ?? "";
                    string dtype = m.Attribute("Datatype")?.Value ?? "";
                    // Skip top-level VAR_* section members (they have no meaningful Datatype)
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(dtype))
                        result.Add($"{name}: {dtype}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OpennessService] GetDbStructureAsync failed for '{dbName}': {ex.Message}");
            }
            return result;
        }

        private static dynamic FindTypeRecursive(dynamic group, string name)
        {
            if (group == null) return null;
            try
            {
                var t = group.Types.Find(name);
                if (t != null) return t;
                foreach (var g in group.Groups)
                {
                    var found = FindTypeRecursive(g, name);
                    if (found != null) return found;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] FindTypeRecursive error in group: {ex.Message}");
            }
            return null;
        }

        private static dynamic FindBlockRecursive(dynamic group, string name)
        {
            if (group == null) return null;
            try
            {
                var b = group.Blocks.Find(name);
                if (b != null) return b;
                foreach (var g in group.Groups)
                {
                    var found = FindBlockRecursive(g, name);
                    if (found != null) return found;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] FindBlockRecursive error in group: {ex.Message}");
            }
            return null;
        }

        /// <inheritdoc/>
        public async Task<IList<string>> ListUdtTypesAsync(int limit = 200)
        {
            await Task.CompletedTask;
            var result = new List<string>();
            if (PlcSoftware == null) return result;
            try
            {
                CollectUdtFromGroup(PlcSoftware.TypeGroup, result, limit);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OpennessService] ListUdtTypesAsync failed: {ex.Message}");
            }
            return result;
        }

        private static void CollectUdtFromGroup(object typeGroup, IList<string> result, int limit)
        {
            try
            {
                dynamic group = typeGroup;
                if (group == null) return;

                foreach (var t in group.Types)
                {
                    if (result.Count >= limit) return;
                    string name = (t.Name as string) ?? "?";
                    // Exporting type XML to parse members is heavy; list name + member count only (lightweight).
                    // The Agent can then use get_db_structure to drill into a specific UDT's structure.
                    int memberCount = 0;
                    try
                    {
                        dynamic iface = t.Interface;
                        if (iface != null)
                        {
                            foreach (var s in iface.Sections)
                            {
                                foreach (var m in s.Members) memberCount++;
                            }
                        }
                    }
                    catch { /* Interface access may fail for some types */ }
                    result.Add(memberCount > 0 ? $"{name} ({memberCount} members)" : name);
                }

                foreach (var sub in group.Groups)
                {
                    if (result.Count >= limit) return;
                    CollectUdtFromGroup(sub, result, limit);
                }
            }
            catch
            {
                // the node lacks an expected member; skip
            }
        }

        public Task<string> ImportBlockCodeAsync(string blockName, string blockType, string code)
        {
            // Back-compat old call: equivalent to importing to the root group
            return ImportBlockCodeAsync(blockName, blockType, code, "");
        }

        public Task<string> ImportBlockCodeAsync(string blockName, string blockType, string code, string groupPath)
            => OnSta(() => ImportBlockCodeCoreAsync(blockName, blockType, code, groupPath));

        /// <summary>Resolve the culture for emitted MultilingualText: the first actually-installed project language, fallback zh-CN.</summary>
        public Task<string> ResolveProjectCultureAsync()
            => OnSta(() => Task.FromResult(ResolveProjectCultureCore()));

        private string ResolveProjectCultureCore()
        {
            try
            {
                var ls = Project?.LanguageSettings;
                if (ls == null) return "zh-CN";

                // EditingLanguage is always a real project language (unlike `.Languages`, which is the
                // full install-wide catalog and yields junk like "af-ZA" that the project does not have).
                var tag = ls.EditingLanguage?.Culture?.Name;
                if (!string.IsNullOrWhiteSpace(tag)) return tag;

                // Fall back to the first active multilingual language of the project.
                foreach (var lang in ls.ActiveLanguages)
                {
                    tag = lang?.Culture?.Name;
                    if (!string.IsNullOrWhiteSpace(tag)) return tag;
                }
            }
            catch { /* fall through to the default culture */ }
            return "zh-CN";
        }

        private async Task<string> ImportBlockCodeCoreAsync(string blockName, string blockType, string code, string groupPath)
        {
            // Runs on the STA host thread (guaranteed by OnSta wrapper).
            await Task.CompletedTask;
            if (PlcSoftware == null)
                throw new InvalidOperationException("No PLC Software connected.");

            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("Import code is empty.");

            bool isUdt = string.Equals(blockType, "UDT", StringComparison.OrdinalIgnoreCase);
            bool isXml = code.TrimStart().StartsWith("<?xml") || code.TrimStart().StartsWith("<Document");
            string normalizedPath = groupPath ?? "";

            // Parse the path, auto-creating intermediate groups
            string locationLabel = string.IsNullOrEmpty(normalizedPath) ? "<root>" : normalizedPath;

            // Not XML and not UDT => plain SCL code
            if (!isXml && !isUdt)
            {
                string safeName = string.IsNullOrWhiteSpace(blockName) ? "AI_Block" : blockName;
                string tempSclFile = Path.Combine(Path.GetTempPath(), $"AgentImport_{safeName}.scl");

                // Snapshot existing block for rollback on failure
                string snapshotXml = null;
                try
                {
                    var existingBlock = PlcSoftware.BlockGroup.Blocks.Find(safeName);
                    if (existingBlock != null)
                    {
                        snapshotXml = Path.Combine(Path.GetTempPath(), $"AgentSnapshot_{safeName}_{Guid.NewGuid():N}.xml");
                        existingBlock.Export(new FileInfo(snapshotXml), ExportOptions.None);
                    }
                }
                catch (Exception snapEx)
                {
                    AppLogger.Log($"[Openness] Snapshot export failed for '{safeName}': {snapEx.Message}");
                    snapshotXml = null; // Can't rollback without snapshot, proceed with caution
                }
                // Encoding strategy: UTF-8 with BOM. The project must cover all versions (V17/V18/V19...)
                // and all languages (Chinese/English/German/mixed), so no single ANSI codepage works
                // (GBK drops German umlauts ä ö ü ß, 1252 drops Chinese) — UTF-8 is required.
                // Without a BOM, CreateFromFile decodes with the machine's system ANSI codepage, so the
                // result depends on locale and Chinese becomes garbled; a BOM lets TIA reliably detect
                // UTF-8 regardless of machine language. This matches the XML/UDT branch below.
                // Note: "Please check the mnemonic settings" is caused by editing-language/mnemonic
                // settings (EditingLanguage is set above), not the BOM itself.
                File.WriteAllText(tempSclFile, code, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                // GenerateBlocksFromSource names the generated block from the SCL declaration
                // (e.g. FUNCTION_BLOCK "FB_MotorControl"), which may differ from the file name /
                // passed blockName. Finding by file name would miss it => move skipped => block stays
                // in the root. So parse the truly declared block name for the move lookup.
                string declaredName = ExtractDeclaredBlockName(code) ?? safeName;

                bool importSucceeded = false;
                try
                {
                    // 1. Set the editing language to Chinese, German or English so compile errors are in the
                    // expected language. Default to Chinese (zh-CN) for accurate raw error text.
                    try
                    {
                        if (Project != null && Project.LanguageSettings != null)
                        {
                            var targetLang = Project.LanguageSettings.Languages.Find(new System.Globalization.CultureInfo("zh-CN")) ??
                                             Project.LanguageSettings.Languages.Find(new System.Globalization.CultureInfo("en-US")) ??
                                             Project.LanguageSettings.Languages.Find(new System.Globalization.CultureInfo("de-DE"));
                            if (targetLang != null) Project.LanguageSettings.EditingLanguage = targetLang;
                        }
                    }
                    catch { /* ignore language-setting failure */ }

                    var extSources = PlcSoftware.ExternalSourceGroup.ExternalSources;
                    var existingSource = extSources.Find(safeName);
                    if (existingSource != null)
                    {
                        existingSource.Delete();
                    }

                    // Record existing root block names before generation to diff out the truly new block
                    // (fallback: when declaredName / safeName both fail to match, locate via diff)
                    var beforeNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var b in PlcSoftware.BlockGroup.Blocks) beforeNames.Add(b.Name);
                    }
                    catch { /* enumeration failure is non-blocking */ }

                    var sourceFile = extSources.CreateFromFile(safeName, tempSclFile);

                    try
                    {
                        var compilerResult = sourceFile.GenerateBlocksFromSource(Siemens.Engineering.SW.ExternalSources.GenerateBlockOption.None);

                        // Resolve the truly generated block: prefer declaredName, then safeName, then diff
                        var actualBlock = PlcSoftware.BlockGroup.Blocks.Find(declaredName)
                                          ?? PlcSoftware.BlockGroup.Blocks.Find(safeName);
                        if (actualBlock == null)
                        {
                            try
                            {
                                foreach (var b in PlcSoftware.BlockGroup.Blocks)
                                {
                                    if (!beforeNames.Contains(b.Name)) { actualBlock = b; break; }
                                }
                            }
                            catch { /* enumeration failure is non-blocking */ }
                        }
                        string actualName = actualBlock?.Name ?? declaredName;
                        AppLogger.Log($"[Openness] Generated block resolved: declaredName='{declaredName}', safeName='{safeName}', actualName='{actualName}', found={(actualBlock != null)}");

                        var errorSb = new System.Text.StringBuilder();

                        // 2. Compile only the newly generated target block, not the whole PLC Software.
                        //    Compiling the whole PLC also compiles the safety program (F-Program /
                        //    Safety Administration), which TIA explicitly forbids Openness from modifying,
                        //    producing "Changes to the F-program with the optional package Openness are
                        //    not allowed" and wrongly failing an unrelated ordinary block import.
                        var generatedForCompile = actualBlock;
                        if (generatedForCompile != null)
                        {
                            var blockCompilable = generatedForCompile.GetService<Siemens.Engineering.Compiler.ICompilable>();
                            if (blockCompilable != null)
                            {
                                var blockResult = blockCompilable.Compile();
                                if (blockResult.ErrorCount > 0)
                                {
                                    errorSb.AppendLine($"块 '{safeName}' 编译错误:");
                                    AppendCompileMessages(blockResult.Messages, errorSb, 0, safeName);
                                }
                            }
                        }

                        if (compilerResult == null || compilerResult.Count == 0)
                        {
                            string detail = errorSb.Length > 0 ? $"\n详细信息:\n{errorSb.ToString()}" : "";
                            throw new InvalidOperationException($"Compilation failed. No blocks were generated from the source file. This usually indicates a severe syntax error or mnemonic conflict (Please check the mnemonic settings).{detail}");
                        }

                        if (errorSb.Length > 0)
                        {
                            throw new InvalidOperationException($"Compilation failed with errors:\n{errorSb.ToString()}");
                        }
                    }
                    finally
                    {
                        // Always clean up the external source file
                        sourceFile.Delete();
                    }

                    importSucceeded = true;

                    // Look up by the truly declared name (declaredName); fall back to the file name (safeName).
                    var generatedBlock = PlcSoftware.BlockGroup.Blocks.Find(declaredName)
                                         ?? PlcSoftware.BlockGroup.Blocks.Find(safeName);
                    string movedName = generatedBlock?.Name ?? declaredName;
                    AppLogger.Log($"[Openness] After generate: declaredName='{declaredName}', safeName='{safeName}', foundInRoot={(generatedBlock != null)}, normalizedPath='{normalizedPath}'");
                    if (generatedBlock != null && !string.IsNullOrEmpty(normalizedPath))
                    {
                        string tempXml = Path.Combine(Path.GetTempPath(), $"AgentImport_{movedName}_move.xml");
                        generatedBlock.Export(new FileInfo(tempXml), ExportOptions.None);
                        generatedBlock.Delete();

                        var targetGroup = ResolveBlockGroup(PlcSoftware.BlockGroup, normalizedPath, createMissing: true);
                        targetGroup.Blocks.Import(new FileInfo(tempXml), ImportOptions.Override);
                        File.Delete(tempXml);

                        // Read snapshot content before finally cleans it up, then raise BlockWritten
                        string snapshotContent = null;
                        if (!string.IsNullOrEmpty(snapshotXml) && File.Exists(snapshotXml))
                            try { snapshotContent = File.ReadAllText(snapshotXml); } catch { }
                        RaiseBlockWritten(movedName, blockType, snapshotContent);

                        AppLogger.Log($"[Openness] Moved block '{movedName}' to Blocks\\{normalizedPath}");
                        return $"Block '{movedName}' generated from SCL source and moved to Blocks\\{locationLabel}.";
                    }

                    if (generatedBlock == null)
                        AppLogger.Log($"[Openness] WARNING: block (declared='{declaredName}', file='{safeName}') not found in root after generate. Move to '{normalizedPath}' skipped.");

                    // Read snapshot content before finally cleans it up, then raise BlockWritten
                    {
                        string snapshotContent = null;
                        if (!string.IsNullOrEmpty(snapshotXml) && File.Exists(snapshotXml))
                            try { snapshotContent = File.ReadAllText(snapshotXml); } catch { }
                        RaiseBlockWritten(movedName, blockType, snapshotContent);
                    }

                    return $"Block '{movedName}' generated from SCL source.";
                }
                catch (Exception ex)
                {
                    // Rollback: restore the original block from snapshot if import failed
                    if (!string.IsNullOrEmpty(snapshotXml) && File.Exists(snapshotXml))
                    {
                        try
                        {
                            // Delete the partially-generated block (if any)
                            var brokenBlock = PlcSoftware.BlockGroup.Blocks.Find(safeName);
                            if (brokenBlock != null)
                            {
                                brokenBlock.Delete();
                            }
                            // Re-import the snapshot
                            PlcSoftware.BlockGroup.Blocks.Import(new FileInfo(snapshotXml), ImportOptions.Override);
                            AppLogger.Log($"[Openness] Rollback: restored '{safeName}' from snapshot after import failure");
                        }
                        catch (Exception rollbackEx)
                        {
                            AppLogger.LogError($"[Openness] Rollback failed for '{safeName}'", rollbackEx);
                        }
                        finally
                        {
                            try { File.Delete(snapshotXml); }
                            catch { /* best effort */ }
                        }
                    }
                    throw new InvalidOperationException($"GenerateBlocksFromSource failed for '{safeName}'. SCL source preserved at: {tempSclFile}. {ex.Message}", ex);
                }
                finally
                {
                    if (importSucceeded)
                    {
                        RaiseBlockChanged(declaredName ?? safeName ?? blockName);
                        try { File.Delete(tempSclFile); }
                        catch (Exception delEx) { AppLogger.Log($"[Openness] Delete temp SCL file failed: {delEx.Message}"); }
                    }
                    // Clean up snapshot on success
                    if (!string.IsNullOrEmpty(snapshotXml))
                    {
                        try { File.Delete(snapshotXml); }
                        catch { /* best effort */ }
                    }
                }
            }

            string extension;
            if (isXml) extension = ".xml";
            else if (string.Equals(blockType, "DB", StringComparison.OrdinalIgnoreCase)) extension = ".db";
            else if (isUdt) extension = ".udt";
            else extension = ".scl";

            string tempFile = Path.Combine(Path.GetTempPath(), $"AgentImport_{blockName}{extension}");
            // TIA Portal XML import requires UTF-8 with BOM, otherwise Chinese comments turn garbled.
            File.WriteAllText(tempFile, code, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var fileInfo = new FileInfo(tempFile);

            try
            {
                if (isUdt)
                {
                    var typeGroup = ResolveTypeGroup(PlcSoftware.TypeGroup, normalizedPath, createMissing: true);

                    // Snapshot the existing type so a failed import can be rolled back.
                    string udtSnapshot = null;
                    string udtSnapshotContent = null;
                    try
                    {
                        dynamic existingType = PlcSoftware.TypeGroup.Types.Find(blockName);
                        if (existingType == null) existingType = FindTypeRecursive(PlcSoftware.TypeGroup, blockName);
                        if (existingType != null)
                        {
                            udtSnapshot = Path.Combine(Path.GetTempPath(), $"AgentTypeSnapshot_{Guid.NewGuid():N}.xml");
                            existingType.Export(new FileInfo(udtSnapshot), ExportOptions.None);
                        }
                    }
                    catch (Exception snapEx)
                    {
                        AppLogger.Log($"[Openness] UDT snapshot failed for '{blockName}': {snapEx.Message}");
                        udtSnapshot = null;
                    }

                    try
                    {
                        typeGroup.Types.Import(fileInfo, ImportOptions.Override);
                        if (!string.IsNullOrEmpty(udtSnapshot))
                        {
                            try { udtSnapshotContent = File.ReadAllText(udtSnapshot); }
                            catch { /* snapshot content is optional */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!string.IsNullOrEmpty(udtSnapshot) && File.Exists(udtSnapshot))
                        {
                            try
                            {
                                typeGroup.Types.Import(new FileInfo(udtSnapshot), ImportOptions.Override);
                                AppLogger.Log($"[Openness] Rollback: restored UDT '{blockName}' after import failure");
                            }
                            catch (Exception rollbackEx)
                            {
                                AppLogger.LogError($"[Openness] UDT rollback failed for '{blockName}'", rollbackEx);
                            }
                        }
                        throw new InvalidOperationException($"Type import failed for '{blockName}'. {ex.Message}", ex);
                    }
                    finally
                    {
                        if (!string.IsNullOrEmpty(udtSnapshot))
                        {
                            try { File.Delete(udtSnapshot); }
                            catch { /* best effort */ }
                        }
                    }

                    RaiseBlockChanged(blockName);
                    RaiseBlockWritten(blockName, blockType, udtSnapshotContent);
                    return $"Type '{blockName}' imported into Types\\{locationLabel} ({extension}).";
                }
                else
                {
                    // Capture snapshot of existing XML block before overwriting
                    string xmlSnapshot = null;
                    try
                    {
                        var existingXmlBlock = FindBlockRecursive(PlcSoftware.BlockGroup, blockName);
                        if (existingXmlBlock != null)
                            xmlSnapshot = await ExportBlockXmlAsync(existingXmlBlock);
                    }
                    catch (Exception snapEx)
                    {
                        AppLogger.Log($"[Openness] XML branch snapshot failed for '{blockName}': {snapEx.Message}");
                    }

                    var blockGroup = ResolveBlockGroup(PlcSoftware.BlockGroup, normalizedPath, createMissing: true);
                    try
                    {
                        blockGroup.Blocks.Import(fileInfo, ImportOptions.Override);
                    }
                    catch (Exception ex)
                    {
                        // Rollback: remove the broken block (if any) and re-import the snapshot.
                        if (!string.IsNullOrEmpty(xmlSnapshot))
                        {
                            string rollbackFile = Path.Combine(
                                Path.GetTempPath(), $"AgentRollback_{Guid.NewGuid():N}.xml");
                            try
                            {
                                File.WriteAllText(rollbackFile, xmlSnapshot, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                                var brokenBlock = FindBlockRecursive(PlcSoftware.BlockGroup, blockName);
                                if (brokenBlock != null) brokenBlock.Delete();
                                blockGroup.Blocks.Import(new FileInfo(rollbackFile), ImportOptions.Override);
                                AppLogger.Log($"[Openness] Rollback: restored '{blockName}' after XML import failure");
                            }
                            catch (Exception rollbackEx)
                            {
                                AppLogger.LogError($"[Openness] XML rollback failed for '{blockName}'", rollbackEx);
                            }
                            finally
                            {
                                try { File.Delete(rollbackFile); }
                                catch { /* best effort */ }
                            }
                        }
                        throw new InvalidOperationException($"XML import failed for '{blockName}'. {ex.Message}", ex);
                    }

                    RaiseBlockChanged(blockName);
                    RaiseBlockWritten(blockName, blockType, xmlSnapshot);
                    return $"Block '{blockName}' imported into Blocks\\{locationLabel} ({extension}).";
                }
            }
            finally
            {
                try { File.Delete(tempFile); }
                catch (Exception delEx) { AppLogger.Log($"[Openness] Delete temp file failed: {delEx.Message}"); }
            }
        }

        /// <summary>
        /// Walk down the BlockGroup tree by backslash-delimited path. Creates missing user groups when
        /// <paramref name="createMissing"/> is true. Empty path returns the root group itself.
        /// </summary>
        private static PlcBlockGroup ResolveBlockGroup(PlcBlockSystemGroup root, string path, bool createMissing)
        {
            if (root == null) throw new InvalidOperationException("BlockGroup root is null.");
            PlcBlockGroup current = root;
            if (string.IsNullOrEmpty(path)) return current;

            var segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                var next = current.Groups.FirstOrDefault(g => string.Equals(g.Name, seg, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    if (!createMissing)
                        throw new InvalidOperationException($"Block group '{seg}' not found under '{current.Name}'.");
                    AppLogger.Log($"[OpennessService] Creating block group '{seg}' under '{current.Name}'");
                    next = current.Groups.Create(seg);
                }
                current = next;
            }
            return current;
        }

        /// <summary>
        /// Walk down the TypeGroup tree by backslash-delimited path. Creates missing user groups when
        /// <paramref name="createMissing"/> is true. Empty path returns the root group itself.
        /// </summary>
        private static PlcTypeGroup ResolveTypeGroup(PlcTypeSystemGroup root, string path, bool createMissing)
        {
            if (root == null) throw new InvalidOperationException("TypeGroup root is null.");
            PlcTypeGroup current = root;
            if (string.IsNullOrEmpty(path)) return current;

            var segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                var next = current.Groups.FirstOrDefault(g => string.Equals(g.Name, seg, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    if (!createMissing)
                        throw new InvalidOperationException($"Type group '{seg}' not found under '{current.Name}'.");
                    AppLogger.Log($"[OpennessService] Creating type group '{seg}' under '{current.Name}'");
                    next = current.Groups.Create(seg);
                }
                current = next;
            }
            return current;
        }

        public Task<string> CompileAsync()
            => OnSta(() => CompileCoreAsync());

        private async Task<string> CompileCoreAsync()
        {
            // Runs on the STA host thread (guaranteed by OnSta wrapper).
            await Task.CompletedTask;
            if (PlcSoftware == null)
                throw new InvalidOperationException("No PLC Software connected.");

            var compilable = PlcSoftware.GetService<Siemens.Engineering.Compiler.ICompilable>();
            if (compilable == null)
                throw new InvalidOperationException("Compile service not available for current PLC.");

            var result = compilable.Compile();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Compile State: {result.State}");
            sb.AppendLine($"Errors: {result.ErrorCount}");
            sb.AppendLine($"Warnings: {result.WarningCount}");
            sb.AppendLine();

            if (result.Messages.Count > 0)
            {
                sb.AppendLine("Messages:");
                AppendCompileMessages(result.Messages, sb, 0, null);
            }
            else
            {
                sb.AppendLine("No messages.");
            }

            return sb.ToString();
        }

        private static void AppendCompileMessages(Siemens.Engineering.Compiler.CompilerResultMessageComposition messages, System.Text.StringBuilder sb, int depth, string inheritedBlock)
        {
            string indent = new string(' ', depth * 2);
            foreach (Siemens.Engineering.Compiler.CompilerResultMessage msg in messages)
            {
                string text = (msg.Description ?? "").Trim();
                string block = ExtractBlockNameFromMessage(text, inheritedBlock);
                string label = string.IsNullOrWhiteSpace(block) ? "" : $"[{block}] ";
                sb.AppendLine($"{indent}- {msg.State}: {label}{text}");
                if (msg.Messages != null && msg.Messages.Count > 0)
                {
                    // Container nodes often have no text of their own; the block name they carry
                    // (when TIA provides one) applies to their children.
                    AppendCompileMessages(msg.Messages, sb, depth + 1, block ?? inheritedBlock);
                }
            }
        }

        public bool OpenProject(FileInfo projectPath)
            => RunOnStaBlocking(() => OpenProjectCore(projectPath));

        private bool OpenProjectCore(FileInfo projectPath)
        {
            if (TiaPortal == null) return false;

            try
            {
                if (TiaPortal.Projects.Count > 0)
                {
                    Project = TiaPortal.Projects[0];
                    if (projectPath != null && Project.Path.FullName != projectPath.FullName)
                    {
                        _uiService.ShowNotification("Error", "Another project is already open in TIA Portal.", NotificationType.Warning);
                        return false;
                    }
                    return true;
                }

                if (projectPath != null)
                {
                    Project = TiaPortal.Projects.Open(projectPath);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _uiService.ShowNotification("Error", "Error opening project: " + ex.Message, NotificationType.Error);
                return false;
            }
        }

        public PlcSoftware FindPlcSoftware()
            => RunOnStaBlocking(() => FindPlcSoftwareCore());

        /// <summary>
        /// Async, STA-safe variant of <see cref="FindPlcSoftware"/>. The PlcSoftware lookup
        /// runs on the STA host thread without blocking the caller, so UI-thread callers never
        /// deadlock waiting on the STA host. Returns true when a PLC was found.
        /// </summary>
        public Task<bool> FindPlcSoftwareAsync()
            => OnSta(() => Task.FromResult(FindPlcSoftwareCore() != null));

        private PlcSoftware FindPlcSoftwareCore()
        {
            if (Project == null) return null;

            foreach (Device device in Project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (item.Classification.ToString() == "CPU")
                    {
                        SoftwareContainer container = ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
                        if (container != null)
                        {
                            PlcSoftware = container.Software as PlcSoftware;
                            return PlcSoftware;
                        }
                    }
                }
            }
            return null;
        }

        public string GetPlcCompatibilityContext()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## PLC Compatibility");

            string family = "Unknown";
            string cpuName = "";
            string deviceName = "";
            string typeIdentifier = "";
            string orderNumber = "";
            bool standardAccessOnly = false;

            try
            {
                if (Project != null)
                {
                    foreach (Device device in Project.Devices)
                    {
                        deviceName = device.Name ?? "";
                        typeIdentifier = device.TypeIdentifier ?? "";
                        foreach (DeviceItem item in device.DeviceItems)
                        {
                            if (TryFindCpuInfo(item, ref cpuName, ref orderNumber))
                            {
                                break;
                            }
                        }

                        family = InferPlcFamily(typeIdentifier, deviceName, cpuName, orderNumber);
                        if (!string.Equals(family, "Unknown", StringComparison.OrdinalIgnoreCase)) break;
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("PLC detection error: " + ex.Message);
            }

            standardAccessOnly = string.Equals(family, "S7-300/400", StringComparison.OrdinalIgnoreCase);

            sb.AppendLine("PLC family: " + family);
            if (!string.IsNullOrWhiteSpace(deviceName)) sb.AppendLine("Device: " + deviceName);
            if (!string.IsNullOrWhiteSpace(cpuName)) sb.AppendLine("CPU: " + cpuName);
            if (!string.IsNullOrWhiteSpace(typeIdentifier)) sb.AppendLine("TypeIdentifier: " + typeIdentifier);
            if (!string.IsNullOrWhiteSpace(orderNumber)) sb.AppendLine("OrderNumber: " + orderNumber);
            sb.AppendLine("Optimized block access supported: " + (standardAccessOnly ? "No" : "Yes/Unknown"));
            sb.AppendLine("SCL generation rule: " + (standardAccessOnly
                ? "Generate S7-300/400 compatible SCL. Use { S7_Optimized_Access := 'FALSE' }. Avoid R_TRIG/F_TRIG and prefer manual edge detection with Bool memory. Avoid S7-1200/1500-only features."
                : "Prefer broadly compatible SCL. If optimized access is emitted, it must be compatible with the detected PLC family."));
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>
        /// Live environment facts (TIA version + PLC/CPU), never cached: the user can switch the CPU
        /// or TIA version mid-session, so every call re-walks the devices instead of reusing an
        /// earlier snapshot. Never throws; unknown facts stay empty and Live turns false.
        /// </summary>
        public EnvironmentProfileInfo GetEnvironmentProfile()
        {
            var info = new EnvironmentProfileInfo
            {
                TiaVersion = "Unknown",
                ProjectName = "",
                ProjectKey = "",
                PlcName = "",
                DeviceName = "",
                CpuName = "",
                OrderNumber = "",
                TypeIdentifier = "",
                PlcFamily = "Unknown",
                Fingerprint = "",
                DetectedAt = DateTime.Now.ToString("o"),
                Live = false,
            };

            try
            {
                var version = Services.Implementations.TiaVersionAdapter.CurrentVersion;
                if (string.IsNullOrWhiteSpace(version) &&
                    Services.Implementations.TiaVersionAdapter.CurrentMajorVersion > 0)
                {
                    version = "V" + Services.Implementations.TiaVersionAdapter.CurrentMajorVersion;
                }
                info.TiaVersion = string.IsNullOrWhiteSpace(version) ? "Unknown" : version;

                var project = Project;
                if (project != null)
                {
                    info.ProjectName = project.Name ?? "";
                    info.ProjectKey = project.Path?.FullName ?? info.ProjectName;
                    info.PlcName = PlcSoftware?.Name ?? "";

                    var selected = false;
                    foreach (Device device in project.Devices)
                    {
                        string deviceName = device.Name ?? "";
                        string typeIdentifier = device.TypeIdentifier ?? "";
                        string cpuName = "";
                        string orderNumber = "";
                        foreach (DeviceItem item in device.DeviceItems)
                        {
                            if (TryFindCpuInfo(item, ref cpuName, ref orderNumber)) break;
                        }

                        string family = InferPlcFamily(typeIdentifier, deviceName, cpuName, orderNumber);
                        info.Devices.Add(new EnvironmentDeviceInfo
                        {
                            Name = deviceName,
                            TypeIdentifier = typeIdentifier,
                            CpuName = cpuName,
                            OrderNumber = orderNumber,
                            PlcFamily = family,
                        });

                        // Top-level facts keep pointing at the first device that resolves a PLC family;
                        // every device is still listed in Devices for multi-station projects.
                        if (!selected && !string.Equals(family, "Unknown", StringComparison.OrdinalIgnoreCase))
                        {
                            info.DeviceName = deviceName;
                            info.TypeIdentifier = typeIdentifier;
                            info.CpuName = cpuName;
                            info.OrderNumber = orderNumber;
                            info.PlcFamily = family;
                            selected = true;
                        }
                    }
                }

                // Only a live project yields a meaningful fingerprint; an empty hash over
                // placeholder facts would look like a real environment identity.
                if (project != null)
                {
                    info.Fingerprint = BuildEnvironmentFingerprint(info);
                    info.Live = true;
                }
            }
            catch (Exception ex)
            {
                // A failed live read must not break tool callers; report facts we already have.
                AppLogger.Log("[Openness] GetEnvironmentProfile failed: " + ex.Message);
                info.Live = false;
                if (string.IsNullOrWhiteSpace(info.PlcFamily)) info.PlcFamily = "Unknown";
            }

            return info;
        }

        /// <summary>
        /// Stable short fingerprint of the environment: SHA1 over (tia version | PLC family |
        /// order number | project key), first 12 hex chars. It identifies a plan's environment;
        /// blocking decisions still compare the individual facts, not this hash.
        /// </summary>
        private static string BuildEnvironmentFingerprint(EnvironmentProfileInfo info)
        {
            string raw = string.Join("|",
                info.TiaVersion ?? "", info.PlcFamily ?? "", info.OrderNumber ?? "", info.ProjectKey ?? "");
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
                string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                return hex.Length <= 12 ? hex : hex.Substring(0, 12);
            }
        }

        private static bool TryFindCpuInfo(DeviceItem item, ref string cpuName, ref string orderNumber)
        {
            if (item == null) return false;

            bool isCpu = string.Equals(item.Classification.ToString(), "CPU", StringComparison.OrdinalIgnoreCase);
            if (isCpu)
            {
                cpuName = item.Name ?? "";
                orderNumber = TryGetAttribute(item, "OrderNumber");
                return true;
            }

            if (item.DeviceItems != null)
            {
                foreach (DeviceItem child in item.DeviceItems)
                {
                    if (TryFindCpuInfo(child, ref cpuName, ref orderNumber)) return true;
                }
            }

            return false;
        }

        private static string TryGetAttribute(IEngineeringObject obj, string attributeName)
        {
            try
            {
                object value = obj?.GetAttribute(attributeName);
                return value?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string InferPlcFamily(string typeIdentifier, string deviceName, string cpuName, string orderNumber)
        {
            string text = ((typeIdentifier ?? "") + " " + (deviceName ?? "") + " " + (cpuName ?? "") + " " + (orderNumber ?? "")).ToUpperInvariant();
            if (text.Contains("S7300") || text.Contains("S7400") || text.Contains("S7-300") || text.Contains("S7-400") || text.Contains("CPU 31") || text.Contains("CPU 41"))
                return "S7-300/400";
            if (text.Contains("S71200") || text.Contains("S7-1200") || text.Contains("CPU 12"))
                return "S7-1200";
            if (text.Contains("S71500") || text.Contains("S7-1500") || text.Contains("CPU 15"))
                return "S7-1500";
            return "Unknown";
        }

        public string GetProjectContext()
        {
            if (PlcSoftware == null) return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Project Context (for AI reference):");
            sb.Append(GetPlcCompatibilityContext());

            try
            {
                var tags = new List<string>();
                if (PlcSoftware.TagTableGroup != null)
                {
                    foreach(var table in PlcSoftware.TagTableGroup.TagTables)
                    {
                        foreach(var tag in table.Tags)
                        {
                            tags.Add($"{tag.Name}:{tag.DataTypeName}");
                            if(tags.Count > 50) break;
                        }
                        if(tags.Count > 50) break;
                    }
                }
                sb.AppendLine("Tags: " + string.Join(", ", tags));

                var types = new List<string>();
                if (PlcSoftware.TypeGroup != null)
                {
                    foreach(var type in PlcSoftware.TypeGroup.Types)
                    {
                        types.Add(type.Name);
                        if(types.Count > 50) break;
                    }
                    foreach(var group in PlcSoftware.TypeGroup.Groups)
                    {
                        foreach(var type in group.Types)
                        {
                            types.Add(type.Name);
                            if(types.Count > 50) break;
                        }
                        if(types.Count > 50) break;
                    }
                }
                sb.AppendLine("UDTs: " + string.Join(", ", types));
            }
            catch (Exception ex)
            {
                sb.AppendLine("Error reading context: " + ex.Message);
            }

            return sb.ToString();
        }

        // ===== High-priority MCP tool migration implementation =====

        public async Task<BootstrapInfo> BootstrapAsync()
        {
            await Task.CompletedTask;
            
            var info = new BootstrapInfo
            {
                IsConnected = IsConnected,
                CurrentProjectName = Project?.Name ?? "None",
                KnownLimitations = new List<string>()
            };

            // TIA version: report the version resolved at startup (single source of truth),
            // falling back to the major mirror. The COM reflection on TiaPortal no longer works.
            var version = TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentVersion;
            if (string.IsNullOrWhiteSpace(version) &&
                TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion > 0)
            {
                version = "V" + TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion;
            }
            info.TiaVersion = string.IsNullOrWhiteSpace(version) ? "Unknown" : version;

            // Openness is usable once the Siemens.Engineering assembly is loaded and TiaPortal is attached.
            info.OpennessGroupValid = TiaPortal != null;

            // Recommend the next tool
            if (!IsConnected)
            {
                info.RecommendedNextTool = "Connect";
            }
            else if (Project == null)
            {
                info.RecommendedNextTool = "OpenProject";
            }
            else if (PlcSoftware == null)
            {
                info.RecommendedNextTool = "FindPlcSoftware";
            }
            else
            {
                info.RecommendedNextTool = "GetProjectTree";
            }

            // Known limitations
            info.KnownLimitations.Add("TIA Openness requires STA thread for most operations");
            info.KnownLimitations.Add("Some HMI operations may not be supported in all TIA versions");

            return info;
        }

        public async Task<bool> AttachToOpenProjectAsync(string projectName)
        {
            await Task.CompletedTask;
            
            if (TiaPortal == null)
            {
                AppLogger.Log("[Openness] AttachToOpenProject: Not connected to TIA Portal");
                return false;
            }

            try
            {
                foreach (var proj in TiaPortal.Projects)
                {
                    if (string.Equals(proj.Name, projectName, StringComparison.OrdinalIgnoreCase))
                    {
                        Project = proj;
                        AppLogger.Log($"[Openness] Attached to project: {proj.Name}");
                        return true;
                    }
                }
                
                AppLogger.Log($"[Openness] Project '{projectName}' not found in TIA Portal");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] AttachToOpenProject failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DeleteBlockAsync(string blockName)
        {
            await Task.CompletedTask;
            
            if (PlcSoftware == null)
                throw new InvalidOperationException("No PLC Software connected");

            try
            {
                var block = FindBlockRecursive(PlcSoftware.BlockGroup, blockName);
                if (block == null)
                {
                    AppLogger.Log($"[Openness] Block '{blockName}' not found");
                    return false;
                }

                block.Delete();
                AppLogger.Log($"[Openness] Deleted block '{blockName}'");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] DeleteBlock failed: {ex.Message}");
                return false;
            }
        }

        public async Task<CompilationResult> GetCompilationResultAsync()
        {
            await Task.CompletedTask;
            
            if (PlcSoftware == null)
                throw new InvalidOperationException("No PLC Software connected");

            var compilable = PlcSoftware.GetService<Siemens.Engineering.Compiler.ICompilable>();
            if (compilable == null)
                throw new InvalidOperationException("Compile service not available");

            var result = compilable.Compile();
            
            var errors = new List<CompilationError>();
            var warnings = new List<CompilationWarning>();
            CollectCompileMessages(result.Messages, null, errors, warnings);

            return new CompilationResult
            {
                Success = errors.Count == 0,
                ErrorMessage = errors.Count > 0 ? $"Compile failed with {errors.Count} error(s), {warnings.Count} warning(s)" : null,
                Errors = errors,
                Warnings = warnings
            };
        }

        /// <summary>
        /// Compile a single block instead of the whole PLC software. The block-level compile is what
        /// the import path already uses to avoid compiling Safety programs; reusing it here keeps the
        /// post-write check local, so it does not fight the user editing other blocks.
        /// </summary>
        public async Task<CompilationResult> CompileBlockAsync(string blockName)
        {
            await Task.CompletedTask;

            if (PlcSoftware == null)
                throw new InvalidOperationException("No PLC Software connected");
            if (string.IsNullOrWhiteSpace(blockName))
                throw new ArgumentException("blockName is required", nameof(blockName));

            string name = blockName.Trim();
            var block = FindPlcBlock(PlcSoftware.BlockGroup, name, 0);
            if (block == null)
                throw new InvalidOperationException($"Block '{name}' not found.");

            var compilable = block.GetService<Siemens.Engineering.Compiler.ICompilable>();
            if (compilable == null)
                throw new InvalidOperationException($"Block '{name}' cannot be compiled.");

            var result = compilable.Compile();

            var errors = new List<CompilationError>();
            var warnings = new List<CompilationWarning>();
            CollectCompileMessages(result.Messages, name, errors, warnings);

            return new CompilationResult
            {
                Success = errors.Count == 0,
                ErrorMessage = errors.Count > 0
                    ? $"Block '{name}' compile failed with {errors.Count} error(s)"
                    : null,
                Errors = errors,
                Warnings = warnings
            };
        }

        /// <summary>Depth-first block lookup by name across PLC block groups (STA only).</summary>
        private static PlcBlock FindPlcBlock(PlcBlockGroup group, string name, int depth)
        {
            if (group == null || depth > 32) return null;

            var hit = group.Blocks.Find(name);
            if (hit != null) return hit;

            foreach (PlcBlockUserGroup child in group.Groups)
            {
                var found = FindPlcBlock(child, name, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Flatten TIA's nested compile-message tree. The useful text (and the only place a block
        /// name can be recovered from) sits in the leaves; walking only the top level loses them.
        /// </summary>
        private static void CollectCompileMessages(
            Siemens.Engineering.Compiler.CompilerResultMessageComposition messages,
            string inheritedBlock,
            List<CompilationError> errors,
            List<CompilationWarning> warnings)
        {
            if (messages == null) return;
            foreach (Siemens.Engineering.Compiler.CompilerResultMessage msg in messages)
            {
                string text = (msg.Description ?? "").Trim();
                string block = ExtractBlockNameFromMessage(text, inheritedBlock);
                if (!string.IsNullOrEmpty(text) && !IsCompileSummaryMessage(text))
                {
                    string state = msg.State.ToString();
                    if (state == "Error")
                        errors.Add(new CompilationError { BlockName = block ?? "Unknown", Message = text });
                    else if (state == "Warning")
                        warnings.Add(new CompilationWarning { BlockName = block ?? "Unknown", Message = text });
                }
                if (msg.Messages != null && msg.Messages.Count > 0)
                    CollectCompileMessages(msg.Messages, block ?? inheritedBlock, errors, warnings);
            }
        }

        /// <summary>
        /// TIA appends a synthetic top-level "Compiling finished (errors: N; warnings: M)" message
        /// next to the real leaf errors. It is a status line, not an actionable error, so it must not
        /// inflate the structured error count / baseline.
        /// </summary>
        private static bool IsCompileSummaryMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.StartsWith("Compiling finished", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Compilation finished", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("编译结束", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Recover the affected block from a compile message. TIA only names it in some locales/messages
        /// ("Block 'X' ...", "Baustein 'X' ..."); otherwise inherit from the enclosing node. Never use
        /// the whole sentence as a block name (the old Split(':') heuristic did exactly that).
        /// </summary>
        private static string ExtractBlockNameFromMessage(string message, string inherited = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return inherited;

            foreach (var marker in new[] { "Block", "Baustein", "块" })
            {
                int idx = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string rest = message.Substring(idx + marker.Length).TrimStart();
                if (rest.Length >= 2 && (rest[0] == '\'' || rest[0] == '"'))
                {
                    int close = rest.IndexOf(rest[0], 1);
                    if (close > 1)
                    {
                        string inside = rest.Substring(1, close - 1).Trim();
                        if (inside.Length > 0) return inside;
                    }
                }
            }

            // "Name: detail" — accept only a bare identifier, so a plain sentence like
            // "Data type DTL is unknown." never becomes a block name.
            int colon = message.IndexOf(':');
            if (colon > 0)
            {
                string head = message.Substring(0, colon).Trim().Trim('"', '\'');
                if (head.Length > 0 && head.Length <= 120 && !head.Any(char.IsWhiteSpace))
                    return head;
            }
            return inherited;
        }

        /// <summary>
        /// Parses the first block declaration name from SCL source.
        /// GenerateBlocksFromSource names the generated block from the declaration in the source
        /// (e.g. FUNCTION_BLOCK "FB_MotorControl"), independent of the file name / passed blockName.
        /// Returns null when nothing parsed, so the caller falls back to the file name.
        /// Supports: FUNCTION_BLOCK / FUNCTION / DATA_BLOCK / ORGANIZATION_BLOCK, with or without quotes.
        /// </summary>
        private static string ExtractDeclaredBlockName(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            try
            {
                // (?im): case-insensitive + multiline. Matches the block name (quoted or bare identifier) right after the keyword.
                var m = System.Text.RegularExpressions.Regex.Match(
                    code,
                    "(?im)^\\s*(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK)\\s+(?:\"([^\"]+)\"|([A-Za-z_][A-Za-z0-9_]*))");
                if (m.Success)
                {
                    string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    name = name?.Trim();
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { /* parse failed; fall back to the file name */ }
            return null;
        }

        public async Task<List<HardwareCatalogItem>> SearchHardwareCatalogAsync(string searchTerm)
        {
            await Task.CompletedTask;

            var results = new List<HardwareCatalogItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (TiaPortal == null)
            {
                AppLogger.Log("[Openness] SearchHardwareCatalog: not connected to TIA Portal");
                return results;
            }

            try
            {
                // HardwareCatalog is accessible via reflection on all TIA versions (V15.1+).
                var hwProp = TiaPortal.GetType().GetProperty("HardwareCatalog");
                if (hwProp == null)
                {
                    AppLogger.Log("[Openness] HardwareCatalog property not found on TiaPortal type");
                    return results;
                }
                var catalog = hwProp.GetValue(TiaPortal);
                if (catalog == null)
                {
                    AppLogger.Log("[Openness] HardwareCatalog is null");
                    return results;
                }

                // Catalog.Find(string keyword) returns IEnumerable of entries.
                var findMethod = catalog.GetType()
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Find"
                                         && m.GetParameters().Length == 1
                                         && m.GetParameters()[0].ParameterType == typeof(string));
                if (findMethod == null)
                {
                    AppLogger.Log("[Openness] HardwareCatalog.Find(string) method not found");
                    return results;
                }

                var keyword = (searchTerm ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    AppLogger.Log("[Openness] SearchHardwareCatalog: empty keyword");
                    return results;
                }

                var found = findMethod.Invoke(catalog, new object[] { keyword });
                if (found is not System.Collections.IEnumerable enumerable)
                {
                    AppLogger.Log("[Openness] HardwareCatalog.Find returned non-enumerable result");
                    return results;
                }

                foreach (var entry in enumerable)
                {
                    if (entry == null) continue;

                    // Use dynamic to read entry properties (compatible across TIA versions).
                    string name = "", typeId = "", orderNo = "", version = "";
                    try
                    {
                        dynamic d = entry;
                        try { name = d.Name?.ToString() ?? ""; } catch { }
                        try { typeId = d.TypeIdentifier?.ToString() ?? ""; } catch { }
                        try { orderNo = d.ArticleNumber?.ToString() ?? ""; } catch { }
                        try { version = d.Version?.ToString() ?? ""; } catch { }
                    }
                    catch
                    {
                        continue;
                    }

                    // Dedup by TypeIdentifier (primary) or Name (fallback).
                    var key = !string.IsNullOrWhiteSpace(typeId) ? typeId : name;
                    if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;

                    results.Add(new HardwareCatalogItem
                    {
                        Name = name,
                        TypeIdentifier = typeId,
                        OrderNumber = orderNo,
                        Version = version
                    });

                    if (results.Count >= 50) break;
                }

                AppLogger.Log($"[Openness] HardwareCatalog search for '{keyword}' returned {results.Count} result(s)");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] SearchHardwareCatalog failed: {ex.Message}");
            }

            return results;
        }

        public async Task<CrossReferenceResult> GetCrossReferencesAsync(string variableName, string blockName = null)
        {
            await Task.CompletedTask;

            var result = new CrossReferenceResult
            {
                VariableName = variableName
            };

            if (PlcSoftware == null)
            {
                AppLogger.Log("[Openness] GetCrossReferences: PlcSoftware not available");
                return result;
            }

            try
            {
                // Try to get the variable type and scope
                if (!string.IsNullOrWhiteSpace(blockName))
                {
                    result.DataType = await FindVariableTypeAsync(variableName, blockName);
                }

                // Use reflection to call TIA Openness CrossReferenceService
                var crossRefEntries = GetCrossReferencesViaReflection(variableName, blockName);
                if (crossRefEntries != null)
                {
                    result.Entries = crossRefEntries
                        .Select(e => new CrossReferenceEntry
                        {
                            SourceName = e.SourceName,
                            SourcePath = e.SourcePath,
                            ReferenceName = e.ReferenceName,
                            ReferencePath = e.ReferencePath,
                            LocationName = e.LocationName,
                            ReferenceLocation = e.ReferenceLocation,
                            ReferenceType = e.ReferenceType,
                            Access = e.Access,
                            VariableName = variableName,
                            BlockName = e.SourceName // block name used for jump-to navigation
                        })
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Openness] GetCrossReferences failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Gets cross-references via reflection onto TIA Openness CrossReferenceService.
        /// Prefers finding the variable object in TagTableGroup and calling CrossRef on it
        /// (whole-project references). Degrades to calling CrossRef on the target block and
        /// filtering by variable name when no Tag object is found.
        /// </summary>
        private List<ModelContextProtocol.CrossReferenceEntry> GetCrossReferencesViaReflection(string variableName, string blockName)
        {
            try
            {
                // ── Strategy 1: call CrossRef on the Tag object (whole-project level, most precise) ──
                var tagObject = FindTagInTagTableGroup(variableName);
                if (tagObject != null)
                {
                    var tagCrossRefService = GetServiceByTypeSuffix(tagObject, "CrossReferenceService");
                    if (tagCrossRefService == null)
                        tagCrossRefService = GetServiceByTypeSuffix(tagObject, "CrossReferenceProvider");

                    if (tagCrossRefService != null)
                    {
                        var tagResult = InvokeGetCrossReferences(tagCrossRefService, "AllObjects");
                        if (tagResult != null)
                        {
                            var entries = FlattenCrossReferenceResult(tagResult, null); // no filter; return everything
                            if (entries.Count > 0)
                            {
                                AppLogger.Log($"[CrossRef] Tag-level query for '{variableName}': {entries.Count} refs");
                                return entries;
                            }
                        }
                    }
                }

                // ── Strategy 2: call CrossRef on the block, then filter by variable name (fallback) ──
                object targetBlock = null;
                if (!string.IsNullOrWhiteSpace(blockName))
                {
                    targetBlock = FindBlockInGroup(PlcSoftware.BlockGroup, blockName);
                }

                if (targetBlock == null)
                {
                    targetBlock = PlcSoftware.BlockGroup;
                }

                if (targetBlock == null) return new List<ModelContextProtocol.CrossReferenceEntry>();

                var crossRefService = GetServiceByTypeSuffix(targetBlock, "CrossReferenceService");
                if (crossRefService == null) return new List<ModelContextProtocol.CrossReferenceEntry>();

                var result = InvokeGetCrossReferences(crossRefService, "AllObjects");
                if (result == null) return new List<ModelContextProtocol.CrossReferenceEntry>();

                return FlattenCrossReferenceResult(result, variableName);
            }
            catch
            {
                return new List<ModelContextProtocol.CrossReferenceEntry>();
            }
        }

        /// <summary>
        /// Recursively searches PlcSoftware.TagTableGroup for the Tag object with the given name.
        /// This is the correct target object for variable-level cross-reference queries.
        /// </summary>
        private object FindTagInTagTableGroup(string variableName)
        {
            if (PlcSoftware == null || string.IsNullOrWhiteSpace(variableName))
                return null;

            try
            {
                // Access TagTableGroup via dynamic (avoid strong-typed dependency on a specific TIA version)
                dynamic plc = PlcSoftware;
                object tagTableGroup = null;
                try { tagTableGroup = plc.TagTableGroup; } catch { }
                if (tagTableGroup == null) return null;

                return FindTagInGroupRecursive(tagTableGroup, variableName);
            }
            catch
            {
                return null;
            }
        }

        private static object FindTagInGroupRecursive(object group, string variableName)
        {
            if (group == null) return null;

            try
            {
                // Search TagTables
                var tagTablesProp = group.GetType().GetProperty("TagTables")
                    ?? group.GetType().GetProperty("Tables");
                if (tagTablesProp != null)
                {
                    var tables = tagTablesProp.GetValue(group) as System.Collections.IEnumerable;
                    if (tables != null)
                    {
                        foreach (var table in tables)
                        {
                            var tag = FindTagInTable(table, variableName);
                            if (tag != null) return tag;
                        }
                    }
                }

                // Recurse into subgroups
                var groupsProp = group.GetType().GetProperty("Groups");
                if (groupsProp != null)
                {
                    var groups = groupsProp.GetValue(group) as System.Collections.IEnumerable;
                    if (groups != null)
                    {
                        foreach (var subGroup in groups)
                        {
                            var tag = FindTagInGroupRecursive(subGroup, variableName);
                            if (tag != null) return tag;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static object FindTagInTable(object table, string variableName)
        {
            if (table == null) return null;
            try
            {
                var tagsProp = table.GetType().GetProperty("Tags");
                if (tagsProp == null) return null;

                var tags = tagsProp.GetValue(table) as System.Collections.IEnumerable;
                if (tags == null) return null;

                foreach (var tag in tags)
                {
                    var nameProp = tag.GetType().GetProperty("Name");
                    if (nameProp == null) continue;
                    var name = nameProp.GetValue(tag)?.ToString();
                    if (string.Equals(name, variableName, StringComparison.OrdinalIgnoreCase))
                        return tag;
                }
            }
            catch { }

            return null;
        }

        private static object FindBlockInGroup(object group, string blockName)
        {
            try
            {
                var blocksProp = group.GetType().GetProperty("Blocks");
                if (blocksProp == null) return null;
                var blocks = blocksProp.GetValue(group) as System.Collections.IEnumerable;
                if (blocks == null) return null;

                foreach (var block in blocks)
                {
                    var nameProp = block.GetType().GetProperty("Name");
                    if (nameProp == null) continue;
                    var name = nameProp.GetValue(block)?.ToString();
                    if (string.Equals(name, blockName, StringComparison.OrdinalIgnoreCase))
                        return block;
                }

                // Recurse into subgroups
                var groupsProp = group.GetType().GetProperty("Groups");
                if (groupsProp != null)
                {
                    var groups = groupsProp.GetValue(group) as System.Collections.IEnumerable;
                    if (groups != null)
                    {
                        foreach (var subGroup in groups)
                        {
                            var found = FindBlockInGroup(subGroup, blockName);
                            if (found != null) return found;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static object GetServiceByTypeSuffix(object target, string serviceTypeNameSuffix)
        {
            try
            {
                var getService = target.GetType()
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getService == null) return null;

                var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t.Name.Equals(serviceTypeNameSuffix, StringComparison.OrdinalIgnoreCase) ||
                                         (t.FullName?.EndsWith("." + serviceTypeNameSuffix, StringComparison.OrdinalIgnoreCase) == true));
                if (serviceType == null) return null;

                return getService.MakeGenericMethod(serviceType).Invoke(target, Array.Empty<object>());
            }
            catch { return null; }
        }

        private static object InvokeGetCrossReferences(object crossReferenceService, string filterName)
        {
            try
            {
                var svcType = crossReferenceService.GetType();
                var filterType = svcType.Assembly.GetTypes()
                    .FirstOrDefault(t => t.IsEnum && t.Name.Equals("CrossReferenceFilter", StringComparison.OrdinalIgnoreCase));
                if (filterType == null) return null;

                var filterValue = Enum.Parse(filterType, filterName, ignoreCase: true);
                var m = svcType.GetMethod("GetCrossReferences", new[] { filterType });
                if (m == null) return null;

                return m.Invoke(crossReferenceService, new[] { filterValue });
            }
            catch { return null; }
        }

        private static List<ModelContextProtocol.CrossReferenceEntry> FlattenCrossReferenceResult(object crossReferenceResult, string variableFilter)
        {
            var items = new List<ModelContextProtocol.CrossReferenceEntry>();
            try
            {
                var sources = crossReferenceResult.GetType().GetProperty("Sources")?.GetValue(crossReferenceResult) as System.Collections.IEnumerable;
                if (sources == null) return items;

                foreach (var src in sources)
                {
                    if (src == null) continue;
                    var srcName = src.GetType().GetProperty("Name")?.GetValue(src)?.ToString();
                    var srcPath = src.GetType().GetProperty("Path")?.GetValue(src)?.ToString() ?? "";

                    var refs = src.GetType().GetProperty("References")?.GetValue(src) as System.Collections.IEnumerable;
                    if (refs == null) continue;

                    foreach (var rf in refs)
                    {
                        if (rf == null) continue;
                        var refName = rf.GetType().GetProperty("Name")?.GetValue(rf)?.ToString();
                        var refPath = rf.GetType().GetProperty("Path")?.GetValue(rf)?.ToString();

                        var locations = rf.GetType().GetProperty("Locations")?.GetValue(rf) as System.Collections.IEnumerable;
                        if (locations == null)
                        {
                            items.Add(new ModelContextProtocol.CrossReferenceEntry
                            {
                                SourceName = srcName,
                                SourcePath = srcPath,
                                ReferenceName = refName,
                                ReferencePath = refPath
                            });
                            continue;
                        }

                        foreach (var loc in locations)
                        {
                            if (loc == null) continue;
                            var locName = loc.GetType().GetProperty("Name")?.GetValue(loc)?.ToString();
                            var refNameAtLoc = loc.GetType().GetProperty("ReferenceName")?.GetValue(loc)?.ToString() ?? refName;

                            // Filter by variable name
                            if (!string.IsNullOrWhiteSpace(variableFilter) &&
                                !string.IsNullOrWhiteSpace(refNameAtLoc) &&
                                !refNameAtLoc.Contains(variableFilter))
                                continue;

                            items.Add(new ModelContextProtocol.CrossReferenceEntry
                            {
                                SourceName = srcName,
                                SourcePath = srcPath,
                                ReferenceName = refNameAtLoc,
                                ReferencePath = refPath,
                                LocationName = locName,
                                ReferenceLocation = loc.GetType().GetProperty("ReferenceLocation")?.GetValue(loc)?.ToString(),
                                ReferenceType = loc.GetType().GetProperty("ReferenceType")?.GetValue(loc)?.ToString(),
                                Access = loc.GetType().GetProperty("Access")?.GetValue(loc)?.ToString()
                            });
                        }
                    }
                }
            }
            catch { }
            return items;
        }
    }

    /// <summary>
    /// Event args for <see cref="OpennessService.BlockWritten"/>.
    /// Carries the name, type, and a pre-import XML snapshot of the block so callers
    /// can store it for per-message undo in the chat UI.
    /// </summary>
    public sealed class BlockWrittenEventArgs : EventArgs
    {
        public string BlockName    { get; }
        public string BlockType    { get; }
        /// <summary>TIA XML export captured before the write-back. Null for brand-new blocks.</summary>
        public string SnapshotXml  { get; }

        public BlockWrittenEventArgs(string blockName, string blockType, string snapshotXml)
        {
            BlockName   = blockName   ?? "";
            BlockType   = blockType   ?? "";
            SnapshotXml = snapshotXml;
        }
    }
}
