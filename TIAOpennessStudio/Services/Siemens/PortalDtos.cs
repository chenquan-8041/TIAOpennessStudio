using System.Collections.Generic;

namespace TIAOpennessStudio.Services.Portal
{
    /// <summary>Online/offline state reported by the in-process Portal gateway.</summary>
    public class ResponseOnlineState
    {
        public string State { get; set; }
        public bool IsOnline { get; set; }
        public bool IsReachable { get; set; }
        public string Message { get; set; }
    }

    /// <summary>Simple message result with optional metadata payload.</summary>
    public class ResponseMessage
    {
        public string Message { get; set; }
        public object Meta { get; set; }
    }

    /// <summary>Result of an offline/online block comparison.</summary>
    public class ResponseCompare
    {
        public string Message { get; set; }
        public bool IsOnline { get; set; }
        public CompareEntry[] Entries { get; set; }
        public Dictionary<string, int> Summary { get; set; }
        public bool Truncated { get; set; }
    }

    /// <summary>A single difference entry in an online comparison.</summary>
    public class CompareEntry
    {
        public string Path { get; set; }
        public string LeftName { get; set; }
        public string RightName { get; set; }
        public string Status { get; set; }
        public string Details { get; set; }
    }

    /// <summary>Summary of a batch import (count imported/failed).</summary>
    public class ResponseImportBatch
    {
        public int Imported { get; set; }
        public int Failed { get; set; }
    }

    /// <summary>Result of a download-to-PLC operation.</summary>
    public class ResponseDownload
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string[] Errors { get; set; }
    }

    /// <summary>Result of a download-readiness check.</summary>
    public class ResponseCheckDownload
    {
        public bool Ready { get; set; }
        public IReadOnlyList<string> Issues { get; set; }
        public bool HasDownloadProvider { get; set; }
        public bool HasConfiguration { get; set; }
        public bool IsConsistent { get; set; }
        public string Message { get; set; }
    }
}