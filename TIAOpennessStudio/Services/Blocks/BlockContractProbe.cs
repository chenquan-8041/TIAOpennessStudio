using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TIAOpennessStudio.Models.Blocks;
using TIAOpennessStudio.Models.Lad;
using TIAOpennessStudio.Services.Builders;
using TIAOpennessStudio.Services.Implementations;
using TIAOpennessStudio.Services.Lad.Codecs;

namespace TIAOpennessStudio.Services.Blocks
{
    /// <summary>
    /// T0 contract probe.
    ///
    /// Freezes the current read-side contract (block XML -> IR projection) of every fixture
    /// into <c>docs/contract/block-contract-baseline.json</c>, so every later contract phase
    /// can prove it only changed what it intended to change.
    ///
    /// Usage (WinExe writes results to docs/contract/contract-probe-report.txt and returns
    /// 0 = pass / 1 = behaviour diff / 2 = probe error):
    ///   TIAOpennessStudio.exe --contract-probe
    ///   TIAOpennessStudio.exe --contract-probe --write-baseline
    ///
    /// A behaviour diff means either a real regression or an intentional contract change.
    /// Intentional changes must be re-baselined (<c>--write-baseline</c>) and reviewed.
    /// </summary>
    public static class BlockContractProbe
    {
        private const string BaselineRelativePath = @"docs\contract\block-contract-baseline.json";
        private const string ReportRelativePath = @"docs\contract\contract-probe-report.txt";
        private const int BaselineSchemaVersion = 1;
        private const string BaselineContractVersion = "T0";
        private const int LargeNetworkCloneCount = 60;

        // ─────────────────────────────────────────────────────────────────────
        // Entry point
        // ─────────────────────────────────────────────────────────────────────

        public static int Run(bool writeBaseline)
        {
            var report = new List<string>();
            try
            {
                var root = FindRepoRoot();
                if (root == null)
                {
                    report.Add("RESULT: FAIL - repository root not found (TIAOpennessStudio.sln missing).");
                    WriteReport(null, report);
                    return 2;
                }

                report.Add("Block Contract Probe");
                report.Add("Repo root : " + root);
                report.Add("Mode      : " + (writeBaseline ? "write-baseline" : "compare"));
                report.Add("Timestamp : " + DateTime.UtcNow.ToString("O"));
                report.Add("");

                var fixtures = BuildFixtures(root);
                report.Add("Fixtures  : " + fixtures.Count + " (file " + fixtures.Count(f => f.Origin == "file")
                           + ", synthetic " + fixtures.Count(f => f.Origin == "synthetic") + ")");

                var observations = new List<FixtureObservation>();
                foreach (var fixture in fixtures)
                {
                    var observation = Observe(fixture);
                    observations.Add(observation);
                    report.Add((observation.ChecksPassed ? "[PASS] " : "[FAIL] ") + observation.Id
                               + " | element=" + observation.XmlBlockElement
                               + " kind=" + observation.IrKind
                               + " lang=" + observation.IrLanguage
                               + " nets=" + observation.XmlNetworkCount + "/" + observation.ParsedNetworkCount
                               + " members=" + observation.MemberCount
                               + " ir=" + observation.IrLength);
                    foreach (var failure in observation.CheckFailures)
                        report.Add("        check: " + failure);
                }

                var baselinePath = Path.Combine(root, BaselineRelativePath);

                if (writeBaseline)
                {
                    WriteBaseline(root, baselinePath, observations);
                    report.Add("");
                    report.Add("BASELINE: written to " + BaselineRelativePath);
                    report.Add("RESULT: BASELINE WRITTEN (" + observations.Count + " fixtures).");
                    WriteReport(root, report);
                    return 0;
                }

                if (!File.Exists(baselinePath))
                {
                    report.Add("");
                    report.Add("RESULT: FAIL - baseline missing. Run with --write-baseline first.");
                    WriteReport(root, report);
                    return 2;
                }

                var failures = CompareBaseline(root, baselinePath, observations, report);
                report.Add("");
                report.Add(failures == 0
                    ? "RESULT: PASS (" + observations.Count + "/" + observations.Count + ")."
                    : "RESULT: FAIL (" + failures + " diff(s), " + observations.Count + " fixtures).");
                WriteReport(root, report);
                return failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                report.Add("RESULT: FAIL - probe exception: " + ex);
                WriteReport(null, report);
                return 2;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Fixture discovery
        // ─────────────────────────────────────────────────────────────────────

        private sealed class FixtureSpec
        {
            public string Id;
            public string Origin;          // file | synthetic
            public string DisplayVersion;  // "15.1" / "16" ...
            public int MajorVersion;
            public string SourcePath;      // absolute path for file fixtures
            public string Xml;             // synthetic fixtures only
        }

        private static List<FixtureSpec> BuildFixtures(string root)
        {
            var fixtures = new List<FixtureSpec>();

            // 1) Block samples under docs/samples/V<ver>/<language>/ — the single sample library.
            var samplesRoot = Path.Combine(root, "docs", "samples");
            if (Directory.Exists(samplesRoot))
            {
                foreach (var versionDir in Directory.GetDirectories(samplesRoot))
                {
                    var versionName = Path.GetFileName(versionDir);
                    var display = ParseVersion(versionName);
                    if (display == null) continue;

                    foreach (var file in Directory.GetFiles(versionDir, "*.xml", SearchOption.AllDirectories)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        fixtures.Add(new FixtureSpec
                        {
                            Id = RelativePath(root, file),
                            Origin = "file",
                            DisplayVersion = display,
                            MajorVersion = MajorOf(display),
                            SourcePath = file
                        });
                    }
                }
            }

            // 2) Synthetic fixtures: derived from the V15.1 samples so the schema stays valid.
            var v151 = Path.Combine(samplesRoot, "V15.1");
            // Name the exported baseline explicitly: the language folders also hold authored samples,
            // and synthetic fixtures must derive from the export, not from a hand-written file.
            var ladSample = FindSample(v151, "LAD", "LAD_V0.1.xml");
            var dbSample = FindSample(v151, "DB", "DB_V0.1.xml");
            var udtSample = FindSample(v151, "UDT", "plcType_UDT.xml");

            if (ladSample != null && dbSample != null && udtSample != null)
            {
                AddSynthetic(fixtures, "synthetic/GlobalDb_Optimized", () => MutateGlobalDb(ladSample, dbSample));
                AddSynthetic(fixtures, "synthetic/InstanceDb", () => MutateInstanceDb(dbSample));
                AddSynthetic(fixtures, "synthetic/Fc_BlockAttributes", () => MutateFcAttributes(ladSample));
                AddSynthetic(fixtures, "synthetic/Udt_ExternalWritable", () => MutateUdtExternalWritable(udtSample));
                AddSynthetic(fixtures, "synthetic/Lad_Large_Truncated", () => MutateLargeLad(ladSample));
            }

            return fixtures;
        }

        private static void AddSynthetic(List<FixtureSpec> fixtures, string id, Func<string> loader)
        {
            fixtures.Add(new FixtureSpec
            {
                Id = id,
                Origin = "synthetic",
                DisplayVersion = "15.1",
                MajorVersion = 15,
                Xml = loader()
            });
        }

        private static string FindSample(string sampleRoot, string folder, string fileName)
        {
            if (!Directory.Exists(sampleRoot)) return null;
            var dir = Path.Combine(sampleRoot, folder);
            if (!Directory.Exists(dir)) return null;
            var exact = Path.Combine(dir, fileName);
            if (File.Exists(exact)) return exact;
            return Directory.GetFiles(dir, "*.xml", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static string ParseVersion(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return null;
            if (!folderName.StartsWith("V", StringComparison.OrdinalIgnoreCase)) return null;
            var text = folderName.Substring(1);
            if (text.Length == 0) return null;
            foreach (var ch in text)
            {
                if (!char.IsDigit(ch) && ch != '.') return null;
            }
            return text;
        }

        private static int MajorOf(string version)
        {
            var idx = version.IndexOf('.');
            var major = idx < 0 ? version : version.Substring(0, idx);
            return int.TryParse(major, out var value) ? value : 0;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Synthetic XML mutations (XDocument based, deterministic)
        // ─────────────────────────────────────────────────────────────────────

        private static string MutateGlobalDb(string ladSamplePath, string dbSamplePath)
        {
            var doc = LoadXml(dbSamplePath);
            var block = FindBlockElement(doc);
            var attrList = AttributeListOf(block);
            if (attrList == null) throw new InvalidOperationException("DB sample has no AttributeList.");

            InsertBeforeName(attrList, new XElement("MemoryLayout", "Optimized"));
            return Serialize(doc);
        }

        private static string MutateInstanceDb(string dbSamplePath)
        {
            var doc = LoadXml(dbSamplePath);
            var block = FindBlockElement(doc);
            if (block == null) throw new InvalidOperationException("DB sample has no block element.");

            // Keep the element namespace empty (as in real exports) and only change the local name.
            block.Name = XName.Get("SW.Blocks.InstanceDB");
            var attrList = AttributeListOf(block);
            if (attrList == null) throw new InvalidOperationException("DB sample has no AttributeList.");
            InsertBeforeName(attrList, new XElement("InstanceOfName", "FB_Sample"));
            return Serialize(doc);
        }

        private static string MutateFcAttributes(string ladSamplePath)
        {
            var doc = LoadXml(ladSamplePath);
            var block = FindBlockElement(doc);
            var attrList = AttributeListOf(block);
            if (attrList == null) throw new InvalidOperationException("LAD sample has no AttributeList.");

            InsertBeforeName(attrList, new XElement("SetENOAutomatically", "false"));
            InsertBeforeName(attrList, new XElement("CheckArrayLimits", "True"));
            return Serialize(doc);
        }

        private static string MutateUdtExternalWritable(string udtSamplePath)
        {
            var doc = LoadXml(udtSamplePath);
            var member = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Member");
            if (member == null) throw new InvalidOperationException("UDT sample has no Member element.");

            // Children must live in the section namespace (the sample declares it as default xmlns).
            var ns = member.Name.Namespace;
            member.AddFirst(
                new XElement(ns + "AttributeList",
                    new XElement(ns + "BooleanAttribute",
                        new XAttribute("Name", "ExternalWritable"),
                        new XAttribute("SystemDefined", "true"),
                        "true")));
            return Serialize(doc);
        }

        private static string MutateLargeLad(string ladSamplePath)
        {
            var doc = LoadXml(ladSamplePath);
            var block = FindBlockElement(doc);
            if (block == null) throw new InvalidOperationException("LAD sample has no block element.");

            var compileUnits = block.Descendants()
                .Where(IsCompileUnit)
                .ToList();
            if (compileUnits.Count == 0) throw new InvalidOperationException("LAD sample has no CompileUnit.");

            var last = compileUnits[compileUnits.Count - 1];
            var template = compileUnits[0];
            for (var i = 0; i < LargeNetworkCloneCount; i++)
            {
                var clone = new XElement(template);
                clone.SetAttributeValue("ID", (0x100 + i).ToString("X"));
                last.AddAfterSelf(clone);
                last = clone;
            }
            return Serialize(doc);
        }

        private static XDocument LoadXml(string path)
        {
            return XDocument.Parse(File.ReadAllText(path, Encoding.UTF8), LoadOptions.PreserveWhitespace);
        }

        private static string Serialize(XDocument doc)
        {
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        private static XElement FindBlockElement(XDocument doc)
        {
            if (doc?.Root == null) return null;
            if (IsBlockElement(doc.Root)) return doc.Root;
            return doc.Root.Descendants().FirstOrDefault(IsBlockElement);
        }

        private static bool IsBlockElement(XElement element)
        {
            var local = element?.Name.LocalName ?? "";
            return local.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase)
                   || local.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCompileUnit(XElement element)
        {
            var local = element?.Name.LocalName ?? "";
            return local.EndsWith("CompileUnit", StringComparison.OrdinalIgnoreCase);
        }

        private static XElement AttributeListOf(XElement block)
        {
            return block?.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
        }

        private static void InsertBeforeName(XElement attrList, XElement element)
        {
            var nameElement = attrList.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            if (nameElement != null) nameElement.AddBeforeSelf(element);
            else attrList.Add(element);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Observation
        // ─────────────────────────────────────────────────────────────────────

        private sealed class FixtureObservation
        {
            public string Id;
            public string Origin;
            public string DisplayVersion;
            public string XmlSha256;
            public int XmlLength;
            public string XmlBlockElement;
            public string XmlLanguage;
            public string XmlName;
            public int XmlNumber;
            public int XmlNetworkCount;        // raw CompileUnit elements in the export
            public int ParsedNetworkCount;     // networks the canonical parser can actually build
            public List<string> XmlMembers = new List<string>();
            public List<string> BlockAttributes = new List<string>();

            // T1 snapshot contract
            public bool SnapshotParsed;
            public string SnapshotBlockType;
            public string SnapshotXmlBlockKind;
            public string SnapshotLanguage;
            public string SnapshotName;
            public bool SnapshotIsInstanceDb;
            public string SnapshotInstanceOfName;
            public string SnapshotSourceProfile;
            public string SnapshotRevision;
            /// <summary>Same block "re-exported" with only the DocumentInfo timestamp changed must keep the revision.</summary>
            public bool RevisionStable;
            public List<string> SnapshotAttributes = new List<string>();
            public bool SnapshotConsistent;

            // T2 projection contract
            public string ProjXmlSha;
            public string ProjInterfaceJson;
            public string ProjInterfaceHash;
            public string ProjSourceText;
            public string ProjSourceHash;
            public int ProjSourceLength;
            public int ProjSourceNetworks;
            public int XmlTextNetworkCount;
            public bool ProjectionsConsistent;

            // T4 Add-to-AI/file-drop reference policy
            public string ReferenceFormat;
            public int ReferenceLength;
            public bool ReferencePolicyConsistent;

            // T6 IR block metadata (attributes / instance-DB identity)
            public string IrXmlBlockKind;
            public bool IrIsInstanceDb;
            public string IrInstanceOfName;
            public List<string> IrBlockAttributes = new List<string>();
            public bool IrMetadataConsistent;

            // T7 patch identity round-trip
            public bool PatchNetworkApplicable;
            public bool PatchNetworkOk;
            public bool PatchInterfaceApplicable;
            public bool PatchInterfaceOk;
            public bool PatchNegativeOk;
            public bool PatchChecksPassed;
            public List<string> PatchErrors = new List<string>();

            // T9 build_* fail-closed guards
            public bool TruncatedGuardOk;
            public bool InstanceGuardOk;
            public bool MemoryLayoutGuardOk;
            public bool WriteGuardsPassed;
            public List<string> GuardErrors = new List<string>();

            // T10 version capability table
            public bool CapabilityChecksOk;
            public List<string> CapabilityErrors = new List<string>();

            public bool IrParsed;
            public int IrLength;
            public string IrKind;
            public string IrLanguage;
            public string BlockName;
            public int BlockNumber;
            public int IrNetworkCount;
            public List<string> IrMembers = new List<string>();
            public bool FidelityTruncated;
            public int FidelityTotal;
            public int FidelityReturned;
            public string InterfaceHash;
            public string BodyHash;

            public bool MembersMatch;
            public bool FidelityConsistent;
            public bool RangePageOk = true;
            public bool RangePageApplicable;

            public List<string> CheckFailures = new List<string>();

            public int MemberCount => XmlMembers.Count;

            public bool ChecksPassed => IrParsed && MembersMatch && FidelityConsistent && RangePageOk
                                        && SnapshotConsistent && ProjectionsConsistent
                                        && ReferencePolicyConsistent && IrMetadataConsistent
                                        && RevisionStable
                                        && PatchChecksPassed && WriteGuardsPassed && CapabilityChecksOk
                                        && CheckFailures.Count == 0;
        }

        private static FixtureObservation Observe(FixtureSpec fixture)
        {
            var o = new FixtureObservation
            {
                Id = fixture.Id,
                Origin = fixture.Origin,
                DisplayVersion = fixture.DisplayVersion
            };

            var xml = fixture.Origin == "file"
                ? File.ReadAllText(fixture.SourcePath, Encoding.UTF8)
                : fixture.Xml;

            o.XmlLength = xml.Length;
            o.XmlSha256 = Sha256(xml);

            // Same version mirror rule the app and the knowledge worker use at startup.
            TiaVersionAdapter.CurrentMajorVersion = fixture.MajorVersion;
            TiaVersionAdapter.CurrentVersion = fixture.DisplayVersion;

            XElement block = null;
            BlockSnapshot snapshot = null;
            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                block = FindBlockElement(doc);
            }
            catch (Exception ex)
            {
                o.CheckFailures.Add("xml parse failed: " + ex.Message);
            }

            if (block != null)
            {
                var attrList = AttributeListOf(block);
                o.XmlBlockElement = block.Name.LocalName;
                o.XmlName = ReadChildText(attrList, "Name");
                var numberText = ReadChildText(attrList, "Number");
                if (int.TryParse(numberText, out var xmlNumber)) o.XmlNumber = xmlNumber;

                o.XmlLanguage = ReadChildText(attrList, "ProgrammingLanguage");
                if (string.IsNullOrWhiteSpace(o.XmlLanguage))
                    o.XmlLanguage = o.XmlBlockElement.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase) ? "UDT" : "";
                o.XmlNetworkCount = block.Descendants().Count(IsCompileUnit);
                o.BlockAttributes = ReadBlockAttributes(block);
                o.XmlMembers = CollectMembersFromXml(block, IsGraphLanguage(o.XmlLanguage));

                // T1 snapshot contract: the snapshot must reproduce the XML facts above and
                // additionally expose the block-level attributes and instance-DB identity that
                // the current canonical IR loses.
                try
                {
                    snapshot = BlockSnapshotService.FromXml(xml, fixture.Id, "");
                    o.SnapshotParsed = true;
                    o.SnapshotBlockType = snapshot.BlockType;
                    o.SnapshotXmlBlockKind = snapshot.XmlBlockKind;
                    o.SnapshotLanguage = snapshot.Language;
                    o.SnapshotName = snapshot.Name;
                    o.SnapshotIsInstanceDb = snapshot.IsInstanceDb;
                    o.SnapshotInstanceOfName = snapshot.InstanceOfName;
                    o.SnapshotSourceProfile = snapshot.SourceProfile;
                    o.SnapshotRevision = snapshot.Revision;
                    o.RevisionStable = CheckRevisionStable(xml, snapshot, out var revisionError);
                    if (!o.RevisionStable) o.CheckFailures.Add(revisionError);
                    o.SnapshotAttributes = snapshot.Attributes
                        .Select(a => a.ToString())
                        .OrderBy(a => a, StringComparer.Ordinal)
                        .ToList();

                    // T2 projections: xml / interface / source must all be derived from this snapshot.
                    o.ProjXmlSha = Sha256(snapshot.RawXml);
                    var interfaceJson = BlockSnapshotService.BuildInterface(snapshot);
                    o.ProjInterfaceJson = interfaceJson;
                    o.ProjInterfaceHash = interfaceJson == null ? "" : Sha256(interfaceJson);
                    var sourceText = BlockSnapshotService.BuildSource(snapshot);
                    o.ProjSourceText = sourceText;
                    o.ProjSourceHash = sourceText == null ? "" : Sha256(sourceText);
                    o.ProjSourceLength = sourceText?.Length ?? 0;
                    o.ProjSourceNetworks = CountSourceHeaders(sourceText);
                    o.XmlTextNetworkCount = BlockSnapshotService.CountSourceNetworks(snapshot);

                    // T4 policy: SCL/STL references are plain source text; everything else is canonical IR.
                    if (BlockSnapshotService.BuildReferenceText(snapshot, fixture.Id, out var referenceText))
                    {
                        o.ReferenceLength = referenceText.Length;
                        o.ReferenceFormat = referenceText.IndexOf("Format=source-text", StringComparison.Ordinal) >= 0
                            ? "source"
                            : (referenceText.IndexOf("TIA Agent IR Reference", StringComparison.Ordinal) >= 0 ? "ir" : "unknown");
                    }
                    var expectedReferenceFormat = IsTextLanguage(o.XmlLanguage) && o.XmlTextNetworkCount > 0
                        ? "source"
                        : "ir";
                    o.ReferencePolicyConsistent = string.Equals(o.ReferenceFormat, expectedReferenceFormat, StringComparison.Ordinal);
                    if (!o.ReferencePolicyConsistent)
                    {
                        o.CheckFailures.Add("reference policy mismatch: expectedFormat=" + expectedReferenceFormat
                                            + " actualFormat=" + (o.ReferenceFormat ?? "null")
                                            + " length=" + o.ReferenceLength);
                    }

                    var expectedInstanceOf = ReadChildText(attrList, "InstanceOfName");
                    var expectedIsInstance = string.Equals(o.XmlBlockElement, "SW.Blocks.InstanceDB",
                                                     StringComparison.OrdinalIgnoreCase)
                                             || !string.IsNullOrWhiteSpace(expectedInstanceOf);
                    var attributeMatch = ListsEqual(o.BlockAttributes, o.SnapshotAttributes);

                    o.SnapshotConsistent = string.Equals(snapshot.Name, o.XmlName, StringComparison.Ordinal)
                                           && snapshot.Number == o.XmlNumber
                                           && string.Equals(snapshot.Language, o.XmlLanguage, StringComparison.Ordinal)
                                           && snapshot.IsInstanceDb == expectedIsInstance
                                           && string.Equals(snapshot.InstanceOfName, expectedInstanceOf, StringComparison.Ordinal)
                                           && attributeMatch
                                           && !string.IsNullOrEmpty(snapshot.Revision)
                                           && !string.IsNullOrEmpty(snapshot.SourceProfile);
                    if (!o.SnapshotConsistent)
                    {
                        o.CheckFailures.Add("snapshot mismatch: name='" + snapshot.Name + "/" + o.XmlName
                                            + "' number=" + snapshot.Number + "/" + o.XmlNumber
                                            + " language='" + snapshot.Language + "/" + o.XmlLanguage
                                            + "' instanceDb=" + snapshot.IsInstanceDb + "/" + expectedIsInstance
                                            + " instanceOf='" + snapshot.InstanceOfName + "/" + expectedInstanceOf
                                            + "' attributesMatch=" + attributeMatch
                                            + " revision=" + snapshot.Revision
                                            + " sourceProfile=" + snapshot.SourceProfile);
                    }
                }
                catch (Exception ex)
                {
                    o.SnapshotParsed = false;
                    o.CheckFailures.Add("snapshot parse failed: " + ex.Message);
                }

                // The fidelity contract counts networks the canonical parser produced, not raw
                // CompileUnit elements: an export may carry an empty compile unit with no body.
                try
                {
                    o.ParsedNetworkCount = IsGraphLanguage(o.XmlLanguage)
                        ? 1
                        : TIAOpennessStudio.Services.Lad.LadBlockParser
                            .Parse(xml, "probe", LadSchemaRegistry.Resolve(TiaVersionAdapter.CurrentMajorVersion))
                            .Networks.Count;
                }
                catch (Exception ex)
                {
                    o.ParsedNetworkCount = -1;
                    o.CheckFailures.Add("canonical network parse failed: " + ex.Message);
                }
            }

            o.IrParsed = TiaXmlAgentReferenceBuilder.TryBuildJsonIr(xml, out var irJson, out _);
            if (!o.IrParsed)
            {
                o.CheckFailures.Add("TryBuildJsonIr returned false");
                return o;
            }

            o.IrLength = irJson?.Length ?? 0;

            JsonObject ir = null;
            try { ir = JsonNode.Parse(irJson) as JsonObject; }
            catch (Exception ex) { o.CheckFailures.Add("ir parse failed: " + ex.Message); }

            if (ir == null) return o;

            var isGraph = IsGraphLanguage(StrOf(ir["programmingLanguage"]));
            if (isGraph)
            {
                o.IrKind = StrOf(ir["blockType"]);
                o.IrLanguage = "GRAPH";
                o.BlockName = StrOf(ir["blockName"]);
                o.BlockNumber = IntOf(ir["blockNumber"]);
                o.IrNetworkCount = 1;
                o.IrMembers = CollectMembersFromGraphIr(ir);
            }
            else
            {
                o.IrKind = StrOf(ir["kind"]);
                o.IrLanguage = StrOf(ir["language"]);
                o.BlockName = StrOf(ir["blockName"]);
                o.BlockNumber = IntOf(ir["number"]);
                var networks = ir["networks"] as JsonArray;
                o.IrNetworkCount = networks?.Count ?? 0;
                o.IrMembers = CollectMembersFromCanonicalIr(ir);
                o.IrXmlBlockKind = StrOf(ir["xmlBlockKind"]);
                o.IrIsInstanceDb = BoolOf(ir["isInstanceDb"]);
                o.IrInstanceOfName = StrOf(ir["instanceOfName"]);
                o.IrBlockAttributes = ReadIrBlockAttributes(ir["blockAttributes"] as JsonObject);
            }

            var fidelity = ir["fidelity"] as JsonObject;
            o.FidelityTruncated = BoolOf(fidelity?["truncated"]);
            o.FidelityTotal = IntOf(fidelity?["totalNetworks"]);
            o.FidelityReturned = IntOf(fidelity?["returnedCount"]);

            o.InterfaceHash = Sha256OrEmpty(ir["interface"]?.ToJsonString());
            o.BodyHash = ComputeBodyHash(ir, isGraph);

            var irInterfaceJson = ir["interface"]?.ToJsonString();
            o.ProjectionsConsistent = CheckProjections(o, isGraph, irInterfaceJson);
            if (!o.ProjectionsConsistent)
            {
                o.CheckFailures.Add("projection mismatch: xmlShaMatch=" + (o.ProjXmlSha == o.XmlSha256)
                                    + " interfaceMatch=" + (isGraph
                                        ? o.ProjInterfaceJson == null
                                        : JsonEquals(o.ProjInterfaceJson, irInterfaceJson))
                                    + " sourceApplicable=" + (o.XmlTextNetworkCount > 0)
                                    + " sourcePresent=" + (o.ProjSourceText != null)
                                    + " sourceNetworks=" + o.ProjSourceNetworks + "/" + o.XmlTextNetworkCount);
            }

            // T6: IR block metadata must agree with the snapshot (kind / instance DB / attributes).
            if (isGraph)
            {
                o.IrMetadataConsistent = true;
            }
            else
            {
                var expectedKind = string.Equals(o.SnapshotBlockType, "UDT", StringComparison.Ordinal)
                    ? "UDT"
                    : (o.SnapshotXmlBlockKind ?? "");
                var kindOk = string.Equals(o.IrKind, expectedKind, StringComparison.Ordinal);
                var instanceOk = o.IrIsInstanceDb == o.SnapshotIsInstanceDb;
                var instanceNameOk = string.Equals(o.IrInstanceOfName, o.SnapshotInstanceOfName, StringComparison.Ordinal);
                var attributesOk = ListsEqual(o.SnapshotAttributes, o.IrBlockAttributes);
                o.IrMetadataConsistent = kindOk && instanceOk && instanceNameOk && attributesOk;
                if (!o.IrMetadataConsistent)
                {
                    o.CheckFailures.Add("ir metadata mismatch: kind='" + o.IrKind + "/" + expectedKind
                                        + "' isInstanceDb=" + o.IrIsInstanceDb + "/" + o.SnapshotIsInstanceDb
                                        + " instanceOf='" + o.IrInstanceOfName + "/" + o.SnapshotInstanceOfName
                                        + "' attributesMatch=" + attributesOk);
                }
            }

            // T7: identity patch round-trips — patching a subtree back must not change anything else.
            if (!isGraph && snapshot != null)
            {
                var networks = ir["networks"] as JsonArray;
                if (networks != null && networks.Count > 0 && networks[0] is JsonObject firstNetwork)
                {
                    o.PatchNetworkApplicable = true;
                    o.PatchNetworkOk = RunNetworkPatchProbe(snapshot, ir, firstNetwork, out var networkError);
                    if (!o.PatchNetworkOk)
                        o.PatchErrors.Add("network patch: " + networkError);
                }

                if (o.ProjInterfaceJson != null)
                {
                    o.PatchInterfaceApplicable = true;
                    o.PatchInterfaceOk = RunInterfacePatchProbe(snapshot, o.ProjInterfaceJson, out var interfaceError);
                    if (!o.PatchInterfaceOk)
                        o.PatchErrors.Add("interface patch: " + interfaceError);
                }

                o.PatchNegativeOk = RunPatchNegativeProbe(snapshot, isGraph, ir, o.ProjInterfaceJson);
                if (!o.PatchNegativeOk)
                    o.PatchErrors.Add("negative guards (revision/version) did not fail closed");

                o.PatchChecksPassed = (!o.PatchNetworkApplicable || o.PatchNetworkOk)
                                      && (!o.PatchInterfaceApplicable || o.PatchInterfaceOk)
                                      && o.PatchNegativeOk;
                if (!o.PatchChecksPassed)
                    o.CheckFailures.Add("patch checks failed: " + string.Join("; ", o.PatchErrors));
            }
            else
            {
                // GRAPH still has to reject body patches explicitly.
                var graphPatch = BlockPatchService.PatchNetwork(snapshot, snapshot.Revision, "", 1, "{}");
                o.PatchNegativeOk = graphPatch.ErrorCode == "graph_unsupported";
                o.PatchChecksPassed = o.PatchNegativeOk;
                if (!o.PatchChecksPassed)
                    o.CheckFailures.Add("graph patch guard failed: " + graphPatch.ErrorCode);
            }

            o.WriteGuardsPassed = CheckWriteGuards(o, ir);
            if (!o.WriteGuardsPassed)
                o.CheckFailures.Add("build guards failed: " + string.Join("; ", o.GuardErrors));

            o.CapabilityChecksOk = CheckVersionCapabilities(o);
            if (!o.CapabilityChecksOk)
                o.CheckFailures.Add("version capabilities failed: " + string.Join("; ", o.CapabilityErrors));

            o.MembersMatch = ListsEqual(o.XmlMembers, o.IrMembers);
            if (!o.MembersMatch)
                o.CheckFailures.Add("member mismatch: " + ListDiff(o.XmlMembers, o.IrMembers));

            o.FidelityConsistent = o.ParsedNetworkCount >= 0
                                   && o.FidelityTotal == o.ParsedNetworkCount
                                   && (o.FidelityTruncated
                                       ? o.FidelityReturned > 0 && o.FidelityReturned < o.FidelityTotal
                                       : o.FidelityReturned == o.FidelityTotal);
            if (!o.FidelityConsistent)
                o.CheckFailures.Add("fidelity inconsistent: total=" + o.FidelityTotal
                                    + " parsedNetworks=" + o.ParsedNetworkCount
                                    + " xmlCompileUnits=" + o.XmlNetworkCount
                                    + " returned=" + o.FidelityReturned
                                    + " truncated=" + o.FidelityTruncated);

            // Network-range paging (non-GRAPH only: GRAPH has no network list to page).
            if (!isGraph && o.ParsedNetworkCount >= 2)
            {
                o.RangePageApplicable = true;
                var rangeOk = TiaXmlAgentReferenceBuilder.TryBuildJsonIrRange(xml, 2, 3, out var rangeJson, out _);
                if (!rangeOk)
                {
                    o.RangePageOk = false;
                    o.CheckFailures.Add("range page conversion failed");
                }
                else
                {
                    var range = JsonNode.Parse(rangeJson) as JsonObject;
                    var rf = range?["fidelity"] as JsonObject;
                    var from = IntOf(rf?["returnedFrom"]);
                    var returned = IntOf(rf?["returnedCount"]);
                    var expected = Math.Min(3, o.ParsedNetworkCount - 1);
                    o.RangePageOk = from == 2 && returned == expected;
                    if (!o.RangePageOk)
                        o.CheckFailures.Add("range page mismatch: from=" + from
                                            + " returned=" + returned + " expected=" + expected);
                }
            }

            return o;
        }

        private static bool IsGraphLanguage(string language)
        {
            return string.Equals((language ?? "").Trim(), "GRAPH", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTextLanguage(string language)
        {
            var text = (language ?? "").Trim();
            return text.EndsWith("SCL", StringComparison.OrdinalIgnoreCase)
                   || text.EndsWith("STL", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadChildText(XElement parent, string childName)
        {
            var child = parent?.Elements().FirstOrDefault(e => e.Name.LocalName == childName);
            return child?.Value?.Trim() ?? "";
        }

        private static List<string> ReadBlockAttributes(XElement block)
        {
            var result = new List<string>();
            var attrList = AttributeListOf(block);
            if (attrList == null) return result;

            foreach (var element in attrList.Elements())
            {
                var name = element.Name.LocalName;
                var value = element.HasElements
                    ? "<" + name + ":" + element.Elements().Count() + ">"
                    : (element.Value ?? "").Trim();
                result.Add(name + "=" + value);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Member extraction: XML vs IR
        // ─────────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, string> GraphSectionNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Input"] = "Input", ["In"] = "Input",
                ["Output"] = "Output", ["Out"] = "Output",
                ["InOut"] = "InOut", ["In_Out"] = "InOut",
                ["Static"] = "Static", ["Stat"] = "Static",
                ["Temp"] = "Temp", ["Var_Temp"] = "Temp"
            };

        private static List<string> CollectMembersFromXml(XElement block, bool graphMode)
        {
            var result = new List<string>();
            var iface = block.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interface");
            var sections = iface?.Elements().FirstOrDefault(e => e.Name.LocalName == "Sections");
            if (sections == null) return result;

            foreach (var section in sections.Elements().Where(e => e.Name.LocalName == "Section"))
            {
                var rawSection = section.Attribute("Name")?.Value ?? "";
                if (graphMode)
                {
                    string canonical;
                    if (!GraphSectionNames.TryGetValue(rawSection, out canonical)) continue;
                    foreach (var member in section.Elements().Where(e => e.Name.LocalName == "Member"))
                    {
                        var name = member.Attribute("Name")?.Value ?? "";
                        var datatype = ReadMemberDatatype(member);
                        if (GraphSystemMembers.IsSystemMember(rawSection, name, datatype)) continue;
                        result.Add(canonical + ":" + name + ":" + datatype);
                    }
                }
                else
                {
                    foreach (var member in section.Elements().Where(e => e.Name.LocalName == "Member"))
                        WalkXmlMember(member, rawSection, "", result);
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static void WalkXmlMember(XElement member, string section, string parentPath, List<string> result)
        {
            var name = member.Attribute("Name")?.Value ?? "";
            var datatype = ReadMemberDatatype(member);
            var path = parentPath.Length == 0 ? name : parentPath + "." + name;
            result.Add(section + ":" + path + ":" + datatype);

            foreach (var nestedSections in member.Elements().Where(e => e.Name.LocalName == "Sections"))
            {
                foreach (var nestedSection in nestedSections.Elements().Where(e => e.Name.LocalName == "Section"))
                {
                    foreach (var child in nestedSection.Elements().Where(e => e.Name.LocalName == "Member"))
                        WalkXmlMember(child, section, path, result);
                }
            }
        }

        private static string ReadMemberDatatype(XElement member)
        {
            var datatype = member.Attribute("Datatype")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(datatype)) return datatype;

            var datatypeElement = member.Elements().FirstOrDefault(e => e.Name.LocalName == "Datatype");
            if (datatypeElement != null) return datatypeElement.Value.Trim();

            var typeElement = member.Elements().FirstOrDefault(e => e.Name.LocalName == "Type");
            return typeElement?.Attribute("Name")?.Value?.Trim() ?? "";
        }

        private static List<string> CollectMembersFromCanonicalIr(JsonObject ir)
        {
            var result = new List<string>();
            var iface = ir["interface"] as JsonObject;
            if (iface == null) return result;

            var sections = FindChild(iface, "Sections");
            if (sections == null) return result;

            foreach (var sectionNode in ChildrenOf(sections))
            {
                if (!KindEquals(sectionNode, "Section")) continue;
                var sectionName = StrOf(AttrsOf(sectionNode)?["Name"]);
                foreach (var memberNode in ChildrenOf(sectionNode))
                {
                    if (KindEquals(memberNode, "Member"))
                        WalkIrMember(memberNode, sectionName, "", result);
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static void WalkIrMember(JsonObject member, string section, string parentPath, List<string> result)
        {
            var name = StrOf(AttrsOf(member)?["Name"]);
            var datatype = StrOf(AttrsOf(member)?["Datatype"]);
            var path = parentPath.Length == 0 ? name : parentPath + "." + name;
            result.Add(section + ":" + path + ":" + datatype);

            foreach (var child in ChildrenOf(member))
            {
                if (!KindEquals(child, "Sections")) continue;
                foreach (var nestedSection in ChildrenOf(child))
                {
                    if (!KindEquals(nestedSection, "Section")) continue;
                    foreach (var nestedMember in ChildrenOf(nestedSection))
                    {
                        if (KindEquals(nestedMember, "Member"))
                            WalkIrMember(nestedMember, section, path, result);
                    }
                }
            }
        }

        private static List<string> CollectMembersFromGraphIr(JsonObject ir)
        {
            var result = new List<string>();
            var sectionMap = new[] { ("inputs", "Input"), ("outputs", "Output"), ("inOuts", "InOut"),
                                     ("statics", "Static"), ("temps", "Temp") };

            foreach (var pair in sectionMap)
            {
                var array = ir[pair.Item1] as JsonArray;
                if (array == null) continue;
                foreach (var item in array)
                {
                    if (!(item is JsonObject member)) continue;
                    var name = StrOf(member["name"]);
                    var datatype = StrOf(member["datatype"]);
                    result.Add(pair.Item2 + ":" + name + ":" + datatype);
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>Flatten the IR blockAttributes object into the same "Name=value" list as the XML attributes.</summary>
        private static List<string> ReadIrBlockAttributes(JsonObject attributes)
        {
            var result = new List<string>();
            if (attributes == null) return result;

            foreach (var pair in attributes)
                result.Add(pair.Key + "=" + StrOf(pair.Value));

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static JsonObject AttrsOf(JsonObject node)
        {
            return node?["attrs"] as JsonObject;
        }

        private static IEnumerable<JsonObject> ChildrenOf(JsonObject node)
        {
            var children = node?["children"] as JsonArray;
            if (children == null) yield break;
            foreach (var child in children)
            {
                if (child is JsonObject obj) yield return obj;
            }
        }

        private static JsonObject FindChild(JsonObject node, string kind)
        {
            return ChildrenOf(node).FirstOrDefault(child => KindEquals(child, kind));
        }

        private static bool KindEquals(JsonObject node, string kind)
        {
            return string.Equals(StrOf(node?["kind"]), kind, StringComparison.OrdinalIgnoreCase);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Hashing / JSON helpers
        // ─────────────────────────────────────────────────────────────────────

        private static bool RunNetworkPatchProbe(
            BlockSnapshot snapshot, JsonObject ir, JsonObject network, out string error)
        {
            error = null;
            var bodyJson = network["body"]?.ToJsonString();
            if (string.IsNullOrWhiteSpace(bodyJson))
            {
                error = "network has no body to patch";
                return false;
            }

            var patch = BlockPatchService.PatchNetwork(
                snapshot, snapshot.Revision,
                StrOf(network["compileUnitId"]), IntOf(network["order"]), bodyJson);
            if (!patch.Ok)
            {
                error = patch.ErrorCode + ": " + patch.Message;
                return false;
            }

            if (!BlockSnapshotService.BuildIr(patch.PatchedXml, 1, 0, out var patchedIrJson, out _))
            {
                error = "patched XML cannot be projected back";
                return false;
            }
            var patched = JsonNode.Parse(patchedIrJson) as JsonObject;
            if (patched == null)
            {
                error = "patched IR is not a JSON object";
                return false;
            }

            if (!JsonEquals(patched["interface"]?.ToJsonString(), ir["interface"]?.ToJsonString()))
            {
                error = "interface changed after a network patch";
                return false;
            }
            if (!string.Equals(ComputeBodyHash(patched, false), ComputeBodyHash(ir, false), StringComparison.Ordinal))
            {
                error = "network bodies changed after a network patch";
                return false;
            }
            if (!string.Equals(StrOf(patched["kind"]), StrOf(ir["kind"]), StringComparison.Ordinal)
                || BoolOf(patched["isInstanceDb"]) != BoolOf(ir["isInstanceDb"])
                || !string.Equals(StrOf(patched["instanceOfName"]), StrOf(ir["instanceOfName"]), StringComparison.Ordinal)
                || !string.Equals(StrOf(patched["language"]), StrOf(ir["language"]), StringComparison.Ordinal))
            {
                error = "block metadata changed after a network patch";
                return false;
            }
            return true;
        }

        private static bool RunInterfacePatchProbe(
            BlockSnapshot snapshot, string interfaceJson, out string error)
        {
            error = null;
            var patch = BlockPatchService.PatchInterface(snapshot, snapshot.Revision, interfaceJson);
            if (!patch.Ok)
            {
                error = patch.ErrorCode + ": " + patch.Message;
                return false;
            }

            BlockSnapshot patchedSnapshot;
            try
            {
                patchedSnapshot = BlockSnapshotService.FromXml(patch.PatchedXml);
            }
            catch (Exception ex)
            {
                error = "patched XML cannot be re-read: " + ex.Message;
                return false;
            }

            var patchedInterface = BlockSnapshotService.BuildInterface(patchedSnapshot);
            if (!JsonEquals(patchedInterface, interfaceJson))
            {
                error = "interface tree changed after an interface patch";
                return false;
            }
            if (!string.Equals(patchedSnapshot.Name, snapshot.Name, StringComparison.Ordinal)
                || patchedSnapshot.Number != snapshot.Number
                || !string.Equals(patchedSnapshot.Language, snapshot.Language, StringComparison.Ordinal))
            {
                error = "block identity changed after an interface patch";
                return false;
            }
            if (!ScalarAttributesEqual(snapshot, patchedSnapshot))
            {
                error = "block scalar attributes changed after an interface patch";
                return false;
            }
            if (patchedSnapshot.IsInstanceDb != snapshot.IsInstanceDb
                || !string.Equals(patchedSnapshot.InstanceOfName, snapshot.InstanceOfName, StringComparison.Ordinal))
            {
                error = "instance DB identity changed after an interface patch";
                return false;
            }
            return true;
        }

        /// <summary>T9: build_* guards must reject truncated IR and instance DBs, and keep MemoryLayout.</summary>
        private static bool CheckWriteGuards(FixtureObservation o, JsonObject ir)
        {
            JsonObject envelope;
            try { envelope = new JsonObject { ["content"] = JsonNode.Parse(ir.ToJsonString()) }; }
            catch { envelope = null; }

            var expectTruncated = o.Id != null && o.Id.EndsWith("Lad_Large_Truncated", StringComparison.Ordinal);
            var truncatedRaw = BlockWriteGuards.TryRejectTruncatedIr(ir, out _);
            var truncatedEnvelope = envelope != null && BlockWriteGuards.TryRejectTruncatedIr(envelope, out _);
            o.TruncatedGuardOk = truncatedRaw == expectTruncated && truncatedEnvelope == expectTruncated;
            if (!o.TruncatedGuardOk)
                o.GuardErrors.Add("truncated guard raw=" + truncatedRaw + " envelope=" + truncatedEnvelope
                                  + " expected=" + expectTruncated);

            var expectInstance = o.Id != null && o.Id.EndsWith("InstanceDb", StringComparison.Ordinal);
            var instanceRaw = BlockWriteGuards.TryRejectInstanceDb(ir, out _);
            var instanceEnvelope = envelope != null && BlockWriteGuards.TryRejectInstanceDb(envelope, out _);
            o.InstanceGuardOk = instanceRaw == expectInstance && instanceEnvelope == expectInstance;
            if (!o.InstanceGuardOk)
                o.GuardErrors.Add("instance guard raw=" + instanceRaw + " envelope=" + instanceEnvelope
                                  + " expected=" + expectInstance);

            var expectedLayout = "Standard";
            foreach (var attribute in o.SnapshotAttributes)
            {
                const string prefix = "MemoryLayout=";
                if (!attribute.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var value = attribute.Substring(prefix.Length);
                if (string.Equals(value, "Optimized", StringComparison.OrdinalIgnoreCase))
                    expectedLayout = "Optimized";
                break;
            }

            var resolved = BlockWriteGuards.TryResolveMemoryLayout(ir, null, out var layout, out _)
                           && string.Equals(layout, expectedLayout, StringComparison.Ordinal);
            var explicitOverride = BlockWriteGuards.TryResolveMemoryLayout(ir, "Standard", out var forced, out _)
                                   && string.Equals(forced, "Standard", StringComparison.Ordinal);
            var invalidRejected = !BlockWriteGuards.TryResolveMemoryLayout(ir, "Compact", out _, out _);
            o.MemoryLayoutGuardOk = resolved && explicitOverride && invalidRejected;
            if (!o.MemoryLayoutGuardOk)
                o.GuardErrors.Add("memory layout guard resolved=" + resolved + " ('" + layout + "' vs '"
                                  + expectedLayout + "') explicit=" + explicitOverride
                                  + " invalidRejected=" + invalidRejected);

            return o.TruncatedGuardOk && o.InstanceGuardOk && o.MemoryLayoutGuardOk;
        }

        /// <summary>
        /// T10: the version facts table must be the only capability source, and builders must obey it:
        /// ExternalWritable is version-gated, block-level attributes are emitted only where a real XML
        /// import verified them, SystemDefined materialization uses the single codec decision.
        /// </summary>
        private static bool CheckVersionCapabilities(FixtureObservation o)
        {
            var facts = ResolveProfileFacts(o.SnapshotSourceProfile);
            var ok = true;
            void Fail(string message)
            {
                ok = false;
                o.CapabilityErrors.Add(message);
            }

            if (facts.MajorVersion <= 20 && facts.UdtMembersSupportExternalWritable)
                Fail("UDT ExternalWritable must be disabled before V21 (" + facts.DisplayVersion + ").");
            if (facts.MajorVersion >= 21 && !facts.UdtMembersSupportExternalWritable)
                Fail("UDT ExternalWritable must be enabled from V21 (" + facts.DisplayVersion + ").");
            if (facts.DbMembersSupportExternalWritable)
                Fail("DB member ExternalWritable is not declared for any known version.");
            // Block attributes are import-verified only: a version that has not been proven by a real
            // import must not claim them (real V15.1 rejects MemoryLayout and SetENOAutomatically).
            if (facts.SupportsBlockAttribute("MemoryLayout")
                || facts.SupportsBlockAttribute("SetENOAutomatically"))
                Fail("Block attributes claim XML-import support on " + facts.DisplayVersion
                     + ", which no real import has verified.");
            if (facts.SupportsBlockAttribute("Namespace"))
                Fail("Namespace must not be treated as a block attribute.");

            var sameProfile = !NetworkBodyCodec.ShouldMaterializeSystemDefined(facts, facts);
            var v16 = LadSchemaRegistry.Resolve(16);
            var expectedCross = facts.SystemDefinedDefault != v16.SystemDefinedDefault;
            var crossProfile = NetworkBodyCodec.ShouldMaterializeSystemDefined(facts, v16) == expectedCross;
            if (!sameProfile || !crossProfile)
                Fail("SystemDefined materialization decision is inconsistent for " + facts.DisplayVersion + ".");

            // UDT builder: plain trees stay clean on V15.1; a true ExternalWritable fails closed.
            if (o.ProjInterfaceJson != null
                && string.Equals(o.SnapshotBlockType, "UDT", StringComparison.Ordinal))
            {
                try
                {
                    var node = TIAOpennessStudio.Services.Lad.LadJson.NodeFromJson(
                        JsonNode.Parse(o.ProjInterfaceJson) as JsonObject);
                    if (o.Id != null && o.Id.EndsWith("Udt_ExternalWritable", StringComparison.Ordinal))
                    {
                        try
                        {
                            PlcUdtXmlBuilder.BuildDocument(o.SnapshotName, node);
                            Fail("UDT ExternalWritable=true was not rejected on " + facts.DisplayVersion + ".");
                        }
                        catch (ArgumentException)
                        {
                            // expected: fail closed instead of emitting an unsupported attribute
                        }
                    }
                    else
                    {
                        var document = PlcUdtXmlBuilder.BuildDocument(o.SnapshotName, node);
                        if (document.ToString().IndexOf("ExternalWritable", StringComparison.OrdinalIgnoreCase) >= 0)
                            Fail("UDT build emitted ExternalWritable on " + facts.DisplayVersion + ".");
                    }
                }
                catch (Exception ex)
                {
                    Fail("UDT build capability check failed: " + ex.Message);
                }
            }

            // DB builder: the MemoryLayout element must only appear where the version accepts it on import.
            if (o.ProjInterfaceJson != null
                && string.Equals(o.SnapshotBlockType, "DB", StringComparison.Ordinal)
                && !string.Equals(o.SnapshotXmlBlockKind, "InstanceDB", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var node = TIAOpennessStudio.Services.Lad.LadJson.NodeFromJson(
                        JsonNode.Parse(o.ProjInterfaceJson) as JsonObject);
                    CheckDbLayout(o.SnapshotName, o.BlockNumber, node, "Optimized", facts, Fail);
                    CheckDbLayout(o.SnapshotName, o.BlockNumber, node, "Standard", facts, Fail);
                }
                catch (Exception ex)
                {
                    Fail("DB build capability check failed: " + ex.Message);
                }
            }

            return ok;
        }

        private static void CheckDbLayout(string name, int number, LadNode node, string expected,
            LadSchemaFacts facts, Action<string> fail)
        {
            var document = PlcGlobalDbXmlBuilder.BuildDocument(name, number, node, expected);
            var layoutElement = document.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "MemoryLayout", StringComparison.Ordinal));
            if (facts.SupportsBlockAttribute("MemoryLayout"))
            {
                if (!string.Equals(layoutElement?.Value, expected, StringComparison.Ordinal))
                    fail("DB MemoryLayout should be '" + expected + "' but was '" + (layoutElement?.Value ?? "") + "'.");
            }
            else if (layoutElement != null)
            {
                fail("DB build must not emit MemoryLayout on " + facts.DisplayVersion
                     + " (its XML import rejects the element).");
            }
        }

        private static LadSchemaFacts ResolveProfileFacts(string profile)
        {
            var text = (profile ?? "").Trim();
            if (text.StartsWith("V", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            var major = 0;
            int.TryParse(text.Split('.')[0], out major);
            return LadSchemaRegistry.Resolve(major);
        }

        private static bool ScalarAttributesEqual(BlockSnapshot left, BlockSnapshot right)
        {
            var leftList = left.Attributes
                .Where(a => !a.IsComplex)
                .Select(a => a.Name + "=" + a.Value)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            var rightList = right.Attributes
                .Where(a => !a.IsComplex)
                .Select(a => a.Name + "=" + a.Value)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            return ListsEqual(leftList, rightList);
        }

        /// <summary>Fail-closed guards: missing/stale revision, and cross-version patch refusal.</summary>
        private static bool RunPatchNegativeProbe(
            BlockSnapshot snapshot, bool isGraph, JsonObject ir, string interfaceJson)
        {
            const string staleRevision = "0000000000000000000000000000000000000000000000000000000000000000";

            JsonObject firstNetwork = null;
            var networks = ir["networks"] as JsonArray;
            if (networks != null && networks.Count > 0)
                firstNetwork = networks[0] as JsonObject;
            var bodyJson = firstNetwork?["body"]?.ToJsonString();

            if (!isGraph && bodyJson != null)
            {
                var missing = BlockPatchService.PatchNetwork(snapshot, "", "", 1, bodyJson);
                if (missing.ErrorCode != "revision_required") return false;

                var stale = BlockPatchService.PatchNetwork(snapshot, staleRevision, "", 1, bodyJson);
                if (stale.ErrorCode != "revision_mismatch") return false;
            }

            if (interfaceJson != null)
            {
                var staleInterface = BlockPatchService.PatchInterface(snapshot, staleRevision, interfaceJson);
                if (staleInterface.ErrorCode != "revision_mismatch") return false;
            }

            if (string.IsNullOrEmpty(snapshot.SourceProfile))
                return true;

            var savedMajor = TiaVersionAdapter.CurrentMajorVersion;
            var savedVersion = TiaVersionAdapter.CurrentVersion;
            try
            {
                // Pretend the attached TIA is a different version; the patch must be refused.
                TiaVersionAdapter.CurrentMajorVersion = savedMajor + 1;
                TiaVersionAdapter.CurrentVersion = "V" + (savedMajor + 1);
                var crossVersion = BlockPatchService.PatchInterface(snapshot, snapshot.Revision, interfaceJson ?? "{}");
                if (crossVersion.ErrorCode != "version_mismatch") return false;
            }
            finally
            {
                TiaVersionAdapter.CurrentMajorVersion = savedMajor;
                TiaVersionAdapter.CurrentVersion = savedVersion;
            }
            return true;
        }

        private static bool CheckProjections(FixtureObservation o, bool isGraph, string irInterfaceJson)
        {
            if (!string.Equals(o.ProjXmlSha, o.XmlSha256, StringComparison.Ordinal)) return false;

            if (isGraph)
            {
                if (o.ProjInterfaceJson != null) return false;
            }
            else if (!JsonEquals(o.ProjInterfaceJson, irInterfaceJson))
            {
                return false;
            }

            if (o.XmlTextNetworkCount > 0)
            {
                if (o.ProjSourceText == null) return false;
                if (o.ProjSourceNetworks != o.XmlTextNetworkCount) return false;
            }
            else if (o.ProjSourceText != null)
            {
                return false;
            }

            return true;
        }

        private static int CountSourceHeaders(string sourceText)
        {
            if (string.IsNullOrEmpty(sourceText)) return 0;
            var count = 0;
            var index = 0;
            const string marker = "// Network ";
            while ((index = sourceText.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += marker.Length;
            }
            return count;
        }

        private static bool JsonEquals(string left, string right)
        {
            if (left == null && right == null) return true;
            if (left == null || right == null) return false;
            try
            {
                return JsonNodeEquals(JsonNode.Parse(left), JsonNode.Parse(right));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Semantic JSON comparison (objects are order-insensitive, arrays are ordered).
        /// System.Text.Json 6 has no JsonNode.DeepEquals, so keep this local and explicit.
        /// </summary>
        private static bool JsonNodeEquals(JsonNode left, JsonNode right)
        {
            if (left == null && right == null) return true;
            if (left == null || right == null) return false;

            if (left is JsonObject leftObject && right is JsonObject rightObject)
            {
                if (leftObject.Count != rightObject.Count) return false;
                foreach (var pair in leftObject)
                {
                    if (!rightObject.TryGetPropertyValue(pair.Key, out var rightValue)) return false;
                    if (!JsonNodeEquals(pair.Value, rightValue)) return false;
                }
                return true;
            }

            if (left is JsonArray leftArray && right is JsonArray rightArray)
            {
                if (leftArray.Count != rightArray.Count) return false;
                for (var i = 0; i < leftArray.Count; i++)
                {
                    if (!JsonNodeEquals(leftArray[i], rightArray[i])) return false;
                }
                return true;
            }

            return string.Equals(left.ToJsonString(), right.ToJsonString(), StringComparison.Ordinal);
        }

        private static string ComputeBodyHash(JsonObject ir, bool isGraph)
        {
            if (isGraph)
                return Sha256OrEmpty(ir["sequence"]?.ToJsonString());

            var networks = ir["networks"] as JsonArray;
            if (networks == null || networks.Count == 0) return Sha256OrEmpty("");

            var sb = new StringBuilder();
            foreach (var network in networks)
            {
                if (!(network is JsonObject net)) continue;
                sb.Append(StrOf(net["language"]));
                sb.Append('|');
                sb.Append(IntOf(net["order"]));
                sb.Append('|');
                sb.Append(net["body"]?.ToJsonString() ?? "");
                sb.Append('\n');
            }
            return Sha256OrEmpty(sb.ToString());
        }

        private static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Real-machine guard for patch-first: TIA stamps &lt;DocumentInfo&gt;&lt;Created&gt; on every
        /// export. Simulate a second export by replacing only that timestamp and require the
        /// revision to stay identical; otherwise a base_revision read would be stale on arrival.
        /// </summary>
        private static bool CheckRevisionStable(string xml, BlockSnapshot snapshot, out string error)
        {
            error = null;
            var variant = ReplaceCreatedTimestamp(xml);
            if (variant == null) return true; // no Created node in this fixture: nothing to vary

            BlockSnapshot second;
            try
            {
                second = BlockSnapshotService.FromXml(variant, snapshot.BlockId, snapshot.ProjectKey);
            }
            catch (Exception ex)
            {
                error = "revision stability: re-export parse failed: " + ex.Message;
                return false;
            }

            if (!string.Equals(snapshot.Revision, second.Revision, StringComparison.Ordinal))
            {
                error = "revision depends on the export timestamp: rev1=" + Short(snapshot.Revision)
                        + " rev2=" + Short(second.Revision);
                return false;
            }
            return true;
        }

        /// <summary>Replace the text of the first &lt;Created&gt; element; null when the fixture has none.</summary>
        private static string ReplaceCreatedTimestamp(string xml)
        {
            const string openTag = "<Created>";
            const string closeTag = "</Created>";
            var start = xml.IndexOf(openTag, StringComparison.Ordinal);
            if (start < 0) return null;
            var end = xml.IndexOf(closeTag, start, StringComparison.Ordinal);
            if (end < 0) return null;

            return xml.Substring(0, start + openTag.Length)
                   + "2000-01-01T00:00:00.0000000Z"
                   + xml.Substring(end);
        }

        private static string Short(string revision)
        {
            return string.IsNullOrEmpty(revision) ? "-" : revision.Substring(0, Math.Min(12, revision.Length));
        }

        private static string Sha256OrEmpty(string text)
        {
            return Sha256(text ?? "");
        }

        private static string StrOf(JsonNode node)
        {
            if (node is JsonValue value)
            {
                try { return value.GetValue<string>() ?? ""; }
                catch { return ""; }
            }
            return "";
        }

        private static int IntOf(JsonNode node)
        {
            if (node is JsonValue value)
            {
                try { return value.GetValue<int>(); }
                catch { return 0; }
            }
            return 0;
        }

        private static bool BoolOf(JsonNode node)
        {
            if (node is JsonValue value)
            {
                try { return value.GetValue<bool>(); }
                catch { return false; }
            }
            return false;
        }

        private static bool ListsEqual(List<string> left, List<string> right)
        {
            if (left == null || right == null) return left == right;
            if (left.Count != right.Count) return false;
            for (var i = 0; i < left.Count; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static string ListDiff(List<string> baseline, List<string> current)
        {
            var onlyBaseline = baseline.Except(current, StringComparer.Ordinal).Take(4).ToList();
            var onlyCurrent = current.Except(baseline, StringComparer.Ordinal).Take(4).ToList();
            return "onlyInXml=[" + string.Join(", ", onlyBaseline) + "] onlyInIr=["
                   + string.Join(", ", onlyCurrent) + "]";
        }

        // ─────────────────────────────────────────────────────────────────────
        // Baseline write / compare
        // ─────────────────────────────────────────────────────────────────────

        private static JsonObject ToBaselineJson(FixtureObservation o)
        {
            return new JsonObject
            {
                ["id"] = o.Id,
                ["origin"] = o.Origin,
                ["version"] = o.DisplayVersion,
                ["xmlSha256"] = o.XmlSha256,
                ["xmlLength"] = o.XmlLength,
                ["xmlBlockElement"] = o.XmlBlockElement ?? "",
                ["xmlLanguage"] = o.XmlLanguage ?? "",
                ["xmlName"] = o.XmlName ?? "",
                ["xmlNumber"] = o.XmlNumber,
                ["xmlNetworkCount"] = o.XmlNetworkCount,
                ["parsedNetworkCount"] = o.ParsedNetworkCount,
                ["blockAttributes"] = new JsonArray(o.BlockAttributes.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                ["snapshot"] = new JsonObject
                {
                    ["parsed"] = o.SnapshotParsed,
                    ["blockType"] = o.SnapshotBlockType ?? "",
                    ["xmlBlockKind"] = o.SnapshotXmlBlockKind ?? "",
                    ["language"] = o.SnapshotLanguage ?? "",
                    ["name"] = o.SnapshotName ?? "",
                    ["isInstanceDb"] = o.SnapshotIsInstanceDb,
                    ["instanceOfName"] = o.SnapshotInstanceOfName ?? "",
                    ["sourceProfile"] = o.SnapshotSourceProfile ?? "",
                    ["revision"] = o.SnapshotRevision ?? "",
                    ["stableAcrossExports"] = o.RevisionStable,
                    ["attributes"] = new JsonArray(o.SnapshotAttributes.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                    ["consistent"] = o.SnapshotConsistent
                },
                ["xmlTextNetworkCount"] = o.XmlTextNetworkCount,
                ["projections"] = new JsonObject
                {
                    ["xmlSha256"] = o.ProjXmlSha ?? "",
                    ["interfaceSha256"] = o.ProjInterfaceHash ?? "",
                    ["sourceSha256"] = o.ProjSourceHash ?? "",
                    ["sourceLength"] = o.ProjSourceLength,
                    ["sourceNetworks"] = o.ProjSourceNetworks,
                    ["consistent"] = o.ProjectionsConsistent
                },
                ["reference"] = new JsonObject
                {
                    ["format"] = o.ReferenceFormat ?? "",
                    ["length"] = o.ReferenceLength,
                    ["consistent"] = o.ReferencePolicyConsistent
                },
                ["patches"] = new JsonObject
                {
                    ["networkApplicable"] = o.PatchNetworkApplicable,
                    ["networkOk"] = o.PatchNetworkOk,
                    ["interfaceApplicable"] = o.PatchInterfaceApplicable,
                    ["interfaceOk"] = o.PatchInterfaceOk,
                    ["negativeOk"] = o.PatchNegativeOk,
                    ["consistent"] = o.PatchChecksPassed,
                    ["errors"] = new JsonArray(o.PatchErrors.Select(x => (JsonNode)JsonValue.Create(x)).ToArray())
                },
                ["writeGuards"] = new JsonObject
                {
                    ["truncated"] = o.TruncatedGuardOk,
                    ["instance"] = o.InstanceGuardOk,
                    ["memoryLayout"] = o.MemoryLayoutGuardOk,
                    ["consistent"] = o.WriteGuardsPassed,
                    ["errors"] = new JsonArray(o.GuardErrors.Select(x => (JsonNode)JsonValue.Create(x)).ToArray())
                },
                ["capabilities"] = new JsonObject
                {
                    ["consistent"] = o.CapabilityChecksOk,
                    ["errors"] = new JsonArray(o.CapabilityErrors.Select(x => (JsonNode)JsonValue.Create(x)).ToArray())
                },
                ["memberCount"] = o.MemberCount,
                ["members"] = new JsonArray(o.XmlMembers.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                ["irParsed"] = o.IrParsed,
                ["irKind"] = o.IrKind ?? "",
                ["irLanguage"] = o.IrLanguage ?? "",
                ["blockName"] = o.BlockName ?? "",
                ["blockNumber"] = o.BlockNumber,
                ["irNetworkCount"] = o.IrNetworkCount,
                ["irXmlBlockKind"] = o.IrXmlBlockKind ?? "",
                ["irIsInstanceDb"] = o.IrIsInstanceDb,
                ["irInstanceOfName"] = o.IrInstanceOfName ?? "",
                ["irBlockAttributes"] = new JsonArray(o.IrBlockAttributes.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                ["irMemberCount"] = o.IrMembers.Count,
                ["irMembers"] = new JsonArray(o.IrMembers.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                ["fidelity"] = new JsonObject
                {
                    ["truncated"] = o.FidelityTruncated,
                    ["totalNetworks"] = o.FidelityTotal,
                    ["returnedCount"] = o.FidelityReturned
                },
                ["interfaceHash"] = o.InterfaceHash ?? "",
                ["bodyHash"] = o.BodyHash ?? "",
                ["irLength"] = o.IrLength,
                ["checks"] = new JsonObject
                {
                    ["membersMatch"] = o.MembersMatch,
                    ["fidelityConsistent"] = o.FidelityConsistent,
                    ["rangePageApplicable"] = o.RangePageApplicable,
                    ["rangePageOk"] = o.RangePageOk,
                    ["snapshotConsistent"] = o.SnapshotConsistent,
                    ["projectionsConsistent"] = o.ProjectionsConsistent,
                    ["referencePolicyConsistent"] = o.ReferencePolicyConsistent,
                    ["irMetadataConsistent"] = o.IrMetadataConsistent,
                    ["patchChecksPassed"] = o.PatchChecksPassed,
                    ["writeGuardsPassed"] = o.WriteGuardsPassed,
                    ["capabilityChecksOk"] = o.CapabilityChecksOk,
                    ["passed"] = o.ChecksPassed
                },
                ["checkFailures"] = new JsonArray(o.CheckFailures.Select(x => (JsonNode)JsonValue.Create(x)).ToArray())
            };
        }

        private static void WriteBaseline(string root, string baselinePath, List<FixtureObservation> observations)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath) ?? root);
            var rootJson = new JsonObject
            {
                ["schemaVersion"] = BaselineSchemaVersion,
                ["contractVersion"] = BaselineContractVersion,
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["description"] =
                    "Frozen read-side contract (block XML -> IR projection). Any diff means a contract change: " +
                    "review it, then re-run with --contract-probe --write-baseline.",
                ["fixtures"] = new JsonArray(observations.Select(o => (JsonNode)ToBaselineJson(o)).ToArray())
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(baselinePath, rootJson.ToJsonString(options), new UTF8Encoding(false));
        }

        private static int CompareBaseline(string root, string baselinePath, List<FixtureObservation> observations, List<string> report)
        {
            var baselineRoot = JsonNode.Parse(File.ReadAllText(baselinePath, Encoding.UTF8)) as JsonObject;
            var baselineArray = baselineRoot?["fixtures"] as JsonArray;
            if (baselineArray == null)
            {
                report.Add("RESULT: FAIL - baseline has no fixtures array.");
                return 1;
            }

            var baselineMap = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var item in baselineArray)
            {
                if (!(item is JsonObject entry)) continue;
                baselineMap[StrOf(entry["id"])] = entry;
            }

            var currentMap = observations.ToDictionary(o => o.Id, StringComparer.Ordinal);
            var failures = 0;

            report.Add("");
            report.Add("Baseline  : " + BaselineRelativePath
                       + " (" + baselineMap.Count + " fixtures, contract " + StrOf(baselineRoot?["contractVersion"]) + ")");
            report.Add("");

            foreach (var pair in baselineMap.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var current = currentMap.TryGetValue(pair.Key, out var found) ? found : null;
                if (current == null)
                {
                    failures++;
                    report.Add("[DIFF] " + pair.Key + " | fixture missing from the current probe run");
                    continue;
                }

                var diffs = new List<string>();
                CompareString(diffs, pair.Value, "xmlSha256", current.XmlSha256);
                CompareString(diffs, pair.Value, "xmlBlockElement", current.XmlBlockElement);
                CompareString(diffs, pair.Value, "xmlLanguage", current.XmlLanguage);
                CompareString(diffs, pair.Value, "xmlName", current.XmlName);
                CompareInt(diffs, pair.Value, "xmlNumber", current.XmlNumber);
                CompareInt(diffs, pair.Value, "xmlNetworkCount", current.XmlNetworkCount);
                CompareInt(diffs, pair.Value, "parsedNetworkCount", current.ParsedNetworkCount);
                CompareList(diffs, pair.Value, "blockAttributes", current.BlockAttributes);
                CompareSnapshot(diffs, pair.Value["snapshot"] as JsonObject, current);
                CompareInt(diffs, pair.Value, "xmlTextNetworkCount", current.XmlTextNetworkCount);
                CompareProjections(diffs, pair.Value["projections"] as JsonObject, current);
                CompareReference(diffs, pair.Value["reference"] as JsonObject, current);
                ComparePatches(diffs, pair.Value["patches"] as JsonObject, current);
                CompareWriteGuards(diffs, pair.Value["writeGuards"] as JsonObject, current);
                CompareCapabilities(diffs, pair.Value["capabilities"] as JsonObject, current);
                CompareList(diffs, pair.Value, "members", current.XmlMembers);
                CompareString(diffs, pair.Value, "irKind", current.IrKind);
                CompareString(diffs, pair.Value, "irLanguage", current.IrLanguage);
                CompareString(diffs, pair.Value, "blockName", current.BlockName);
                CompareInt(diffs, pair.Value, "blockNumber", current.BlockNumber);
                CompareInt(diffs, pair.Value, "irNetworkCount", current.IrNetworkCount);
                CompareString(diffs, pair.Value, "irXmlBlockKind", current.IrXmlBlockKind);
                CompareBool(diffs, pair.Value, "irIsInstanceDb", current.IrIsInstanceDb);
                CompareString(diffs, pair.Value, "irInstanceOfName", current.IrInstanceOfName);
                CompareList(diffs, pair.Value, "irBlockAttributes", current.IrBlockAttributes);
                CompareList(diffs, pair.Value, "irMembers", current.IrMembers);
                CompareString(diffs, pair.Value, "interfaceHash", current.InterfaceHash);
                CompareString(diffs, pair.Value, "bodyHash", current.BodyHash);
                CompareFidelity(diffs, pair.Value, current);

                if (!current.ChecksPassed)
                    diffs.Add("current checks failed: " + string.Join("; ", current.CheckFailures));

                if (diffs.Count == 0)
                {
                    report.Add("[PASS] " + pair.Key
                               + " | ir=" + current.IrLength + " chars, xml=" + current.XmlLength + " chars");
                }
                else
                {
                    failures++;
                    report.Add("[DIFF] " + pair.Key);
                    foreach (var diff in diffs) report.Add("        " + diff);
                }
            }

            foreach (var extra in observations.Where(o => !baselineMap.ContainsKey(o.Id)))
            {
                failures++;
                report.Add("[DIFF] " + extra.Id + " | new fixture not present in the baseline");
            }

            return failures;
        }

        private static void CompareString(List<string> diffs, JsonObject baseline, string key, string current)
        {
            var expected = StrOf(baseline[key]);
            var actual = current ?? "";
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                diffs.Add(key + ": baseline='" + expected + "' current='" + actual + "'");
        }

        private static void CompareInt(List<string> diffs, JsonObject baseline, string key, int current)
        {
            var expected = IntOf(baseline[key]);
            if (expected != current)
                diffs.Add(key + ": baseline=" + expected + " current=" + current);
        }

        private static void CompareBool(List<string> diffs, JsonObject baseline, string key, bool current)
        {
            var expected = BoolOf(baseline[key]);
            if (expected != current)
                diffs.Add(key + ": baseline=" + expected + " current=" + current);
        }

        private static void CompareList(List<string> diffs, JsonObject baseline, string key, List<string> current)
        {
            var array = baseline[key] as JsonArray;
            var expected = array == null
                ? new List<string>()
                : array.Select(StrOf).ToList();
            var actual = current ?? new List<string>();
            if (ListsEqual(expected, actual)) return;

            var onlyExpected = expected.Except(actual, StringComparer.Ordinal).Take(4).ToList();
            var onlyActual = actual.Except(expected, StringComparer.Ordinal).Take(4).ToList();
            diffs.Add(key + ": onlyInBaseline=[" + string.Join(", ", onlyExpected)
                      + "] onlyInCurrent=[" + string.Join(", ", onlyActual) + "]");
        }

        private static void CompareFidelity(List<string> diffs, JsonObject baseline, FixtureObservation current)
        {
            var fidelity = baseline["fidelity"] as JsonObject;
            var expectedTruncated = BoolOf(fidelity?["truncated"]);
            var expectedTotal = IntOf(fidelity?["totalNetworks"]);

            if (expectedTruncated != current.FidelityTruncated)
                diffs.Add("fidelity.truncated: baseline=" + expectedTruncated + " current=" + current.FidelityTruncated);
            if (expectedTotal != current.FidelityTotal)
                diffs.Add("fidelity.totalNetworks: baseline=" + expectedTotal + " current=" + current.FidelityTotal);

            // Truncated fixtures legitimately return a variable slice; only the truncation flag is contractual.
            if (!current.FidelityTruncated)
            {
                var expectedReturned = IntOf(fidelity?["returnedCount"]);
                if (expectedReturned != current.FidelityReturned)
                    diffs.Add("fidelity.returnedCount: baseline=" + expectedReturned
                              + " current=" + current.FidelityReturned);
            }
        }

        private static void CompareSnapshot(List<string> diffs, JsonObject baseline, FixtureObservation current)
        {
            if (baseline == null)
            {
                diffs.Add("snapshot: missing in baseline");
                return;
            }

            if (BoolOf(baseline["parsed"]) != current.SnapshotParsed)
                diffs.Add("snapshot.parsed: baseline=" + BoolOf(baseline["parsed"]) + " current=" + current.SnapshotParsed);
            if (BoolOf(baseline["consistent"]) != current.SnapshotConsistent)
                diffs.Add("snapshot.consistent: baseline=" + BoolOf(baseline["consistent"]) + " current=" + current.SnapshotConsistent);

            CompareString(diffs, baseline, "blockType", current.SnapshotBlockType);
            CompareString(diffs, baseline, "xmlBlockKind", current.SnapshotXmlBlockKind);
            CompareString(diffs, baseline, "language", current.SnapshotLanguage);
            CompareString(diffs, baseline, "name", current.SnapshotName);
            CompareString(diffs, baseline, "instanceOfName", current.SnapshotInstanceOfName);
            CompareString(diffs, baseline, "sourceProfile", current.SnapshotSourceProfile);
            CompareString(diffs, baseline, "revision", current.SnapshotRevision);
            if (BoolOf(baseline["stableAcrossExports"]) != current.RevisionStable)
                diffs.Add("snapshot.stableAcrossExports: baseline=" + BoolOf(baseline["stableAcrossExports"])
                          + " current=" + current.RevisionStable);
            CompareList(diffs, baseline, "attributes", current.SnapshotAttributes);

            var expectedInstanceDb = BoolOf(baseline["isInstanceDb"]);
            if (expectedInstanceDb != current.SnapshotIsInstanceDb)
                diffs.Add("snapshot.isInstanceDb: baseline=" + expectedInstanceDb + " current=" + current.SnapshotIsInstanceDb);
        }

        private static void CompareProjections(List<string> diffs, JsonObject baseline, FixtureObservation current)
        {
            if (baseline == null)
            {
                diffs.Add("projections: missing in baseline");
                return;
            }

            CompareString(diffs, baseline, "xmlSha256", current.ProjXmlSha);
            CompareString(diffs, baseline, "interfaceSha256", current.ProjInterfaceHash);
            CompareString(diffs, baseline, "sourceSha256", current.ProjSourceHash);
            CompareInt(diffs, baseline, "sourceLength", current.ProjSourceLength);
            CompareInt(diffs, baseline, "sourceNetworks", current.ProjSourceNetworks);

            var expectedConsistent = BoolOf(baseline["consistent"]);
            if (expectedConsistent != current.ProjectionsConsistent)
                diffs.Add("projections.consistent: baseline=" + expectedConsistent
                          + " current=" + current.ProjectionsConsistent);
        }

        private static void CompareReference(List<string> diffs, JsonObject baseline, FixtureObservation current)
        {
            if (baseline == null)
            {
                diffs.Add("reference: missing in baseline");
                return;
            }

            CompareString(diffs, baseline, "format", current.ReferenceFormat);
            CompareInt(diffs, baseline, "length", current.ReferenceLength);

            var expectedConsistent = BoolOf(baseline["consistent"]);
            if (expectedConsistent != current.ReferencePolicyConsistent)
                diffs.Add("reference.consistent: baseline=" + expectedConsistent
                          + " current=" + current.ReferencePolicyConsistent);
        }

        private static void ComparePatches(List<string> diffs, JsonObject baseline, FixtureObservation current)
        {
            if (baseline == null)
            {
                diffs.Add("patches: missing in baseline");
                return;
            }

            CompareBool(diffs, baseline, "networkApplicable", current.PatchNetworkApplicable);
            CompareBool(diffs, baseline, "networkOk", current.PatchNetworkOk);
            CompareBool(diffs, baseline, "interfaceApplicable", current.PatchInterfaceApplicable);
            CompareBool(diffs, baseline, "interfaceOk", current.PatchInterfaceOk);
            CompareBool(diffs, baseline, "negativeOk", current.PatchNegativeOk);
            CompareBool(diffs, baseline, "consistent", current.PatchChecksPassed);
        }

        private static void CompareWriteGuards(List<string> diffs, JsonObject baseline, FixtureObservation current)
        {
            if (baseline == null)
            {
                diffs.Add("writeGuards: missing in baseline");
                return;
            }

            CompareBool(diffs, baseline, "truncated", current.TruncatedGuardOk);
            CompareBool(diffs, baseline, "instance", current.InstanceGuardOk);
            CompareBool(diffs, baseline, "memoryLayout", current.MemoryLayoutGuardOk);
            CompareBool(diffs, baseline, "consistent", current.WriteGuardsPassed);
        }

        private static void CompareCapabilities(List<string> diffs, JsonObject baseline, FixtureObservation current)
        {
            if (baseline == null)
            {
                diffs.Add("capabilities: missing in baseline");
                return;
            }

            CompareBool(diffs, baseline, "consistent", current.CapabilityChecksOk);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Paths / report
        // ─────────────────────────────────────────────────────────────────────

        private static string FindRepoRoot()
        {
            var candidates = new List<string> { Directory.GetCurrentDirectory(), AppDomain.CurrentDomain.BaseDirectory };
            foreach (var candidate in candidates)
            {
                var dir = new DirectoryInfo(candidate);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "TIAOpennessStudio.sln")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            return null;
        }

        private static string RelativePath(string root, string absolute)
        {
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? absolute.Substring(prefix.Length)
                : absolute;
        }

        private static void WriteReport(string root, List<string> lines)
        {
            foreach (var line in lines) Console.WriteLine(line);

            try
            {
                var targetRoot = root;
                if (string.IsNullOrEmpty(targetRoot))
                {
                    // A probe exception happens before the repo root is known. Still leave a
                    // diagnosable report next to the executable instead of console noise only.
                    targetRoot = AppDomain.CurrentDomain.BaseDirectory;
                    if (string.IsNullOrEmpty(targetRoot)) return;
                    var fallbackPath = Path.Combine(targetRoot, "contract-probe-report.txt");
                    File.WriteAllText(fallbackPath, string.Join(Environment.NewLine, lines) + Environment.NewLine,
                        new UTF8Encoding(false));
                    return;
                }

                var path = Path.Combine(targetRoot, ReportRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? targetRoot);
                File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // The probe must not fail because the report file cannot be written.
            }
        }
    }
}
