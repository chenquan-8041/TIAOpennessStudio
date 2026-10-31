using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TIAOpennessStudio.Services.Standards
{
    /// <summary>Metadata of a mounted knowledge base (persisted to mounts.json).</summary>
    public sealed class MountedLibraryInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string DisplayName { get; set; }
        public string KbPath { get; set; }
        /// <summary>Encryption scheme: "official" | "passphrase" | "none".</summary>
        public string Encryption { get; set; } = "passphrase";
        public bool Enabled { get; set; } = true;
        /// <summary>Whether the passphrase is remembered via DPAPI to auto-unlock on restart.</summary>
        public bool RememberPassphrase { get; set; }
        /// <summary>Whether the key is bound to this machine: true merges a machine fingerprint, so a correct passphrase cannot decrypt elsewhere (pack and decrypt must match).</summary>
        public bool BindToMachine { get; set; }
    }

    /// <summary>
    /// Registry for official and user-mounted knowledge bases. Searches merge results from all
    /// enabled libraries. Passphrases are never persisted; only metadata is saved, with optional
    /// DPAPI remember.
    /// </summary>
    public sealed class StandardsRegistry
    {
        private readonly string _appBase;
        private readonly string _mountsConfigPath;
        private readonly object _sync = new object();

        // Official libraries: load all "standards*.kb" files (case-insensitive) under the Knowledge directory.
        private List<StandardsKnowledgeStore> _officialStores = new List<StandardsKnowledgeStore>();
        private readonly PassphraseVault _vault;
        // User libraries: id -> metadata / opened store
        private readonly Dictionary<string, MountedLibraryInfo> _mounts = new Dictionary<string, MountedLibraryInfo>();
        private readonly Dictionary<string, StandardsKnowledgeStore> _openStores = new Dictionary<string, StandardsKnowledgeStore>();

        public StandardsRegistry(string appBaseDirectory)
        {
            _appBase = string.IsNullOrWhiteSpace(appBaseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory : appBaseDirectory;
            var knowledgeDir = Path.Combine(_appBase, "Knowledge");
            Directory.CreateDirectory(knowledgeDir);
            _mountsConfigPath = Path.Combine(knowledgeDir, "mounts.json");

            _officialStores = CreateOfficialStores();
            _vault = new PassphraseVault(_appBase);
            LoadMounts();
            AutoUnlockRemembered();
        }

        /// <summary>On startup, auto-unlock passphrase libraries via DPAPI-remembered passphrases; plaintext libraries are usable directly.</summary>
        private void AutoUnlockRemembered()
        {
            List<MountedLibraryInfo> infos;
            lock (_sync) infos = _mounts.Values.ToList();

            foreach (var info in infos)
            {
                try
                {
                    var kind = (info.Encryption ?? "passphrase").ToLowerInvariant();
                    if (kind == "none" || kind == "official")
                    {
                        var s = CreateStore(info, null);
                        s.SearchRules("__probe__", 1);
                        lock (_sync) _openStores[info.Id] = s;
                    }
                    else if (kind == "passphrase" && info.RememberPassphrase)
                    {
                        var pp = _vault.TryGet(info.Id);
                        if (pp != null)
                        {
                            var s = CreateStore(info, pp);
                            s.SearchRules("__probe__", 1);
                            lock (_sync) _openStores[info.Id] = s;
                        }
                    }
                }
                catch { /* a failed auto-unlock for one library must not affect others; the user can unlock it manually */ }
            }
        }

        /// <summary>Whether the given passphrase library is currently unlocked.</summary>
        public bool IsUnlocked(string mountId)
        {
            lock (_sync) return _openStores.ContainsKey(mountId);
        }

        public bool OfficialAvailable
        {
            get { lock (_sync) return _officialStores.Any(s => s != null && s.IsAvailable); }
        }

        /// <summary>Display name of the official library (used as UI tab title).</summary>
        public string OfficialDisplayName => "默认知识库";

        /// <summary>
        /// Official libraries: scan the Knowledge directory (program directory first, then the user
        /// cache directory) and load every "standards*.kb" file (case-insensitive), e.g.
        /// standards.kb, Standards_VASS.kb and STANDARDS-2024.kb.
        /// </summary>
        private List<StandardsKnowledgeStore> CreateOfficialStores()
        {
            var stores = new List<StandardsKnowledgeStore>();

            // Official library search directories: program-directory Knowledge first, then user-cache Knowledge.
            var dirs = new List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Knowledge"),
                Path.Combine(_appBase, "Knowledge")
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.kb", SearchOption.TopDirectoryOnly); }
                catch { continue; }

                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    // prefix match "standards"; uppercase before comparing for case-insensitivity.
                    if (name == null || !name.ToUpperInvariant().StartsWith("STANDARDS", StringComparison.Ordinal))
                        continue;
                    // de-duplicate: skip a user-directory file already loaded under the same name from the program directory.
                    if (!seen.Add(Path.GetFileName(file))) continue;

                    // each official library uses its file name as display name and gets its own row in the UI.
                    stores.Add(new StandardsKnowledgeStore(file, new OfficialSecretProvider(), name));
                }
            }

            return stores;
        }

        public IReadOnlyList<MountedLibraryInfo> Mounts
        {
            get { lock (_sync) return _mounts.Values.ToList(); }
        }

        /// <summary>Mounts a user library and verifies it opens; remember=true stores the passphrase via DPAPI; bindToMachine=true marks it as a machine-locked library.</summary>
        public MountedLibraryInfo Mount(string kbPath, string displayName, string encryption, string passphrase, bool remember = false, bool bindToMachine = false)
        {
            if (string.IsNullOrWhiteSpace(kbPath) || !File.Exists(kbPath))
                throw new FileNotFoundException("Knowledge base file does not exist.", kbPath);

            var kind = string.IsNullOrWhiteSpace(encryption) ? "passphrase" : encryption;
            var info = new MountedLibraryInfo
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(kbPath) : displayName,
                KbPath = kbPath,
                Encryption = kind,
                Enabled = true,
                BindToMachine = bindToMachine,
                RememberPassphrase = remember && kind.Equals("passphrase", StringComparison.OrdinalIgnoreCase)
            };

            var store = CreateStore(info, passphrase);
            store.SearchRules("__probe__", 1); // trigger decryption verification; throws on failure

            lock (_sync)
            {
                _mounts[info.Id] = info;
                _openStores[info.Id] = store;
                if (info.RememberPassphrase) _vault.Save(info.Id, passphrase);
                SaveMounts();
            }
            return info;
        }

        /// <summary>Provides the passphrase for a library and opens it; remember=true stores it.</summary>
        public void UnlockPassphraseLibrary(string mountId, string passphrase, bool remember = false)
        {
            lock (_sync)
            {
                if (!_mounts.TryGetValue(mountId, out var info)) throw new InvalidOperationException("Library not found.");
                var store = CreateStore(info, passphrase);
                store.SearchRules("__probe__", 1); // verify the passphrase
                _openStores[mountId] = store;
                if (remember)
                {
                    info.RememberPassphrase = true;
                    _vault.Save(mountId, passphrase);
                    SaveMounts();
                }
            }
        }

        public void SetEnabled(string mountId, bool enabled)
        {
            lock (_sync)
            {
                if (_mounts.TryGetValue(mountId, out var info)) { info.Enabled = enabled; SaveMounts(); }
            }
        }

        public void Unmount(string mountId)
        {
            lock (_sync)
            {
                _mounts.Remove(mountId);
                if (_openStores.TryGetValue(mountId, out var s)) { try { s.Dispose(); } catch { } _openStores.Remove(mountId); }
                _vault.Remove(mountId);   // forget the remembered passphrase
                SaveMounts();
            }
        }

        /// <summary>Lists all rules of all official libraries (for the central tab). Returns an empty list when unavailable or not authorized.</summary>
        public IReadOnlyList<StandardRule> GetOfficialRules()
        {
            if (!OfficialAvailable) return Array.Empty<StandardRule>();

            List<StandardsKnowledgeStore> stores;
            lock (_sync) stores = _officialStores.ToList();

            var merged = new List<StandardRule>();
            foreach (var s in stores)
            {
                if (s == null || !s.IsAvailable) continue;
                try { merged.AddRange(s.GetAllRules()); }
                catch { /* a failure to read a single official library must not affect others */ }
            }
            return merged;
        }

        /// <summary>Official library list entries: KbPath is the unique id and the file name is the display name; one row per library in the UI.</summary>
        public IReadOnlyList<(string Id, string DisplayName)> OfficialLibraries
        {
            get
            {
                List<StandardsKnowledgeStore> stores;
                lock (_sync) stores = _officialStores.ToList();
                return stores
                    .Where(s => s != null && s.IsAvailable)
                    .Select(s => (s.KbPath, s.DisplayName))
                    .ToList();
            }
        }

        /// <summary>Lists all rules of one official library by id (KbPath).</summary>
        public IReadOnlyList<StandardRule> GetOfficialRulesById(string id)
        {
            if (string.IsNullOrEmpty(id)) return Array.Empty<StandardRule>();

            StandardsKnowledgeStore store;
            lock (_sync)
                store = _officialStores.FirstOrDefault(
                    s => s != null && string.Equals(s.KbPath, id, StringComparison.OrdinalIgnoreCase));

            if (store == null || !store.IsAvailable) return Array.Empty<StandardRule>();
            try { return store.GetAllRules(); }
            catch { return Array.Empty<StandardRule>(); }
        }

        /// <summary>Lists all rules of a mounted library. Throws when not unlocked or on failure; the caller surfaces the message.</summary>
        public IReadOnlyList<StandardRule> GetMountRules(string mountId)
        {
            StandardsKnowledgeStore store;
            lock (_sync)
            {
                if (!_openStores.TryGetValue(mountId, out store) || store == null)
                    throw new InvalidOperationException("This library is not open or not unlocked.");
            }
            return store.GetAllRules();
        }

        /// <summary>Merged search across all enabled libraries, returning (library name, rule).</summary>
        public IReadOnlyList<(string Library, StandardRule Rule)> SearchAll(string query, int topKPerLib = 3)
        {
            var results = new List<(string, StandardRule)>();

            List<StandardsKnowledgeStore> officialStores;
            lock (_sync) officialStores = _officialStores.ToList();

            foreach (var s in officialStores)
            {
                if (s == null || !s.IsAvailable) continue;
                try
                {
                    foreach (var r in s.SearchRules(query, topKPerLib))
                        results.Add((s.DisplayName, r));
                }
                catch { /* silently skip when a single official library fails to decrypt/search (e.g. no secret dll) */ }
            }

            List<KeyValuePair<string, StandardsKnowledgeStore>> open;
            lock (_sync) open = _openStores.ToList();

            foreach (var kv in open)
            {
                if (!_mounts.TryGetValue(kv.Key, out var info) || !info.Enabled) continue;
                try
                {
                    foreach (var r in kv.Value.SearchRules(query, topKPerLib))
                        results.Add((info.DisplayName, r));
                }
                catch { /* a single library failure must not affect others */ }
            }
            return results;
        }

        // The bookmark outline treats a library as one virtual document: every rule (one heading
        // section) follows the previous one with a blank line between them. doc_offset is the
        // absolute character position of a heading in that virtual document.
        private const int OutlineSeparatorChars = 2;   // "\n\n" between sections
        private const int OutlinePageChars = 5000;     // page label only; reads are heading-aligned
        private const int DefaultSectionReadChars = 20000;

        private sealed class OutlineEntry
        {
            public StandardRule Rule { get; set; }
            public int DocOffset { get; set; }
            public int Length { get; set; }
        }

        /// <summary>
        /// Orders rules as they appeared in the source document. The Markdown importer encodes the
        /// sequence in the rule id ("doc#NNN#title"); rules without one keep their store order
        /// (OrderBy is stable).
        /// </summary>
        private static List<OutlineEntry> BuildOutline(StandardsKnowledgeStore store)
        {
            var rules = new List<StandardRule>(store.GetAllRules() ?? Array.Empty<StandardRule>());
            var ordered = rules
                .Select((r, i) => new { Rule = r, Index = i, Seq = RuleSequence(r) })
                .OrderBy(x => x.Seq)
                .ThenBy(x => x.Index)
                .Select(x => x.Rule)
                .ToList();

            var outline = new List<OutlineEntry>(ordered.Count);
            int offset = 0;
            foreach (var r in ordered)
            {
                int length = r?.Content?.Length ?? 0;
                outline.Add(new OutlineEntry { Rule = r, DocOffset = offset, Length = length });
                offset += length + OutlineSeparatorChars;
            }
            return outline;
        }

        private static int RuleSequence(StandardRule rule)
        {
            var parts = (rule?.RuleId ?? string.Empty).Split('#');
            if (parts.Length >= 2 && int.TryParse(parts[1], out int seq)) return seq;
            return int.MaxValue;
        }

        private static int HeadingLevel(string content)
        {
            var text = (content ?? string.Empty).TrimStart();
            int i = 0;
            while (i < text.Length && text[i] == '#') i++;
            if (i == 0 || i > 6) return 0;
            if (i >= text.Length || text[i] != ' ') return 0;
            return i;
        }

        /// <summary>
        /// Finds the next ATX heading after <paramref name="afterOffset"/> inside one rule's raw
        /// content, skipping headings inside fenced code blocks. Returns the line-start offset, or -1.
        /// Most imported libraries split every heading into its own rule, so this is normally -1;
        /// it matters for whole-file imports and hand-made libraries.
        /// </summary>
        private static int FindNextHeadingOffset(string content, int afterOffset)
        {
            if (string.IsNullOrEmpty(content)) return -1;
            bool inFence = false;
            int lineStart = 0;
            while (lineStart < content.Length)
            {
                int lineEnd = content.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = content.Length;
                var line = content.Substring(lineStart, lineEnd - lineStart);
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("```"))
                    inFence = !inFence;
                else if (!inFence && lineStart > afterOffset &&
                         StandardsMarkdownImporter.IsAtxHeading(trimmed, out _, out _))
                    return lineStart;
                if (lineEnd == content.Length) break;
                lineStart = lineEnd + 1;
            }
            return -1;
        }

        private static int FindOutlineIndex(List<OutlineEntry> outline, int docOffset)
        {
            for (int i = 0; i < outline.Count; i++)
            {
                var e = outline[i];
                if (docOffset < e.DocOffset) return i;                 // in the gap before this heading
                if (docOffset < e.DocOffset + e.Length) return i;     // inside this section
                if (docOffset == e.DocOffset + e.Length)              // exactly at its end
                    return i + 1 < outline.Count ? i + 1 : i;
            }
            return -1;
        }

        private static int LastEnd(List<OutlineEntry> outline)
        {
            if (outline.Count == 0) return 0;
            var last = outline[outline.Count - 1];
            return last.DocOffset + last.Length;
        }

        /// <summary>
        /// Lists every currently-available library (official + enabled/unlocked mounted) as a
        /// bookmark outline: heading level, title, rule_id, page, absolute doc_offset, content
        /// length and the doc_offset of the next heading. No rule body is returned; the agent
        /// picks a bookmark and follows up with <see cref="ReadSectionChunk"/>.
        /// </summary>
        public string ListLibrariesAndTopics(string libraryId = null)
        {
            var libs = new List<(string Id, string Name, string Source, StandardsKnowledgeStore Store)>();

            List<StandardsKnowledgeStore> officialStores;
            lock (_sync) officialStores = _officialStores.ToList();
            foreach (var s in officialStores)
                if (s != null && s.IsAvailable) libs.Add((s.KbPath, s.DisplayName, "official", s));

            List<KeyValuePair<string, StandardsKnowledgeStore>> open;
            lock (_sync) open = _openStores.ToList();
            foreach (var kv in open)
                if (_mounts.TryGetValue(kv.Key, out var info) && info.Enabled)
                    libs.Add((kv.Key, info.DisplayName, "mounted", kv.Value));

            if (!string.IsNullOrWhiteSpace(libraryId))
                libs = libs
                    .Where(l => string.Equals(l.Id, libraryId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (libs.Count == 0)
                return string.IsNullOrWhiteSpace(libraryId)
                    ? "No coding-standard library is currently available."
                    : "Library not found: " + libraryId + ". Call list_standard_topics with no library_id to see all libraries.";

            var sb = new StringBuilder();
            sb.AppendLine("Available coding-standard libraries (bookmark outline):");
            foreach (var lib in libs)
            {
                sb.AppendLine();
                sb.Append("[").Append(lib.Source).Append("] ").Append(lib.Name)
                  .Append(" (library_id: ").Append(lib.Id);
                try
                {
                    var outline = BuildOutline(lib.Store);
                    sb.Append("; ").Append(LastEnd(outline)).Append(" chars, ")
                      .Append(outline.Count).AppendLine(" bookmarks)");
                    for (int i = 0; i < outline.Count; i++)
                    {
                        var e = outline[i];
                        int level = HeadingLevel(e.Rule.Content);
                        var indent = new string(' ', Math.Max(0, level - 1) * 2);
                        int next = i + 1 < outline.Count ? outline[i + 1].DocOffset : -1;
                        sb.Append(indent).Append("- ");
                        if (level > 0) sb.Append("[L").Append(level).Append("] ");
                        sb.Append(e.Rule.Title)
                          .Append(" (rule_id: ").Append(e.Rule.RuleId)
                          .Append(", scope: ").Append(e.Rule.Scope ?? "-")
                          .Append(", page: ").Append(e.DocOffset / OutlinePageChars + 1)
                          .Append(", doc_offset: ").Append(e.DocOffset)
                          .Append(", content_length: ").Append(e.Length)
                          .Append(", next_doc_offset: ").Append(next)
                          .AppendLine(")");
                    }
                }
                catch
                {
                    sb.AppendLine(")");
                    sb.AppendLine("  (failed to enumerate this library's topics)");
                }
            }
            sb.AppendLine();
            sb.AppendLine("doc_offset is the absolute position in this library's virtual document (page = 5000 chars).");
            sb.AppendLine("Read a bookmark with read_standard_section(doc_offset=...) or (rule_id=..., offset=...).");
            sb.AppendLine("A read stops at the next heading by default; the response returns next_heading_doc_offset and next_rule_id.");
            return sb.ToString();
        }

        /// <summary>
        /// Reads a heading-bounded section of a library. The starting point is either a rule id plus
        /// a relative offset, or an absolute doc_offset from <see cref="ListLibrariesAndTopics"/>.
        /// By default the read stops at the next heading (or at 20000 chars), so a page never spans
        /// two bookmarks; the response tells the agent where the next heading is.
        /// </summary>
        public string ReadSectionChunk(string libraryId, string ruleId, int offset, int length, int docOffset = -1)
        {
            if (string.IsNullOrWhiteSpace(libraryId))
                return "library_id is required.";
            if (string.IsNullOrWhiteSpace(ruleId) && docOffset < 0)
                return "rule_id or doc_offset is required.";

            var store = LocateStore(libraryId);
            if (store == null)
                return "Library not found: " + libraryId + ". Call list_standard_topics to see available library ids.";

            List<OutlineEntry> outline;
            try { outline = BuildOutline(store); }
            catch (Exception ex) { return "Failed to read the library outline: " + ex.Message; }
            if (outline.Count == 0)
                return "This library has no rules.";

            int index;
            if (docOffset >= 0)
            {
                index = FindOutlineIndex(outline, docOffset);
                if (index < 0)
                    return "doc_offset " + docOffset + " is outside the library (valid range 0.." + (LastEnd(outline) - 1) + ").";
                offset = docOffset - outline[index].DocOffset;
                if (offset < 0) offset = 0;
            }
            else
            {
                index = outline.FindIndex(
                    e => string.Equals(e.Rule.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    return "Rule not found: " + ruleId + ". Call list_standard_topics to see available rule ids and doc offsets.";
            }

            var entry = outline[index];
            var rule = entry.Rule;
            var content = rule.Content ?? string.Empty;
            int total = content.Length;
            if (offset < 0) offset = 0;
            if (offset > total) offset = total;

            int nextHeadingInRule = FindNextHeadingOffset(content, offset);
            int sectionEnd = nextHeadingInRule > offset ? nextHeadingInRule : total;
            if (length <= 0) length = DefaultSectionReadChars;
            int end = Math.Min(sectionEnd, offset + length);
            if (end < offset) end = offset;
            bool hasMore = end < total;

            int nextHeadingDocOffset = -1;
            string nextRuleId = string.Empty;
            string nextTitle = string.Empty;
            if (nextHeadingInRule > offset)
            {
                nextHeadingDocOffset = entry.DocOffset + nextHeadingInRule;
            }
            else if (index + 1 < outline.Count)
            {
                nextHeadingDocOffset = outline[index + 1].DocOffset;
                nextRuleId = outline[index + 1].Rule.RuleId ?? string.Empty;
                nextTitle = outline[index + 1].Rule.Title ?? string.Empty;
            }

            string chunk = offset >= total ? string.Empty : content.Substring(offset, Math.Max(0, end - offset));

            var sb = new StringBuilder();
            sb.Append("rule_id: ").Append(rule.RuleId).AppendLine();
            sb.Append("title: ").AppendLine(rule.Title ?? "");
            sb.Append("page: ").Append((entry.DocOffset + offset) / OutlinePageChars + 1)
              .Append(", doc_start: ").Append(entry.DocOffset)
              .Append(", doc_end: ").Append(entry.DocOffset + end)
              .Append(", content_length: ").Append(total).AppendLine();
            sb.Append("char_start: ").Append(offset).Append(", char_end: ").Append(end)
              .Append(", has_more: ").Append(hasMore ? "true" : "false").AppendLine();
            sb.Append("next_heading_offset: ").Append(nextHeadingInRule > offset ? nextHeadingInRule : -1)
              .Append(", next_heading_doc_offset: ").Append(nextHeadingDocOffset)
              .Append(", next_rule_id: ").Append(string.IsNullOrEmpty(nextRuleId) ? "-" : nextRuleId)
              .AppendLine();
            sb.AppendLine("(char_start/char_end are relative to this rule; doc_start/doc_end are absolute)");
            sb.AppendLine();
            sb.Append(chunk);
            sb.AppendLine();

            if (end < sectionEnd)
                sb.AppendLine("(more content follows in this section — call read_standard_section again with offset=" + end + ")");
            else if (nextHeadingInRule > offset)
                sb.AppendLine("(next heading in this rule at offset=" + nextHeadingInRule +
                              " — call again with offset=" + nextHeadingInRule + ")");
            else if (!string.IsNullOrEmpty(nextRuleId))
                sb.AppendLine("(next heading '" + nextTitle + "' at doc_offset=" + nextHeadingDocOffset +
                              " — call with doc_offset=" + nextHeadingDocOffset +
                              " or rule_id=" + nextRuleId + ", offset=0)");
            else
                sb.AppendLine("(end of library)");
            return sb.ToString();
        }

        /// <summary>Locates a store by library id: official libraries match by KbPath, mounted by mount id.</summary>
        private StandardsKnowledgeStore LocateStore(string libraryId)
        {
            if (string.IsNullOrWhiteSpace(libraryId)) return null;

            lock (_sync)
            {
                var official = _officialStores.FirstOrDefault(
                    s => s != null && string.Equals(s.KbPath, libraryId, StringComparison.OrdinalIgnoreCase));
                if (official != null) return official;

                if (_openStores.TryGetValue(libraryId, out var mounted) && mounted != null)
                {
                    if (_mounts.TryGetValue(libraryId, out var info) && info.Enabled)
                        return mounted;
                }
            }
            return null;
        }

        private StandardsKnowledgeStore CreateStore(MountedLibraryInfo info, string passphrase)
        {
            IStandardsSecretProvider secret;
            switch ((info.Encryption ?? "passphrase").ToLowerInvariant())
            {
                case "none": secret = new NullSecretProvider(); break;
                case "official": secret = new OfficialSecretProvider(); break;
                default: secret = new PassphraseSecretProvider(passphrase ?? string.Empty); break;
            }
            return new StandardsKnowledgeStore(info.KbPath, secret, info.DisplayName, info.BindToMachine);
        }

        private void LoadMounts()
        {
            try
            {
                if (!File.Exists(_mountsConfigPath)) return;
                var json = File.ReadAllText(_mountsConfigPath);
                var list = JsonSerializer.Deserialize<List<MountedLibraryInfo>>(json);
                if (list == null) return;
                lock (_sync)
                    foreach (var m in list) if (m != null && !string.IsNullOrEmpty(m.Id)) _mounts[m.Id] = m;
            }
            catch { /* ignore corrupt config and start fresh */ }
        }

        private void SaveMounts()
        {
            try
            {
                var json = JsonSerializer.Serialize(_mounts.Values.ToList(),
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_mountsConfigPath, json);
            }
            catch { /* best effort */ }
        }
    }
}
