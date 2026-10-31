using System;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;

namespace TIAOpennessStudio.Services.WriteGate
{
    /// <summary>
    /// Finds a block or type by name anywhere under the PLC's block / type groups. Touches Openness objects, so
    /// callers must run it on the TIA STA thread. It is only a read, and it finishes before the user is asked
    /// anything: the gate never waits for a person while holding the STA thread.
    /// </summary>
    internal static class BlockLookup
    {
        private const int MaxDepth = 32;

        public static BlockLookupResult Find(PlcSoftware plc, string name, bool isType)
        {
            if (plc == null) return BlockLookupResult.Unknown("No PLC software connected.");
            if (string.IsNullOrWhiteSpace(name)) return BlockLookupResult.Unknown("No name to look up.");

            try
            {
                if (isType)
                    return FindType(plc.TypeGroup, name, "", 0) ?? BlockLookupResult.NotFound();
                return FindBlock(plc.BlockGroup, name, "", 0) ?? BlockLookupResult.NotFound();
            }
            catch (Exception ex)
            {
                return BlockLookupResult.Unknown(ex.Message);
            }
        }

        private static BlockLookupResult FindBlock(PlcBlockGroup group, string name, string path, int depth)
        {
            if (group == null || depth > MaxDepth) return null;

            var hit = group.Blocks.Find(name);
            if (hit != null) return BlockLookupResult.Found(KindOf(hit), path);

            foreach (PlcBlockUserGroup child in group.Groups)
            {
                string childPath = path.Length == 0 ? child.Name : path + "\\" + child.Name;
                var r = FindBlock(child, name, childPath, depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        private static BlockLookupResult FindType(PlcTypeGroup group, string name, string path, int depth)
        {
            if (group == null || depth > MaxDepth) return null;

            var hit = group.Types.Find(name);
            if (hit != null) return BlockLookupResult.Found("UDT", path);

            foreach (PlcTypeUserGroup child in group.Groups)
            {
                string childPath = path.Length == 0 ? child.Name : path + "\\" + child.Name;
                var r = FindType(child, name, childPath, depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        private static string KindOf(PlcBlock b)
        {
            if (b is OB) return "OB";
            if (b is FB) return "FB";
            if (b is FC) return "FC";
            if (b is GlobalDB || b is InstanceDB) return "DB";
            return "";
        }
    }
}
