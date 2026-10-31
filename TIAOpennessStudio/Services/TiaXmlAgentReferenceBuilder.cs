using System;
using System.Linq;
using System.Xml.Linq;
using TIAOpennessStudio.Services.Blocks;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Backward-compatible facade over <see cref="BlockSnapshotService"/>.
    ///
    /// The canonical XML -> JSON conversion, the reference wrapper and the network-range paging
    /// now live in the snapshot projection layer, so every consumer (MCP export, Add to AI,
    /// file drop, probe) resolves the same contract from the same implementation.
    ///
    /// Kept only for existing call sites; new code should call BlockSnapshotService directly.
    /// </summary>
    public static class TiaXmlAgentReferenceBuilder
    {
        public static bool TryBuildReference(string xmlContent, string displayName, out string referenceText)
        {
            referenceText = null;
            if (string.IsNullOrWhiteSpace(xmlContent)) return false;

            try
            {
                var snapshot = BlockSnapshotService.FromXml(xmlContent);
                return BlockSnapshotService.BuildReferenceText(snapshot, displayName, out referenceText);
            }
            catch
            {
                referenceText = null;
                return false;
            }
        }

        public static bool TryBuildJsonIr(string xmlContent, out string json, out string summary)
        {
            return BlockSnapshotService.BuildIr(xmlContent, 1, 0, out json, out summary);
        }

        public static bool TryBuildJsonIrRange(string xmlContent, int networkStart, int networkCount, out string json, out string summary)
        {
            return BlockSnapshotService.BuildIr(xmlContent, networkStart, networkCount, out json, out summary);
        }

        /// <summary>
        /// Returns the raw XML of one compile unit (or the element addressed by UId), used by
        /// patch flows that need the unmodified source fragment before editing it.
        /// </summary>
        public static bool TryExtractFragment(string xmlContent, string compileUnitId, string uid, out string fragmentXml)
        {
            fragmentXml = null;
            if (string.IsNullOrWhiteSpace(xmlContent))
            {
                return false;
            }

            try
            {
                var document = XDocument.Parse(xmlContent, LoadOptions.PreserveWhitespace);
                var root = document.Root;
                if (root == null)
                {
                    return false;
                }

                var compileUnit = FindCompileUnit(root, compileUnitId);
                if (compileUnit == null)
                {
                    return false;
                }

                XElement fragment = null;
                if (!string.IsNullOrWhiteSpace(uid))
                {
                    fragment = compileUnit
                        .DescendantsAndSelf()
                        .FirstOrDefault(x => string.Equals(x.Attribute("UId")?.Value ?? "", uid, StringComparison.OrdinalIgnoreCase));
                }

                fragment ??= compileUnit.Descendants().FirstOrDefault(x =>
                {
                    var ln = x.Name.LocalName;
                    return string.Equals(ln, "FlgNet", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(ln, "FbdNet", StringComparison.OrdinalIgnoreCase) ||
                           x.Descendants().Any(d => string.Equals(d.Name.LocalName, "Parts", StringComparison.OrdinalIgnoreCase));
                });
                fragment ??= compileUnit;
                fragmentXml = fragment.ToString(SaveOptions.DisableFormatting);
                return !string.IsNullOrWhiteSpace(fragmentXml);
            }
            catch
            {
                return false;
            }
        }

        private static XElement FindCompileUnit(XElement root, string compileUnitId)
        {
            var compileUnits = root
                .Descendants()
                .Where(x =>
                {
                    var ln = x.Name.LocalName;
                    return string.Equals(ln, "CompileUnit", StringComparison.OrdinalIgnoreCase) ||
                           ln.EndsWith("CompileUnit", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (!string.IsNullOrWhiteSpace(compileUnitId))
            {
                var matched = compileUnits.FirstOrDefault(x =>
                    string.Equals(x.Attribute("ID")?.Value ?? "", compileUnitId, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    return matched;
                }
            }

            return compileUnits.FirstOrDefault();
        }
    }
}
