using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HealthAssistant.Api.Models;
using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace HealthAssistant.Api.Services;

public sealed class DocumentIngestionService(
    IConfiguration configuration,
    OllamaClient ollama)
{
    private const int ChunkSize = 1_200;
    private const int ChunkOverlap = 160;
    private const int EmbeddingBatchSize = 32;
    private const int SearchBatchSize = 500;
    public const long MaxDocumentSizeBytes = 10 * 1024 * 1024;

    public async Task<DocumentIngestionResponse> IndexDocumentAsync(
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var safeFileName = Path.GetFileName(fileName).Trim();
        var extension = Path.GetExtension(safeFileName);
        if (string.IsNullOrWhiteSpace(safeFileName) ||
            !extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDocumentException("Upload a PDF or DOCX document.");
        }

        string extractedText;
        try
        {
            extractedText = ExtractText(content, extension);
        }
        catch (OpenXmlPackageException)
        {
            throw new InvalidDocumentException("The DOCX file is invalid or damaged.");
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new InvalidDocumentException("Encrypted PDF files are not supported.");
        }
        catch (InvalidDataException)
        {
            throw new InvalidDocumentException("The uploaded document is invalid or damaged.");
        }

        var chunks = SplitIntoChunks(extractedText);
        if (chunks.Count == 0)
        {
            throw new InvalidDocumentException("The document contains no extractable text.");
        }

        var searchEndpoint = configuration["AzureSearch:Endpoint"];
        var searchIndex = configuration["AzureSearch:IndexName"];
        var searchApiKey = configuration["AzureSearch:ApiKey"];
        if (string.IsNullOrWhiteSpace(searchEndpoint) ||
            string.IsNullOrWhiteSpace(searchIndex) ||
            string.IsNullOrWhiteSpace(searchApiKey))
        {
            throw new AssistantConfigurationException(
                "Configure the Azure AI Search endpoint, index name, and admin key for document ingestion.");
        }

        var contentField = configuration["AzureSearch:ContentField"] ?? "snippet";
        var vectorField = configuration["AzureSearch:VectorField"] ?? "snippet_vector";
        var sourceField = configuration["AzureSearch:SourceField"] ?? "source";
        var documentNameField = configuration["AzureSearch:DocumentNameField"] ?? "document_name";
        var keyCredential = new AzureKeyCredential(searchApiKey);
        var indexClient = new SearchIndexClient(new Uri(searchEndpoint), keyCredential);
        var index = (await indexClient.GetIndexAsync(searchIndex, cancellationToken)).Value;
        var keyField = index.Fields.SingleOrDefault(field => field.IsKey == true);
        var contentSearchField = index.Fields.SingleOrDefault(field => field.Name == contentField);
        var vectorSearchField = index.Fields.SingleOrDefault(field => field.Name == vectorField);
        var sourceSearchField = index.Fields.SingleOrDefault(field => field.Name == sourceField);
        var documentNameSearchField = index.Fields.SingleOrDefault(field => field.Name == documentNameField);
        if (keyField is null ||
            contentSearchField?.IsSearchable != true ||
            sourceSearchField is null ||
            vectorSearchField?.VectorSearchDimensions is not int vectorDimensions ||
            vectorSearchField.IsSearchable != true ||
            documentNameSearchField?.IsFilterable != true)
        {
            throw new SearchIndexConfigurationException(
                $"The configured index must have a key field, a {vectorField} vector field, and a filterable {documentNameField} field.");
        }

        var embeddings = new List<float[]>(chunks.Count);
        for (var start = 0; start < chunks.Count; start += EmbeddingBatchSize)
        {
            var batch = chunks.Skip(start).Take(EmbeddingBatchSize).ToArray();
            var vectors = await ollama.EmbedAsync(batch, cancellationToken);
            embeddings.AddRange(vectors);
        }

        if (embeddings.Any(vector => vector.Length != vectorDimensions))
        {
            throw new SearchIndexConfigurationException(
                $"The configured {vectorField} field has {vectorDimensions} dimensions, but nomic-embed-text returned {embeddings[0].Length}. Create a separate index with the model's dimensions.");
        }

        var searchClient = new SearchClient(new Uri(searchEndpoint), searchIndex, keyCredential);
        var canonicalName = safeFileName.ToLowerInvariant();
        var oldIds = await FindExistingDocumentIdsAsync(
            searchClient,
            documentNameField,
            keyField.Name,
            canonicalName,
            cancellationToken);
        var indexedIds = new HashSet<string>(StringComparer.Ordinal);
        var searchDocuments = new List<SearchDocument>(chunks.Count);
        for (var indexNumber = 0; indexNumber < chunks.Count; indexNumber++)
        {
            var id = CreateChunkId(canonicalName, indexNumber);
            indexedIds.Add(id);
            searchDocuments.Add(new SearchDocument
            {
                [keyField.Name] = id,
                [contentField] = chunks[indexNumber],
                [vectorField] = new ReadOnlyMemory<float>(embeddings[indexNumber]),
                [sourceField] = safeFileName,
                [documentNameField] = canonicalName
            });
        }

        for (var start = 0; start < searchDocuments.Count; start += SearchBatchSize)
        {
            var batch = IndexDocumentsBatch.Upload(
                searchDocuments.Skip(start).Take(SearchBatchSize));
            var result = await searchClient.IndexDocumentsAsync(
                batch,
                options: null,
                cancellationToken);
            var failedCount = result.Value.Results.Count(indexResult => !indexResult.Succeeded);
            if (failedCount > 0)
            {
                throw new SearchIndexConfigurationException(
                    $"Azure AI Search failed to index {failedCount} document chunks.");
            }
        }

        var staleIds = oldIds.Where(id => !indexedIds.Contains(id)).ToArray();
        for (var start = 0; start < staleIds.Length; start += SearchBatchSize)
        {
            var batch = IndexDocumentsBatch.Delete(
                keyField.Name,
                staleIds.Skip(start).Take(SearchBatchSize));
            var result = await searchClient.IndexDocumentsAsync(
                batch,
                options: null,
                cancellationToken);
            var failedCount = result.Value.Results.Count(indexResult => !indexResult.Succeeded);
            if (failedCount > 0)
            {
                throw new SearchIndexConfigurationException(
                    $"Azure AI Search failed to remove {failedCount} stale chunks from the previous document version.");
            }
        }

        return new DocumentIngestionResponse(safeFileName, chunks.Count);
    }

    private static string ExtractText(Stream content, string extension)
    {
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var pdf = PdfDocument.Open(content);
            return string.Join(
                Environment.NewLine,
                pdf.GetPages().Select(page => page.Text));
        }

        using var wordDocument = WordprocessingDocument.Open(content, false);
        return string.Join(
            Environment.NewLine,
            wordDocument.MainDocumentPart?.Document?.Body?
                .Descendants<Paragraph>()
                .Select(paragraph => paragraph.InnerText) ?? []);
    }

    private static List<string> SplitIntoChunks(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        var start = 0;
        while (start < words.Length)
        {
            var end = start;
            var length = 0;
            while (end < words.Length)
            {
                var nextLength = words[end].Length + (end == start ? 0 : 1);
                if (length + nextLength > ChunkSize && end > start)
                {
                    break;
                }

                length += nextLength;
                end++;
            }

            chunks.Add(string.Join(' ', words[start..end]));
            if (end == words.Length)
            {
                break;
            }

            var overlapStart = end;
            var overlapLength = 0;
            while (overlapStart > start)
            {
                var wordLength = words[overlapStart - 1].Length + (overlapLength == 0 ? 0 : 1);
                if (overlapLength + wordLength > ChunkOverlap)
                {
                    break;
                }

                overlapLength += wordLength;
                overlapStart--;
            }

            start = Math.Max(start + 1, overlapStart);
        }

        return chunks;
    }

    private static async Task<List<string>> FindExistingDocumentIdsAsync(
        SearchClient searchClient,
        string documentNameField,
        string keyField,
        string canonicalName,
        CancellationToken cancellationToken)
    {
        var options = new SearchOptions
        {
            Filter = $"{documentNameField} eq '{canonicalName.Replace("'", "''")}'",
            Size = 1_000
        };
        options.Select.Add(keyField);
        var results = await searchClient.SearchAsync<SearchDocument>("*", options, cancellationToken);
        var ids = new List<string>();
        await foreach (var result in results.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            if (result.Document.TryGetValue(keyField, out var value) && value is not null)
            {
                ids.Add(value.ToString()!);
            }
        }

        return ids;
    }

    private static string CreateChunkId(string canonicalName, int chunkNumber)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalName)))
            .ToLowerInvariant();
        return $"{hash}-{chunkNumber}";
    }
}

public sealed class InvalidDocumentException(string message) : Exception(message);
