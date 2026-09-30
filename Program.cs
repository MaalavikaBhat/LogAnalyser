using Azure;
using Azure.AI.OpenAI;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using OpenTelemetry;
using LogAnalyser.Models;
using LogAnalyser.Services;
using Azure.Monitor.OpenTelemetry.Exporter;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

var openAiEndpoint = GetRequiredConfiguration(builder.Configuration, "OpenAI:Endpoint");
var openAiApiKey = GetRequiredConfiguration(builder.Configuration, "OpenAI:ApiKey");
_ = GetRequiredConfiguration(builder.Configuration, "OpenAI:DeploymentName");
_ = GetRequiredConfiguration(builder.Configuration, "OpenAI:EmbeddingDeploymentName");
_ = GetRequiredConfiguration(builder.Configuration, "AzureSearch:Endpoint");
_ = GetRequiredConfiguration(builder.Configuration, "AzureSearch:ApiKey");

var logAnalysisOptions = builder.Configuration
    .GetSection(LogAnalysisOptions.SectionName)
    .Get<LogAnalysisOptions>() ?? new LogAnalysisOptions();

if (string.IsNullOrWhiteSpace(logAnalysisOptions.ApplicationInsightsResourceId))
{
    throw new InvalidOperationException(
        $"{LogAnalysisOptions.SectionName}:ApplicationInsightsResourceId configuration is missing or empty.");
}

if (logAnalysisOptions.HistoryDays <= 0 ||
    logAnalysisOptions.EmbeddingDimensions <= 0 ||
    logAnalysisOptions.SimilarityThreshold < 0 ||
    logAnalysisOptions.CandidateLimit <= 0 ||
    logAnalysisOptions.ResultLimit <= 0 ||
    logAnalysisOptions.MaxPromptCharacters <= 0)
{
    throw new InvalidOperationException("LogAnalysis numeric configuration values must be positive.");
}

// Azure OpenAI Client (used for both chat and embeddings)
var azureOpenAIClient = new AzureOpenAIClient(
    new Uri(openAiEndpoint),
    new AzureKeyCredential(openAiApiKey)
);

builder.Services.AddSingleton(azureOpenAIClient);
builder.Services.AddSingleton<OpenAIClient>(azureOpenAIClient);
builder.Services.AddSingleton(logAnalysisOptions);

// Register existing services (Basic RAG)
builder.Services.AddSingleton<LogService>();
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<AIService>();

// Register Hybrid RAG services
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<HybridStoreService>();

// Only add Azure Monitor if connection string is configured
var connectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}
else
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults();
}

builder.Build().Run();

static string GetRequiredConfiguration(IConfiguration configuration, string key)
{
    var value = configuration[key];
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{key} configuration is missing or empty.")
        : value;
}
