using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Prism.Commands;
using Prism.Mvvm;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Models.Dependency;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.ViewModels
{
    public sealed class DependencyAnalysisViewModel : BindableBase
    {
        // Data source: the knowledge Worker's structured call/symbol index
        // (plc_call_relation / plc_symbol_doc), the same DB the Agent MCP tools query.
        // Replaces the old JSON-snapshot path, which was never generated at runtime.
        private readonly IProjectKnowledgeWorkerHostService _workerHost;
        private CancellationTokenSource _cts;

        public DependencyAnalysisViewModel(IProjectKnowledgeWorkerHostService workerHost)
        {
            _workerHost = workerHost ?? throw new ArgumentNullException(nameof(workerHost));

            AnalyzeCallChainCommand = new DelegateCommand(async () => await AnalyzeCallChainAsync());
            AnalyzeVariableCommand = new DelegateCommand(async () => await AnalyzeVariableImpactAsync());
            ExpandNodeCommand = new DelegateCommand<CallChainNode>(node => { /* TreeView handles expansion */ });
        }

        // ---- Commands ----

        public DelegateCommand AnalyzeCallChainCommand { get; }
        public DelegateCommand AnalyzeVariableCommand { get; }
        public DelegateCommand<CallChainNode> ExpandNodeCommand { get; }

        // ---- Query Input Properties ----

        private string _blockName = "";
        public string BlockName
        {
            get => _blockName;
            set => SetProperty(ref _blockName, value);
        }

        private string _variablePath = "";
        public string VariablePath
        {
            get => _variablePath;
            set => SetProperty(ref _variablePath, value);
        }

        private string _projectKey = "";
        public string ProjectKey
        {
            get => _projectKey;
            set => SetProperty(ref _projectKey, value);
        }

        private int _maxDepth = 5;
        public int MaxDepth
        {
            get => _maxDepth;
            set => SetProperty(ref _maxDepth, value);
        }

        private int _maxNodeCount = 200;
        public int MaxNodeCount
        {
            get => _maxNodeCount;
            set => SetProperty(ref _maxNodeCount, value);
        }

        private bool _includeCallers = true;
        public bool IncludeCallers
        {
            get => _includeCallers;
            set => SetProperty(ref _includeCallers, value);
        }

        private bool _includeCallees = true;
        public bool IncludeCallees
        {
            get => _includeCallees;
            set => SetProperty(ref _includeCallees, value);
        }

        // ---- Result Properties ----

        private ObservableCollection<CallChainNode> _callChainCallers = new ObservableCollection<CallChainNode>();
        public ObservableCollection<CallChainNode> CallChainCallers
        {
            get => _callChainCallers;
            set => SetProperty(ref _callChainCallers, value);
        }

        private ObservableCollection<CallChainNode> _callChainCallees = new ObservableCollection<CallChainNode>();
        public ObservableCollection<CallChainNode> CallChainCallees
        {
            get => _callChainCallees;
            set => SetProperty(ref _callChainCallees, value);
        }

        private ObservableCollection<VariableImpactNode> _impactNodes = new ObservableCollection<VariableImpactNode>();
        public ObservableCollection<VariableImpactNode> ImpactNodes
        {
            get => _impactNodes;
            set => SetProperty(ref _impactNodes, value);
        }

        private List<string> _cycleBlocks = new List<string>();
        public List<string> CycleBlocks
        {
            get => _cycleBlocks;
            set => SetProperty(ref _cycleBlocks, value);
        }

        private string _resultNote = "";
        public string ResultNote
        {
            get => _resultNote;
            set => SetProperty(ref _resultNote, value);
        }

        private string _statusText = "Ready. Enter a block name or variable path and click Analyze.";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        private bool _hasResults;
        public bool HasResults
        {
            get => _hasResults;
            set => SetProperty(ref _hasResults, value);
        }

        private int _totalNodes;
        public int TotalNodes
        {
            get => _totalNodes;
            set => SetProperty(ref _totalNodes, value);
        }

        private int _maxDepthReached;
        public int MaxDepthReached
        {
            get => _maxDepthReached;
            set => SetProperty(ref _maxDepthReached, value);
        }

        private bool _wasTruncated;
        public bool WasTruncated
        {
            get => _wasTruncated;
            set => SetProperty(ref _wasTruncated, value);
        }

        // ---- Public API (called from code-behind) ----

        public async Task SetBlockNameAsync(string blockName)
        {
            BlockName = blockName;
            VariablePath = "";
            await AnalyzeCallChainAsync();
        }

        public async Task SetVariablePathAsync(string variablePath)
        {
            VariablePath = variablePath;
            BlockName = "";
            await AnalyzeVariableImpactAsync();
        }

        // ---- Analysis Methods ----

        private async Task AnalyzeCallChainAsync()
        {
            if (string.IsNullOrWhiteSpace(BlockName))
            {
                StatusText = "Please enter a block name.";
                return;
            }

            CancelPending();
            _cts = new CancellationTokenSource();
            IsBusy = true;
            HasResults = false;
            StatusText = $"Analyzing call chain for '{BlockName}'...";

            try
            {
                var direction = ResolveDirection();
                var result = await _workerHost.QueryCallChainAsync(
                    BlockName, direction, MaxDepth, MaxNodeCount,
                    ProjectKnowledgeFreshnessModes.CachePreferred, _cts.Token);

                var callees = BuildTree(BlockName, result.Nodes, "callee");
                var callers = BuildTree(BlockName, result.Nodes, "caller");

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    CallChainCallees = new ObservableCollection<CallChainNode>(callees);
                    CallChainCallers = new ObservableCollection<CallChainNode>(callers);
                    ImpactNodes = new ObservableCollection<VariableImpactNode>();
                    CycleBlocks = result.CycleBlocks ?? new List<string>();
                    ResultNote = result.Note ?? "";
                    TotalNodes = result.TotalNodes;
                    MaxDepthReached = result.MaxDepthReached;
                    WasTruncated = result.WasTruncated;
                    HasResults = result.TotalNodes > 0;

                    StatusText = result.WasTruncated
                        ? $"Call chain complete (truncated). {result.TotalNodes} nodes, depth {result.MaxDepthReached}."
                        : $"Call chain complete. {result.TotalNodes} nodes, depth {result.MaxDepthReached}.";

                    if (CycleBlocks.Count > 0)
                        StatusText += $" Cycles detected: {string.Join(", ", CycleBlocks)}";
                });
            }
            catch (OperationCanceledException)
            {
                StatusText = "Analysis cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Analysis failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task AnalyzeVariableImpactAsync()
        {
            if (string.IsNullOrWhiteSpace(VariablePath))
            {
                StatusText = "Please enter a variable path (e.g. DB_Main.Motor.Speed).";
                return;
            }

            CancelPending();
            _cts = new CancellationTokenSource();
            IsBusy = true;
            HasResults = false;
            StatusText = $"Analyzing impact for '{VariablePath}'...";

            try
            {
                var direction = ResolveDirection();
                var result = await _workerHost.QueryVariableImpactAsync(
                    VariablePath, direction, MaxDepth, MaxNodeCount,
                    ProjectKnowledgeFreshnessModes.CachePreferred, _cts.Token);

                var impact = result.Nodes.Select(n => new VariableImpactNode
                {
                    BlockName = n.BlockName,
                    BlockType = n.BlockType,
                    // Depth 0 = direct reference of the variable; deeper = reached via call graph.
                    AccessKind = n.Depth == 0 ? "Ref" : (n.Relation == "caller" ? "Caller" : "Callee"),
                    NetworkIndex = n.NetworkIndex,
                    Depth = n.Depth,
                    CanonicalPath = n.Depth == 0 ? VariablePath : string.Join(" → ", n.Path ?? new List<string>())
                }).ToList();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ImpactNodes = new ObservableCollection<VariableImpactNode>(impact);
                    CallChainCallers = new ObservableCollection<CallChainNode>();
                    CallChainCallees = new ObservableCollection<CallChainNode>();
                    CycleBlocks = result.CycleBlocks ?? new List<string>();
                    ResultNote = result.Note ?? "";
                    TotalNodes = result.TotalNodes;
                    MaxDepthReached = result.MaxDepthReached;
                    WasTruncated = result.WasTruncated;
                    HasResults = result.Nodes.Count > 0;

                    StatusText = result.WasTruncated
                        ? $"Impact analysis complete (truncated). {result.TotalNodes} nodes, depth {result.MaxDepthReached}."
                        : $"Impact analysis complete. {result.TotalNodes} nodes, depth {result.MaxDepthReached}.";
                });
            }
            catch (OperationCanceledException)
            {
                StatusText = "Analysis cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Analysis failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private string ResolveDirection()
        {
            if (IncludeCallers && !IncludeCallees) return "caller";
            if (IncludeCallees && !IncludeCallers) return "callee";
            return "both";
        }

        /// <summary>
        /// Rebuild a UI tree (CallChainNode.Children) from the flat traversal result,
        /// filtered to one relation ("callee" downstream / "caller" upstream). Each node
        /// carries its full Path from the root, which lets us reconstruct parent/child
        /// links without the server returning a nested structure.
        /// </summary>
        private static List<CallChainNode> BuildTree(string root, List<DependencyChainNode> nodes, string relation)
        {
            if (nodes == null || nodes.Count == 0) return new List<CallChainNode>();

            var relevant = nodes.Where(n => n.Relation == relation).ToList();
            if (relevant.Count == 0) return new List<CallChainNode>();

            // Map "path string" -> tree node, so children can find their parent by the
            // path prefix (path without the last element).
            var byPath = new Dictionary<string, CallChainNode>(StringComparer.OrdinalIgnoreCase);
            var rootNode = new CallChainNode { BlockName = root, BlockType = "", Depth = 0 };
            byPath[root] = rootNode;

            // Ensure parents are created before children (shorter paths first).
            foreach (var n in relevant.OrderBy(x => x.Path?.Count ?? 0))
            {
                var path = n.Path ?? new List<string> { n.BlockName };
                var key = string.Join("/", path);
                if (byPath.ContainsKey(key)) continue;

                var node = new CallChainNode
                {
                    BlockName = n.BlockName,
                    BlockType = n.BlockType,
                    Depth = n.Depth,
                    NetworkIndex = n.NetworkIndex,
                    SourceRef = n.SourceRef
                };
                byPath[key] = node;

                // Parent = path minus last segment.
                var parentKey = path.Count > 1 ? string.Join("/", path.Take(path.Count - 1)) : root;
                if (!byPath.TryGetValue(parentKey, out var parent))
                    parent = rootNode; // fallback: attach to root if parent path missing
                parent.Children.Add(node);
            }

            // The UI shows the root as the top of the tree.
            return new List<CallChainNode> { rootNode };
        }

        private void CancelPending()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }
    }
}
