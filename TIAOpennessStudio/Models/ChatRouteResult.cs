using System;

namespace TIAOpennessStudio.Models
{
    public enum ChatRouteKind
    {
        Chat,
        SimpleAnalyze,
        AgentTask
    }

    public class ChatRouteResult
    {
        public ChatRouteKind RouteKind { get; set; }
        public AgentIntent Intent { get; set; } = AgentIntent.Chat;
        public double Confidence { get; set; }
        public bool RequiresTools { get; set; }
        public bool RequiresWrite { get; set; }
        public bool RequiresProjectContext { get; set; }
        public string Reason { get; set; } = string.Empty;

        public static ChatRouteResult Create(
            ChatRouteKind routeKind,
            AgentIntent intent,
            double confidence,
            string reason,
            bool requiresTools = false,
            bool requiresWrite = false,
            bool requiresProjectContext = false)
        {
            return new ChatRouteResult
            {
                RouteKind = routeKind,
                Intent = intent,
                Confidence = confidence,
                Reason = reason ?? string.Empty,
                RequiresTools = requiresTools,
                RequiresWrite = requiresWrite,
                RequiresProjectContext = requiresProjectContext
            };
        }
    }
}
