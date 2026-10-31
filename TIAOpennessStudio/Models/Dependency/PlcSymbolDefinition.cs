namespace TIAOpennessStudio.Models.Dependency
{
    public sealed class PlcSymbolDefinition
    {
        public string CanonicalPath { get; set; }
        public string OwnerBlock { get; set; }
        public string DefinitionKind { get; set; }
        public string DataType { get; set; }
    }
}
