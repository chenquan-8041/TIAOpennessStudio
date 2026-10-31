using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Builds HMI screens, tag tables, and scripts from JSON/XDocument specifications.
    /// </summary>
    public interface IHmiBuilderService
    {
        JsonObject BuildClassicHmiScreen(string designJson);
        XDocument BuildClassicHmiScreenDocument(JsonObject design);
        JsonObject BuildClassicHmiTagTable(string tableJson);
        XDocument BuildClassicHmiTagTableDocument(JsonObject root);
        JsonObject BuildHmiScreen(string designJson, string target, string tiaVersion);
        JsonObject BuildHmiTagTable(string tableJson, string target, string tiaVersion);
        JsonObject BuildHmiActionScript(string recipeKind, string eventName, IEnumerable<string> targetTags, string targetScreen = "", string targetPopup = "");
        string BuildHmiTemplateDesignJson(string templateFile, int fallbackWidth, int fallbackHeight);
        JsonObject BuildHmiTemplateDesign(string templateFile, int fallbackWidth, int fallbackHeight);
        JsonObject BuildHmiTemplateDesign(JsonObject root, int fallbackWidth, int fallbackHeight);
        JsonObject BuildHmiThemeDesign(JsonObject theme);
        JsonObject BuildHmiLayoutDesign(JsonObject layout);
    }
}
