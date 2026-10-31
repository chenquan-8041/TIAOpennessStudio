namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// Represents an AI reference item (file drop or tree drag).
    /// </summary>
    public class AiReferenceItem
    {
        public string Id { get; set; }
        public string Source { get; set; }
        public string DisplayName { get; set; }
        public string Packed { get; set; }
    }
}
