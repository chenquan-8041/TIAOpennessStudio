using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace TIAOpennessStudio.LadRendering.ViewModels
{
    /// <summary>
    /// Base class for LAD graph view models with INotifyPropertyChanged.
    /// </summary>
    public abstract class LadObservable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }

        protected void Raise([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// A single connector (pin) on a node. The <see cref="Anchor"/> is written back
    /// by Nodify's NodeInput/NodeOutput control via a OneWayToSource binding, so it
    /// always reflects the connector's real rendered position. Connections bind their
    /// endpoints to this Anchor, so wires follow nodes automatically (incl. dragging).
    /// </summary>
    public sealed class LadConnectorViewModel : LadObservable
    {
        private Point _anchor;
        /// <summary>Absolute canvas position of this pin, reported by Nodify.</summary>
        public Point Anchor
        {
            get => _anchor;
            set => Set(ref _anchor, value);
        }

        /// <summary>Pin name (e.g. "in", "out", "en", "eno", or a Call parameter name).</summary>
        public string Title { get; set; } = "";

        /// <summary>True for left-side input pins, false for right-side output pins.</summary>
        public bool IsInput { get; set; }

        /// <summary>Owning node UID (for diagnostics / edit routing).</summary>
        public string NodeUid { get; set; } = "";
    }

    /// <summary>
    /// A node in the ladder network. Location drives the ItemContainer position;
    /// Inputs/Outputs feed the Node's connector item templates.
    /// </summary>
    public sealed class LadNodeViewModel : LadObservable
    {
        private Point _location;
        /// <summary>Top-left position on the editor canvas.</summary>
        public Point Location
        {
            get => _location;
            set => Set(ref _location, value);
        }

        /// <summary>Stable UID from the TIA XML (Part.Uid).</summary>
        public string Uid { get; set; } = "";

        /// <summary>Normalized kind: contact, coil, scoil, rcoil, call, box, ...</summary>
        public string Kind { get; set; } = "";

        /// <summary>Primary label (variable / operand / block name).</summary>
        public string Title { get; set; } = "";

        /// <summary>Secondary label (e.g. FB/FC type for Call blocks).</summary>
        public string Subtitle { get; set; } = "";

        /// <summary>True for negated contacts/coils (drawn with a slash).</summary>
        public bool IsNegated { get; set; }

        public double Width { get; set; }
        public double Height { get; set; }

        public ObservableCollection<LadConnectorViewModel> Inputs { get; } = new ObservableCollection<LadConnectorViewModel>();
        public ObservableCollection<LadConnectorViewModel> Outputs { get; } = new ObservableCollection<LadConnectorViewModel>();

        // Convenience flags for DataTemplate selection / triggers.
        public bool IsContact => Kind == "contact" || Kind == "pcontact" || Kind == "ncontact" || Kind == "not_contact";
        public bool IsCoil => Kind == "coil" || Kind == "scoil" || Kind == "rcoil" || Kind == "pcoil" || Kind == "ncoil";
        public bool IsBlock => !IsContact && !IsCoil;
    }

    /// <summary>
    /// A wire between two connectors. Source/Target reference the connector VMs;
    /// the connection template binds to Source.Anchor / Target.Anchor.
    /// </summary>
    public sealed class LadConnectionViewModel : LadObservable
    {
        public LadConnectorViewModel Source { get; set; }
        public LadConnectorViewModel Target { get; set; }

        /// <summary>True for the left power-rail feed / right rail; used to style rails differently.</summary>
        public bool IsRail { get; set; }
    }

    /// <summary>
    /// One ladder network (CompileUnit). Bound to a NodifyEditor's ItemsSource/Connections.
    /// </summary>
    public sealed class LadNetworkViewModel : LadObservable
    {
        public int Index { get; set; }
        public string Title { get; set; } = "";

        public ObservableCollection<LadNodeViewModel> Nodes { get; } = new ObservableCollection<LadNodeViewModel>();
        public ObservableCollection<LadConnectionViewModel> Connections { get; } = new ObservableCollection<LadConnectionViewModel>();
    }

    /// <summary>
    /// Whole block = ordered list of networks. Root DataContext for the LAD view.
    /// </summary>
    public sealed class LadDocumentViewModel : LadObservable
    {
        public ObservableCollection<LadNetworkViewModel> Networks { get; } = new ObservableCollection<LadNetworkViewModel>();
    }
}
