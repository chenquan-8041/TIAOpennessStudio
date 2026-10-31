using System.Collections.Generic;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services
{
    // Lightweight snapshot model used by current graph renderer.
    public sealed class GraphSnapshot
    {
        public string XmlContent { get; set; }
        public XDocument Document { get; set; }
        public bool ParseSucceeded { get; set; }
        public bool IsFromCache { get; set; }
        public long ParseElapsedMs { get; set; }
        public List<GraphPinSnapshot> Pins { get; set; } = new List<GraphPinSnapshot>();
    }

    public sealed class GraphPinSnapshot
    {
        public string Uid { get; set; }
        public string Symbol { get; set; }
    }
}
