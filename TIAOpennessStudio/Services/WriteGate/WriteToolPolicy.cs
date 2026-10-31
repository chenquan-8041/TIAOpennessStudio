using System;
using System.Collections.Generic;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.WriteGate
{
    /// <summary>What a gated tool does to the TIA project. Decides the wording and the risk of the approval card.</summary>
    public enum WriteKind
    {
        /// <summary>Not gated: read-only or side-effect-free for the project source (for example compile).</summary>
        None,
        /// <summary>Creates or replaces a block / type.</summary>
        BlockWrite,
        /// <summary>Removes a block from the project.</summary>
        BlockDelete,
        /// <summary>Changes the hardware configuration (devices, modules, attributes, subnets). No block semantics.</summary>
        HardwareWrite
    }

    public sealed class WriteToolPolicy
    {
        public WriteToolPolicy(string tool, WriteKind kind, InteractionRisk risk, bool allowRemember)
        {
            Tool = tool;
            Kind = kind;
            Risk = risk;
            AllowRemember = allowRemember && risk != InteractionRisk.High;
        }

        public string Tool { get; }
        public WriteKind Kind { get; }
        public InteractionRisk Risk { get; }

        /// <summary>True when the drawer may offer "don't ask again in this task". Never true for High risk.</summary>
        public bool AllowRemember { get; }

        public bool RequiresApproval => Kind != WriteKind.None;
    }

    /// <summary>
    /// The single policy table for every write entry of the :8001 server.
    /// MCP <c>tools/call</c> and the REST endpoints both look up here, so a tool cannot be gated on one
    /// channel and forgotten on the other. The write tools named in the Python side's
    /// <c>_MCP_WRITE_TOOLS</c> must all be present.
    /// </summary>
    public static class WriteToolPolicies
    {
        private static readonly Dictionary<string, WriteToolPolicy> Table =
            new Dictionary<string, WriteToolPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                // Create or replace a block. Medium: replacing is recoverable through the write-back snapshot.
                ["import_block"] = new WriteToolPolicy("import_block", WriteKind.BlockWrite, InteractionRisk.Medium, allowRemember: true),
                ["build_lad_fc"] = new WriteToolPolicy("build_lad_fc", WriteKind.BlockWrite, InteractionRisk.Medium, allowRemember: true),
                ["build_lad_fb"] = new WriteToolPolicy("build_lad_fb", WriteKind.BlockWrite, InteractionRisk.Medium, allowRemember: true),
                ["build_graph_fb"] = new WriteToolPolicy("build_graph_fb", WriteKind.BlockWrite, InteractionRisk.Medium, allowRemember: true),
                ["build_udt"] = new WriteToolPolicy("build_udt", WriteKind.BlockWrite, InteractionRisk.Medium, allowRemember: true),
                ["build_global_db"] = new WriteToolPolicy("build_global_db", WriteKind.BlockWrite, InteractionRisk.Medium, allowRemember: true),
                // In-place patches: only one subtree changes, so the same Medium risk applies.
                ["patch_block_network"] = new WriteToolPolicy("patch_block_network", WriteKind.BlockWrite, InteractionRisk.Medium, allowRemember: true),
                ["patch_block_interface"] = new WriteToolPolicy("patch_block_interface", WriteKind.BlockWrite, InteractionRisk.Medium, allowRemember: true),
                ["repair_inconsistent_types"] = new WriteToolPolicy("repair_inconsistent_types", WriteKind.BlockWrite, InteractionRisk.Medium, allowRemember: true),
                // OB envelope: can overwrite OB1 (the user's main program), so never remembered.
                ["build_ob"] = new WriteToolPolicy("build_ob", WriteKind.BlockWrite, InteractionRisk.High, allowRemember: false),

                // Hardware config: devices/modules/attributes/subnets. Not block semantics — the approval
                // card shows a dedicated hardware line, not "create/overwrite block". Medium recover-by-snapshot risk.
                ["configure_device"] = new WriteToolPolicy("configure_device", WriteKind.HardwareWrite, InteractionRisk.Medium, allowRemember: true),
                ["set_device_attribute"] = new WriteToolPolicy("set_device_attribute", WriteKind.HardwareWrite, InteractionRisk.Medium, allowRemember: true),
                ["add_module"] = new WriteToolPolicy("add_module", WriteKind.HardwareWrite, InteractionRisk.Medium, allowRemember: true),
                ["configure_subnet"] = new WriteToolPolicy("configure_subnet", WriteKind.HardwareWrite, InteractionRisk.Medium, allowRemember: true),

                // Legacy REST endpoint /api/import_scl: always replaces an existing block, same policy as import_block.
                ["import_scl"] = new WriteToolPolicy("import_scl", WriteKind.BlockWrite, InteractionRisk.Medium, allowRemember: true),

                // Destructive and irreversible from the app's point of view: never remembered.
                ["delete_block"] = new WriteToolPolicy("delete_block", WriteKind.BlockDelete, InteractionRisk.High, allowRemember: false),

                // Compiling does not change project source. Allowed without asking.
                ["compile"] = new WriteToolPolicy("compile", WriteKind.None, InteractionRisk.Low, allowRemember: false),
                // Compile baseline/diff are also compile-only: they never change project source.
                ["compile_snapshot"] = new WriteToolPolicy("compile_snapshot", WriteKind.None, InteractionRisk.Low, allowRemember: false),
                ["compile_diff"] = new WriteToolPolicy("compile_diff", WriteKind.None, InteractionRisk.Low, allowRemember: false),
            };

        /// <summary>The policy for a tool, or null when the tool is not a write tool known to the gate.</summary>
        public static WriteToolPolicy Find(string tool)
        {
            if (string.IsNullOrWhiteSpace(tool)) return null;
            return Table.TryGetValue(tool.Trim(), out var p) ? p : null;
        }

        /// <summary>All tool names in the table (used by consistency checks).</summary>
        public static IReadOnlyCollection<string> Tools => Table.Keys;
    }
}
