using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.WriteGate
{
    /// <summary>What a caller of a gated write tool is asking for. Every field is untrusted input.</summary>
    public sealed class WriteRequest
    {
        public string Tool { get; set; }

        /// <summary>The name the caller passed. The payload may declare a different one; the card shows both when they differ.</summary>
        public string BlockName { get; set; }

        /// <summary>FB / FC / DB / OB / UDT, or empty.</summary>
        public string BlockType { get; set; }

        /// <summary>SCL or XML content to import. Null for delete.</summary>
        public string Code { get; set; }

        /// <summary>Requested group path. Empty = root.</summary>
        public string GroupPath { get; set; }

        /// <summary>Overrides the format label, for example "LAD (XML)" for the ladder builders.</summary>
        public string FormatLabel { get; set; }

        /// <summary>Correlation id from the caller (X-Request-Id), when it sent one. Only used for the audit trail.</summary>
        public string Rid { get; set; }

        /// <summary>
        /// True when the request did not come from this machine (TIA_MCP_EXPOSE_REMOTE). Such a caller is not
        /// the user's chat run: it must not be able to use, or be offered, "don't ask again in this task".
        /// </summary>
        public bool IsRemote { get; set; }

        /// <summary>
        /// Plain-language operation summary for hardware-config writes (configure_device / set_device_attribute /
        /// add_module / configure_subnet). Shown on the approval card in place of the block/type fields, which
        /// are unused for these tools. Untrusted input; sanitised before display.
        /// </summary>
        public string HardwareDescription { get; set; }
    }

    public sealed class WriteGateDecision
    {
        public bool Allowed { get; private set; }
        public InteractionStatus Status { get; private set; }

        /// <summary>Stable machine-readable code the agent can branch on: user_denied, approval_timeout, ...</summary>
        public string Code { get; private set; } = "";

        /// <summary>Text that is safe to hand back to the agent as the tool error.</summary>
        public string Message { get; private set; } = "";

        public static WriteGateDecision Allow(InteractionStatus status) =>
            new WriteGateDecision { Allowed = true, Status = status };

        public static WriteGateDecision Refuse(InteractionStatus status, string code, string message) =>
            new WriteGateDecision { Allowed = false, Status = status, Code = code, Message = message };
    }

    /// <summary>
    /// Asks the user before the :8001 server changes the TIA project.
    ///
    /// Order of work, and why it matters:
    ///   1. Read the payload and look the target up in the project. These are short reads that finish and
    ///      release the TIA STA thread BEFORE anybody is asked. The STA thread serialises every Openness call
    ///      (UI refresh, the knowledge worker index...), so it must never be held while a person thinks.
    ///   2. Check the access mode: if the run that issued this request started in ReadOnly mode, show an
    ///      elevation card that says "you set this task to read-only — do you still want to write?" BEFORE
    ///      we even build the normal approval card. If the user declines, return user_denied immediately.
    ///   3. Build the card text from those facts with fixed templates. Nothing the model wrote is copied
    ///      into a sentence; names are sanitised and shown in quotes.
    ///   4. Ask. The caller is an HttpListener worker, so awaiting here parks only that request.
    ///   5. Only after "Allow" does the caller go to the STA thread and write.
    ///
    /// Everything that is not a clear yes is a refusal (fail closed).
    /// </summary>
    public sealed class WriteGate
    {
        /// <summary>Looks one name up in the open project. Production: marshals to the STA thread and reads.</summary>
        public delegate Task<BlockLookupResult> LookupAsync(string name, bool isType);

        private const int MaxCardTargets = 5;
        private const int MaxLookupTargets = 8;
        private const int PreviewLines = 40;
        private const int PreviewChars = 4000;

        /// <summary>How long all project lookups for one request may take together, before and after the user decides.</summary>
        public static readonly TimeSpan DefaultLookupBudget = TimeSpan.FromSeconds(10);

        private readonly IUserInteractionService _interaction;
        private readonly LookupAsync _lookup;
        private readonly int _approvalTimeoutSeconds;
        private readonly int _approvalHardTimeoutSeconds;
        private readonly int _approvalMaxAttempts;
        private readonly TimeSpan _lookupBudget;

        // Optional access-mode service. When non-null, writes from read-only runs are intercepted.
        private readonly IAgentAccessMode _accessMode;

        /// <param name="approvalTimeoutSeconds">
        /// Soft approval deadline. 0 = the service default (180 s). After this the card is marked expired
        /// but stays approvable until <paramref name="approvalHardTimeoutSeconds"/>.
        /// </param>
        /// <param name="approvalHardTimeoutSeconds">
        /// Hard approval deadline. 0 = the service default (600 s); capped at
        /// <see cref="UserInteractionService.MaxTimeoutSeconds"/>. The Python clients' read timeouts
        /// (REST/MCP 700 s) are sized above this ceiling.
        /// </param>
        /// <param name="approvalMaxAttempts">
        /// How many times the card is shown before a timeout finally refuses the write (0/1 = once).
        /// A card that times out is re-shown so a user who walked away gets another window; an explicit
        /// deny or cancel is never re-asked. Keep the Python MCP read timeout above
        /// attempts × hard deadline, or the tool call is cut off while the card is still up.
        /// </param>
        public WriteGate(IUserInteractionService interaction, LookupAsync lookup, int approvalTimeoutSeconds = 0,
            TimeSpan? lookupBudget = null, IAgentAccessMode accessMode = null, int approvalHardTimeoutSeconds = 0,
            int approvalMaxAttempts = 0)
        {
            _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _approvalTimeoutSeconds = approvalTimeoutSeconds;
            _approvalHardTimeoutSeconds = approvalHardTimeoutSeconds;
            _approvalMaxAttempts = approvalMaxAttempts;
            _lookupBudget = lookupBudget ?? DefaultLookupBudget;
            _accessMode = accessMode;
        }

        public async Task<WriteGateDecision> AuthorizeAsync(WriteRequest req, CancellationToken ct = default)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));

            var policy = WriteToolPolicies.Find(req.Tool);
            if (policy == null)
            {
                // A write tool the table does not know is a bug in the caller; never wave it through.
                AppLogger.Log("[WriteGate] refused: no policy for tool '" + CardText.Clean(req.Tool, 60) + "'");
                return WriteGateDecision.Refuse(InteractionStatus.Denied, "write_not_permitted",
                    "This write tool has no approval policy, so it was not run.");
            }
            if (!policy.RequiresApproval)
                return WriteGateDecision.Allow(InteractionStatus.AutoApproved);

            // ── Read-only elevation check ────────────────────────────────────
            // If the run that issued this request started in ReadOnly mode, let the user
            // decide before we even look up the block facts. This is fast and surfaces
            // the mismatch between "what the user said" and "what the agent now wants".
            if (_accessMode != null && !req.IsRemote &&
                _accessMode.ModeFor(req.Rid ?? "") == AgentAccessMode.ReadOnly)
            {
                var elevResult = await AskForElevationAsync(req, ct).ConfigureAwait(false);
                if (!elevResult.Allowed) return elevResult;
                // User approved the override: raise the run so further writes in the same
                // task skip the elevation card (the normal approval card will still fire).
                if (!string.IsNullOrWhiteSpace(req.Rid))
                    _accessMode.ElevateRun(req.Rid);
            }

            WriteFacts facts;
            try
            {
                facts = await BuildFactsAsync(req, policy).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // We could not even describe the write. Do not ask about something we cannot explain.
                AppLogger.Log("[WriteGate] refused: could not build the approval facts: " + ex.Message);
                return WriteGateDecision.Refuse(InteractionStatus.Unavailable, "approval_unavailable",
                    "The write was not run because it could not be checked for approval.");
            }

            // "Don't ask again" belongs to a run. A caller with no run (an external MCP client) cannot have it:
            // the service would accept the tick and then silently remember nothing, so do not offer it.
            bool hasRun = !string.IsNullOrEmpty(_interaction.ActiveRunId);
            InteractionRequest card = BuildCard(facts, policy, req, hasRun, _approvalTimeoutSeconds, _approvalHardTimeoutSeconds);

            var (decision, status) = await AskWithRetryAsync(
                attempt => attempt == 1 ? card : BuildRetryCard(card, attempt, MaxAttempts),
                req.Tool, ct).ConfigureAwait(false);

            // A person may have looked at the card for minutes. If the project changed in the meantime, what
            // they approved ("create X") may no longer be what would happen ("overwrite X"). Look again.
            // Skipped for a remembered approval: its lookup was milliseconds ago.
            if (decision.Allowed && status == InteractionStatus.Answered)
            {
                string changed = await FindChangeSinceCardAsync(facts).ConfigureAwait(false);
                if (changed != null)
                {
                    AppLogger.Log("[WriteGate] refused '" + req.Tool + "': project changed while waiting for approval (" + changed + ")");
                    return WriteGateDecision.Refuse(InteractionStatus.Denied, "state_changed",
                        "state_changed: the project changed while the user was deciding (" + changed + "), " +
                        "so what they approved is no longer what would happen and nothing was written. " +
                        "If you still want to do it, call the tool again; the user will be asked again.");
                }
            }
            return decision;
        }

        /// <summary>
        /// Null when every target still looks the way the card said. Targets that could not be checked before
        /// are skipped, and so is a recheck that fails to answer: the user has already decided, the importer
        /// queues behind the same STA thread anyway, and blocking a write on a busy TIA would only cause noise.
        /// </summary>
        private async Task<string> FindChangeSinceCardAsync(WriteFacts facts)
        {
            var clock = Stopwatch.StartNew();
            foreach (var t in facts.Targets)
            {
                var before = t.Existing;
                if (before == null || !before.Known || string.IsNullOrEmpty(t.LookupName)) continue;

                var now = await SafeLookupAsync(t.LookupName, t.IsType, clock).ConfigureAwait(false);
                if (!now.Known) continue;
                if (now.Exists != before.Exists)
                    return "'" + t.Name + "' " + (now.Exists ? "now exists" : "no longer exists");
            }
            return null;
        }

        // ── facts ─────────────────────────────────────────────────────────

        private async Task<WriteFacts> BuildFactsAsync(WriteRequest req, WriteToolPolicy policy)
        {
            var facts = new WriteFacts { Tool = policy.Tool };
            string group = CardText.Clean(req.GroupPath, 120);
            facts.GroupPath = group;
            facts.RequestedName = CardText.Clean(req.BlockName, 80);

            if (policy.Kind == WriteKind.HardwareWrite)
            {
                // Hardware-config writes touch device items, not PLC blocks: no block/lookup inspection, no
                // ImportPayloadInspector. The target is the device item name; the description is the caller's
                // plain-language summary of the operation. Both are sanitised and shown verbatim on the card.
                facts.HardwareTarget = CardText.Clean(req.BlockName, 80);
                facts.HardwareDescription = CardText.Clean(req.HardwareDescription, 200);
                return facts;
            }

            if (policy.Kind == WriteKind.BlockDelete)
            {
                var one = new WriteTarget { Name = facts.RequestedName, LookupName = req.BlockName };
                one.Existing = await SafeLookupAsync(req.BlockName, false, Stopwatch.StartNew()).ConfigureAwait(false);
                if (one.Existing.Known && one.Existing.Exists) one.Kind = one.Existing.Kind;
                facts.Targets.Add(one);
                return facts;
            }

            var inspected = ImportPayloadInspector.Inspect(req.BlockName, req.BlockType, req.Code);
            facts.Format = !string.IsNullOrEmpty(req.FormatLabel) ? req.FormatLabel : inspected.Format;
            facts.Lines = inspected.Lines;
            facts.Chars = inspected.Chars;
            facts.IsType = inspected.IsType;
            facts.Preview = CardText.CleanMultiline(req.Code, PreviewLines, PreviewChars);
            facts.Truncated = inspected.Truncated;
            facts.Unreadable = inspected.Unreadable;

            // Only mention the requested name when the payload really declares something else.
            if (!inspected.NameFromPayload) facts.RequestedName = "";
            else if (inspected.Declared.Count == 1
                     && string.Equals(inspected.Declared[0].Item1, req.BlockName?.Trim(), StringComparison.OrdinalIgnoreCase))
                facts.RequestedName = "";

            var clock = Stopwatch.StartNew();
            int looked = 0;
            foreach (var d in inspected.Declared)
            {
                bool isType = inspected.IsType || string.Equals(d.Item2, "UDT", StringComparison.OrdinalIgnoreCase);
                var t = new WriteTarget
                {
                    Name = CardText.Clean(d.Item1, 80),
                    Kind = CardText.Clean(d.Item2, 8),
                    IsType = isType,
                    LookupName = d.Item1
                };
                if (looked < MaxLookupTargets)
                {
                    t.Existing = await SafeLookupAsync(d.Item1, isType, clock).ConfigureAwait(false);
                    looked++;
                }
                else
                {
                    t.Existing = BlockLookupResult.Unknown("too many targets to check");
                }
                facts.Targets.Add(t);
            }
            return facts;
        }

        /// <summary>A lookup that never throws and never takes more than the shared budget.</summary>
        private async Task<BlockLookupResult> SafeLookupAsync(string name, bool isType, Stopwatch clock)
        {
            TimeSpan left = _lookupBudget - clock.Elapsed;
            if (left <= TimeSpan.Zero) return BlockLookupResult.Unknown("lookup budget used up");

            try
            {
                var work = _lookup(name, isType);
                var done = await Task.WhenAny(work, Task.Delay(left)).ConfigureAwait(false);
                if (done != work) return BlockLookupResult.Unknown("project did not answer in time");
                return await work.ConfigureAwait(false) ?? BlockLookupResult.Unknown("no answer");
            }
            catch (Exception ex)
            {
                return BlockLookupResult.Unknown(ex.Message);
            }
        }

        // ── card ──────────────────────────────────────────────────────────

        internal static InteractionRequest BuildCard(WriteFacts f, WriteToolPolicy policy, WriteRequest req, bool hasRun, int timeoutSeconds = 0, int hardTimeoutSeconds = 0)
        {
            var lm = LanguageManager.Instance;

            // Hardware-config writes: a dedicated single-line card. No block semantics, no lookup, no
            // "create/overwrite block" wording. The description was sanitised when the facts were built.
            if (policy.Kind == WriteKind.HardwareWrite)
            {
                string target = string.IsNullOrEmpty(f.HardwareTarget) ? lm.GetString("Gate.Kind.Device") : f.HardwareTarget;
                string desc = string.IsNullOrEmpty(f.HardwareDescription) ? "" : f.HardwareDescription;
                return new InteractionRequest
                {
                    Kind = InteractionKind.Approve,
                    Title = lm.GetString("Gate.Title.HardwareWrite"),
                    Summary = string.Format(CultureInfo.CurrentCulture, lm.GetString("Gate.Line.HardwareApply"), target, desc),
                    Detail = null,
                    Risk = policy.Risk,
                    Source = req.IsRemote ? policy.Tool + " (" + lm.GetString("Gate.Remote") + ")" : policy.Tool,
                    Rid = CardText.Clean(req.Rid, 64),
                    TimeoutSeconds = timeoutSeconds,
                    HardTimeoutSeconds = hardTimeoutSeconds
                };
            }

            var summary = new List<string>();
            bool anyExists = false;
            bool allKnown = true;

            int shown = 0;
            foreach (var t in f.Targets)
            {
                var ex = t.Existing;
                bool known = ex != null && ex.Known;
                bool exists = known && ex.Exists;
                if (!known) allKnown = false;
                if (exists) anyExists = true;

                if (shown >= MaxCardTargets) continue;
                shown++;

                string kind = !string.IsNullOrEmpty(t.Kind) ? t.Kind
                            : exists && !string.IsNullOrEmpty(ex.Kind) ? ex.Kind
                            : lm.GetString("Gate.Kind.Block");

                if (policy.Kind == WriteKind.BlockDelete)
                {
                    string key = !known ? "Gate.Line.Delete" : exists ? "Gate.Line.Delete" : "Gate.Line.DeleteMissing";
                    summary.Add(string.Format(CultureInfo.CurrentCulture, lm.GetString(key),
                        exists && !string.IsNullOrEmpty(ex.Kind) ? ex.Kind : lm.GetString("Gate.Kind.Block"), t.Name));
                }
                else
                {
                    string key = !known ? "Gate.Line.Unchecked" : exists ? "Gate.Line.Overwrite" : "Gate.Line.Create";
                    summary.Add(string.Format(CultureInfo.CurrentCulture, lm.GetString(key), kind, t.Name));
                }

                if (exists && !string.IsNullOrEmpty(ex.GroupPath))
                    summary.Add("    " + string.Format(CultureInfo.CurrentCulture, lm.GetString("Gate.Line.Where"), CardText.Clean(ex.GroupPath, 120)));
            }
            if (f.Targets.Count > shown)
                summary.Add(string.Format(CultureInfo.CurrentCulture, lm.GetString("Gate.Line.More"), f.Targets.Count - shown));

            if (policy.Kind == WriteKind.BlockWrite)
            {
                if (f.Unreadable)
                    summary.Add(lm.GetString("Gate.Line.Unreadable"));
                else if (f.Truncated)
                    summary.Add(lm.GetString("Gate.Line.Truncated"));

                if (!string.IsNullOrEmpty(f.RequestedName) && f.Targets.Count > 0)
                    summary.Add(string.Format(CultureInfo.CurrentCulture, lm.GetString("Gate.Line.NameDiffers"),
                        f.Targets[0].Name, f.RequestedName));

                string where = string.IsNullOrEmpty(f.GroupPath) ? lm.GetString("Gate.Root") : f.GroupPath;
                summary.Add(string.Format(CultureInfo.CurrentCulture, lm.GetString("Gate.Line.Target"), where));
                summary.Add(string.Format(CultureInfo.CurrentCulture, lm.GetString("Gate.Line.Content"),
                    f.Format, f.Lines.ToString(CultureInfo.CurrentCulture)));
            }

            // "Don't ask again" is split in two so a tick on a harmless "create" can never cover a later
            // "overwrite" of something the user wrote. A card that overwrites even one target counts as an
            // overwrite card. Not offered when the project could not be consulted or the content could not be
            // fully listed (the user cannot be said to have approved what the card did not show), without a run
            // to scope it to, and never for delete.
            string remember = null;
            if (hasRun && !req.IsRemote && policy.AllowRemember && policy.Kind == WriteKind.BlockWrite
                && allKnown && !f.Unreadable && !f.Truncated)
                remember = anyExists ? "tia.write.block.overwrite" : "tia.write.block.create";

            return new InteractionRequest
            {
                Kind = InteractionKind.Approve,
                Title = lm.GetString(policy.Kind == WriteKind.BlockDelete ? "Gate.Title.Delete" : "Gate.Title.Write"),
                Summary = string.Join("\n", summary),
                Detail = string.IsNullOrEmpty(f.Preview) ? null : f.Preview,
                Risk = policy.Risk,
                RememberKey = remember,
                Source = req.IsRemote ? policy.Tool + " (" + lm.GetString("Gate.Remote") + ")" : policy.Tool,
                Rid = CardText.Clean(req.Rid, 64),
                TimeoutSeconds = timeoutSeconds,
                HardTimeoutSeconds = hardTimeoutSeconds
            };
        }

        // ── outcome ───────────────────────────────────────────────────────

        private const string CancelledMessage =
            "run_cancelled: the task was stopped while waiting for the user, so this write was not run. Do not retry it.";

        private const string UnavailableMessage =
            "approval_unavailable: there is no confirmation window to ask the user, so this write was not run. " +
            "Tell the user it needs their approval in the app.";

        // ── Access-mode elevation card ────────────────────────────────────
        private async Task<WriteGateDecision> AskForElevationAsync(WriteRequest req, CancellationToken ct)
        {
            // The first write of a read-only run asks for an explicit mode switch. A timeout here is retried
            // exactly like the write card — otherwise the whole run dies on one unanswered card.
            var (decision, _) = await AskWithRetryAsync(
                attempt => BuildElevationCard(req, attempt), req.Tool, ct).ConfigureAwait(false);
            return decision;
        }

        private int MaxAttempts => _approvalMaxAttempts > 0 ? _approvalMaxAttempts : 1;

        private InteractionRequest BuildElevationCard(WriteRequest req, int attempt)
        {
            var lm = LanguageManager.Instance;
            var card = new InteractionRequest
            {
                Kind = InteractionKind.Approve,
                Title = lm.GetString("Access.ElevationTitle"),
                Summary = lm.GetString("Access.ElevationSummary"),
                Risk = InteractionRisk.Medium,
                Source = req.IsRemote ? req.Tool + " (" + lm.GetString("Gate.Remote") + ")" : req.Tool,
                Rid = CardText.Clean(req.Rid, 64),
                TimeoutSeconds = _approvalTimeoutSeconds,
                HardTimeoutSeconds = _approvalHardTimeoutSeconds,
                AllowLabel = lm.GetString("Access.ElevationAllow"),
                DenyLabel = lm.GetString("Access.ElevationDeny"),
            };

            return attempt <= 1 ? card : BuildRetryCard(card, attempt, MaxAttempts);
        }

        /// <summary>
        /// Show the approval card, and when it times out re-show it — up to <see cref="_approvalMaxAttempts"/>.
        /// Only a timeout is retried: an explicit deny, a cancel or an unavailable UI is final, because asking
        /// again would ignore a decision the user actually made.
        /// </summary>
        private async Task<(WriteGateDecision decision, InteractionStatus status)> AskWithRetryAsync(
            Func<int, InteractionRequest> cardFactory, string tool, CancellationToken ct)
        {
            int maxAttempts = MaxAttempts;
            for (int attempt = 1; ; attempt++)
            {
                var card = cardFactory(attempt);
                InteractionResult result;
                try
                {
                    // allowWithoutRun: an external MCP client has no chat run. The drawer is still the place to ask.
                    result = await _interaction.RequestAsync(card, ct, allowWithoutRun: true).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (WriteGateDecision.Refuse(InteractionStatus.Cancelled, "run_cancelled", CancelledMessage),
                        InteractionStatus.Cancelled);
                }
                catch (Exception ex)
                {
                    AppLogger.Log("[WriteGate] refused: interaction service failed: " + ex.Message);
                    return (WriteGateDecision.Refuse(InteractionStatus.Unavailable, "approval_unavailable", UnavailableMessage),
                        InteractionStatus.Unavailable);
                }

                var status = result?.Status ?? InteractionStatus.Unavailable;
                var decision = Map(result, tool);
                if (status != InteractionStatus.TimedOut || attempt >= maxAttempts)
                {
                    if (status == InteractionStatus.TimedOut)
                        AppLogger.Log($"[WriteGate] '{tool}' refused: approval timed out after {attempt}/{maxAttempts} attempt(s)");
                    return (decision, status);
                }

                AppLogger.Log($"[WriteGate] '{tool}' approval timed out; asking again ({attempt + 1}/{maxAttempts})");
            }
        }

        /// <summary>Copy of the card with a retry line, so the drawer shows which attempt this is.</summary>
        private static InteractionRequest BuildRetryCard(InteractionRequest card, int attempt, int maxAttempts)
        {
            return new InteractionRequest
            {
                Kind = card.Kind,
                Title = card.Title,
                Summary = card.Summary + "\n" + LanguageManager.Instance.Format("Gate.Line.Retry",
                    "Approval retry {0}/{1}: the previous card timed out without an answer.", attempt, maxAttempts),
                Detail = card.Detail,
                Options = card.Options,
                InputHint = card.InputHint,
                DefaultInput = card.DefaultInput,
                Risk = card.Risk,
                RememberKey = card.RememberKey,
                Source = card.Source,
                Rid = card.Rid,
                TimeoutSeconds = card.TimeoutSeconds,
                HardTimeoutSeconds = card.HardTimeoutSeconds,
                AllowLabel = card.AllowLabel,
                DenyLabel = card.DenyLabel,
            };
        }

        private static WriteGateDecision Map(InteractionResult r, string tool)
        {
            if (r == null)
                return WriteGateDecision.Refuse(InteractionStatus.Unavailable, "approval_unavailable", UnavailableMessage);

            switch (r.Status)
            {
                case InteractionStatus.Answered:
                case InteractionStatus.AutoApproved:
                    return r.IsPositive
                        ? WriteGateDecision.Allow(r.Status)
                        : WriteGateDecision.Refuse(InteractionStatus.Denied, "user_denied", DeniedMessage(tool));

                case InteractionStatus.Denied:
                    return WriteGateDecision.Refuse(r.Status, "user_denied", DeniedMessage(tool));

                case InteractionStatus.TimedOut:
                    return WriteGateDecision.Refuse(r.Status, "approval_timeout",
                        "approval_timeout: the user did not answer in time, so this write was not run. " +
                        "Do not retry it on your own. Tell the user the write is waiting for their approval and ask how to proceed.");

                case InteractionStatus.Cancelled:
                    return WriteGateDecision.Refuse(r.Status, "run_cancelled", CancelledMessage);

                default:
                    return WriteGateDecision.Refuse(InteractionStatus.Unavailable, "approval_unavailable", UnavailableMessage);
            }
        }

        private static string DeniedMessage(string tool) =>
            "user_denied: the user declined this write (" + tool + "), so nothing was changed. " +
            "Do not retry the same write. Tell the user what you intended and ask how they want to proceed, " +
            "or propose a different approach.";
    }
}
