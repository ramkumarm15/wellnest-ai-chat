namespace HealthAssistant.Api.Models;

public sealed record ChatRequest(
    string? Message,
    IReadOnlyList<ConversationTurn>? History);

public sealed record ConversationTurn(string? Role, string? Content);
