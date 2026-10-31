namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Marker interface declaring that a tool can safely execute concurrently with others.
    ///
    /// The TIA Openness API is single-STA-thread-affine — any tool touching
    /// <c>PlcSoftware</c> is ultimately marshalled back to the same STA thread and runs
    /// serially, so running them in parallel is pointless and possibly harmful. Only tools
    /// that never touch STA (pure .NET computation, SQLite knowledge-index queries,
    /// file/network reads, etc.) should implement this interface.
    ///
    /// Determination rules (see <c>ReActEngine.ExecuteToolsParallelAsync</c>):
    /// - Write tools (<see cref="ToolPermissionLevel.RequiresConfirmation"/>) always run serially, even if marked with this interface;
    /// - Tools implementing this interface and that are not write tools may run concurrently;
    /// - All other tools default to serial execution.
    /// </summary>
    public interface IParallelSafeTool
    {
    }
}
