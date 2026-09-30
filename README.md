# LogAnalyser

The application analyzes a current Application Insights operation and checks whether a comparable incident occurred during the previous 30 days.

## Workflow

1. Manually populate the historical incident index by calling `POST /api/IngestLogs`.
2. Analyze an incident by calling `GET /api/ExplainError?correlationId=<value>`.
3. Repeat ingestion whenever the historical index needs to be refreshed.

Historical ingestion groups exceptions, error traces, and failed dependencies by Application Insights operation. Each operation is stored in Azure AI Search as one incident document with a normalized signature and embedding.

## ExplainError response

The existing `correlationId`, `summary`, and `rootCause` fields remain available. The response also includes `recurrence`:

- `status`: `found`, `notFound`, or `unavailable`.
- `hasOccurredBefore`: whether a qualified historical incident was found.
- `similarIncidentCount`: number of bounded matches returned.
- `confidence`: `none`, `low`, `medium`, or `high`.
- `mostRecentOccurrence`: timestamp of the newest returned match.
- `similarIncidents`: bounded source metadata without raw log content.
- `unavailableReason`: safe diagnostic text when historical search fails.

A historical incident qualifies only when its exception type and component match the current normalized signature and its hybrid keyword/vector score meets `LogAnalysis__SimilarityThreshold`. The current operation and correlation ID are excluded.

## Required configuration

- `OpenAI__Endpoint`
- `OpenAI__ApiKey`
- `OpenAI__DeploymentName`
- `OpenAI__EmbeddingDeploymentName`
- `AzureSearch__Endpoint`
- `AzureSearch__ApiKey`
- `LogAnalysis__ApplicationInsightsResourceId`
- `LogAnalysis__SearchIndexName`
- `LogAnalysis__HistoryDays`
- `LogAnalysis__EmbeddingDimensions`
- `LogAnalysis__SimilarityThreshold`
- `LogAnalysis__CandidateLimit`
- `LogAnalysis__ResultLimit`
- `LogAnalysis__MaxPromptCharacters`

The configured embedding dimensions must match the selected embedding deployment. The default local configuration uses a versioned `log-incidents-v2` index with 3072 dimensions and a 30-day history window.

For deployed environments, store credentials in secure application settings or Key Vault references and use managed identity where supported.
