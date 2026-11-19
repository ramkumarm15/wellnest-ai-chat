namespace HealthAssistant.Api.Models;

/// <summary>Reports a document indexed in Azure AI Search.</summary>
public sealed record DocumentIngestionResponse(string FileName, int IndexedChunks);
