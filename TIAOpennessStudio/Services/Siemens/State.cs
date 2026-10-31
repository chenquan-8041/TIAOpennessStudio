namespace TIAOpennessStudio.Services.Portal
{
    /// <summary>Snapshot of the TIA Portal connection state.</summary>
    public class State
    {
        public bool? IsConnected { get; set; }
        public string? Project { get; set; }
        public string? Session { get; set; }
    }
}