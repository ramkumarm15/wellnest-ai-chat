# Wellnest health-document assistant

An Angular frontend and ASP.NET Core API using local Ollama models for embeddings and chat, with Azure AI Search for document retrieval. PDF and DOCX uploads are text-extracted, chunked, embedded with `nomic-embed-text`, and indexed; chat questions use the same embedding model and answers stream from `qwen2.5:7b`.

## Run locally

Requirements: Node.js 20.19+, .NET 10 SDK, Ollama with `nomic-embed-text` and `qwen2.5:7b`, and Azure AI Search.

1. Download the local models if needed:

   ```powershell
   ollama pull nomic-embed-text
   ollama pull qwen2.5:7b
   ```

2. Configure the API process environment. Use a separate Azure AI Search index for 768-dimensional `nomic-embed-text` vectors; do not point this API at an index created with a different embedding model. Document ingestion requires an Azure AI Search **admin** key:

   ```powershell
   $env:AzureSearch__Endpoint = "https://YOUR-SEARCH-SERVICE.search.windows.net"
   $env:AzureSearch__IndexName = "YOUR-NEW-NOMIC-INDEX"
   $env:AzureSearch__ApiKey = "YOUR-SEARCH-ADMIN-KEY"
   ```

   Provision the new index before running the API. It must include the following fields:

   | Field | Type | Required properties |
   |---|---|---|
   | `chunk_id` | `Edm.String` | Key |
   | `snippet` | `Edm.String` | Searchable |
   | `snippet_vector` | `Collection(Edm.Single)` | Searchable, 768 dimensions, vector search profile |
   | `source` | `Edm.String` | Retrievable |
   | `document_name` | `Edm.String` | Filterable, retrievable |

   Configure the vector profile with an HNSW algorithm. The API validates the key, searchable content/vector fields, vector dimension, source field, and filterable document-name field before indexing. It does not create, replace, or migrate indexes.

3. In one terminal, start the API:

   ```powershell
   dotnet run --project server\HealthAssistant.Api
   ```

4. In another terminal, start Angular:

   ```powershell
   cd client
   npm start
   ```

   Open `http://localhost:4200`.

   The interactive Scalar API reference is available at `http://localhost:5124/scalar/v1` while the API runs in Development. The OpenAPI document is at `http://localhost:5124/openapi/v1.json`.

   To upload a document:

   ```powershell
   curl.exe -F "file=@.\guidance.pdf" http://localhost:5124/api/documents
   ```

## Retrieval flow and boundaries

- `POST /api/documents` accepts one multipart `file` field containing a PDF or DOCX up to 10 MiB. It extracts text (scanned and encrypted PDFs are not supported), creates overlapping 1,200-character chunks, embeds them with Ollama, then uploads them to Azure AI Search.
- Re-uploading a document with the same filename replaces its indexed chunks. Use unique filenames where documents should remain distinct.
- The frontend sends only the question and conversation history. The API embeds each question with the same Ollama embedding model, retrieves up to six matching chunks, then streams generated text from the local chat model.
- `Ollama:Endpoint`, `Ollama:EmbeddingModel`, and `Ollama:ChatModel` are configurable in `appsettings.json`; defaults are `http://localhost:11434`, `nomic-embed-text`, and `qwen2.5:7b`.
- Protect the Azure AI Search admin key in an environment variable or secret manager. This prototype does not authenticate users or apply per-user/document authorization filters; add identity and security trimming before exposing restricted company documents.
- The API does not persist conversation history. Do not use real patient information in this prototype.
- This is not a production-ready healthcare system and makes no HIPAA, clinical-safety, or regulatory-compliance claim. Before real use, add authentication and authorization, encrypted/controlled storage and retention, audit and privacy controls, abuse protection, clinical validation, and deployment-specific compliance review.
- The assistant provides general explanations only; it is not medical advice, diagnosis, or a substitute for a clinician. It does not process emergency requests.
