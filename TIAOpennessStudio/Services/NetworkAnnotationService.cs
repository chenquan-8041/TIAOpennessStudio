using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models.NetworkKnowledge;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Orchestrates single-Network semantic annotation via a local 7-27B model.
    /// Enforces: single Network scope, token limits, timeout, retry, evidence validation,
    /// and code generation rejection per Requirements 5 and 6.
    /// </summary>
    public sealed class NetworkAnnotationService
    {
        public const int MaxInputTokens = 32768;
        public const int MaxOutputTokens = 2048;
        public const int TimeoutSeconds = 120;
        public const int MaxAttempts = 2;
        public const string SchemaVersion = "1.0.0";
        public const string PromptVersion = "1.0.0";

        private const int CharsPerToken = 4;

        private readonly Func<string, CancellationToken, Task<string>> _modelInvoke;

        /// <summary>
        /// Constructor accepts a model invocation delegate.
        /// Signature: (prompt, ct) → raw JSON output.
        /// </summary>
        public NetworkAnnotationService(Func<string, CancellationToken, Task<string>> modelInvoke)
        {
            _modelInvoke = modelInvoke ?? throw new ArgumentNullException(nameof(modelInvoke));
        }

        /// <summary>
        /// Annotate a single Network. Returns the annotation result (may be Failed/Skipped).
        /// </summary>
        public async Task<SemanticAnnotation> AnnotateAsync(
            PlcNetworkDocument network,
            IReadOnlyList<PlcNetworkDocument> contextNetworks = null,
            CancellationToken ct = default)
        {
            var annotation = new SemanticAnnotation
            {
                NetworkId = network.NetworkId,
                SchemaVersion = SchemaVersion,
                PromptVersion = PromptVersion
            };

            // Check: skip if parse failed
            if (network.Status != ParseStatus.Succeeded)
            {
                annotation.Status = AnnotationStatus.Skipped;
                annotation.FailureReason = "Network parse status is not Succeeded";
                return annotation;
            }

            // Check: input token limit
            var prompt = BuildPrompt(network, contextNetworks);
            var inputTokens = prompt.Length / CharsPerToken;
            if (inputTokens > MaxInputTokens)
            {
                annotation.Status = AnnotationStatus.Skipped;
                annotation.FailureReason = $"Input exceeds {MaxInputTokens} tokens ({inputTokens} estimated)";
                return annotation;
            }

            // Attempt with retry
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                annotation.AttemptCount = attempt;

                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                        var rawOutput = await _modelInvoke(prompt, cts.Token).ConfigureAwait(false);

                        if (string.IsNullOrWhiteSpace(rawOutput))
                        {
                            annotation.FailureReason = "Empty model output";
                            continue;
                        }

                        // Validate output
                        var validationResult = ValidateOutput(rawOutput, network);
                        if (!validationResult.IsValid)
                        {
                            annotation.FailureReason = validationResult.Reason;
                            continue;
                        }

                        // Parse and populate
                        PopulateAnnotation(annotation, rawOutput);
                        annotation.Status = AnnotationStatus.Succeeded;
                        return annotation;
                    }
                }
                catch (OperationCanceledException)
                {
                    annotation.FailureReason = $"Timeout after {TimeoutSeconds}s on attempt {attempt}";
                }
                catch (Exception ex)
                {
                    annotation.FailureReason = $"Attempt {attempt} error: {ex.Message}";
                }
            }

            annotation.Status = AnnotationStatus.Failed;
            return annotation;
        }

        private string BuildPrompt(PlcNetworkDocument network, IReadOnlyList<PlcNetworkDocument> contextNetworks)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are a PLC program analyst. Analyze the following LAD Network and provide a structured semantic annotation.");
            sb.AppendLine();
            sb.AppendLine("## Constraints");
            sb.AppendLine("- Do NOT generate PLC code.");
            sb.AppendLine("- Do NOT suggest modifications.");
            sb.AppendLine("- Every fact claim MUST reference an evidence source from the provided data.");
            sb.AppendLine("- Output ONLY valid JSON matching the schema below.");
            sb.AppendLine();
            sb.AppendLine("## Network Facts");
            sb.AppendLine($"Block: {network.BlockId}");
            sb.AppendLine($"Network Order: {network.NetworkOrder}");
            sb.AppendLine($"Title: {network.Title}");
            sb.AppendLine($"Comment: {network.Comment}");
            sb.AppendLine();

            sb.AppendLine("### Accesses");
            foreach (var access in network.Accesses.Take(50))
            {
                sb.AppendLine($"- {access.CanonicalPath} [{access.Direction}] scope={access.Scope}");
            }

            sb.AppendLine();
            sb.AppendLine("### Call Sites");
            foreach (var call in network.CallSites.Take(20))
            {
                sb.AppendLine($"- {call.RawCalleeName} type={call.RawBlockType} instance={call.RawInstancePath}");
            }

            sb.AppendLine();
            sb.AppendLine("### Parts");
            foreach (var part in network.Parts.Take(30))
            {
                sb.AppendLine($"- {part.Name} uid={part.Uid} negated={part.Negated}");
            }

            sb.AppendLine();
            sb.AppendLine("### Wires (connections)");
            sb.AppendLine($"Total: {network.Wires.Count} wires");

            // Include limited context from adjacent networks
            if (contextNetworks != null && contextNetworks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Adjacent Context (read-only)");
                foreach (var ctx in contextNetworks.Take(4))
                {
                    sb.AppendLine($"- Network {ctx.NetworkOrder}: {ctx.Title} ({ctx.Accesses.Count} accesses, {ctx.CallSites.Count} calls)");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Output Schema (JSON)");
            sb.AppendLine(@"{
  ""summary"": ""...(one sentence describing what this Network does)..."",
  ""functional_category"": ""...(e.g. start-permission, interlock, mode-switch, output-control, fault-handling, communication, initialization)..."",
  ""business_area"": ""...(e.g. conveyor, motor, safety, valve, robot, station)..."",
  ""preconditions"": [""...""],
  ""effects"": [""...""],
  ""tags"": [""...""],
  ""confidence"": 0.0-1.0,
  ""evidence_refs"": [""access:DB_X.Y"", ""call:FB_Z"", ""part:Coil""]
}");

            return sb.ToString();
        }

        private static ValidationResult ValidateOutput(string output, PlcNetworkDocument network)
        {
            // Reject code generation attempts
            var codePatterns = new[] { "FUNCTION_BLOCK", "FUNCTION ", "DATA_BLOCK", "TYPE ", "VAR_INPUT", "END_FUNCTION", "#region", "void ", "class " };
            foreach (var pattern in codePatterns)
            {
                if (output.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new ValidationResult { IsValid = false, Reason = "Output contains code generation (rejected per Requirement 6)" };
                }
            }

            // Must contain JSON structure indicators
            if (!output.Contains("summary") || !output.Contains("evidence_refs"))
            {
                return new ValidationResult { IsValid = false, Reason = "Output missing required schema fields" };
            }

            // Check output token limit
            if (output.Length / CharsPerToken > MaxOutputTokens)
            {
                return new ValidationResult { IsValid = false, Reason = $"Output exceeds {MaxOutputTokens} token limit" };
            }

            // Evidence refs must reference known Access paths or Call names
            // (lightweight check: at least one evidence_ref should match something in the network)
            var hasEvidence = network.Accesses.Any(a =>
                output.IndexOf(a.CanonicalPath ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                || network.CallSites.Any(c =>
                    output.IndexOf(c.RawCalleeName ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                || network.Parts.Any(p =>
                    output.IndexOf(p.Name ?? "", StringComparison.OrdinalIgnoreCase) >= 0);

            if (!hasEvidence)
            {
                return new ValidationResult { IsValid = false, Reason = "No evidence references match Network facts" };
            }

            return new ValidationResult { IsValid = true };
        }

        private static void PopulateAnnotation(SemanticAnnotation annotation, string rawOutput)
        {
            // Simple JSON extraction (production would use System.Text.Json)
            annotation.Summary = ExtractJsonString(rawOutput, "summary");
            annotation.FunctionalCategory = ExtractJsonString(rawOutput, "functional_category");
            annotation.BusinessArea = ExtractJsonString(rawOutput, "business_area");
            annotation.Confidence = ExtractJsonDouble(rawOutput, "confidence");

            // Extract arrays
            annotation.Preconditions = ExtractJsonStringArray(rawOutput, "preconditions");
            annotation.Effects = ExtractJsonStringArray(rawOutput, "effects");
            annotation.Tags = ExtractJsonStringArray(rawOutput, "tags");
            annotation.EvidenceRefs = ExtractJsonStringArray(rawOutput, "evidence_refs");
        }

        private static string ExtractJsonString(string json, string key)
        {
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";

            var afterColon = json.IndexOf(':', idx + pattern.Length);
            if (afterColon < 0) return "";

            var quoteStart = json.IndexOf('"', afterColon + 1);
            if (quoteStart < 0) return "";

            var quoteEnd = json.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0) return "";

            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        private static double ExtractJsonDouble(string json, string key)
        {
            var str = ExtractJsonString(json, key);
            if (double.TryParse(str, out var val)) return val;

            // Try without quotes
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
            var afterColon = json.IndexOf(':', idx + pattern.Length);
            if (afterColon < 0) return 0;

            var segment = json.Substring(afterColon + 1, Math.Min(20, json.Length - afterColon - 1)).Trim();
            var numEnd = 0;
            while (numEnd < segment.Length && (char.IsDigit(segment[numEnd]) || segment[numEnd] == '.'))
                numEnd++;
            if (numEnd > 0 && double.TryParse(segment.Substring(0, numEnd), out var v))
                return v;

            return 0;
        }

        private static List<string> ExtractJsonStringArray(string json, string key)
        {
            var result = new List<string>();
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return result;

            var bracketStart = json.IndexOf('[', idx + pattern.Length);
            if (bracketStart < 0) return result;

            var bracketEnd = json.IndexOf(']', bracketStart);
            if (bracketEnd < 0) return result;

            var content = json.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
            var inQuote = false;
            var current = new System.Text.StringBuilder();

            for (int i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (c == '"')
                {
                    if (inQuote)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                    inQuote = !inQuote;
                }
                else if (inQuote)
                {
                    current.Append(c);
                }
            }

            return result;
        }

        private struct ValidationResult
        {
            public bool IsValid;
            public string Reason;
        }
    }
}
