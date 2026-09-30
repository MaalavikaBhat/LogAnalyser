namespace LogAnalyser.Models
{
    public class LogAnalysisOptions
    {
        public const string SectionName = "LogAnalysis";

        public string ApplicationInsightsResourceId { get; set; } = string.Empty;
        public string SearchIndexName { get; set; } = "log-incidents-v2";
        public int HistoryDays { get; set; } = 30;
        public int EmbeddingDimensions { get; set; } = 3072;
        public double SimilarityThreshold { get; set; } = 0.01;
        public int CandidateLimit { get; set; } = 50;
        public int ResultLimit { get; set; } = 5;
        public int MaxPromptCharacters { get; set; } = 50000;
    }
}
