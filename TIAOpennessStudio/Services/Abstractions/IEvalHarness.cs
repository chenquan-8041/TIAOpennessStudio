using System.Collections.Generic;
using System.Threading.Tasks;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Evaluation harness that runs evaluation cases against the AI engine and scores
    /// the results (pattern matching, LLM-as-a-Judge, and compile-pass hard gate).
    /// </summary>
    public interface IEvalHarness
    {
        List<EvalCase> GetDefaultTestCases();
        Task<EvalReport> RunEvaluationAsync(List<EvalCase> cases, IAIEngineService aiService, IProjectIndexService indexService);

        /// <summary>
        /// G-2: advanced evaluation with LLM-as-a-Judge + compile-pass hard gate.
        /// Pass a default <see cref="EvalConfig"/> to reproduce legacy pattern-only scoring.
        /// </summary>
        Task<EvalReport> RunEvaluationAsync(List<EvalCase> cases, IAIEngineService aiService, IProjectIndexService indexService, EvalConfig config);

        Task<EvalReport> RunQuickEvalAsync(IAIEngineService aiService);
        EvalReport LoadReport(string reportPath);
        void SaveReport(EvalReport report, string reportPath);
        string GenerateReportSummary(EvalReport report);
    }
}
