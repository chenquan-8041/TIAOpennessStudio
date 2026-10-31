namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Language support facade for code formatting and syntax coloring.
    /// </summary>
    public interface ILanguageService
    {
        string LanguageName { get; }
        string FormatCode(string rawCode);
        object GetSyntaxColorizer(); // Can return a custom object depending on the editor
    }
}