namespace TIAOpennessStudio.Services.Portal
{
    /// <summary>Structured error categories surfaced through <see cref="PortalException"/>.</summary>
    public enum PortalErrorCode
    {
        NotFound,
        ExportFailed,
        ImportFailed,
        InvalidParams,
        InvalidState,
        OpennessError,
        NotSupportedOnVersion
    }
}