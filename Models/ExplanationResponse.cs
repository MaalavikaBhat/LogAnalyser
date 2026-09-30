using System.Text.Json.Serialization;

namespace LogAnalyser.Models
{
    public class ExplanationResponse
    {
        public ExplanationResponse()
        {
            Summary = new Summary
                {
                    WhatSucceeded = new List<string>(),
                    WhatFailed = new List<string>()
                };
            RootCause = string.Empty;
            Signature = new ErrorSignature();
        }

        public Summary Summary { get; set; }
        public string RootCause { get; set; }
        public ErrorSignature Signature { get; set; }
    }

    public class Summary
    {
        public Summary()
        {
            WhatSucceeded = new List<string>();
            WhatFailed = new List<string>();
        }


        [JsonPropertyName("whatSucceeded")]
        public List<string> WhatSucceeded { get; set; }

        [JsonPropertyName("whatFailed")]
        public List<string> WhatFailed { get; set; }
    }

    public class ErrorSignature
    {
        public string ExceptionType { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public string NormalizedMessage { get; set; } = string.Empty;
        public string? ErrorCode { get; set; }
    }

    public class ExplainErrorResponse
    {
        public string CorrelationId { get; set; } = string.Empty;
        public Summary Summary { get; set; } = new();
        public string RootCause { get; set; } = string.Empty;
        public RecurrenceInfo Recurrence { get; set; } = new();
    }

    public class RecurrenceInfo
    {
        public string Status { get; set; } = "available";
        public bool HasOccurredBefore { get; set; }
        public int SimilarIncidentCount { get; set; }
        public string Confidence { get; set; } = "none";
        public DateTimeOffset? MostRecentOccurrence { get; set; }
        public List<SimilarIncident> SimilarIncidents { get; set; } = new();
        public string? UnavailableReason { get; set; }
    }

    public class SimilarIncident
    {
        public string CorrelationId { get; set; } = string.Empty;
        public DateTimeOffset Timestamp { get; set; }
        public string ExceptionType { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public double Score { get; set; }
        public string SimilarityReason { get; set; } = string.Empty;
    }
}
