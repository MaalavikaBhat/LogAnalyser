using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using LogAnalyser.Models;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogAnalyser.Services
{
    public class AIService
    {
        private readonly OpenAIClient _client;
        private readonly string _deploymentName;

        public AIService(OpenAIClient client, IConfiguration config)
        {
            _client = client;
            _deploymentName = config["OpenAI:DeploymentName"] ?? throw new ArgumentNullException("OpenAI:DeploymentName", "Deployment name configuration is missing.");
        }

        public async Task<string> GetExplanation(string prompt, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            
            Console.WriteLine($"[AIService] Prompt length: {prompt.Length} chars (~{prompt.Length / 4} tokens)");
            
            var chatClient = _client.GetChatClient(_deploymentName);
            
            var response = await chatClient.CompleteChatAsync(
                new ChatMessage[]
                {
                    ChatMessage.CreateSystemMessage(
                        "You are a backend failure analysis assistant. " +
                        "Always respond with ONLY valid JSON, no markdown formatting, no code blocks, no backticks."
                    ),
                    ChatMessage.CreateUserMessage(prompt)
                },
                new ChatCompletionOptions
                {
                    Temperature = 0.2f,
                    MaxOutputTokenCount = 1000,
                    ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
                },
                cancellationToken);

            sw.Stop();
            Console.WriteLine($"[AIService] OpenAI call completed in {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"[AIService] Usage - Prompt tokens: {response.Value.Usage.InputTokenCount}, Completion tokens: {response.Value.Usage.OutputTokenCount}");

            var content = response.Value.Content;
            var textParts = content
                .OfType<ChatMessageContentPart>()
                .Where(part => part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
                .Select(part => part.Text);

            var result = string.Join(" ", textParts);
            
            // Clean up markdown code blocks if LLM still returns them
            result = CleanJsonResponse(result);
            
            return result;
        }

        public async Task<ExplanationResponse> GetExplanationResponse(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            var result = await GetExplanation(prompt, cancellationToken);

            try
            {
                var explanation = JsonSerializer.Deserialize<ExplanationResponse>(result, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (explanation == null ||
                    string.IsNullOrWhiteSpace(explanation.RootCause) ||
                    string.IsNullOrWhiteSpace(explanation.Signature.ExceptionType) ||
                    string.IsNullOrWhiteSpace(explanation.Signature.Component) ||
                    string.IsNullOrWhiteSpace(explanation.Signature.NormalizedMessage))
                {
                    throw new InvalidDataException("The AI response did not contain the required explanation and signature fields.");
                }

                return explanation;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The AI response was not valid explanation JSON.", ex);
            }
        }

        /// <summary>
        /// Removes markdown code block formatting from JSON responses
        /// </summary>
        private static string CleanJsonResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return response;

            var cleaned = response.Trim();
            
            // Remove ```json ... ``` or ``` ... ```
            var codeBlockPattern = @"^```(?:json)?\s*([\s\S]*?)\s*```$";
            var match = Regex.Match(cleaned, codeBlockPattern);
            if (match.Success)
            {
                cleaned = match.Groups[1].Value.Trim();
            }
            
            // Also handle case where there's text before/after code block
            var inlinePattern = @"```(?:json)?\s*([\s\S]*?)\s*```";
            match = Regex.Match(cleaned, inlinePattern);
            if (match.Success)
            {
                cleaned = match.Groups[1].Value.Trim();
            }
            
            // Remove leading/trailing backticks if any remain
            cleaned = cleaned.Trim('`').Trim();
            
            return cleaned;
        }
    }
}
