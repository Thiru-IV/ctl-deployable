using System.ClientModel;
using Azure.AI.OpenAI;
using OpenAI.Embeddings;

namespace Cascade.CTL.Agent.Infrastructure.RAG;

/// <summary>
/// Concrete implementation of <see cref="IRAGEmbeddingGenerator"/> backed by Azure OpenAI embeddings.
/// </summary>
public sealed class AzureOpenAIEmbeddingGenerator : IRAGEmbeddingGenerator
{
    private readonly EmbeddingClient _client;

    public AzureOpenAIEmbeddingGenerator(EmbeddingClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var result = await _client.GenerateEmbeddingAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Value.ToFloats();
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
            return Array.Empty<ReadOnlyMemory<float>>();

        var result = await _client.GenerateEmbeddingsAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);
        var vectors = new ReadOnlyMemory<float>[result.Value.Count];
        for (var i = 0; i < result.Value.Count; i++)
            vectors[i] = result.Value[i].ToFloats();
        return vectors;
    }
}
