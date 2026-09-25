# Migration: Anthropic SDK → Microsoft.Extensions.AI

DocQaMs is a copy of DocQa (`D:\code\docqa`, github.com/genehinckley-coder/docqa) in which the application
talks to the model **only through Microsoft.Extensions.AI** (`IChatClient`, `ChatMessage`,
`ChatResponseUpdate`, `ChatResponse`, `AIContent`, `UsageDetails`, `ChatFinishReason`). The behaviour,
the UI, the wire requests and the cache design are unchanged.

## Projects

| Project | Depends on | Role |
|---|---|---|
| `DocQaMs.Core` | `Microsoft.Extensions.AI.Abstractions` only (no Anthropic) | Corpus, answering, quote checking, conversations. Provider-neutral. |
| `DocQaMs.Claude` | Core, `Microsoft.Extensions.AI`, `Anthropic` | The Claude provider: builds the `IChatClient` pipeline, token counting, error translation, API key handling. The only project that references Anthropic types. |
| `DocQaMs.Web` | Core, Claude | Blazor Server UI. `Program.cs` wires `AddDocQaCore(...)` and `AddDocQaMsClaude()`. |
| `tests/DocQaMs.Core.Tests` | Core | Provider-neutral tests, using fake `IChatClient`s and neutral builders. |
| `tests/DocQaMs.Claude.Tests` | Claude | Wire-level tests: real `AnthropicClient` + `AsIChatClient()` + the Claude middleware over a stub `HttpMessageHandler` fed canned SSE. Also the live tests (gated by `DOCQA_LIVE=1`). |
| `tests/DocQaMs.Web.Tests` | Web | bUnit tests. |

## Why a middleware, and not just `AsIChatClient()`

The official `Anthropic` package (12.50.0) ships `AsIChatClient()`, but DocQa relies on API features the
Microsoft.Extensions.AI abstraction does not model, and the adapter drops or cannot express them:

| Needed by DocQa | What the adapter does | How DocQaMs carries it |
|---|---|---|
| Citable plain-text `document` blocks (title, context, `citations.enabled`) | No M.E.AI content type | `CitableDocumentContent : AIContent` (Core) |
| Cache breakpoints: 1h on the last document, 5m on the previous question, 5m top-level automatic | `WithCacheControl` only for some content; top-level unsupported | `ChatKeys.CacheTtl` on content, `ChatKeys.ConversationCacheTtl` on options |
| `thinking: adaptive, display: summarized`, `output_config.effort` incl. `max` | `ReasoningEffort` has no `max` | `ChatOptions.Reasoning` (+ `ChatKeys.Effort`, authoritative) |
| Citation document index and code-point offsets | `CitationAnnotation` has neither; `AnnotatedRegions` is null | `CitationAnnotation.AdditionalProperties`: `ChatKeys.DocumentIndex`, `StartCharIndex`, `EndCharIndex`, `CitationType` |
| Content block boundaries (per-block citations and coverage) | Not on updates; `ToChatResponse()` merges text blocks | `ChatKeys.BlockIndex` on every response content |
| Cache writes split by TTL (1h / 5m), input usage before output starts | Split dropped; usage only at the end | `UsageContent` at message start and at the end; `ChatKeys.CacheWrite1hTokens` / `CacheWrite5mTokens` |
| Raw stop reason and refusal details | `stop_details` dropped; `stop_sequence` folded into `Stop` | `ChatKeys.StopReason`, `RefusalCategory`, `RefusalExplanation` |
| Replaying cited answers with their citations | Annotations are not sent back | Middleware renders assistant text with its citations |
| Token counting (`count_tokens`) | No M.E.AI abstraction | `ITokenCounter` (Core), implemented in Claude via `GetService<IAnthropicClient>()`-equivalent access |
| Typed API failures | SDK exceptions | `ChatProviderException` (Core) with a `ChatProviderFailure` kind |

The Claude middleware, `ClaudeChatClient : DelegatingChatClient`, sits over `AsIChatClient()`. On the
request path it renders every content item (documents, questions, thinking, redacted thinking, cited
text) as the SDK's `ContentBlockParam` and passes it through `AIContent.RawRepresentation`, and it
supplies thinking, effort, the system prompt and the top-level cache control via
`ChatOptions.RawRepresentationFactory`. The resulting request body is byte-for-byte the shape DocQa sent.
On the response path it reads each update's `RawRepresentation` (the SDK's `RawMessageStreamEvent`) to
add block indexes, citation offsets, the cache-write split, the stop reason and refusal details, and it
translates SDK exceptions into `ChatProviderException`.

Everything the app itself does — building requests, streaming, aggregating the reply per block,
checking quotes, replaying the conversation, costing — uses Microsoft.Extensions.AI types and `ChatKeys`.

## Neutral contract (in `DocQaMs.Core/Chat`)

* `ChatKeys` — every AdditionalProperties key, with its semantics documented.
* `CitableDocumentContent` — a citable plain-text document.
* `ChatProviderException` / `ChatProviderFailure` — provider-neutral failures.
* `IChatClientProvider` — `IChatClient ForEffort(EffortLevel)`; one client per level because each has its own timeout.

### Request shape (built by `MessageRequestBuilder`, Core)

* First turn: `ChatMessage(User, [CitableDocumentContent × n (last one: CacheTtl = "1h"), TextContent(question)])`.
* Follow-up: `ChatMessage(User, [TextContent(question)])`.
* Assistant turn: `ChatMessage(Assistant, replay contents)` from `ReplayConverter`.
* `Create(...)` returns a `ChatRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions Options)`:
  the previous question (`messages[^3]`, when there is history) gets a copy of its last `TextContent`
  with `CacheTtl = "5m"`; options carry `ModelId`, `MaxOutputTokens`, `Instructions` (the system prompt),
  `Reasoning = { Effort, Output = Summary }`, and AdditionalProperties `Effort` (wire value) and
  `ConversationCacheTtl = "5m"`.

### Streaming contract (produced by `ClaudeChatClient`, consumed by Core's `AnswerService`)

* The first `UsageContent` carries the input side (output 0); the last carries the final counts.
  `InputTokenCount` = uncached + cache read + cache write, `CachedInputTokenCount` = cache read,
  `AdditionalCounts[CacheWrite1hTokens / CacheWrite5mTokens]` split the writes.
* `TextReasoningContent` deltas: text in `Text`, signature in `ProtectedData`; redacted reasoning has
  `RedactedReasoning = true` and its data in `ProtectedData`. Each has `BlockIndex`.
* `TextContent` deltas have `BlockIndex`. Citations arrive as a `TextContent("")` with the block's index
  and one `CitationAnnotation` (`Snippet` = cited text, `Title` = document title, offsets in
  AdditionalProperties).
* The end of each content block is marked by an update (usually with no contents) carrying
  `CompletedBlockIndex`, so a text block completes, with its citations, as soon as it ends. A response
  stopped or failed between blocks therefore keeps every finished block's `[n]` markers, as DocQa did
  (it completed blocks at `content_block_stop`). Content with a higher index, or the end of the stream,
  still completes a block when no marker arrives.
* The update carrying `FinishReason` also carries `StopReason` (and refusal details).
* A failure after the provider has signalled the end of the message ends the stream normally (logged).
* Failures are `ChatProviderException`; cancellation is `OperationCanceledException`.

### Replay contract (`ReplayConverter`, Core → sent back through `ClaudeChatClient`)

* Reasoning → `TextReasoningContent(text) { ProtectedData = signature }`.
* Redacted reasoning → `TextReasoningContent("") { ProtectedData = data, [RedactedReasoning] = true }`.
* Text → `TextContent(text)` with its `CitationAnnotation`s (neutral keys). Empty text is dropped.
  The middleware sends the text verbatim with its `char_location` citations.

## Behavioural differences from DocQa

* The system prompt is supplied as `ChatOptions.Instructions`; the middleware still sends it as the
  plain `system` string, so the prompt and cache prefix are unchanged.
* The streaming client's whole-request timeout equals the effort level's `RequestTimeouts` value, so the
  app recognizes a timeout by elapsed time against that value (DocQa read `IAnthropicClient.Timeout`).
* System messages are refused by `ClaudeChatClient`: the system prompt travels only as
  `ChatOptions.Instructions`, so nothing can silently change the cached prefix.
* A stream that ends without `message_stop` fails as `StreamEndedEarly`. The Anthropic 12.50.0 raw stream
  ends quietly in that case (12.47.0, which DocQa used, threw), so the middleware checks explicitly.
* `ChatResponseExtensions.ToChatResponse()` sums every `UsageContent`, so it would double-count the
  input-side usage the provider streams first. The app aggregates per block itself (`AnswerStreamReader`),
  and `ClaudeChatClient.GetResponseAsync` keeps only the final usage. For the same reason the
  OpenTelemetry middleware sits *inside* `ClaudeChatClient` (order: `ClaudeChatClient` → OpenTelemetry →
  adapter), and its log output is disabled (it logged every user Stop as a warning with a stack trace).
* **SDK change worth knowing:** Anthropic 12.50.0 retries its own header timeout (1 + `MaxRetries`
  attempts; 12.47.0 made one). Answers are unaffected — `AnswerService` cancels on its own token at the
  same deadline, and a cancelled token is not retried — but the token counter, which relies on the SDK's
  default timeout, can take up to three times that timeout before failing.

## Test layout

| Project | Tests | What |
|---|---|---|
| `DocQaMs.Core.Tests` | 554 | Provider-neutral: `FakeChatClient` scripted with contract-shaped updates (`NeutralUpdates`). |
| `DocQaMs.Claude.Tests` | 181 (+7 live, skipped) | Real `AnthropicClient` + adapter + `ClaudeChatClient` over a stub handler with canned SSE; request bodies asserted byte for byte; end-to-end `AnswerService`. |
| `DocQaMs.Web.Tests` | 287 | bUnit. |
