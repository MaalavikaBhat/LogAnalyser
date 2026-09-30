using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LogAnalyser.Models;
using LogAnalyser.Services;
using System.Diagnostics;
using System.Net;

public class ExplainErrorFunction
{
    private readonly LogService _logService;
    private readonly PromptBuilder _promptBuilder;
    private readonly AIService _aiService;
    private readonly HybridStoreService _hybridStore;
    private readonly double _similarityThreshold;

    public ExplainErrorFunction(
        LogService logService,
        PromptBuilder promptBuilder,
        AIService aiService,
        HybridStoreService hybridStore,
        LogAnalysisOptions options)
    {
        _logService = logService;
        _promptBuilder = promptBuilder;
        _aiService = aiService;
        _hybridStore = hybridStore;
        _similarityThreshold = options.SimilarityThreshold;
    }

    [Function("ExplainError")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        string? correlationId = query["correlationId"];

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new
            {
                error = "Missing or empty correlationId parameter."
            });
            return badRequestResponse;
        }

        CurrentLogIncident incident;
        try
        {
            incident = await _logService.GetIncidentAsync(correlationId, cancellationToken);
        }
        catch (ArgumentException)
        {
            var invalidResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await invalidResponse.WriteAsJsonAsync(new
            {
                error = "correlationId contains unsupported characters."
            });
            return invalidResponse;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExplainError] Current log retrieval failed: {ex.GetType().Name}");
            var unavailableResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await unavailableResponse.WriteAsJsonAsync(new
            {
                error = "Current log retrieval is unavailable."
            });
            return unavailableResponse;
        }

        if (incident.Logs.Count == 0)
        {
            var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
            await notFoundResponse.WriteAsJsonAsync(new
            {
                error = "No logs found for given correlationId"
            });
            return notFoundResponse;
        }

        var prompt = _promptBuilder.BuildPrompt(incident.Logs);

        ExplanationResponse explanation;
        try
        {
            explanation = await _aiService.GetExplanationResponse(prompt, cancellationToken);
        }
        catch (InvalidDataException)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new
            {
                error = "Failed to parse explanation response."
            });
            return errorResponse;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExplainError] Explanation generation failed: {ex.GetType().Name}");
            var unavailableResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await unavailableResponse.WriteAsJsonAsync(new
            {
                error = "Explanation generation is unavailable."
            });
            return unavailableResponse;
        }

        RecurrenceInfo recurrence;
        try
        {
            var matches = await _hybridStore.SearchHistoricalIncidentsAsync(
                explanation.Signature,
                correlationId,
                incident.OperationId,
                incident.InvocationId,
                cancellationToken);
            var topScore = matches.FirstOrDefault()?.Score ?? 0;

            recurrence = new RecurrenceInfo
            {
                Status = matches.Count > 0 ? "found" : "notFound",
                HasOccurredBefore = matches.Count > 0,
                SimilarIncidentCount = matches.Count,
                Confidence = GetConfidence(topScore),
                MostRecentOccurrence = matches.Count > 0
                    ? matches.Max(match => match.Incident.Timestamp)
                    : null,
                SimilarIncidents = matches.Select(match => new SimilarIncident
                {
                    CorrelationId = match.Incident.CorrelationId,
                    Timestamp = match.Incident.Timestamp,
                    ExceptionType = match.Incident.ExceptionType,
                    Component = match.Incident.Component,
                    Score = match.Score,
                    SimilarityReason = match.SimilarityReason
                }).ToList()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExplainError] Historical search unavailable: {ex.GetType().Name}");
            recurrence = new RecurrenceInfo
            {
                Status = "unavailable",
                Confidence = "none",
                UnavailableReason = "Historical recurrence search is currently unavailable."
            };
        }

        var okResponse = req.CreateResponse(HttpStatusCode.OK);
        await okResponse.WriteAsJsonAsync(new ExplainErrorResponse
        {
            CorrelationId = correlationId,
            Summary = explanation.Summary,
            RootCause = explanation.RootCause,
            Recurrence = recurrence
        });
        totalStopwatch.Stop();
        Console.WriteLine($"[ExplainError] Completed in {totalStopwatch.ElapsedMilliseconds}ms; recurrence status: {recurrence.Status}; matches: {recurrence.SimilarIncidentCount}");
        return okResponse;
    }

    private string GetConfidence(double score)
    {
        if (score <= 0)
        {
            return "none";
        }

        return score >= _similarityThreshold * 2.5
            ? "high"
            : score >= _similarityThreshold * 1.5
                ? "medium"
                : "low";
    }
}