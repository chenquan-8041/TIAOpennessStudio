namespace TIAOpennessStudio.Models.Dependency
{
    public sealed class DependencyQueryOptions
    {
        public int MaxDepth { get; set; } = 5;
        public int MaxNodeCount { get; set; } = 200;
        public bool IncludeRead { get; set; } = true;
        public bool IncludeWrite { get; set; } = true;
        public bool IncludeCallers { get; set; } = true;
        public bool IncludeCallees { get; set; } = true;
    }
}
