using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// WinCC Unified HMI action-script recipe generator.
    /// Produces offline script generation and lint only; it does not connect to TIA or write to
    /// an HMI project.
    /// </summary>
    public static class HmiActionScriptRecipeBuilder
    {
        public static JsonObject Build(string recipeKind, string eventName, IEnumerable<string> targetTags, string targetScreen = "", string targetPopup = "")
        {
            var kind = NormalizeRecipeKind(recipeKind);
            var tags = targetTags?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();

            var errors = new JsonArray();
            var warnings = new JsonArray();
            var script = "";
            var requiresApiDiscovery = false;
            var requiresSafetyPolicy = false;
            var applyBlockedReason = "";

            if (string.IsNullOrWhiteSpace(eventName))
                errors.Add("Event name is required.");

            switch (kind)
            {
                case "set-bit":
                    script = BuildSingleTagScript("SetBitInTag", tags, errors);
                    break;
                case "reset-bit":
                    script = BuildSingleTagScript("ResetBitInTag", tags, errors);
                    break;
                case "toggle-bit":
                    script = BuildSingleTagScript("ToggleBitInTag", tags, errors);
                    break;
                case "set-value":
                    script = BuildSetValueScript(tags, errors);
                    requiresSafetyPolicy = true;
                    applyBlockedReason = "SetValue is intentionally blocked until range validation, operator confirmation, permission checks, TIA SyntaxCheck, and ScriptCode readback are implemented for the target project.";
                    warnings.Add("SetValue is high risk: use an explicit ConfirmWrite or project-specific validated write recipe before applying.");
                    break;
                case "confirm-write":
                    script = BuildConfirmWriteScript(tags, errors);
                    requiresSafetyPolicy = true;
                    applyBlockedReason = "ConfirmWrite is intentionally blocked until range validation, operator confirmation, permission checks, TIA SyntaxCheck, and ScriptCode readback are implemented for the target project.";
                    warnings.Add("ConfirmWrite is high risk: range validation, operator confirmation, and TIA SyntaxCheck/readback are required.");
                    break;
                case "open-popup":
                    script = BuildOpenPopupScript(targetPopup, errors);
                    requiresApiDiscovery = !string.IsNullOrWhiteSpace(script);
                    applyBlockedReason = requiresApiDiscovery ? "Popup API is not verified from local TIA export/reference yet." : "";
                    if (requiresApiDiscovery)
                        warnings.Add(applyBlockedReason);
                    break;
                case "goto-screen":
                    script = BuildGotoScreenScript(targetScreen, errors);
                    requiresApiDiscovery = !string.IsNullOrWhiteSpace(script);
                    applyBlockedReason = requiresApiDiscovery ? "Screen navigation API is not verified from local TIA export/reference yet." : "";
                    if (requiresApiDiscovery)
                        warnings.Add(applyBlockedReason);
                    break;
                case "script":
                    warnings.Add("Generic script recipe cannot be generated deterministically from action metadata.");
                    break;
                case "project-binding-placeholder":
                    warnings.Add("Navigation/popup placeholder is structural only; no ScriptCode is generated or applied.");
                    break;
                default:
                    errors.Add("Unknown recipe kind: " + kind);
                    break;
            }

            return new JsonObject
            {
                ["kind"] = kind,
                ["eventName"] = eventName,
                ["targetTags"] = new JsonArray(tags.Select(x => JsonValue.Create(x)).ToArray()),
                ["targetScreen"] = targetScreen,
                ["targetPopup"] = targetPopup,
                ["script"] = script,
                ["requiresApiDiscovery"] = requiresApiDiscovery,
                ["requiresSafetyPolicy"] = requiresSafetyPolicy,
                ["applyBlockedReason"] = applyBlockedReason,
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["offlineOnly"] = true,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "离线脚本生成；不连接 TIA Portal，不写 HMI 项目。",
                    ["write"] = "本工具只返回脚本字符串，不写工程、不写交付包。"
                }
            };
        }

        private static string NormalizeRecipeKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) return "unknown";
            return kind.Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        }

        private static string BuildSingleTagScript(string action, string[] tags, JsonArray errors)
        {
            if (tags.Length == 0)
            {
                errors.Add("At least one target tag is required.");
                return "";
            }
            if (tags.Length > 1)
            {
                errors.Add("Only one target tag is supported for " + action + ".");
                return "";
            }
            return $"HMIRuntime.Tags[\"{EscapeJs(tags[0])}\"].{action}();";
        }

        private static string BuildSetValueScript(string[] tags, JsonArray errors)
        {
            if (tags.Length == 0)
            {
                errors.Add("At least one target tag is required.");
                return "";
            }
            if (tags.Length > 1)
            {
                errors.Add("Only one target tag is supported for SetValue.");
                return "";
            }
            return $"HMIRuntime.Tags[\"{EscapeJs(tags[0])}\"].Write(<value>);";
        }

        private static string BuildConfirmWriteScript(string[] tags, JsonArray errors)
        {
            if (tags.Length == 0)
            {
                errors.Add("At least one target tag is required.");
                return "";
            }
            if (tags.Length > 1)
            {
                errors.Add("Only one target tag is supported for ConfirmWrite.");
                return "";
            }
            return $"if (confirm(\"确认写入 {EscapeJs(tags[0])}?\")) {{ HMIRuntime.Tags[\"{EscapeJs(tags[0])}\"].Write(<value>); }}";
        }

        private static string BuildOpenPopupScript(string popup, JsonArray errors)
        {
            if (string.IsNullOrWhiteSpace(popup))
            {
                errors.Add("Target popup name is required.");
                return "";
            }
            return $"// TODO: Verify popup API from TIA export\n// HMIRuntime.Screens[\"{EscapeJs(popup)}\"].Show();";
        }

        private static string BuildGotoScreenScript(string screen, JsonArray errors)
        {
            if (string.IsNullOrWhiteSpace(screen))
            {
                errors.Add("Target screen name is required.");
                return "";
            }
            return $"HMIRuntime.Screens[\"{EscapeJs(screen)}\"].Show();";
        }

        private static string EscapeJs(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
