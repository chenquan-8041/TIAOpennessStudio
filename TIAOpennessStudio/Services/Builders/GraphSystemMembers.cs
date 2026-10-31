using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// GRAPH system-interface classification, shared by the read and write paths.
    ///
    /// The read path uses this filter so export_block_ir returns only user members; the write
    /// path uses the same rule so writing user members back cannot duplicate the system signals
    /// (the composer would reject "Duplicate ... OFF_SQ").
    ///
    /// Kept dependency-free on purpose: the knowledge worker links this file directly, so this
    /// rule must not pull in the full GRAPH composer (schema templates, XML composition).
    /// </summary>
    public static class GraphSystemMembers
    {
        private static readonly HashSet<string> SystemInputMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OFF_SQ", "INIT_SQ", "ACK_EF", "S_PREV", "S_NEXT", "SW_AUTO", "SW_TAP",
            "SW_TOP", "SW_MAN", "S_SEL", "S_ON", "S_OFF", "T_PUSH"
        };

        private static readonly HashSet<string> SystemOutputMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "S_NO", "S_MORE", "S_ACTIVE", "ERR_FLT", "AUTO_ON", "TAP_ON", "TOP_ON", "MAN_ON"
        };

        private static readonly HashSet<string> SystemStaticMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "S_DISPLAY", "S_SEL_OLD", "S_DISPIDX", "T_DISPIDX", "MOP", "TICKS", "SQ_FLAGS"
        };

        /// <summary>True when the member is part of the GRAPH system interface (not user-declared).</summary>
        public static bool IsSystemMember(string sectionName, string name, string datatype)
        {
            var section = (sectionName ?? "").Trim();
            var memberName = (name ?? "").Trim();
            var type = (datatype ?? "").Trim();

            if (string.Equals(section, "Input", StringComparison.OrdinalIgnoreCase))
                return SystemInputMembers.Contains(memberName);
            if (string.Equals(section, "Output", StringComparison.OrdinalIgnoreCase))
                return SystemOutputMembers.Contains(memberName);
            if (string.Equals(section, "Static", StringComparison.OrdinalIgnoreCase))
            {
                if (SystemStaticMembers.Contains(memberName))
                    return true;
                // Per-step / per-transition runtime structures are regenerated from the sequence.
                return string.Equals(type, "GraphStep", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(type, "GraphTransition", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
    }
}
