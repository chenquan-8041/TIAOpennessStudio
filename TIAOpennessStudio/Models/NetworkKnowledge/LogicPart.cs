using System.Collections.Generic;

namespace TIAOpennessStudio.Models.NetworkKnowledge
{
    /// <summary>
    /// A logic element (instruction) within a LAD Network.
    /// Examples: AND, OR, SR, TON, Move, Compare, Coil, etc.
    /// </summary>
    public sealed class LogicPart
    {
        /// <summary>TIA XML UId.</summary>
        public string Uid { get; set; }

        /// <summary>Instruction name (e.g. "Contact", "Coil", "Move", "TON").</summary>
        public string Name { get; set; }

        /// <summary>Whether this part is negated.</summary>
        public bool Negated { get; set; }

        /// <summary>Template values / configuration (e.g. SrcType).</summary>
        public Dictionary<string, string> TemplateValues { get; set; } = new Dictionary<string, string>();

        /// <summary>Known pin names on this part (populated from Wire analysis).</summary>
        public List<string> InputPins { get; set; } = new List<string>();
        public List<string> OutputPins { get; set; } = new List<string>();
    }
}
