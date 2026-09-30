using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using LogAnalyser.Services;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogAnalyser.Functions
{
    public class HybridRAGFunction
    {
        private readonly HybridStoreService _hybridStore;
        private readonly OpenAIClient _openAIClient;
        private readonly string _deploymentName;

        public HybridRAGFunction(
            HybridStoreService hybridStore,
            OpenAIClient openAIClient,
            IConfiguration config)
        {
            _hybridStore = hybridStore;
            _openAIClient = openAIClient;
            _deploymentName = config["OpenAI:DeploymentName"] ?? throw new ArgumentNullException("OpenAI:DeploymentName");
        }

        [Function("HybridRAG")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();

            var body = await req.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var request = JsonSerializer.Deserialize<HybridRAGRequest>(body ?? "{}", options);

            if (string.IsNullOrWhiteSpace(request?.Query))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Query is required" });
                return badRequest;
            }

            // Step 1: Hybrid search (keyword + semantic)
            var searchSw = Stopwatch.StartNew();
            var relevantDocs = await _hybridStore.HybridSearchAsync(
                request.Query,
                topK: request.TopK ?? 5,
                correlationIdFilter: request.CorrelationId,
                cancellationToken: cancellationToken
            );
            searchSw.Stop();

            Console.WriteLine($"[HybridRAG] Found {relevantDocs.Count} documents in {searchSw.ElapsedMilliseconds}ms");

            if (!relevantDocs.Any())
            {
                sw.Stop();
                var notFound = req.CreateResponse(HttpStatusCode.OK);
                await notFound.WriteAsJsonAsync(new
                {
                    query = request.Query,
                    correlationId = request.CorrelationId,
                    answer = new
                    {
                        summary = "No relevant logs found",
                        details = "Try ingesting logs first by calling POST /api/IngestLogs"
                    },
                    metadata = new
                    {
                        searchType = "hybrid (keyword + semantic)",
                        documentsRetrieved = 0,
                        searchTimeMs = searchSw.ElapsedMilliseconds,
                        totalTimeMs = sw.ElapsedMilliseconds
                    }
                });
                return notFound;
            }

            // Step 2: Build context
            var context = string.Join("\n\n", relevantDocs.Select((d, i) =>
                $"[Log {i + 1}] ({d.Timestamp:yyyy-MM-dd HH:mm:ss}) [{d.LogType}] CorrelationId: {d.CorrelationId}\n{d.Content}"));

            // Step 3: Generate analysis
            var llmSw = Stopwatch.StartNew();
            var chatClient = _openAIClient.GetChatClient(_deploymentName);

            var response = await chatClient.CompleteChatAsync(
                new ChatMessage[]
                {
                    ChatMessage.CreateSystemMessage(@"You are a log analysis assistant. Respond with ONLY valid JSON (no markdown, no code blocks).

Format:
{
    ""summary"": ""One-line summary"",
    ""rootCause"": ""Root cause of the issue"",
    ""details"": ""Detailed explanation"",
    ""affectedComponents"": [""component1"", ""component2""],
    ""recommendations"": [""recommendation1"", ""recommendation2""],
    ""severity"": ""low|medium|high|critical"",
    ""confidence"": ""low|medium|high""
}"),
                    ChatMessage.CreateUserMessage($"Question: {request.Query}\n\nLogs:\n{context}\n\nRespond with JSON only.")
                },
                new ChatCompletionOptions
                {
                    Temperature = 0.2f,
                    MaxOutputTokenCount = 1500
                },
                cancellationToken
            );
            llmSw.Stop();

            var rawAnswer = ExtractAndCleanResponse(response);

            object parsedAnswer;
            try
            {
                parsedAnswer = JsonSerializer.Deserialize<JsonElement>(rawAnswer);
            }
            catch
            {
                parsedAnswer = new { raw = rawAnswer, parseError = true };
            }

            sw.Stop();

            var httpResponse = req.CreateResponse(HttpStatusCode.OK);
            await httpResponse.WriteAsJsonAsync(new
            {
                query = request.Query,
                correlationId = request.CorrelationId,
                answer = parsedAnswer,
                metadata = new
                {
                    searchType = "hybrid (keyword + semantic)",
                    documentsRetrieved = relevantDocs.Count,
                    searchTimeMs = searchSw.ElapsedMilliseconds,
                    llmTimeMs = llmSw.ElapsedMilliseconds,
                    totalTimeMs = sw.ElapsedMilliseconds,
                    tokensUsed = new
                    {
                        prompt = response.Value.Usage.InputTokenCount,
                        completion = response.Value.Usage.OutputTokenCount
                    }
                },
                sources = relevantDocs.Select((d, i) => new
                {
                    index = i + 1,
                    timestamp = d.Timestamp,
                    operationId = d.OperationId,
                    correlationId = d.CorrelationId,
                    logType = d.LogType,
                    preview = d.Content.Length > 200 ? d.Content[..200] + "..." : d.Content
                })
            });

            return httpResponse;
        }

        private static string ExtractAndCleanResponse(OpenAI.Chat.ChatCompletion response)
        {
            var rawAnswer = response.Content
                .OfType<ChatMessageContentPart>()
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text)
                .FirstOrDefault() ?? "{}";

            var cleaned = rawAnswer.Trim();
            var match = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)\s*```");
            if (match.Success)
            {
                cleaned = match.Groups[1].Value.Trim();
            }
            return cleaned.Trim('`').Trim();
        }

        private class HybridRAGRequest
        {
            public string Query { get; set; } = "";
            public string? CorrelationId { get; set; }  // Optional filter
            public int? TopK { get; set; }
        }
    }
}