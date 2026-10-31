namespace TIAOpennessStudio.Models.Dependency
{
    public sealed class PlcSymbolReference
    {
        public string CanonicalPath { get; set; }
        public string AccessKind { get; set; }
        public string BlockName { get; set; }
        public int NetworkIndex { get; set; }
        public string Scope { get; set; }
        public string DataType { get; set; }
        public string SourceRef { get; set; }
    }
}
