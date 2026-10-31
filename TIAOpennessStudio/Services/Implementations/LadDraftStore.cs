using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Builders;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// G-6: in-memory session draft store with JSON persistence for restart recovery.
    /// Compose reuses the existing <see cref="PlcLadFcBlockXmlComposer"/> for ladfc so
    /// no new XML template is introduced; each network is built with
    /// <see cref="FlgNetCallXmlBuilder"/>. Global IDs are allocated by the composer at
    /// compose time, so appended networks never carry (or clash on) block-global IDs.
    /// </summary>
    public sealed class LadDraftStore : ILadDraftStore
    {
        private readonly ConcurrentDictionary<string, LadDraft> _drafts =
            new ConcurrentDictionary<string, LadDraft>(StringComparer.OrdinalIgnoreCase);
        private readonly string _draftsDir;
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        public LadDraftStore(string appBaseDirectory)
        {
            var baseDir = string.IsNullOrWhiteSpace(appBaseDirectory) ? AppDomain.CurrentDomain.BaseDirectory : appBaseDirectory;
            _draftsDir = Path.Combine(baseDir, "Drafts");
            Directory.CreateDirectory(_draftsDir);
            LoadPersisted();
        }

        public LadDraft Begin(LadDraft draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (string.IsNullOrWhiteSpace(draft.SessionId))
                draft.SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            draft.Kind = NormalizeKind(draft.Kind);
            draft.CreatedAt = DateTime.UtcNow;
            draft.UpdatedAt = DateTime.UtcNow;
            _drafts[draft.SessionId] = draft;
            Persist(draft);
            return draft;
        }

        public LadDraft Get(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            return _drafts.TryGetValue(sessionId, out var d) ? d : null;
        }

        public LadDraft AppendNetwork(string sessionId, LadDraftNetwork network)
        {
            var draft = Get(sessionId) ?? throw new InvalidOperationException($"No active LAD draft for session '{sessionId}'. Call lad_draft_begin first.");
            if (network == null) throw new ArgumentNullException(nameof(network));
            if (string.IsNullOrWhiteSpace(network.CallName))
                throw new ArgumentException("network.CallName is required (phase 1 supports call-type networks).");
            draft.Networks.Add(network);
            draft.UpdatedAt = DateTime.UtcNow;
            Persist(draft);
            return draft;
        }

        public void Discard(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            _drafts.TryRemove(sessionId, out _);
            var path = PathFor(sessionId);
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }

        public (bool Ok, string XmlOrError) ComposeXml(string sessionId)
        {
            var draft = Get(sessionId);
            if (draft == null) return (false, $"No active LAD draft for session '{sessionId}'.");
            if (draft.Networks.Count == 0) return (false, "Draft has no networks to compose.");

            // ladfc and ladfb both compose. The ladder networks are identical; the composer
            // switches the outer interface by kind (FC: Return section; FB: Static section).
            // Note: the draft model carries only inputs/outputs; FB InOut/Static/Temp members are
            // left empty here (draft-model expansion for those is a separate concern).
            var kind = string.Equals(draft.Kind, "ladfb", StringComparison.OrdinalIgnoreCase)
                ? PlcLadFcBlockXmlComposer.LadBlockKind.Fb
                : PlcLadFcBlockXmlComposer.LadBlockKind.Fc;

            try
            {
                var inputs = draft.Inputs.Select(m => new PlcBlockMemberDefinition(m.Name, m.Datatype, m.Comment)).ToArray();
                var outputs = draft.Outputs.Select(m => new PlcBlockMemberDefinition(m.Name, m.Datatype, m.Comment)).ToArray();

                var ladNetworks = new List<PlcLadFcBlockXmlComposer.LadNetwork>();
                foreach (var net in draft.Networks)
                {
                    var flgNet = FlgNetCallXmlBuilder.BuildFlgNet(net.CallName, net.Parameters.Select(ToBuilderParam));
                    ladNetworks.Add(new PlcLadFcBlockXmlComposer.LadNetwork(flgNet, net.Title, net.Comment));
                }

                var xml = PlcLadFcBlockXmlComposer.ComposeXml(
                    draft.BlockName, draft.BlockNumber, inputs, outputs, ladNetworks,
                    kind: kind,
                    blockCommentZhCn: draft.BlockComment, blockTitleZhCn: draft.BlockTitle);
                return (true, xml);
            }
            catch (Exception ex)
            {
                return (false, "Compose failed: " + ex.Message);
            }
        }

        public IReadOnlyList<string> ActiveSessions() => _drafts.Keys.ToList();

        // ---- helpers ----

        private static FlgNetCallParameter ToBuilderParam(LadDraftCallParameter p)
        {
            if (string.Equals(p.SourceKind, "constant", StringComparison.OrdinalIgnoreCase))
                return FlgNetCallParameter.Constant(p.ParameterName, p.Section, p.DataType, p.ConstantValue ?? "");
            return FlgNetCallParameter.Global(p.ParameterName, p.Section, p.DataType,
                (p.SymbolPath ?? new List<string>()).ToArray());
        }

        private static string NormalizeKind(string kind)
        {
            var k = (kind ?? "ladfc").Trim().ToLowerInvariant();
            return (k == "ladfc" || k == "ladfb") ? k : "ladfc";
        }

        private string PathFor(string sessionId)
        {
            // sessionId is short/alnum; sanitize defensively against path traversal.
            var safe = new string((sessionId ?? "").Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrEmpty(safe)) safe = "default";
            return Path.Combine(_draftsDir, safe + ".json");
        }

        private void Persist(LadDraft draft)
        {
            try { File.WriteAllText(PathFor(draft.SessionId), JsonSerializer.Serialize(draft, JsonOpts)); }
            catch { /* persistence is best-effort; in-memory state remains authoritative */ }
        }

        private void LoadPersisted()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_draftsDir, "*.json"))
                {
                    try
                    {
                        var draft = JsonSerializer.Deserialize<LadDraft>(File.ReadAllText(file));
                        if (draft != null && !string.IsNullOrWhiteSpace(draft.SessionId))
                            _drafts[draft.SessionId] = draft;
                    }
                    catch { /* skip corrupt draft files */ }
                }
            }
            catch { /* Drafts dir unreadable — start empty */ }
        }
    }
}
