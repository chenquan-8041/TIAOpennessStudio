using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Abstracts TIA Openness CrossReferenceProvider API access.
    /// Allows the Network-centric knowledge pipeline to enrich
    /// deterministic facts with authoritative cross-reference data
    /// from the live TIA Portal instance.
    /// </summary>
    public interface ICrossReferenceProvider
    {
        /// <summary>
        /// Whether a live TIA Portal connection is available for cross-reference queries.
        /// When false, callers should fall back to XML-inferred data.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Get all cross-references for a specific PLC block.
        /// Returns all objects that reference the block and all objects the block references.
        /// </summary>
        /// <param name="blockName">Block name (e.g. "FC_Control", "DB_Main").</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of cross-reference entries for the block, or empty if unavailable.</returns>
        Task<IReadOnlyList<BlockCrossReference>> GetBlockCrossReferencesAsync(string blockName, CancellationToken ct = default);

        /// <summary>
        /// Batch query: get cross-references for multiple blocks in one pass.
        /// More efficient than calling GetBlockCrossReferencesAsync per block
        /// because it can reuse the same TIA Portal service handle.
        /// </summary>
        /// <param name="blockNames">Block names to query.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Map of blockName → list of cross-references.</returns>
        Task<Dictionary<string, List<BlockCrossReference>>> GetBatchCrossReferencesAsync(
            IReadOnlyList<string> blockNames, CancellationToken ct = default);
    }

    /// <summary>
    /// A single cross-reference entry from the TIA Openness CrossReferenceProvider.
    /// Represents one usage of a symbol in a specific location.
    /// </summary>
    public sealed class BlockCrossReference
    {
        /// <summary>The block that was queried (source of the cross-reference query).</summary>
        public string QueriedBlockName { get; set; }

        /// <summary>The block/object where the reference occurs.</summary>
        public string ReferencingBlockName { get; set; }

        /// <summary>Full path of the referencing object.</summary>
        public string ReferencingPath { get; set; }

        /// <summary>The referenced symbol/variable name.</summary>
        public string ReferencedSymbol { get; set; }

        /// <summary>Location within the referencing block (e.g. network title).</summary>
        public string Location { get; set; }

        /// <summary>Access type: "Read", "Write", "ReadWrite", "Call".</summary>
        public string AccessType { get; set; }

        /// <summary>Reference type from TIA (e.g. "Use", "Assign", "Call").</summary>
        public string ReferenceType { get; set; }

        /// <summary>Raw source name from TIA API.</summary>
        public string RawSourceName { get; set; }

        /// <summary>Raw reference path from TIA API.</summary>
        public string RawReferencePath { get; set; }
    }
}
