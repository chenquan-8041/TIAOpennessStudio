using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Services.Portal
{
    /// <summary>Domain exception carrying a <see cref="PortalErrorCode"/> and optional candidates.</summary>
    public class PortalException : Exception
    {
        public PortalErrorCode Code { get; }

        public IEnumerable<string>? Candidates { get; }

        public PortalException(PortalErrorCode code, string message, IEnumerable<string>? candidates = null, Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
            Candidates = candidates;
        }
    }
}