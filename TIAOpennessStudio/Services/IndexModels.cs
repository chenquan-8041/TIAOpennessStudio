namespace TIAOpennessStudio.Services
{
    public sealed class ProjectIndexHit
    {
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public string Language { get; set; }
        public string Version { get; set; }
        public string Summary { get; set; }
        public string UpdatedAt { get; set; }
    }

    public sealed class ProjectIndexChange
    {
        public string BlockName { get; set; }
        public string OldVersion { get; set; }
        public string NewVersion { get; set; }
        public string GitBranch { get; set; }
        public string GitCommit { get; set; }
        public string ChangeSummary { get; set; }
    }
}
