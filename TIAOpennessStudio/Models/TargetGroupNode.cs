using System.Collections.ObjectModel;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// One node in the target-group picker (BlockGroup or TypeGroup branch).
    /// Path uses backslash separator and is relative to PlcSoftware.BlockGroup / TypeGroup.
    /// IsRoot=true marks the synthetic "Project root" entry.
    /// </summary>
    public class TargetGroupNode
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsRoot { get; set; }
        public ObservableCollection<TargetGroupNode> Children { get; set; } = new ObservableCollection<TargetGroupNode>();
    }

    /// <summary>
    /// Discriminator for the two target trees (FB/FC/DB/OB go under Block, UDT under Type).
    /// </summary>
    public enum TargetGroupKind
    {
        Block,
        Type
    }
}
