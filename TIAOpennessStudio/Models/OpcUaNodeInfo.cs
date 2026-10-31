using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// OPC UA node info that supports a hierarchical tree structure.
    /// </summary>
    public class OpcUaNodeInfo : INotifyPropertyChanged
    {
        private string _nodeId;
        private string _displayName;
        private string _nodeClass;
        private string _dataType;
        private string _parentNodeId;
        private List<OpcUaNodeInfo> _children;
        private bool _isExpanded;
        private int _level;

        /// <summary>
        /// Uniquely identifies the node in the address space.
        /// </summary>
        public string NodeId
        {
            get => _nodeId;
            set { _nodeId = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Display name of the node.
        /// </summary>
        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Node class (Object, Variable, Method, ObjectType, VariableType, DataType, ReferenceType).
        /// </summary>
        public string NodeClass
        {
            get => _nodeClass;
            set { _nodeClass = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Data type (only for Variable nodes, e.g. Boolean, Int32, Double).
        /// </summary>
        public string DataType
        {
            get => _dataType;
            set { _dataType = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Parent node id (null for the root node).
        /// </summary>
        public string ParentNodeId
        {
            get => _parentNodeId;
            set { _parentNodeId = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Child nodes, forming the hierarchical tree.
        /// </summary>
        public List<OpcUaNodeInfo> Children
        {
            get => _children ?? (_children = new List<OpcUaNodeInfo>());
            set { _children = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether the node is expanded in the tree UI.
        /// </summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Depth of the node in the tree (0 for the root).
        /// </summary>
        public int Level
        {
            get => _level;
            set { _level = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
