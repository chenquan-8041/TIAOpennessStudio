using System.Collections.Generic;

namespace ModelContextProtocol
{
    /// <summary>
    /// A cross-reference entry resolved via reflection by the Portal layer.
    /// </summary>
    public class CrossReferenceEntry
    {
        public string SourceName { get; set; }
        public string SourcePath { get; set; }
        public string ReferenceName { get; set; }
        public string ReferencePath { get; set; }
        public string LocationName { get; set; }
        public string ReferenceLocation { get; set; }
        public string ReferenceType { get; set; }
        public string Access { get; set; }
    }
}