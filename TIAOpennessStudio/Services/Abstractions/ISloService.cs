using System;
using System.Collections.Generic;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Tracks service-level objectives and evaluates SLO compliance.
    /// </summary>
    public interface ISloService
    {
        ServiceLevelObjective CreateDefaultSlo();
        void RecordMeasurement(string sloName, string indicatorName, double value, string rid = null, string context = null);
        SlosResult EvaluateSlos(DateTime? from = null, DateTime? to = null);
        Dictionary<string, double> GetCurrentMetrics();
    }
}
