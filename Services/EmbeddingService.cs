using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Embeddings;

namespace LogAnalyser.Services
{
    public class EmbeddingService
    {
        private readonly EmbeddingClient _embeddingClient;
        private readonly string _deploymentName;

        public EmbeddingService(AzureOpenAIClient client, IConfiguration config)
        {
            _deploymentName = config["OpenAI:EmbeddingDeploymentName"] ?? "text-embedding-ada-002";
            Console.WriteLine($"[EmbeddingService] Using embedding deployment: {_deploymentName}");
            _embeddingClient = client.GetEmbeddingClient(_deploymentName);
        }

        public async Task<IReadOnlyList<float>> GetEmbeddingAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"[EmbeddingService] Generating embedding for {text.Length} characters");
            var response = await _embeddingClient.GenerateEmbeddingAsync(text, null, cancellationToken);
            Console.WriteLine($"[EmbeddingService] Embedding generated, dimensions: {response.Value.ToFloats().Length}");
            return response.Value.ToFloats().ToArray();
        }

        public async Task<List<IReadOnlyList<float>>> GetEmbeddingsAsync(
            List<string> texts,
            CancellationToken cancellationToken = default)
        {
            var response = await _embeddingClient.GenerateEmbeddingsAsync(texts, null, cancellationToken);
            return response.Value.Select(e => (IReadOnlyList<float>)e.ToFloats().ToArray()).ToList();
        }
    }
}