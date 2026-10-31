using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TIAOpennessStudio.Models
{
    public enum OpcUaQuality
    {
        Good,
        Uncertain,
        Bad
    }

    public enum OpcUaConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting
    }

    public class OpcUaValueSnapshot : INotifyPropertyChanged
    {
        private string _nodeId;
        private string _displayName;
        private object _value;
        private string _dataType;
        private OpcUaQuality _quality;
        private DateTime? _sourceTimestamp;
        private DateTime? _serverTimestamp;

        public string NodeId
        {
            get => _nodeId;
            set { _nodeId = value; OnPropertyChanged(); }
        }

        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        public object Value
        {
            get => _value;
            set { _value = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayValue)); }
        }

        public string DataType
        {
            get => _dataType;
            set { _dataType = value; OnPropertyChanged(); }
        }

        public OpcUaQuality Quality
        {
            get => _quality;
            set { _quality = value; OnPropertyChanged(); }
        }

        public DateTime? SourceTimestamp
        {
            get => _sourceTimestamp;
            set { _sourceTimestamp = value; OnPropertyChanged(); }
        }

        public DateTime? ServerTimestamp
        {
            get => _serverTimestamp;
            set { _serverTimestamp = value; OnPropertyChanged(); }
        }

        public string DisplayValue => Value?.ToString() ?? "(null)";

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class OpcUaConnectionConfig
    {
        public string EndpointUrl { get; set; } = "opc.tcp://127.0.0.1:4840";
        public string SecurityPolicy { get; set; } = "None";
        public string MessageSecurityMode { get; set; } = "None";
        public string Username { get; set; }
        public string Password { get; set; }
        public bool AutoReconnect { get; set; } = true;
        public int ReconnectIntervalMs { get; set; } = 5000;
        public int ReadTimeoutMs { get; set; } = 3000;
        public bool WriteRequireApproval { get; set; } = true;
    }
}
