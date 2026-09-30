using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;
using LogAnalyser.Models;
using System.Text.Json.Serialization;

namespace LogAnalyser.Services
{
    public class LogDocument
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        [JsonPropertyName("operationId")]
        public string OperationId { get; set; } = "";

        [JsonPropertyName("invocationId")]
        public string InvocationId { get; set; } = "";

        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; } = "";

        [JsonPropertyName("logType")]
        public string LogType { get; set; } = "";

        [JsonPropertyName("exceptionType")]
        public string ExceptionType { get; set; } = "";

        [JsonPropertyName("component")]
        public string Component { get; set; } = "";

        [JsonPropertyName("normalizedMessage")]
        public string NormalizedMessage { get; set; } = "";

        [JsonPropertyName("errorCode")]
        public string ErrorCode { get; set; } = "";

        [JsonPropertyName("embedding")]
        public IReadOnlyList<float>? Embedding { get; set; }
    }

    public class HistoricalIncidentMatch
    {
        public LogDocument Incident { get; set; } = new();
        public double Score { get; set; }
        public string SimilarityReason { get; set; } = string.Empty;
    }

    public class HybridStoreService
    {
        private readonly SearchClient _searchClient;
        private readonly SearchIndexClient _indexClient;
        private readonly EmbeddingService _embeddingService;
        private readonly string _indexName;
        private readonly int _embeddingDimensions;
        private readonly LogAnalysisOptions _options;

        public HybridStoreService(
            IConfiguration config,
            EmbeddingService embeddingService,
            LogAnalysisOptions options)
        {
            var endpoint = config["AzureSearch:Endpoint"]
                ?? throw new ArgumentNullException("AzureSearch:Endpoint");
            var apiKey = config["AzureSearch:ApiKey"]
                ?? throw new ArgumentNullException("AzureSearch:ApiKey");

            var credential = new AzureKeyCredential(apiKey);
            _options = options;
            _indexName = options.SearchIndexName;
            _embeddingDimensions = options.EmbeddingDimensions;
            _indexClient = new SearchIndexClient(new Uri(endpoint), credential);
            _searchClient = new SearchClient(new Uri(endpoint), _indexName, credential);
            _embeddingService = embeddingService;
        }

        public async Task CreateIndexIfNotExistsAsync(CancellationToken cancellationToken = default)
        {
            var vectorField = new SearchField("embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                VectorSearchDimensions = _embeddingDimensions,
                VectorSearchProfileName = "vector-profile"
            };

            var index = new SearchIndex(_indexName)
            {
                Fields = {
                    new SearchableField("id") { IsKey = true },
                    new SearchableField("content"),
                    new SearchField("timestamp", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                    new SearchField("operationId", SearchFieldDataType.String) { IsFilterable = true },
                    new SearchField("invocationId", SearchFieldDataType.String) { IsFilterable = true },
                    new SearchField("correlationId", SearchFieldDataType.String) { IsFilterable = true },
                    new SearchField("logType", SearchFieldDataType.String) { IsFilterable = true },
                    new SearchField("exceptionType", SearchFieldDataType.String) { IsSearchable = true, IsFilterable = true },
                    new SearchField("component", SearchFieldDataType.String) { IsSearchable = true, IsFilterable = true },
                    new SearchableField("normalizedMessage"),
                    new SearchField("errorCode", SearchFieldDataType.String) { IsSearchable = true, IsFilterable = true },
                    vectorField
                },
                VectorSearch = new VectorSearch
                {
                    Profiles = {
                        new VectorSearchProfile("vector-profile", "vector-config")
                    },
                    Algorithms = {
                        new HnswAlgorithmConfiguration("vector-config")
                        {
                            Parameters = new HnswParameters
                            {
                                Metric = VectorSearchAlgorithmMetric.Cosine,
                                M = 4,
                                EfConstruction = 400,
                                EfSearch = 500
                            }
                        }
                    }
                }
            };

            await _indexClient.CreateOrUpdateIndexAsync(index, false, false, cancellationToken);
        }

        public async Task IndexLogsAsync(
            List<LogDocument> logs,
            CancellationToken cancellationToken = default)
        {
            if (logs.Count == 0)
            {
                return;
            }

            var embeddings = await _embeddingService.GetEmbeddingsAsync(
                logs.Select(BuildEmbeddingText).ToList(),
                cancellationToken);
            for (var index = 0; index < logs.Count; index++)
            {
                var embedding = embeddings[index];
                if (embedding.Count != _embeddingDimensions)
                {
                    throw new InvalidOperationException(
                        $"Embedding deployment returned {embedding.Count} dimensions; the '{_indexName}' index expects {_embeddingDimensions}.");
                }

                logs[index].Embedding = embedding;
            }

            var batch = IndexDocumentsBatch.MergeOrUpload(logs);
            await _searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken);
        }

        private static string BuildEmbeddingText(LogDocument log)
        {
            var signature = string.Join(" ", new[]
            {
                log.ExceptionType,
                log.Component,
                log.ErrorCode,
                log.NormalizedMessage
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            var embeddingText = string.IsNullOrWhiteSpace(signature)
                ? log.Content
                : signature;

            const int maxEmbeddingCharacters = 6000;
            return embeddingText.Length <= maxEmbeddingCharacters
                ? embeddingText
                : embeddingText[..maxEmbeddingCharacters];
        }

        /// <summary>
        /// Semantic-only search
        /// </summary>
        public async Task<List<LogDocument>> SemanticSearchAsync(
            string query,
            int topK = 5,
            string? correlationIdFilter = null,
            CancellationToken cancellationToken = default)
        {
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query, cancellationToken);

            ReadOnlyMemory<float> queryEmbeddingMemory = queryEmbedding is float[] arr
                ? new ReadOnlyMemory<float>(arr)
                : new ReadOnlyMemory<float>(queryEmbedding.ToArray());

            var searchOptions = new SearchOptions
            {
                Size = topK,
                Select = {
                    "id", "content", "timestamp", "operationId", "invocationId", "correlationId", "logType",
                    "exceptionType", "component", "normalizedMessage", "errorCode"
                },
                VectorSearch = new VectorSearchOptions
                {
                    Queries = {
                        new VectorizedQuery(queryEmbeddingMemory)
                        {
                            KNearestNeighborsCount = topK,
                            Fields = { "embedding" }
                        }
                    }
                }
            };

            if (!string.IsNullOrEmpty(correlationIdFilter))
            {
                searchOptions.Filter = $"correlationId eq '{correlationIdFilter}'";
            }

            var response = await _searchClient.SearchAsync<LogDocument>(null, searchOptions, cancellationToken);

            var results = new List<LogDocument>();
            await foreach (var result in response.Value.GetResultsAsync())
            {
                results.Add(result.Document);
            }

            return results;
        }

        /// <summary>
        /// Hybrid search: combines keyword (BM25) + semantic (vector) search
        /// </summary>
        public async Task<List<LogDocument>> HybridSearchAsync(
            string query,
            int topK = 5,
            string? correlationIdFilter = null,
            CancellationToken cancellationToken = default)
        {
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query, cancellationToken);

            ReadOnlyMemory<float> queryEmbeddingMemory = queryEmbedding is float[] arr
                ? new ReadOnlyMemory<float>(arr)
                : new ReadOnlyMemory<float>(queryEmbedding.ToArray());

            var searchOptions = new SearchOptions
            {
                Size = topK,
                Select = {
                    "id", "content", "timestamp", "operationId", "invocationId", "correlationId", "logType",
                    "exceptionType", "component", "normalizedMessage", "errorCode"
                },
                QueryType = SearchQueryType.Simple,
                SearchMode = SearchMode.Any,
                VectorSearch = new VectorSearchOptions
                {
                    Queries = {
                        new VectorizedQuery(queryEmbeddingMemory)
                        {
                            KNearestNeighborsCount = topK * 2,
                            Fields = { "embedding" }
                        }
                    }
                }
            };

            if (!string.IsNullOrEmpty(correlationIdFilter))
            {
                searchOptions.Filter = $"correlationId eq '{correlationIdFilter}'";
            }

            var response = await _searchClient.SearchAsync<LogDocument>(query, searchOptions, cancellationToken);

            var results = new List<LogDocument>();
            await foreach (var result in response.Value.GetResultsAsync())
            {
                results.Add(result.Document);
                if (results.Count >= topK) break;
            }

            return results;
        }

        public async Task<List<HistoricalIncidentMatch>> SearchHistoricalIncidentsAsync(
            ErrorSignature signature,
            string currentCorrelationId,
            string currentOperationId,
            string currentInvocationId,
            CancellationToken cancellationToken = default)
        {
            if (IsUnknown(signature.ExceptionType) || IsUnknown(signature.Component))
            {
                return new List<HistoricalIncidentMatch>();
            }

            var query = string.Join(" ", new[]
            {
                signature.ExceptionType,
                signature.Component,
                signature.ErrorCode,
                signature.NormalizedMessage
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query, cancellationToken);
            var embeddingMemory = queryEmbedding is float[] array
                ? new ReadOnlyMemory<float>(array)
                : new ReadOnlyMemory<float>(queryEmbedding.ToArray());

            var earliestTimestamp = DateTimeOffset.UtcNow.AddDays(-_options.HistoryDays).ToString("O");
            var filters = new List<string> { $"timestamp ge {earliestTimestamp}" };

            if (!string.IsNullOrWhiteSpace(currentCorrelationId))
            {
                filters.Add($"correlationId ne '{EscapeOData(currentCorrelationId)}'");
            }

            if (!string.IsNullOrWhiteSpace(currentOperationId))
            {
                filters.Add($"operationId ne '{EscapeOData(currentOperationId)}'");
            }

            if (!string.IsNullOrWhiteSpace(currentInvocationId))
            {
                filters.Add($"invocationId ne '{EscapeOData(currentInvocationId)}'");
            }

            var searchOptions = new SearchOptions
            {
                Size = _options.CandidateLimit,
                Filter = string.Join(" and ", filters),
                QueryType = SearchQueryType.Simple,
                SearchMode = SearchMode.Any,
                Select = {
                    "id", "content", "timestamp", "operationId", "invocationId", "correlationId", "logType",
                    "exceptionType", "component", "normalizedMessage", "errorCode"
                },
                VectorSearch = new VectorSearchOptions
                {
                    Queries = {
                        new VectorizedQuery(embeddingMemory)
                        {
                            KNearestNeighborsCount = _options.CandidateLimit,
                            Fields = { "embedding" }
                        }
                    }
                }
            };

            var response = await _searchClient.SearchAsync<LogDocument>(query, searchOptions, cancellationToken);
            var matches = new List<HistoricalIncidentMatch>();

            await foreach (var result in response.Value.GetResultsAsync())
            {
                var incident = result.Document;
                if (result.Score < _options.SimilarityThreshold ||
                    !MetadataMatches(signature.ExceptionType, incident.ExceptionType) ||
                    !MetadataMatches(signature.Component, incident.Component))
                {
                    continue;
                }

                matches.Add(new HistoricalIncidentMatch
                {
                    Incident = incident,
                    Score = result.Score ?? 0,
                    SimilarityReason = "Matching exception type and component with similar normalized error content"
                });
            }

            return matches
                .GroupBy(match => match.Incident.OperationId)
                .Select(group => group.OrderByDescending(match => match.Score).First())
                .OrderByDescending(match => match.Score)
                .ThenByDescending(match => match.Incident.Timestamp)
                .Take(_options.ResultLimit)
                .ToList();
        }

        private static bool MetadataMatches(string currentValue, string historicalValue)
        {
            static string Normalize(string value) =>
                new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

            var current = Normalize(currentValue);
            var historical = Normalize(historicalValue);
            if (current.Length == 0 || historical.Length == 0)
            {
                return false;
            }

            return current == historical || current.EndsWith(historical) || historical.EndsWith(current);
        }

        private static bool IsUnknown(string value) =>
            string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase);

        private static string EscapeOData(string value) => value.Replace("'", "''");
    }
}