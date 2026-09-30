using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LogAnalyser.Models;
using LogAnalyser.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LogAnalyser.Functions
{
    public class IngestLogsFunction
    {
        private readonly HybridStoreService _hybridStore;
        private readonly LogsQueryClient _logsClient;
        private readonly string _appInsightsResource;
        private readonly int _historyDays;

        public IngestLogsFunction(HybridStoreService hybridStore, LogAnalysisOptions options)
        {
            _hybridStore = hybridStore;
            _logsClient = new LogsQueryClient(new DefaultAzureCredential());
            _appInsightsResource = options.ApplicationInsightsResourceId;
            _historyDays = options.HistoryDays;
        }

        [Function("IngestLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            CancellationToken cancellationToken)
        {
            await _hybridStore.CreateIndexIfNotExistsAsync(cancellationToken);

            var queryOptions = new LogsQueryOptions
            {
                AllowPartialErrors = true,
                ServerTimeout = TimeSpan.FromMinutes(3)
            };

            var query = $@"
                let traceLogs = traces
                | where timestamp > ago({_historyDays}d)
                | extend invocationId = coalesce(
                    tostring(customDimensions['InvocationId']),
                    tostring(customDimensions['Invocation ID']),
                    tostring(customDimensions['invocationId']),
                    tostring(customDimensions['invocation_id']))
                | extend isFailure = severityLevel >= 3 or message contains 'Error' or message contains 'Exception' or message contains 'Timeout'
                | project timestamp, message, operation_Id, invocationId, itemType = 'trace',
                    exceptionType = iff(isFailure, 'TraceFailure', ''), component = cloud_RoleName, errorCode = '', isFailure;

                let exceptionLogs = exceptions
                | where timestamp > ago({_historyDays}d)
                | extend invocationId = coalesce(
                    tostring(customDimensions['InvocationId']),
                    tostring(customDimensions['Invocation ID']),
                    tostring(customDimensions['invocationId']),
                    tostring(customDimensions['invocation_id']))
                | extend logMessage = strcat(type, ': ', outerMessage, ' - ', innermostMessage)
                | project timestamp, message = logMessage, operation_Id, invocationId, itemType = 'exception',
                    exceptionType = type, component = cloud_RoleName, errorCode = '', isFailure = true;

                let dependencyLogs = dependencies
                | where timestamp > ago({_historyDays}d)
                | extend invocationId = coalesce(
                    tostring(customDimensions['InvocationId']),
                    tostring(customDimensions['Invocation ID']),
                    tostring(customDimensions['invocationId']),
                    tostring(customDimensions['invocation_id']))
                | extend logMessage = strcat(type, ' dependency ', name, ' to ', target, ' completed with ', resultCode)
                | project timestamp, message = logMessage, operation_Id, invocationId, itemType = 'dependency',
                    exceptionType = iff(success == false, 'DependencyFailure', ''), component = target,
                    errorCode = resultCode, isFailure = success == false;

                let requestLogs = requests
                | where timestamp > ago({_historyDays}d)
                | extend invocationId = coalesce(
                    tostring(customDimensions['InvocationId']),
                    tostring(customDimensions['Invocation ID']),
                    tostring(customDimensions['invocationId']),
                    tostring(customDimensions['invocation_id']))
                | extend statusCode = toint(resultCode)
                | extend logMessage = strcat('Request ', name, ' ', url, ' completed with ', resultCode)
                | project timestamp, message = logMessage, operation_Id, invocationId, itemType = 'request',
                    exceptionType = iff(statusCode between (400 .. 599), 'RequestFailure', ''), component = cloud_RoleName,
                    errorCode = resultCode, isFailure = statusCode between (400 .. 599);

                let customEventLogs = customEvents
                | where timestamp > ago({_historyDays}d)
                | extend invocationId = coalesce(
                    tostring(customDimensions['InvocationId']),
                    tostring(customDimensions['Invocation ID']),
                    tostring(customDimensions['invocationId']),
                    tostring(customDimensions['invocation_id']))
                | project timestamp, message = name, operation_Id, invocationId, itemType = 'customEvent',
                    exceptionType = '', component = cloud_RoleName, errorCode = '', isFailure = false;

                let allTelemetry = union traceLogs, exceptionLogs, dependencyLogs, requestLogs, customEventLogs
                | where isnotempty(invocationId) and isnotempty(message);

                let failingInvocations = requestLogs
                | where isnotempty(invocationId) and isFailure
                | distinct invocationId;

                allTelemetry
                | where invocationId in (failingInvocations)
                | project timestamp, message, operation_Id, invocationId, itemType, exceptionType, component, errorCode
                | order by timestamp asc";

            Console.WriteLine("[IngestLogs] Fetching logs...");

            var response = await _logsClient.QueryResourceAsync(
                new ResourceIdentifier(_appInsightsResource),
                query,
                new QueryTimeRange(TimeSpan.FromDays(_historyDays)),
                queryOptions,
                cancellationToken
            );

            var rowCount = response.Value.Table.Rows.Count;
            Console.WriteLine($"[IngestLogs] Retrieved {rowCount} rows");

            var rows = response.Value.Table.Rows.Select(row => new HistoricalLogRow
            {
                Timestamp = DateTimeOffset.TryParse(row[0]?.ToString(), out var timestamp) ? timestamp : DateTimeOffset.UtcNow,
                Message = row[1]?.ToString() ?? string.Empty,
                OperationId = row[2]?.ToString() ?? string.Empty,
                InvocationId = row[3]?.ToString() ?? string.Empty,
                ItemType = row[4]?.ToString() ?? "trace",
                ExceptionType = row[5]?.ToString() ?? string.Empty,
                Component = row[6]?.ToString() ?? string.Empty,
                ErrorCode = row[7]?.ToString() ?? string.Empty
            }).ToList();

            var logs = rows
                .GroupBy(row => row.InvocationId)
                .Select(CreateIncidentDocument)
                .ToList();

            var withCorrelationId = logs.Count(log => !string.IsNullOrEmpty(log.CorrelationId));
            Console.WriteLine($"[IngestLogs] Created {logs.Count} incident documents; {withCorrelationId} include a correlation ID");

            var indexedIncidents = 0;
            var failedBatches = new List<object>();
            if (logs.Any())
            {
                var batchSize = 25;
                for (int i = 0; i < logs.Count; i += batchSize)
                {
                    var batch = logs.Skip(i).Take(batchSize).ToList();
                    try
                    {
                        await _hybridStore.IndexLogsAsync(batch, cancellationToken);
                        indexedIncidents += batch.Count;
                        Console.WriteLine($"[IngestLogs] Indexed {Math.Min(i + batchSize, logs.Count)}/{logs.Count}");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[IngestLogs] Batch error: {ex.Message}");
                        failedBatches.Add(new
                        {
                            batch = i / batchSize + 1,
                            count = batch.Count,
                            errorType = ex.GetType().Name
                        });
                    }
                }
            }

            var httpResponse = req.CreateResponse(HttpStatusCode.OK);
            await httpResponse.WriteAsJsonAsync(new
            {
                message = $"Indexed {indexedIncidents} of {logs.Count} incidents from the previous {_historyDays} days",
                stats = new
                {
                    historyDays = _historyDays,
                    totalRows = rowCount,
                    totalIncidents = logs.Count,
                    indexedIncidents,
                    failedBatchCount = failedBatches.Count,
                    withCorrelationId,
                    partialQueryResult = response.Value.Error != null
                },
                failedBatches,
                sampleIncidents = logs.Take(3).Select(log => new
                {
                    log.CorrelationId,
                    log.InvocationId,
                    log.Timestamp,
                    log.ExceptionType,
                    log.Component
                }),
                timestamp = DateTime.UtcNow
            });

            return httpResponse;
        }

        private static LogDocument CreateIncidentDocument(IGrouping<string, HistoricalLogRow> group)
        {
            var rows = group.OrderBy(row => row.Timestamp).ToList();
            var signatureRow = rows.FirstOrDefault(row =>
                    row.ItemType == "exception" && !string.IsNullOrWhiteSpace(row.ExceptionType))
                ?? rows.FirstOrDefault(row =>
                    row.ItemType == "dependency" && !string.IsNullOrWhiteSpace(row.ExceptionType))
                ?? rows.FirstOrDefault(row =>
                    row.ItemType == "trace" && !string.IsNullOrWhiteSpace(row.ExceptionType))
                ?? rows.FirstOrDefault(row =>
                    row.ItemType == "request" && !string.IsNullOrWhiteSpace(row.ExceptionType))
                ?? rows[0];
            var requestErrorCode = rows.FirstOrDefault(row =>
                row.ItemType == "request" && !string.IsNullOrWhiteSpace(row.ErrorCode))?.ErrorCode ?? string.Empty;
            var correlationId = rows
                .Select(row => ExtractCorrelationIdFromMessage(row.Message))
                .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;
            var normalizedMessage = NormalizeMessage(signatureRow.Message);
            var content = string.Join("\n", rows.Select(row =>
                $"{row.Timestamp:O} [{row.ItemType}] {row.Message}"));

            if (content.Length > 30000)
            {
                content = content[..30000];
            }

            return new LogDocument
            {
                Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key))),
                Content = content,
                Timestamp = rows[^1].Timestamp,
                OperationId = rows.Select(row => row.OperationId)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                InvocationId = group.Key,
                CorrelationId = correlationId,
                LogType = "incident",
                ExceptionType = string.IsNullOrWhiteSpace(signatureRow.ExceptionType)
                    ? "UnknownError"
                    : signatureRow.ExceptionType,
                Component = string.IsNullOrWhiteSpace(signatureRow.Component)
                    ? "unknown"
                    : signatureRow.Component,
                NormalizedMessage = normalizedMessage,
                ErrorCode = string.IsNullOrWhiteSpace(signatureRow.ErrorCode)
                    ? requestErrorCode
                    : signatureRow.ErrorCode
            };
        }

        private static string NormalizeMessage(string message)
        {
            var normalized = Regex.Replace(message, @"https?://\S+", "<url>", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "<id>", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\b[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}\b", "<id>", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\b\d{4}-\d{2}-\d{2}[^\s]*", "<timestamp>");
            normalized = Regex.Replace(normalized, @"\b\d+\b", "<number>");
            return Regex.Replace(normalized, @"\s+", " ").Trim();
        }

        private static string ExtractCorrelationIdFromMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "";

            var match = Regex.Match(
                message,
                @"Correlation(?:Id| ID)\s*[:=]\s*([A-Z0-9][A-Z0-9._:-]{2,127})",
                RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value.TrimEnd('.', ',', ';', ':').ToUpperInvariant();

            match = Regex.Match(message, @"\b([A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4})\b");
            if (match.Success)
                return match.Groups[1].Value.ToUpperInvariant();

            return "";
        }

        private class HistoricalLogRow
        {
            public DateTimeOffset Timestamp { get; set; }
            public string Message { get; set; } = string.Empty;
            public string OperationId { get; set; } = string.Empty;
            public string InvocationId { get; set; } = string.Empty;
            public string ItemType { get; set; } = string.Empty;
            public string ExceptionType { get; set; } = string.Empty;
            public string Component { get; set; } = string.Empty;
            public string ErrorCode { get; set; } = string.Empty;
        }
    }
}