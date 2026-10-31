using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Builders;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>Builds classic HMI screen artifacts (JSON / XDocument) via the classic screen XML builder.</summary>
    public class HmiBuilderService : IHmiBuilderService
    {
        public JsonObject BuildClassicHmiScreen(string designJson)
            => ClassicHmiScreenXmlBuilder.BuildFromJson(designJson);

        public XDocument BuildClassicHmiScreenDocument(JsonObject design)
            => ClassicHmiScreenXmlBuilder.BuildDocument(design);

        public JsonObject BuildClassicHmiTagTable(string tableJson)
            => ClassicHmiTagTableXmlBuilder.BuildFromJson(tableJson);

        public XDocument BuildClassicHmiTagTableDocument(JsonObject root)
            => ClassicHmiTagTableXmlBuilder.BuildDocument(root);

        public JsonObject BuildHmiScreen(string designJson, string target, string tiaVersion)
        {
            switch ((target ?? "").Trim().ToLowerInvariant())
            {
                case "classic":
                    return ClassicHmiScreenXmlBuilder.BuildFromJson(designJson, tiaVersion);
                case "unified":
                    return UnifiedHmiScreenDesignBuilder.BuildFromJson(designJson);
                default:
                    throw new ArgumentException($"Unsupported HMI target '{target}'. Expected 'classic' or 'unified'.");
            }
        }

        public JsonObject BuildHmiTagTable(string tableJson, string target, string tiaVersion)
        {
            switch ((target ?? "").Trim().ToLowerInvariant())
            {
                case "classic":
                    return ClassicHmiTagTableXmlBuilder.BuildFromJson(tableJson, tiaVersion);
                case "unified":
                    return UnifiedHmiTagTableDesignBuilder.BuildFromJson(tableJson);
                default:
                    throw new ArgumentException($"Unsupported HMI target '{target}'. Expected 'classic' or 'unified'.");
            }
        }

        public JsonObject BuildHmiActionScript(string recipeKind, string eventName, IEnumerable<string> targetTags, string targetScreen = "", string targetPopup = "")
            => HmiActionScriptRecipeBuilder.Build(recipeKind, eventName, targetTags, targetScreen, targetPopup);

        public string BuildHmiTemplateDesignJson(string templateFile, int fallbackWidth, int fallbackHeight)
            => HmiTemplateDesignJsonBuilder.BuildApplyDesignJson(templateFile, fallbackWidth, fallbackHeight);

        public JsonObject BuildHmiTemplateDesign(string templateFile, int fallbackWidth, int fallbackHeight)
            => HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, fallbackWidth, fallbackHeight);

        public JsonObject BuildHmiTemplateDesign(JsonObject root, int fallbackWidth, int fallbackHeight)
            => HmiTemplateDesignJsonBuilder.BuildApplyDesign(root, fallbackWidth, fallbackHeight);

        public JsonObject BuildHmiThemeDesign(JsonObject theme)
            => HmiUnifiedThemeLayoutBuilder.BuildThemeDesign(theme);

        public JsonObject BuildHmiLayoutDesign(JsonObject layout)
            => HmiUnifiedThemeLayoutBuilder.BuildLayoutDesign(layout);
    }
}
