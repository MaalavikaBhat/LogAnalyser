using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using LogAnalyser.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LogAnalyser.Services
{
    public class LogService
    {
        private readonly LogsQueryClient _client;
        private readonly string _resource;
        private readonly int _maxPromptCharacters;

        public LogService(LogAnalysisOptions options)
        {
            _resource = options.ApplicationInsightsResourceId;
            _maxPromptCharacters = options.MaxPromptCharacters;
            _client = new LogsQueryClient(new DefaultAzureCredential());
        }

        public async Task<List<string>> GetLogsAsync(
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            var incident = await GetIncidentAsync(correlationId, cancellationToken);
            return incident.Logs;
        }

        public async Task<CurrentLogIncident> GetIncidentAsync(
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            if (!Regex.IsMatch(correlationId, @"^[A-Za-z0-9._:-]{1,128}$"))
            {
                throw new ArgumentException("Correlation ID contains unsupported characters.", nameof(correlationId));
            }

            var sw = Stopwatch.StartNew();

            string query = $@"
            let correlationId = '{correlationId}';
            let matchingLogs = union
                (traces
                | extend invocationId = coalesce(
                    tostring(customDimensions['InvocationId']),
                    tostring(customDimensions['Invocation ID']),
                    tostring(customDimensions['invocationId']),
                    tostring(customDimensions['invocation_id']))
                | project operation_Id, invocationId, searchMessage = message),
                (exceptions
                | extend invocationId = coalesce(
                    tostring(customDimensions['InvocationId']),
                    tostring(customDimensions['Invocation ID']),
                    tostring(customDimensions['invocationId']),
                    tostring(customDimensions['invocation_id']))
                | extend searchMessage = strcat(type, ': ', outerMessage, ' - ', innermostMessage)
                | project operation_Id, invocationId, searchMessage)
                | where searchMessage contains correlationId;
            let opId =
                toscalar(
                    matchingLogs
                    | project operation_Id
                    | take 1
                );
            let currentInvocationId =
                toscalar(
                    matchingLogs
                    | where isnotempty(invocationId)
                    | project invocationId
                    | take 1
                );

            union
                (traces
                | extend invocationId = coalesce(tostring(customDimensions['InvocationId']), tostring(customDimensions['Invocation ID']), tostring(customDimensions['invocationId']), tostring(customDimensions['invocation_id']))
                | where (isnotempty(currentInvocationId) and invocationId == currentInvocationId) or (isempty(currentInvocationId) and operation_Id == opId)
                | project timestamp, message, operation_Id, itemType = 'trace', invocationId),
                (exceptions
                | extend invocationId = coalesce(tostring(customDimensions['InvocationId']), tostring(customDimensions['Invocation ID']), tostring(customDimensions['invocationId']), tostring(customDimensions['invocation_id']))
                | where (isnotempty(currentInvocationId) and invocationId == currentInvocationId) or (isempty(currentInvocationId) and operation_Id == opId)
                | extend message = strcat(type, ': ', outerMessage, ' - ', innermostMessage)
                | project timestamp, message, operation_Id, itemType = 'exception', invocationId),
                (dependencies
                | extend invocationId = coalesce(tostring(customDimensions['InvocationId']), tostring(customDimensions['Invocation ID']), tostring(customDimensions['invocationId']), tostring(customDimensions['invocation_id']))
                | where (isnotempty(currentInvocationId) and invocationId == currentInvocationId) or (isempty(currentInvocationId) and operation_Id == opId)
                | extend message = strcat(type, ' dependency ', name, ' to ', target, ' completed with ', resultCode)
                | project timestamp, message, operation_Id, itemType = 'dependency', invocationId),
                (requests
                | extend invocationId = coalesce(tostring(customDimensions['InvocationId']), tostring(customDimensions['Invocation ID']), tostring(customDimensions['invocationId']), tostring(customDimensions['invocation_id']))
                | where (isnotempty(currentInvocationId) and invocationId == currentInvocationId) or (isempty(currentInvocationId) and operation_Id == opId)
                | extend message = strcat('Request ', name, ' ', url, ' completed with ', resultCode)
                | project timestamp, message, operation_Id, itemType = 'request', invocationId),
                (customEvents
                | extend invocationId = coalesce(tostring(customDimensions['InvocationId']), tostring(customDimensions['Invocation ID']), tostring(customDimensions['invocationId']), tostring(customDimensions['invocation_id']))
                | where (isnotempty(currentInvocationId) and invocationId == currentInvocationId) or (isempty(currentInvocationId) and operation_Id == opId)
                | project timestamp, message = name, operation_Id, itemType = 'customEvent', invocationId)
            | order by timestamp asc
            | take 500
            ";

            var queryOptions = new LogsQueryOptions
            {
                AllowPartialErrors = true
            };

            var response = await _client.QueryResourceAsync(
                new ResourceIdentifier(_resource),
                query,
                new QueryTimeRange(TimeSpan.FromDays(7)),
                queryOptions,
                cancellationToken
            );

            sw.Stop();
            Console.WriteLine($"[LogService] Query completed in {sw.ElapsedMilliseconds}ms");

            if (response.Value.Error != null)
            {
                Console.WriteLine($"[LogService] Partial error: {response.Value.Error.Message}");
            }

            var logs = new List<string>();
            var operationId = string.Empty;
            var invocationId = string.Empty;

            foreach (var row in response.Value.Table.Rows)
            {
                var timestamp = row[0];
                var message = row[1]?.ToString();
                operationId = row[2]?.ToString() ?? operationId;
                var itemType = row[3]?.ToString() ?? "log";
                invocationId = row[4]?.ToString() ?? invocationId;
                logs.Add($"{timestamp} [{itemType}]: {message}");
            }

            var boundedLogs = BoundTimeline(logs, _maxPromptCharacters);
            Console.WriteLine($"[LogService] Retrieved {logs.Count} logs; using {boundedLogs.Count} logs and {boundedLogs.Sum(l => l.Length)} chars");

            return new CurrentLogIncident
            {
                CorrelationId = correlationId,
                OperationId = operationId,
                InvocationId = invocationId,
                Logs = boundedLogs
            };
        }

        private static List<string> BoundTimeline(List<string> logs, int maxCharacters)
        {
            if (logs.Sum(log => log.Length) <= maxCharacters)
            {
                return logs;
            }

            var selectedIndexes = new SortedSet<int>();
            foreach (var index in Enumerable.Range(0, Math.Min(10, logs.Count)))
            {
                selectedIndexes.Add(index);
            }

            foreach (var index in Enumerable.Range(Math.Max(0, logs.Count - 20), Math.Min(20, logs.Count)))
            {
                selectedIndexes.Add(index);
            }

            for (var index = 0; index < logs.Count; index++)
            {
                if (!Regex.IsMatch(logs[index], "error|exception|fail|critical|timeout", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                for (var nearby = Math.Max(0, index - 2); nearby <= Math.Min(logs.Count - 1, index + 2); nearby++)
                {
                    selectedIndexes.Add(nearby);
                }
            }

            var bounded = new List<string>();
            var totalCharacters = 0;
            foreach (var index in selectedIndexes)
            {
                if (totalCharacters + logs[index].Length > maxCharacters)
                {
                    continue;
                }

                bounded.Add(logs[index]);
                totalCharacters += logs[index].Length;
            }

            return bounded;
        }
    }

    public class CurrentLogIncident
    {
        public string CorrelationId { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public string InvocationId { get; set; } = string.Empty;
        public List<string> Logs { get; set; } = new();
    }
}