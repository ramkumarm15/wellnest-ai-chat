using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HealthAssistant.Api.Services;

public sealed class OllamaClient(HttpClient httpClient, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri endpoint = new(
        (configuration["Ollama:Endpoint"] ?? "http://localhost:11434").TrimEnd('/') + "/");

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> input,
        CancellationToken cancellationToken)
    {
        if (input.Count == 0)
        {
            return [];
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "api/embed"))
        {
            Content = JsonContent.Create(
                new EmbedRequest(
                    configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text",
                    input),
                options: JsonOptions)
        };
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new OllamaUnavailableException(
                $"Ollama embeddings returned HTTP {(int)response.StatusCode}. Verify the Ollama service and embedding model.");
        }

        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(
            JsonOptions,
            cancellationToken);
        if (result?.Embeddings is not { Length: > 0 } embeddings ||
            embeddings.Any(embedding => embedding is not { Length: > 0 }))
        {
            throw new InvalidOperationException("Ollama returned no embedding vectors.");
        }

        if (embeddings.Length != input.Count)
        {
            throw new InvalidOperationException("Ollama returned an unexpected number of embedding vectors.");
        }

        return embeddings;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "api/chat"))
        {
            Content = JsonContent.Create(
                new ChatRequest(
                    configuration["Ollama:ChatModel"] ?? "qwen2.5:7b",
                    [new ChatMessage("user", prompt)],
                    Stream: true),
                options: JsonOptions)
        };
        using var response = await SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new OllamaUnavailableException(
                $"Ollama chat returned HTTP {(int)response.StatusCode}. Verify the Ollama service and chat model.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var update = JsonSerializer.Deserialize<ChatStreamResponse>(line, JsonOptions)
                ?? throw new InvalidOperationException("Ollama returned an invalid chat stream update.");
            if (!string.IsNullOrWhiteSpace(update.Error))
            {
                throw new OllamaUnavailableException(
                    "Ollama could not generate a response. Verify that the configured chat model is available.");
            }

            if (!string.IsNullOrEmpty(update.Message?.Content))
            {
                yield return update.Message.Content;
            }

            if (update.Done)
            {
                yield break;
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(request, completionOption, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new OllamaUnavailableException(
                "Could not connect to Ollama. Ensure it is running and the configured models are available.",
                exception);
        }
    }

    private sealed record EmbedRequest(string Model, IReadOnlyList<string> Input);

    private sealed record EmbedResponse(float[][]? Embeddings);

    private sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages, bool Stream);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ChatStreamResponse(ChatMessage? Message, bool Done, string? Error);
}

public sealed class OllamaUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
