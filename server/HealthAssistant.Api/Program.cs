using Azure;
using HealthAssistant.Api.Models;
using HealthAssistant.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddHttpClient<OllamaClient>();
builder.Services.AddScoped<HealthAssistantService>();
builder.Services.AddScoped<DocumentIngestionService>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod());
});
var app = builder.Build();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/chat", async Task<IResult> (
    ChatRequest request,
    HealthAssistantService assistant,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    IAsyncEnumerable<string> answerChunks;
    try
    {
        answerChunks = await assistant.GetAnswerStreamAsync(request, cancellationToken);
    }
    catch (InvalidChatRequestException exception)
    {
        return Results.BadRequest(new { detail = exception.Message });
    }
    catch (AssistantConfigurationException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (SearchIndexConfigurationException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (OllamaUnavailableException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    await using var enumerator = answerChunks.GetAsyncEnumerator(cancellationToken);
    bool hasChunk;
    try
    {
        hasChunk = await enumerator.MoveNextAsync();
    }
    catch (OllamaUnavailableException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!hasChunk)
    {
        httpContext.Response.ContentType = "text/plain; charset=utf-8";
        return Results.Empty;
    }

    httpContext.Response.ContentType = "text/plain; charset=utf-8";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers["X-Accel-Buffering"] = "no";

    do
    {
        await httpContext.Response.WriteAsync(enumerator.Current, cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }
    while (await enumerator.MoveNextAsync());

    return Results.Empty;
})
.DisableAntiforgery()
.WithSummary("Stream a health assistant answer")
.WithDescription("Searches company knowledge and streams generated answer text as it becomes available.")
.Produces(StatusCodes.Status200OK, contentType: "text/plain")
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status500InternalServerError)
.Produces(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/documents", async Task<IResult> (
    [FromForm] IFormFile? file,
    DocumentIngestionService ingestion,
    CancellationToken cancellationToken) =>
{
    if (file is null || file.Length == 0)
    {
        return Results.Problem(
            detail: "Include a non-empty PDF or DOCX in the multipart form field named 'file'.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (file.Length > DocumentIngestionService.MaxDocumentSizeBytes)
    {
        return Results.Problem(
            detail: "The document must be 10 MiB or smaller.",
            statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    await using var content = file.OpenReadStream();
    try
    {
        var result = await ingestion.IndexDocumentAsync(file.FileName, content, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidDocumentException exception)
    {
        return Results.BadRequest(new { detail = exception.Message });
    }
    catch (AssistantConfigurationException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (SearchIndexConfigurationException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (OllamaUnavailableException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (RequestFailedException)
    {
        return Results.Problem(
            detail: "Azure AI Search failed. Verify the configured index, admin permissions, and field schema.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.DisableAntiforgery()
.Accepts<IFormFile>("multipart/form-data")
.WithSummary("Index a PDF or DOCX document")
.WithDescription("Extracts text, embeds overlapping chunks with nomic-embed-text, and indexes them in Azure AI Search.")
.Produces<DocumentIngestionResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status413PayloadTooLarge)
.Produces(StatusCodes.Status500InternalServerError)
.Produces(StatusCodes.Status503ServiceUnavailable);

app.Run();
