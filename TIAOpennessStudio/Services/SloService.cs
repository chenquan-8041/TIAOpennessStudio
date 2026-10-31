using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    public class ServiceLevelIndicator
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public double TargetValue { get; set; }
        public string Unit { get; set; }
        public SliType Type { get; set; }
        public SliComparison Comparison { get; set; } = SliComparison.LessThanOrEqual;
    }

    public enum SliType
    {
        Latency,
        Availability,
        Quality,
        Throughput,
        Cost
    }

    public enum SliComparison
    {
        LessThanOrEqual,
        GreaterThanOrEqual,
        Equal
    }

    public class SliMeasurement
    {
        public string SliName { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public double ActualValue { get; set; }
        public bool MetTarget { get; set; }
        public string Rid { get; set; }
        public string Context { get; set; }
    }

    public class ServiceLevelObjective
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public string Description { get; set; }
        public List<ServiceLevelIndicator> Indicators { get; set; } = new List<ServiceLevelIndicator>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsEnabled { get; set; } = true;
    }

    public class SlosResult
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public List<SliMeasurement> Measurements { get; set; } = new List<SliMeasurement>();
        public List<SliViolation> Violations { get; set; } = new List<SliViolation>();
        public double OverallScore { get; set; }
        public bool AllSlosMet => Violations.Count == 0;
        public string Summary { get; set; }
    }

    public class SliViolation
    {
        public string SliName { get; set; }
        public double TargetValue { get; set; }
        public double ActualValue { get; set; }
        public DateTime Timestamp { get; set; }
        public string Rid { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// In-memory SLO tracking service: records measurements against the default
    /// SLO and reports compliance over a time window.
    /// </summary>
    public class SloService : ISloService
    {
        private readonly ServiceLevelObjective _defaultSlo;
        private readonly List<SliMeasurement> _measurements = new List<SliMeasurement>();
        private readonly object _lock = new object();
        private const int MaxMeasurements = 10000;

        public SloService()
        {
            _defaultSlo = CreateDefaultSlo();
        }

        public ServiceLevelObjective CreateDefaultSlo()
        {
            return new ServiceLevelObjective
            {
                Name = "TIA AI Assistant SLO",
                Description = "Default SLO for TIA Portal AI Assistant",
                Indicators = new List<ServiceLevelIndicator>
                {
                    new ServiceLevelIndicator
                    {
                        Name = "first_token_latency",
                        Description = "首 token 延迟：从请求到首 token 输出的时间",
                        TargetValue = 3000,
                        Unit = "ms",
                        Type = SliType.Latency,
                        Comparison = SliComparison.LessThanOrEqual
                    },
                    new ServiceLevelIndicator
                    {
                        Name = "total_request_latency",
                        Description = "总请求延迟：从请求到完成的时间",
                        TargetValue = 30000,
                        Unit = "ms",
                        Type = SliType.Latency,
                        Comparison = SliComparison.LessThanOrEqual
                    },
                    new ServiceLevelIndicator
                    {
                        Name = "request_failure_rate",
                        Description = "请求失败率",
                        TargetValue = 5,
                        Unit = "%",
                        Type = SliType.Availability,
                        Comparison = SliComparison.LessThanOrEqual
                    },
                    new ServiceLevelIndicator
                    {
                        Name = "stream_interruption_rate",
                        Description = "流中断率",
                        TargetValue = 2,
                        Unit = "%",
                        Type = SliType.Availability,
                        Comparison = SliComparison.LessThanOrEqual
                    },
                    new ServiceLevelIndicator
                    {
                        Name = "patch_application_rate",
                        Description = "补丁可应用率",
                        TargetValue = 90,
                        Unit = "%",
                        Type = SliType.Quality,
                        Comparison = SliComparison.GreaterThanOrEqual
                    },
                    new ServiceLevelIndicator
                    {
                        Name = "patch_success_rate",
                        Description = "补丁验证通过率",
                        TargetValue = 80,
                        Unit = "%",
                        Type = SliType.Quality,
                        Comparison = SliComparison.GreaterThanOrEqual
                    },
                    new ServiceLevelIndicator
                    {
                        Name = "quality_gate_pass_rate",
                        Description = "质量门通过率",
                        TargetValue = 85,
                        Unit = "%",
                        Type = SliType.Quality,
                        Comparison = SliComparison.GreaterThanOrEqual
                    },
                    new ServiceLevelIndicator
                    {
                        Name = "cost_per_request",
                        Description = "单次请求成本",
                        TargetValue = 0.01,
                        Unit = "USD",
                        Type = SliType.Cost,
                        Comparison = SliComparison.LessThanOrEqual
                    },
                    new ServiceLevelIndicator
                    {
                        Name = "requests_per_hour",
                        Description = "每小时请求数（吞吐量）",
                        TargetValue = 100,
                        Unit = "count",
                        Type = SliType.Throughput,
                        Comparison = SliComparison.GreaterThanOrEqual
                    }
                }
            };
        }

        public void RecordMeasurement(string sloName, string indicatorName, double value, string rid = null, string context = null)
        {
            var indicator = _defaultSlo.Indicators.FirstOrDefault(i => i.Name == indicatorName);
            if (indicator == null) return;

            var measurement = new SliMeasurement
            {
                SliName = indicatorName,
                ActualValue = value,
                Rid = rid,
                Context = context,
                MetTarget = EvaluateTarget(indicator, value)
            };

            lock (_lock)
            {
                _measurements.Add(measurement);
                while (_measurements.Count > MaxMeasurements)
                {
                    _measurements.RemoveAt(0);
                }
            }
        }

        private bool EvaluateTarget(ServiceLevelIndicator indicator, double value)
        {
            return indicator.Comparison switch
            {
                SliComparison.LessThanOrEqual => value <= indicator.TargetValue,
                SliComparison.GreaterThanOrEqual => value >= indicator.TargetValue,
                SliComparison.Equal => Math.Abs(value - indicator.TargetValue) < 0.001,
                _ => false
            };
        }

        public SlosResult EvaluateSlos(DateTime? from = null, DateTime? to = null)
        {
            var start = from ?? DateTime.UtcNow.AddHours(-1);
            var end = to ?? DateTime.UtcNow;

            List<SliMeasurement> relevant;
            lock (_lock)
            {
                relevant = _measurements
                    .Where(m => m.Timestamp >= start && m.Timestamp <= end)
                    .ToList();
            }

            var result = new SlosResult
            {
                PeriodStart = start,
                PeriodEnd = end,
                Measurements = relevant
            };

            foreach (var indicator in _defaultSlo.Indicators)
            {
                var measurements = relevant.Where(m => m.SliName == indicator.Name).ToList();
                if (measurements.Count == 0) continue;

                var avgValue = measurements.Average(m => m.ActualValue);
                var metTargetCount = measurements.Count(m => m.MetTarget);
                var complianceRate = (double)metTargetCount / measurements.Count * 100;

                if (complianceRate < 95)
                {
                    result.Violations.Add(new SliViolation
                    {
                        SliName = indicator.Name,
                        TargetValue = indicator.TargetValue,
                        ActualValue = avgValue,
                        Message = $"{indicator.Name} 合规率 {complianceRate:F1}% (目标 95%)"
                    });
                }
            }

            var totalMeasurements = relevant.Count;
            var metMeasurements = relevant.Count(m => m.MetTarget);
            result.OverallScore = totalMeasurements > 0 ? (double)metMeasurements / totalMeasurements * 100 : 100;

            result.Summary = result.AllSlosMet
                ? $"✅ 所有 SLO 达标 (得分: {result.OverallScore:F1}%)"
                : $"❌ {result.Violations.Count} 个 SLO 未达标 (得分: {result.OverallScore:F1}%)";

            return result;
        }

        public Dictionary<string, double> GetCurrentMetrics()
        {
            var metrics = new Dictionary<string, double>();
            var recent = _measurements
                .Where(m => m.Timestamp >= DateTime.UtcNow.AddMinutes(-5))
                .ToList();

            foreach (var indicator in _defaultSlo.Indicators)
            {
                var relevant = recent.Where(m => m.SliName == indicator.Name).ToList();
                metrics[$"{indicator.Name}_current"] = relevant.LastOrDefault()?.ActualValue ?? 0;
                metrics[$"{indicator.Name}_avg_5min"] = relevant.Count > 0 ? relevant.Average(m => m.ActualValue) : 0;
                metrics[$"{indicator.Name}_compliance_5min"] = relevant.Count > 0 
                    ? (double)relevant.Count(m => m.MetTarget) / relevant.Count * 100 
                    : 100;
            }

            return metrics;
        }
    }
}
