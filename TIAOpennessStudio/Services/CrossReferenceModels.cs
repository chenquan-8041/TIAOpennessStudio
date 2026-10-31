using System.Collections.Generic;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// A cross-reference entry: one reference location of a variable in the project.
    /// </summary>
    public class CrossReferenceEntry
    {
        /// <summary>Source name (block/type name).</summary>
        public string SourceName { get; set; }

        /// <summary>Source path.</summary>
        public string SourcePath { get; set; }

        /// <summary>Reference name.</summary>
        public string ReferenceName { get; set; }

        /// <summary>Reference path.</summary>
        public string ReferencePath { get; set; }

        /// <summary>Location name (e.g. network name).</summary>
        public string LocationName { get; set; }

        /// <summary>Reference location description.</summary>
        public string ReferenceLocation { get; set; }

        /// <summary>Reference type (Read/Write/ReadWrite).</summary>
        public string ReferenceType { get; set; }

        /// <summary>Access type.</summary>
        public string Access { get; set; }

        /// <summary>Variable name (extracted from the reference).</summary>
        public string VariableName { get; set; }

        /// <summary>Scope (GlobalVariable/LocalVariable/TypedConstant).</summary>
        public string Scope { get; set; }

        // ---- Navigation context (for jump-to-reference) ----

        /// <summary>The name of the block containing the reference (used for navigation).</summary>
        public string BlockName { get; set; }

        /// <summary>The block type containing the reference (OB/FB/FC/DB).</summary>
        public string BlockType { get; set; }

        /// <summary>The 1-based Network index containing the reference (0 means unknown).</summary>
        public int NetworkIndex { get; set; }

        /// <summary>The title of the Network containing the reference.</summary>
        public string NetworkTitle { get; set; }

        /// <summary>The comment of the Network containing the reference.</summary>
        public string NetworkComment { get; set; }

        /// <summary>Short description for display (with block name and location).</summary>
        public string DisplayText
        {
            get
            {
                // Show a more specific location when block name and Network are available.
                if (!string.IsNullOrWhiteSpace(BlockName) && NetworkIndex > 0)
                {
                    var netLabel = string.IsNullOrWhiteSpace(NetworkTitle)
                        ? $"NW{NetworkIndex}"
                        : $"NW{NetworkIndex}: {NetworkTitle}";
                    return $"{BlockName} → {netLabel} ({ReferenceType ?? "?"})";
                }

                if (!string.IsNullOrWhiteSpace(BlockName))
                    return $"{BlockName} - {LocationName ?? ReferenceLocation ?? "?"} ({ReferenceType ?? "?"})";

                return $"{SourceName ?? "?"} - {LocationName ?? ReferenceLocation ?? "?"} ({ReferenceType ?? "?"})";
            }
        }

        /// <summary>Secondary detail for display (prefers the Network comment).</summary>
        public string DetailText
        {
            get
            {
                // Prefer the Network comment (the most meaningful context).
                if (!string.IsNullOrWhiteSpace(NetworkComment))
                    return NetworkComment;
                if (!string.IsNullOrWhiteSpace(NetworkTitle) && NetworkIndex > 0)
                    return $"{LocationName ?? ReferenceType ?? ""}";
                if (!string.IsNullOrWhiteSpace(LocationName) && LocationName != ReferenceLocation)
                    return LocationName;
                if (!string.IsNullOrWhiteSpace(ReferenceLocation))
                    return ReferenceLocation;
                return "";
            }
        }

        /// <summary>Whether it can navigate to the reference location.</summary>
        public bool CanNavigate => !string.IsNullOrWhiteSpace(BlockName);
    }

    /// <summary>
    /// Cross-reference query result.
    /// </summary>
    public class CrossReferenceResult
    {
        /// <summary>The queried variable name.</summary>
        public string VariableName { get; set; }

        /// <summary>Data type.</summary>
        public string DataType { get; set; }

        /// <summary>Scope (GlobalVariable/LocalVariable/TypedConstant).</summary>
        public string Scope { get; set; }

        /// <summary>All reference entries.</summary>
        public List<CrossReferenceEntry> Entries { get; set; } = new List<CrossReferenceEntry>();

        /// <summary>Total number of references.</summary>
        public int Count => Entries?.Count ?? 0;
    }
}