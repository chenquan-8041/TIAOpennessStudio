using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Services.Portal
{
    /// <summary>
    /// Replaces scattered null-check + return-false patterns with single-line assertions
    /// that throw <see cref="PortalException"/> on failure, letting the MCP layer handle
    /// error mapping uniformly.
    /// </summary>
    internal static class Guard
    {
        /// <summary>Throws <see cref="PortalException"/> (NotFound) if <paramref name="value"/> is null.</summary>
        public static T RequireNotNull<T>(T? value, string kind, string path)
            where T : class
        {
            if (value == null)
                throw new PortalException(PortalErrorCode.NotFound, $"{kind} not found: '{path}'");
            return value;
        }

        /// <summary>Throws <see cref="PortalException"/> (InvalidParams) if <paramref name="value"/> is null or whitespace.</summary>
        public static string RequireNonEmpty(string? value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new PortalException(PortalErrorCode.InvalidParams, $"Parameter '{paramName}' must not be empty.");
            return value!;
        }

        /// <summary>Throws <see cref="PortalException"/> (InvalidState) if <paramref name="condition"/> is false.</summary>
        public static void Require(bool condition, string message)
        {
            if (!condition)
                throw new PortalException(PortalErrorCode.InvalidState, message);
        }

        /// <summary>
        /// Returns <paramref name="candidates"/> formatted as a "Did you mean …?" hint string,
        /// or an empty string when the list is empty. Intended for <see cref="PortalException"/> messages.
        /// </summary>
        public static string DidYouMean(IEnumerable<string>? candidates)
        {
            if (candidates == null) return string.Empty;
            var list = new List<string>(candidates);
            if (list.Count == 0) return string.Empty;
            return " Did you mean: " + string.Join(", ", list) + "?";
        }
    }
}