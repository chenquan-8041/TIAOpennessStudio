using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using Prism.Events;
using Prism.Mvvm;
using TIAOpennessStudio.Events;

namespace TIAOpennessStudio.ViewModels
{
    public class TreeViewModel : BindableBase
    {
        private readonly ITiaOpennessService _tiaService;
        private readonly IEventAggregator _eventAggregator;
        
        // UI control references (decoupled via events)
        private ObservableCollection<string> _devices = new ObservableCollection<string>();
        private ObservableCollection<PlcItem> _plcList = new ObservableCollection<PlcItem>();
        private ObservableCollection<HmiItem> _hmiList = new ObservableCollection<HmiItem>();
        private PlcItem _selectedPlc;
        
        // Block counts
        private int _obCount;
        private int _fbCount;
        private int _fcCount;
        private int _dbCount;
        private int _typeCount;
        
        // Status
        private string _projectStatus;
        private string _saveStatus;
        
        public ObservableCollection<string> Devices
        {
            get => _devices;
            set => SetProperty(ref _devices, value);
        }
        
        public ObservableCollection<PlcItem> PlcList
        {
            get => _plcList;
            set => SetProperty(ref _plcList, value);
        }

        /// <summary>HMI software (classic / unified) found in the open project; carries opaque handles.</summary>
        public ObservableCollection<HmiItem> HmiList
        {
            get => _hmiList;
            set => SetProperty(ref _hmiList, value);
        }
        
        public PlcItem SelectedPlc
        {
            get => _selectedPlc;
            set
            {
                if (SetProperty(ref _selectedPlc, value))
                {
                    OnSelectedPlcChanged();
                }
            }
        }
        
        public int ObCount
        {
            get => _obCount;
            set => SetProperty(ref _obCount, value);
        }
        
        public int FbCount
        {
            get => _fbCount;
            set => SetProperty(ref _fbCount, value);
        }
        
        public int FcCount
        {
            get => _fcCount;
            set => SetProperty(ref _fcCount, value);
        }
        
        public int DbCount
        {
            get => _dbCount;
            set => SetProperty(ref _dbCount, value);
        }
        
        public int TypeCount
        {
            get => _typeCount;
            set => SetProperty(ref _typeCount, value);
        }
        
        public string ProjectStatus
        {
            get => _projectStatus;
            set => SetProperty(ref _projectStatus, value);
        }
        
        public string SaveStatus
        {
            get => _saveStatus;
            set => SetProperty(ref _saveStatus, value);
        }
        
        public TreeViewModel(ITiaOpennessService tiaService, IEventAggregator eventAggregator)
        {
            _tiaService = tiaService ?? throw new ArgumentNullException(nameof(tiaService));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        }
        
        /// <summary>
        /// Populate the device list and PLC list from service-side DTOs. The device/PLC
        /// traversal (COM) runs on the STA host thread inside OpennessService.GetDeviceTreeAsync;
        /// the ViewModel only stores plain names + opaque handles. The legacy Project overload is
        /// kept as a thin adapter so existing callers compile.
        /// </summary>
        public async System.Threading.Tasks.Task IterateDevicesAsync()
        {
            try
            {
                // The device traversal (COM) runs on the STA host; its continuation may resume on
                // the STA thread, so marshal all ObservableCollection writes back to the UI
                // dispatcher (CollectionView only allows changes from the dispatcher thread).
                var devices = await _tiaService.GetDeviceTreeAsync();

                RunOnUi(() =>
                {
                    Devices.Clear();
                    PlcList.Clear();
                    HmiList.Clear();

                    if (devices == null || devices.Count == 0)
                    {
                        _eventAggregator.GetEvent<ShowMessageEvent>().Publish("No devices found in the project. Please ensure a project with devices is open.");
                        return;
                    }

                    foreach (var dev in devices)
                    {
                        Devices.Add(dev.Name);
                        AddPlcItemsFromDto(dev);
                        AddHmiItemsFromDto(dev);
                    }

                    _eventAggregator.GetEvent<UpdateProjectStatusEvent>().Publish();
                    _eventAggregator.GetEvent<RefreshBlockCountsEvent>().Publish();
                    _eventAggregator.GetEvent<UpdateMenuEnableStatesEvent>().Publish();
                });
            }
            catch (Exception ex)
            {
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish($"Error iterating devices: {ex.Message}");
            }
        }

        /// <summary>
        /// Run an action on the WPF UI dispatcher thread. Runs inline when already on the UI
        /// thread; otherwise posts asynchronously (BeginInvoke) so the caller — which may be the
        /// STA host thread — is NOT blocked waiting on the UI thread. Blocking here would create a
        /// UI↔STA deadlock when the UI work in turn marshals back to the STA host.
        /// </summary>
        private static void RunOnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(action);
        }

        /// <summary>Legacy adapter: the Project COM argument is ignored; data comes from DTOs.</summary>
        public void IterateDevices(Project project) => _ = IterateDevicesAsync();

        private void AddPlcItemsFromDto(DeviceNodeDto node)
        {
            if (node == null) return;
            if (node.IsPlc && !string.IsNullOrEmpty(node.PlcHandle))
            {
                PlcList.Add(new PlcItem { Name = node.Name, Handle = node.PlcHandle });
            }
            foreach (var child in node.Children)
                AddPlcItemsFromDto(child);
        }

        private void AddHmiItemsFromDto(DeviceNodeDto node)
        {
            if (node == null) return;
            if (node.IsHmi && !string.IsNullOrEmpty(node.HmiHandle))
            {
                HmiList.Add(new HmiItem { Name = node.Name, Handle = node.HmiHandle, Kind = node.HmiKind });
            }
            foreach (var child in node.Children)
                AddHmiItemsFromDto(child);
        }

        private async void OnSelectedPlcChanged()
        {
            var plc = SelectedPlc;
            if (plc == null || string.IsNullOrEmpty(plc.Handle)) return;

            // Resolve + set the active PlcSoftware on the STA host thread (no COM on the UI thread),
            // then rebuild the tree from DTOs via LoadTreeEvent (parameterless).
            bool ok = await _tiaService.SelectPlcByHandleAsync(plc.Handle);
            if (!ok) return;

            // Publish on the UI dispatcher: the await continuation may resume on the STA thread,
            // and the LoadTreeEvent subscriber touches the WPF tree.
            RunOnUi(() =>
            {
                _eventAggregator.GetEvent<LoadTreeEvent>().Publish();
                _eventAggregator.GetEvent<RefreshBlockCountsEvent>().Publish();
                _eventAggregator.GetEvent<UpdateMenuEnableStatesEvent>().Publish();
            });
        }
        
        /// <summary>
        /// DTO-based block counting. Counts are computed on the STA host thread inside
        /// OpennessService and returned as a plain-data DTO, so no COM object is touched
        /// on the UI thread. The legacy PlcSoftware overload is kept as a thin adapter.
        /// </summary>
        public async void UpdateBlockCounts()
        {
            try
            {
                // Fully async: await the STA-side DTO without blocking the UI thread. Blocking
                // (.GetAwaiter().GetResult()) on the UI thread while the STA host is busy — or
                // vice-versa — is what caused the connect/select hang.
                var counts = await _tiaService.GetBlockCountsAsync();
                RunOnUi(() =>
                {
                    ObCount = counts?.Ob ?? 0;
                    FbCount = counts?.Fb ?? 0;
                    FcCount = counts?.Fc ?? 0;
                    DbCount = counts?.Db ?? 0;
                    TypeCount = counts?.Type ?? 0;
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[TreeViewModel] UpdateBlockCounts failed: {ex.Message}");
                RunOnUi(() => { ObCount = FbCount = FcCount = DbCount = TypeCount = 0; });
            }
        }

        public void UpdateBlockCounts(PlcSoftware software)
        {
            // Legacy adapter: ignore the passed COM object and use the service's current
            // PlcSoftware via the STA-safe DTO path.
            if (software == null)
            {
                ObCount = FbCount = FcCount = DbCount = TypeCount = 0;
                return;
            }
            UpdateBlockCounts();
        }
        
        /// <summary>
        /// DTO-based project status. Reads project Name/IsModified on the STA host thread
        /// inside OpennessService and returns a plain-data DTO — no COM touched on the UI
        /// thread. The legacy Project overload is kept as a thin adapter.
        /// </summary>
        public async void UpdateProjectStatus()
        {
            try
            {
                var info = await _tiaService.GetProjectInfoAsync();
                RunOnUi(() =>
                {
                    if (info == null || !info.IsOpen)
                    {
                        ProjectStatus = "No project open";
                        SaveStatus = "...";
                        return;
                    }
                    ProjectStatus = info.Name;
                    SaveStatus = info.IsModified ? "Modified" : "Saved";
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[TreeViewModel] UpdateProjectStatus failed: {ex.Message}");
                RunOnUi(() => { ProjectStatus = "No project open"; SaveStatus = "..."; });
            }
        }

        public void UpdateProjectStatus(Project project)
        {
            // Legacy adapter: ignore the passed COM object, use the STA-safe DTO path.
            UpdateProjectStatus();
        }
        
        private void CountBlocks(PlcBlockGroup group, ref int ob, ref int fb, ref int fc, ref int db)
            => CountBlocks(group, ref ob, ref fb, ref fc, ref db, new HashSet<int>());
        
        private void CountBlocks(PlcBlockGroup group, ref int ob, ref int fb, ref int fc, ref int db, HashSet<int> visited)
        {
            if (group == null) return;
            if (!visited.Add(ObjId(group))) return;
            
            foreach (PlcBlock block in group.Blocks)
            {
                if (block is OB) ob++;
                else if (block is FB) fb++;
                else if (block is FC) fc++;
                else if (block is GlobalDB || block is InstanceDB) db++;
            }
            
            foreach (PlcBlockUserGroup userGroup in group.Groups)
            {
                CountBlocks(userGroup, ref ob, ref fb, ref fc, ref db, visited);
            }
        }
        
        private void CountBlocks(PlcBlockSystemGroup group, ref int ob, ref int fb, ref int fc, ref int db)
            => CountBlocks(group, ref ob, ref fb, ref fc, ref db, new HashSet<int>());
        
        private void CountBlocks(PlcBlockSystemGroup group, ref int ob, ref int fb, ref int fc, ref int db, HashSet<int> visited)
        {
            if (group == null) return;
            if (!visited.Add(ObjId(group))) return;
            
            foreach (PlcBlock block in group.Blocks)
            {
                if (block is OB) ob++;
                else if (block is FB) fb++;
                else if (block is FC) fc++;
                else if (block is GlobalDB || block is InstanceDB) db++;
            }
            
            foreach (PlcBlockUserGroup userGroup in group.Groups)
            {
                CountBlocks(userGroup, ref ob, ref fb, ref fc, ref db, visited);
            }
        }
        
        private void CountTypes(PlcTypeGroup group, ref int types)
            => CountTypes(group, ref types, new HashSet<int>());
        
        private void CountTypes(PlcTypeGroup group, ref int types, HashSet<int> visited)
        {
            if (group == null) return;
            if (!visited.Add(ObjId(group))) return;
            
            types += group.Types.Count;
            foreach (PlcTypeUserGroup userGroup in group.Groups)
            {
                CountTypes(userGroup, ref types, visited);
            }
        }
        
        private void CountTypes(PlcTypeSystemGroup group, ref int types)
            => CountTypes(group, ref types, new HashSet<int>());
        
        private void CountTypes(PlcTypeSystemGroup group, ref int types, HashSet<int> visited)
        {
            if (group == null) return;
            if (!visited.Add(ObjId(group))) return;
            
            types += group.Types.Count;
            foreach (PlcTypeUserGroup userGroup in group.Groups)
            {
                CountTypes(userGroup, ref types, visited);
            }
        }
        
        private static int ObjId(object obj) => RuntimeHelpers.GetHashCode(obj);
        
        public string BuildAiReferenceFromTreeTag(object tag)
        {
            if (tag == null) return string.Empty;
            
            if (tag is PlcBlock block)
            {
                try
                {
                    string exportDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports");
                    if (!Directory.Exists(exportDir)) Directory.CreateDirectory(exportDir);
                    string safeName = string.Join("_", block.Name.Split(Path.GetInvalidFileNameChars())).Trim();
                    string tempFile = Path.Combine(exportDir, safeName + ".xml");
                    if (File.Exists(tempFile)) File.Delete(tempFile);

                    block.Export(new FileInfo(tempFile), ExportOptions.None);
                    string xmlContent = File.ReadAllText(tempFile);
                    if (TiaXmlAgentReferenceBuilder.TryBuildReference(xmlContent, block.Name, out var irReference))
                    {
                        return irReference;
                    }
                }
                catch
                {
                    // Fallback to a compact textual reference when export is not available.
                }

                return $"Block: {block.Name} (Number: {block.Number}, Type: {block.GetType().Name})";
            }
            else if (tag is PlcStruct str)
            {
                return $"Struct: {str.Name}";
            }
            else if (tag is PlcType type)
            {
                return $"Type: {type.Name}";
            }
            else if (tag is PlcBlockGroup group)
            {
                return $"Block Group: {group.Name}";
            }
            else if (tag is PlcTypeGroup typeGroup)
            {
                return $"Type Group: {typeGroup.Name}";
            }
            
            return tag.ToString();
        }
        
        public string GetTitleFromTreeTag(object tag)
        {
            if (tag is PlcBlock b) return b.Name;
            if (tag is PlcStruct st) return st.Name;
            if (tag is PlcType t) return t.Name;
            return "Tree Context";
        }
    }
    
    public class PlcItem : INotifyPropertyChanged
    {
        private string _name;
        private string _handle;
        
        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged();
            }
        }
        
        /// <summary>
        /// Opaque handle to the backing PlcSoftware inside OpennessService. The UI/ViewModel
        /// never holds the COM object itself; selection is resolved on the STA host thread via
        /// ITiaOpennessService.SelectPlcByHandleAsync(Handle).
        /// </summary>
        public string Handle
        {
            get => _handle;
            set
            {
                _handle = value;
                OnPropertyChanged();
            }
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
        
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// A selectable HMI software entry in the sidebar. Like <see cref="PlcItem"/> it carries
    /// only a name + opaque handle; the backing HMI software is resolved on the STA thread.
    /// </summary>
    public class HmiItem : INotifyPropertyChanged
    {
        private string _name;
        private string _handle;
        private string _kind;

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Opaque handle to the backing HMI software (classic HmiTarget / unified HmiSoftware).</summary>
        public string Handle
        {
            get => _handle;
            set
            {
                _handle = value;
                OnPropertyChanged();
            }
        }

        /// <summary>"Classic" or "Unified".</summary>
        public string Kind
        {
            get => _kind;
            set
            {
                _kind = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
