namespace TIAOpennessStudio.Models.Dependency
{
    public sealed class PlcCallEdge
    {
        public string CallerBlock { get; set; }
        public string CalleeBlock { get; set; }
        public string CalleeBlockType { get; set; }
        public string InstancePath { get; set; }
        public int NetworkIndex { get; set; }
        public string SourceRef { get; set; }
    }
}
