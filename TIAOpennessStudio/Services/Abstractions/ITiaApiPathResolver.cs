namespace TIAOpennessStudio.Services.Abstractions
{
    public sealed class TiaApiResolution
    {
        public string Version { get; set; }
        public string ApiPath { get; set; }
        public bool FromRegistry { get; set; }
    }

    /// <summary>
    /// Resolves the Openness API assembly path for the installed TIA version.
    /// </summary>
    public interface ITiaApiPathResolver
    {
        TiaApiResolution Resolve(string appBaseDirectory);
    }
}
