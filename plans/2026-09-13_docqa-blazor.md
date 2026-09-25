# DocQA — Blazor Port of the NotRag Prototype
_Generated: 2026-09-13 · Revised the same day after the subagent plan review · Revised again the
same day to add model and effort selection, a cost reference, and a required Stop button (user
request), then after a focused review of that revision. See **Plan Review Log**._

## Goal

Build a local, single-user Blazor web app in `D:\code\docqa\` that does what the NotRag
Python prototype (`D:\code\NotRag\`) does, through the UI in the Claude Design project
"Interactive document query application" (`DocQA Prototype.dc.html` plus the *Ice Blue with
Packers Highlights* design system). The app answers questions about Illinois Administrative
Code Title 41, Parts 172 and 174–178, by sending the whole extracted corpus to Claude as
cached, citable document blocks, then checks every quoted passage character for character
against the text that was sent.

Unlike the prototype, which is fixed to Claude Opus 5 at effort `high`, the app offers a
choice of **Claude Opus 5 or Claude Sonnet 5**, at any of the **five effort levels**, per
conversation. Each choice shows what it costs before it is used. A **Stop** button ends the
wait for any answer in progress.

The prototype is frozen as the reference. Its plan (`plans/2026-09-11_docqa-notrag.md`), its
measured results and its 229 tests are the specification. This plan ports behavior that
has already been proven live; it does not re-derive it.

**Success** means all of the following:
- The sump question from the prototype's plan returns a cited answer with every quote
  matched, on Opus 5 at `high`.
- A follow-up in the same conversation reads the cached corpus (about $0.20, against about
  $2 for the first question).
- A new conversation on the same model and level also reads the cache instead of writing it
  again.
- Every model and level combination accepts the same request shape.
- Stop ends an answer and leaves the conversation usable.
- The three screens (Conversation, Documents, Corpus settings) match the design, running
  on real data instead of the design's canned answers.

The guarantee stays exactly as narrow as the prototype's. A match shows that a quote
appears at the cited location (its provenance). It does not show that the quote supports
the claim beside it (entailment). The UI says "matched", never "verified". It matters
more now that cheaper settings exist: a lower effort level can quote correctly and still
reason less thoroughly.

## Assumptions

- **Local and single-user, with no authentication for now.** The app is bound to loopback
  only. Anyone who can reach the port spends money on the API key, so the loopback binding
  is the access control until authentication is added.
- **The API key is read where it already lives:** `D:\claude\apikeys\notrag_key.txt`. It is
  not copied into the repository, user secrets or an environment variable. The loader
  applies the prototype's rules: strip a BOM, take the one line starting `sk-ant-`, and
  never put key text in an error message.
- **Conversations are held in memory for the app's lifetime.** They survive a page refresh
  and are lost on restart. (User decision, 2026-09-13.)
- **The Corpus settings screen is read-only.** Cache-affecting values are not editable at
  runtime, because each edit would force a cache write of about $2. Model and level are
  chosen per conversation on the Conversation screen instead. (User decision, 2026-09-13.)
- **The prototype is a frozen reference and is not kept in sync.** (User decision,
  2026-09-13.)
- **The request shape is the prototype's, with model and effort now parameters.**
  - Fixed as in the prototype:
    - adaptive thinking with `display: "summarized"`
    - plain-text document blocks with `title`, `context` and `citations`
    - a 1-hour breakpoint on the last document block, and a top-level 5-minute breakpoint
      covering the conversation tail
  - **Added at item 1:** on a follow-up, an explicit 5-minute breakpoint on the previous
    question, so that the follow-up reads the conversation history instead of rewriting it.
    See item 4.
    - the same document labels and context template, copied verbatim
  - **Changed after item 11:** the system prompt, which began as the prototype's copied
    verbatim, now also requires a two-part answer — a `Summary` heading with a short,
    uncited executive statement, then a `Detail` heading carrying the full cited analysis.
    Its pinned hash is therefore ours, not the prototype's. The added text is **174 tokens**,
    measured on 2026-09-24: the corpus count went from 199,749 to 199,923, and the 1-hour
    prefix from 199,745 to 199,919. The **Cost Reference** uses the new prefix.
    - **The fingerprint now covers the system prompt.** It renders before the documents, so
      it is part of the cached prefix; before, an edit to it left the state file reporting
      "Unchanged" while every cached corpus copy had gone cold. A state file recorded before
      this change reports Changed once, which is correct.
  - Now parameters:
    - `model`: `claude-opus-5` or `claude-sonnet-5`
    - `output_config.effort`: `low`, `medium`, `high`, `xhigh` or `max`
    - `max_tokens`: 32,000 for `low` through `high`, as in the prototype; 64,000 at
      `xhigh`; and 128,000 at `max`, both models' output ceiling
  - The default, **Opus 5 at `high`**, is exactly the prototype's shape. Its live
    measurements therefore carry over as the acceptance baseline: 199,745 tokens in the
    1-hour prefix, $2.16 cold, $0.23 and $0.18 warm. Items 2 and 3 check that parity
    rather than assume it.
  - **Changed 2026-09-24: the default is now Opus 5.5 at `high`.** The acceptance baseline
    above stays Opus 5's, and Opus 5 at `high` remains the only combination the cost
    preview labels measured.
- **Both models can take the same request.** Sonnet 5 supports document citations, prompt
  caching, adaptive thinking with summarized display, the full five-level effort range, a
  1M context window and 128K output (per the SDK skill's model reference). **Proven at
  item 1:** all ten combinations accept the request shape, including `max_tokens` of 64,000
  at `xhigh` and 128,000 at `max`.
- **Each combination of model and effort level has its own corpus cache.** Caches are scoped
  to the model. A change to `thinking` or `effort` invalidates the messages cache, and this
  app's corpus lives in the messages (the first user message), not in `system`. So there
  are ten independent 1-hour corpus entries, one per combination, and the first question
  on each is a cold write. **Confirmed at item 1** on small fixtures: a level change and a
  model change each write a new entry, and the entries coexist rather than replace one
  another.
  - **Not so on Opus 5.5, observed 2026-09-24.** After a cold write at `high`, the first
    question at `low` and then at `medium` each *read* the whole corpus (199,937 tokens),
    with nothing written. Opus 5.5 appears to share one corpus entry across effort levels.
    The app still keys cache warmth and baselines by model and level, so on Opus 5.5 it
    predicts a cold write that does not happen: the preview overstates, never understates.
    Seen once; not yet acted on.
- **The corpus is 199,749 tokens on both models.** Measured with the free `count_tokens`
  call on 2026-09-13, with the system prompt included; both models use the same tokenizer.
  **Confirmed at item 3** for the C# request, on both models.
  - **Updated 2026-09-24,** after the Summary/Detail system prompt: 199,923 on Opus 5 and
    Sonnet 5, and 199,925 on Opus 5.5, whose tokenizer is not quite the same.
    `CorpusTokenCountLiveTests` pins these three.
  The corpus costs in the **Cost Reference** are therefore exact arithmetic on a measured
  count. Output lengths, tail writes and the question's few dozen tokens are not.
- **The design is the visual spec, not the data spec.** Its data is placeholder: document
  titles like "General Provisions", invented section text, a hard-coded 199,583-token
  total, and keyword-matched canned answers. Real labels come from the prototype's
  `config.py`. The design shows a fixed model badge, no level control and no Stop button;
  those three are additions to it.
- **.NET 10 LTS.** SDK 10.0.400 is installed. .NET 11 is an STS release that reaches GA on
  2026-11-10; do not target it.
- **Package versions, re-checked at item 0 on 2026-09-13.** All are pinned in
  `Directory.Packages.props`.
  - `Anthropic` 12.47.0, the official C# SDK, released 2026-09-10. It targets net8.0, net9.0
    and netstandard2.0, and runs on net10.0.
  - `AngleSharp` 1.8.1. The first draft said 1.5.2; 1.8.1 is the latest stable, and bUnit
    2.11.3 requires at least 1.8.1 anyway.
  - `xunit.v3` 4.0.1. The 4.x line runs only on Microsoft Testing Platform v2 and drops
    VSTest on the .NET 10 SDK, so `global.json` selects the MTP runner for `dotnet test`. No
    `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio` package is needed.
  - `bunit` 2.11.3
  - `Microsoft.Extensions.*` 10.0.12, for options binding and validation in Core
- **The official C# SDK has the needed types.** Confirmed by inspecting the 12.47.0
  assembly:
  - Request types: `DocumentBlockParam` (with `CacheControl`), `PlainTextSource`,
    `CitationsConfigParam`, `CitationCharLocationParam`, `ThinkingBlockParam`,
    `RedactedThinkingBlockParam`, `ThinkingConfigAdaptive.Display`, and top-level
    `MessageCreateParams.CacheControl`.
  - Response and stream types: `CitationCharLocation`, `CitationsDelta`, `SignatureDelta`,
    and `CacheCreation.Ephemeral1hInputTokens` and `Ephemeral5mInputTokens`.
  - A stream aggregator: `MessageContentAggregator`, `SseAggregatorExtensions.Aggregate`
    and `CollectAsync`.
  - Client options: `ClientOptions.Handlers`, `BaseUrl`, `Timeout` and `MaxRetries`.
    `Timeout` is documented as covering the whole HTTP call, response body included.
  - `IAnthropicClient.WithOptions`, which returns a view of the client with modified
    options.
  - Models: `Model.ClaudeOpus5` and `Model.ClaudeSonnet5`.

  **Settled at item 1**, offline against a canned stream and then live:
  - The aggregator folds `citations_delta` and `signature_delta` into the final message.
  - `CollectAsync` passes every event except `ping` through as it aggregates, so one pass
    over the stream serves both the UI and the final message.
  - The `Effort` enum has an `Xhigh` member, which serializes to `"xhigh"`. A test pins all
    five wire strings.
- **AngleSharp builds the same tree lxml does for these six files.** lxml uses libxml2's HTML
  parser, which does not follow the HTML5 parsing algorithm; AngleSharp does. **Verified at
  item 2:** the C# extraction of all six files is byte-identical to the prototype's, with no
  accepted differences.
- **Prices are for display only, never for behavior.** They were confirmed against the
  published pricing page on 2026-09-13; see **Cost Reference**.

## Out of Scope

- Authentication, authorization, multiple users, and binding to anything other than
  loopback.
- Deployment, containers, CI/CD.
- Persisting conversations across restarts.
- Editing labels, TTL, ceiling or system prompt from the UI.
- **Changing model or effort level once a conversation has a committed turn.** See item 6
  and **What We're NOT Doing**.
- Models other than Opus 5 and Sonnet 5. Haiku 4.5 has a 200K context window, which the
  199,749-token corpus plus a question does not fit. Fable 5.1 was not requested.
- Any RAG machinery (embeddings, chunking, vector store) and the agentic grep/read loop. Both
  were rejected in the prototype's plan, and the corpus still sits at 49.9% of the ceiling.
- Document formats other than HTML; corpus switching; recursive folder scanning.
- Entailment checking.
- Rendering answers as Markdown or HTML. Answers render as plain text; see **What We're NOT
  Doing**.
- The design system's night theme and any unused components: Checkbox, Radio, Switch,
  Dialog, Toast, Tabs, IconButton. (Select is now needed, for the model and level
  pickers.)
- Keeping the Python prototype in sync.

## Cost Reference

**What is measured and what is estimated.**
- **Prices:** from the pricing page, 2026-09-13.
- **Corpus token count:** measured on both models the same day, and again on 2026-09-24
  after the system prompt grew by 174 tokens. The corpus figures below use the new 1-hour
  prefix of 199,919 tokens: measured on Opus 5.5's cold write, and on Opus 5 and Sonnet 5
  inferred as their count less 4, the gap measured before.
- **Opus 5 at `high`:** measured by the prototype.
- **Every other combination:** an estimate or an assumption, because the output length
  (thinking plus answer) of an effort level is not published for this workload. Every row
  below says which it is. Item 12 would have replaced the estimates with measurements; it was
  decided against on 2026-09-15, so these bases are final unless it is ever run.

### Prices, USD per million tokens

| Model | Input | 5-minute cache write | 1-hour cache write | Cache read | Output |
|---|---|---|---|---|---|
| Claude Opus 5 | 5.00 | 6.25 | 10.00 | 0.50 | 25.00 |
| Claude Sonnet 5 | 2.00 | 2.50 | 4.00 | 0.20 | 10.00 |

- **Standard pricing.** Sonnet 5's $2/$10 was announced as introductory pricing through
  2026-08-31. The pricing page now states it is the standard price, and that the scheduled
  rise to $3/$15 will not happen.
- **Thinking tokens are billed as output.**
- **No long-context surcharge.** Both models are billed at these rates across the whole 1M
  context window: the pricing page states that Claude 4.6 and later models "include the
  full 1M token context window at standard pricing." That matters here, because every
  follow-up request carries more than 200K tokens.

### The corpus, which costs the same at every effort level

| | Opus 5 | Sonnet 5 |
|---|---|---|
| Cold 1-hour corpus write, on the first question for a model and level, or after an hour without using it | **$2.00** ($1.999) | **$0.80** ($0.800) |
| Warm corpus read, on every other question | $0.10 ($0.1000) | $0.04 ($0.0400) |
| Follow-up tail write, per 1,000 tokens of the previous turn (the 5-minute rate) | $0.00625 | $0.0025 |

That is 199,919 tokens × the 1-hour write price, and × the read price. (With the
prototype's system prompt the prefix was 199,745 tokens, $1.997 and $0.799 cold.) A
follow-up's tail is roughly the previous turn's output: turn 2 of the prototype's live run
wrote 6,338 tail tokens after a 6,358-token answer. Every combination of model and level has
its own cold write, so trying all ten within an hour costs 5 × $1.999 + 5 × $0.800 =
**$13.99** in corpus writes alone.

### Output per answer, by effort level

| Level | Output tokens per answer | Basis |
|---|---|---|
| `low` | about 800–4,300 | **Estimate:** 0.25–0.67 × `high` |
| `medium` | about 1,600–5,400 | **Estimate:** 0.5–0.85 × `high` |
| `high` | about 3,200–6,400 | **Measured on Opus 5**: 5,313 and 6,358 on cold sump answers, and about 3,200–3,600 on the two warm answers (derived from their rounded totals) |
| `xhigh` | about 4,200–12,800 | **Assumption:** 1.3–2 × `high`. No published figure. |
| `max` | about 4,800–19,200 | **Assumption:** 1.5–3 × `high`. No published figure. |

- **Basis for `low` and `medium`:** a judgment spanning two published Anthropic curves.
  - Long-horizon coding on Opus 5 ran at about a quarter of the default's cost at `low`,
    and about half at `medium`.
  - Research and knowledge work ran a third to a half cheaper at `low` (0.5–0.67 ×), and at
    70–85% of the cost at `medium`.

  Those are costs per task, not output-only ratios, so applying them to output alone is an
  estimate.
- **Sonnet 5 is assumed to produce about as many output tokens as Opus 5** at the same level.
  This is unmeasured.
- **Quality at low effort.** Sonnet 5 respects effort strictly at the low end, and the SDK
  guidance warns of under-thinking on multi-step reasoning at `low`. Cross-referencing
  regulations is multi-step reasoning. A cheap answer can have every quote matched and
  still miss a related provision. That is the entailment gap again, not a provenance
  failure.

### Cost per question

| Model and level | Cold first question | Warm question (first or follow-up) | Basis |
|---|---|---|---|
| Opus 5, `low` | $2.02–$2.11 | $0.12–$0.23 | Estimate |
| Opus 5, `medium` | $2.04–$2.13 | $0.14–$0.27 | Estimate |
| **Opus 5, `high`** (default) | **$2.08–$2.16** (measured $2.13 and $2.16) | **$0.18–$0.30** (measured $0.18 and $0.23) | **Measured** |
| Opus 5, `xhigh` | $2.10–$2.32 | $0.20–$0.50 | Assumption |
| Opus 5, `max` | $2.12–$2.48 | $0.22–$0.70 | Assumption |
| Sonnet 5, `low` | $0.81–$0.84 | $0.05–$0.09 | Estimate, plus the Sonnet 5 assumption |
| Sonnet 5, `medium` | $0.82–$0.85 | $0.06–$0.11 | Estimate, plus the Sonnet 5 assumption |
| Sonnet 5, `high` | $0.83–$0.86 | $0.07–$0.12 | Assumption: Opus 5's measured output at Sonnet 5 prices |
| Sonnet 5, `xhigh` | $0.84–$0.93 | $0.08–$0.20 | Assumption |
| Sonnet 5, `max` | $0.85–$0.99 | $0.09–$0.28 | Assumption |

- **Cold** means the corpus write plus the output.
- **Warm** means the corpus read plus the output. On a follow-up it also includes one tail
  write, sized to the previous answer.
- Tail reads on long conversations add a little more at the read price.
- The question's own few dozen input tokens are left out; they are under $0.001.

**Opus 5.5, added 2026-09-24; `high` is now the default.** Prices $4 input, $5 and $8
cache writes, $0.20 read and $20 output per million tokens. Measured on 2026-09-24 with the
sump question and one follow-up, about $4.90 in all:

| Level | Pairs | Output tokens measured | Configured range | Cold question | Warm question |
|---|---|---|---|---|---|
| `low` | 1 | 4,643 and 3,053 | 3,000–4,700 | $1.66–$1.69 | $0.10–$0.16 |
| `medium` | 1 | 6,849 and 4,659 | 4,600–6,900 | $1.69–$1.74 | $0.13–$0.21 |
| `high` | 3 | 8,226, 7,062, 8,264; follow-ups 5,309, 6,961, 5,466 | 5,300–8,300 | **$1.71–$1.77** (measured $1.74 and $1.76) | **$0.15–$0.25** (measured $0.13–$0.21) |

- The ranges are in `appsettings.json`'s `MeasuredOutputTokens`, so the preview labels
  these three levels measured. `low` and `medium` rest on one pair each.
- `xhigh` and `max` still use the Opus 5-derived assumptions above. Opus 5.5 wrote about
  30% more than Opus 5 at `high`, so they may run low.
- At the 32,000-token cap: $2.24 cold, or $0.84 for a follow-up.
- Every answer in these runs had all its quotes matched.

### Worst case: an answer that runs to its `max_tokens` limit

These are totals for the whole question, not amounts added to the table above.

| Model | Levels | Output limit | Cold question | Follow-up after a capped answer |
|---|---|---|---|---|
| Opus 5 | `low`, `medium`, `high` | 32,000 | $2.80 | $1.10 |
| Opus 5 | `xhigh` | 64,000 | $3.60 | $2.10 |
| Opus 5 | `max` | 128,000 | $5.20 | $4.10 |
| Sonnet 5 | `low`, `medium`, `high` | 32,000 | $1.12 | $0.44 |
| Sonnet 5 | `xhigh` | 64,000 | $1.44 | $0.84 |
| Sonnet 5 | `max` | 128,000 | $2.08 | $1.64 |

A follow-up after a capped answer costs the corpus read, plus a tail write of that capped
answer, plus another capped answer.

**Latency.** Only Opus 5 at `high` is measured: 55–67 seconds, about 95 output tokens per
second including the time to the first token. At that rate a 128,000-token `max` answer
takes about 22 minutes, which drives the timeouts in item 0.

## Plan Items

### 0. Repository, solution, configuration surface, credential loading
**Why:** Everything depends on it, and the easy ways to load a credential are the insecure
ones.
**What:**
- **Repository first, before anything else lands.**
  - `git init` in `D:\code\docqa\`.
  - The first commit is `.gitignore` plus `.gitattributes`.
    - `.gitignore` covers:
      - `bin/`, `obj/`, `.vs/`, `*.user`
      - `apikeys/`, `*.key`, `*-key.txt`, `*_key.txt`, `.env*`
      - `admincode/*_files/` (the browser's sidecar folders, which are never read)
      - the runtime state file `.docqa_state.json*`
    - `.gitattributes` marks `tests/golden/*.txt -text` and `admincode/*.htm -text`, so git
      never rewrites line endings in files the tests compare byte for byte.
  - Then create the remote: `gh repo create docqa --private --source=. --remote=origin`.
    A repository named `docqa` does not exist yet on `genehinckley-coder`.
  - The six `admincode/*.htm` files are tracked, as they are in NotRag. They are public ILGA
    pages and the tests depend on them.
- **Solution layout.** Use a `.slnx` solution, the .NET 10 default.
  - `src/DocQaMs.Core`: a class library for extraction, corpus assembly, the answer service,
    the quote checker, conversation state and cost. It has no UI dependency.
  - `src/DocQaMs.Web`: a Blazor Web App with Interactive Server render mode set globally.
  - `tests/DocQaMs.Core.Tests`: xunit.v3.
  - `tests/DocQaMs.Web.Tests`: xunit.v3 plus bUnit.
  - The Core/Web split is justified because every rule worth testing lives in Core, and
    testing it should not need a component host.
- **Shared build settings.**
  - `Directory.Build.props` sets `Nullable` enable, `ImplicitUsings` and
    `TreatWarningsAsErrors`.
  - `GenerateDocumentationFile` is on for `DocQaMs.Core` only. That enforces XML doc comments
    on its public API. Razor-generated component classes and xunit test classes are public
    too, so enabling it everywhere turns CS1591 into build errors in the Web and test
    projects.
  - `Directory.Packages.props` handles central package management, keeping one version per
    package.
- **Configuration is split in two.**
  - **Cache-affecting values are compiled constants,** in a `CacheAffecting` static class
    in Core:
    - the system prompt, document labels and context template
    - the corpus TTL and conversation TTL
    - the thinking configuration
    - **the set of selectable models** (`claude-opus-5`, `claude-sonnet-5`) and **the set of
      effort levels** (`low` through `max`)
    - an exclusion list for any combination item 1 finds unusable, each with its reason
    - the default combination, Opus 5 at `high`

    A test pins the SHA-256 of each one, as the prototype pins its system prompt. They are
    deliberately *not* in `appsettings.json`: a stray value in
    `appsettings.Development.json` would silently cost $2 per question.
  - **Everything else goes in `DocQaOptions`,** bound from `appsettings.json`:
    - paths: the corpus folder, key path and state-file path
    - limits: `max_tokens` per effort level, token ceiling, context window and
      context-warning fraction
    - checks: cache-read tolerance, minimum citation length and thin-coverage fraction
    - **the request timeout per effort level**
    - **a price table keyed by model**, with the five buckets from **Cost Reference**
    - **the estimated output-token range per level**, used only for the selector's cost
      preview, and replaced by measured figures after item 12

    None of these is part of the cached prefix.
  - **Options validation.** `ValidateDataAnnotations().ValidateOnStart()`, plus an
    `IValidateOptions<DocQaOptions>` that checks the options against the `CacheAffecting`
    sets:
    - every selectable model has a complete price row, with every price greater than zero
    - every level has a `max_tokens` value of at most 128,000, a timeout, and an output
      range whose low end is no higher than its high end

    A gap fails at startup instead of becoming a `KeyNotFoundException`, or a $0 cost, in
    the preview.
- **Credential loading.** `ApiKeyLoader` ports `credentials.py`:
  - Read as UTF-8 and strip a BOM.
  - Require exactly one line starting `sk-ant-`.
  - Never include key text in an exception message.
- **The client.** `AnthropicClient` is a DI singleton built with an explicit `ApiKey`, and
  with `BaseUrl` set from a code constant (`https://api.anthropic.com`).
  - Never set `ANTHROPIC_API_KEY`.
  - The SDK documents that it reads `ANTHROPIC_BASE_URL` and `ANTHROPIC_PROFILE`. Setting
    both values explicitly means an ambient environment variable or `ant` profile cannot
    redirect requests or substitute another account.
  - Item 1 proves this, using junk values for those variables.
- **Timeouts, per level.**
  - The SDK's `Timeout` covers the whole HTTP call, including reading the streamed body.
    Each level therefore gets its own `WithOptions` view of the client: 20 minutes for `low`
    through `high`, 30 minutes for `xhigh`, and 60 minutes for `max`.
  - That leaves roughly two to three times the measured-rate duration of an answer at each
    level's output limit. Stop is the user's control for anything shorter.
  - **Corrected at item 1:** the SDK's `Timeout` does *not* cover a streamed body, whatever
    its documentation says. It bounds only the wait for the response headers, and a body
    that stalls after the headers is never interrupted. The answer service (item 4)
    therefore enforces each level's timeout itself.
- **Retries.** `MaxRetries` stays at 2 for failures before the response starts: 429, 5xx
  and connection errors.
  - Item 1, check 6 verifies that a stream failing *after* the response has started is not
    retried. If it is, the streaming views set `MaxRetries = 0`, because a retried answer
    pays its corpus read or write, and its output, a second time. **Verified at item 1:** it
    is not retried (one request, then a raw `IOException`), so `MaxRetries` stays at 2.
  - Every retry is logged through a handler in `ClientOptions.Handlers`.
  - Do not pass a custom `HttpClient`: it brings its own 100-second timeout, which
    conflicts with the SDK's timeout.
- **Clock.** Register `TimeProvider.System`, so the cache warmth tracker (item 6) can be
  tested with a fake clock.
- **Networking.** Kestrel is bound to `http://localhost:<port>` only. `AllowedHosts` is
  `localhost;127.0.0.1;[::1]`. No `0.0.0.0` or `*` URL goes in `launchSettings.json` or
  `appsettings`.
- **Logging.** No HTTP header or request-body logging at any level. Request headers carry
  the key, and bodies carry about 480K characters of corpus.
**Depends on:** Nothing.
**Risks:** bUnit 2.10.3 may not support xunit.v3 4.0.0, which is a new major version.
Check this before committing the test projects. If they are incompatible, pin xunit.v3 to
3.2.x. **Resolved at item 0:** bUnit 2.11.3 has no dependency on any xunit package, and a
bUnit smoke test passes under xunit.v3 4.0.1.
**Open questions:** None.

### 1. Request-shape spike against the C# SDK *(decision gate)*
**Why:** The prototype's item 2 proved what the *API* accepts and returns, but only for Opus 5
at `high`. What is unproven:
- whether the *C# SDK* serializes that request, and streams and aggregates the response,
  without losing anything, in the single-pass shape the answer service needs
- whether **every model and level combination** takes the same shape
- whether each combination caches separately, and whether the separate entries coexist

That is the highest-risk group of assumptions in this plan: if it fails, quote checking,
follow-up turns or the cost preview fail, and possibly silently.
**What:** A test harness (a live-gated xunit test, not a throwaway console app) using the
prototype's approach of two small synthetic fixtures of about 1K tokens each. Every request
goes through a capturing `DelegatingHandler` added via `ClientOptions.Handlers`. The whole
item costs **about $1**: about $0.10 for checks 1–6 and under $1 for check 7. The harness
asserts pass/fail criteria:
1. **Serialization and credentials.** The captured request body carries every field the
   prototype sends: the `document` blocks (`source` of type `text` with `media_type`
   `text/plain`, plus `title`, `context`, `citations.enabled`, and `cache_control` with
   `ttl: "1h"` on the last block), `thinking: {type: "adaptive", display: "summarized"}`,
   `output_config.effort`, and the top-level `cache_control` with a 5-minute TTL.
   - **Each of the five effort values serializes to its wire string.** If the SDK's
     `Effort` enum has no `xhigh` member, construct the value from the raw string
     `"xhigh"` through the SDK's API-enum type, and keep a test pinning the serialized
     output.
   - With `ANTHROPIC_BASE_URL`, `ANTHROPIC_PROFILE` and `ANTHROPIC_AUTH_TOKEN` set to junk
     for the test, the captured request goes to `api.anthropic.com`, carries `x-api-key`,
     and has no `Authorization` header.
   - The 12.47.0 assembly also names `ANTHROPIC_API_KEY`, `ANTHROPIC_WORKSPACE_ID`,
     `ANTHROPIC_ORGANIZATION_ID`, `ANTHROPIC_IDENTITY_TOKEN`, `ANTHROPIC_IDENTITY_TOKEN_FILE`,
     `ANTHROPIC_FEDERATION_RULE_ID` and `ANTHROPIC_SERVICE_ACCOUNT_ID` (found at item 0). Set
     those to junk too, and assert that the key sent is the file's key and that no
     workspace or organization header is added.
2. **One pass over the stream, the shape production uses.** One enumeration of
   `CreateStreaming` must both forward every event to a consumer as it arrives and feed an
   aggregator. The candidate is `CollectAsync` passing events through, then
   `aggregator.Message()`. The assertions:
   - the capture handler saw **exactly one** HTTP request
   - the final message's cited text blocks carry their citations, with `document_index`,
     `start_char_index`, `end_char_index` and `cited_text` intact
   - every thinking block carries its signature

   If the SDK cannot do all three in one pass, write our own accumulator over
   `RawMessageStreamEvent`, covering `content_block_start`, the text, thinking, citations
   and signature deltas, and `content_block_stop`. That adds roughly a day of work.
3. **Offsets.** Build the code-point index type here, and item 5 reuses it. A fixture
   holding one non-BMP character proves that code-point offsets, converted to UTF-16 through
   it, slice the correct span after that character.
4. **Round trip.** The assistant response, converted block by block into parameter types,
   is accepted as turn 2, and turn 2 reads the tail cache. The conversion is:
   - a `ThinkingBlock` becomes a `ThinkingBlockParam`, with its signature
   - a `RedactedThinkingBlock` becomes a `RedactedThinkingBlockParam`
   - a `TextBlock` becomes a `TextBlockParam`, with each `CitationCharLocation` converted to
     a `CitationCharLocationParam`
   - empty text blocks are dropped

   The C# SDK has no `.ToParam()`, so this converter is hand-written, and a lossy converter
   is the single most likely source of a 400 on follow-up turns.
5. **Caching.** Question 1 writes the document prefix into the 1-hour bucket
   (`Usage.CacheCreation.Ephemeral1hInputTokens`), and a different question 2 reads the
   same count.
6. **Usage, stop details, Stop and retries.**
   - Input-side usage is readable from `message_start` as it arrives; item 4 needs this to
     record the cost of stopped turns.
   - `StopReason` and `StopDetails` are readable at the end.
   - Cancelling the enumeration mid-answer, the way the Stop button will, closes the HTTP
     stream promptly and raises a recognizable cancellation, not a generic error.
   - **Offline, with the capture handler returning a canned event stream that breaks partway
     through the body:** the SDK surfaces an error and makes exactly one request, meaning no
     retry after the response started. If it retries instead, apply item 0's
     `MaxRetries = 0` rule.
   - **Offline, with a handler that stalls mid-body:** the configured `Timeout` fires during
     body reading.
7. **Every model and level combination, and cache coexistence.** On the fixtures, with a
   one-line question:
   - **One request per combination, all ten.** Each is accepted, shows a cache write, and a
     second request at the same combination reads it.
     - This also runs `low`, where adaptive thinking may produce no thinking block at all,
       through the replay converter and the commit rule.
     - It also sends `max_tokens` of 64,000 and 128,000 at `xhigh` and `max`.
   - **A → B → A, once for a level change and once for a model change.** The sequences are
     Opus 5 `high`, then Opus 5 `low`, then Opus 5 `high`; and Opus 5 `high`, then Sonnet 5
     `high`, then Opus 5 `high`. In each, B writes and the second A reads the full prefix.
     This proves that the entries coexist rather than replace one another, which the warmth
     tracker assumes.
   - **A failing combination is removed on its own.** It goes on `CacheAffecting`'s
     exclusion list with the reason; the rest stay available.
   - **Check fixture size before concluding.** If a combination shows neither a write nor a
     read, enlarge the fixtures first: they may be below that model's minimum cacheable
     prefix.
   - If an effort change turns out *not* to invalidate the corpus, the preview becomes
     simpler; nothing else changes.
**Depends on:** Item 0.
**Risks:**
- If (2) fails, the custom accumulator is the fallback, as described above.
- If (1) or (4) cannot be made to work through the SDK's types, the fallback is raw HTTP
  via `HttpClient` and `System.Text.Json` for the Messages call only. That is a bigger
  change; stop and re-plan instead of drifting into it.
- If a combination fails (7), only that combination is excluded. Opus 5 at `high` is the
  prototype's proven shape and does not depend on the others.
**Open questions:** None. The capture mechanism is settled: `ClientOptions.Handlers`.

### 2. Structure-preserving HTML extraction (AngleSharp), with golden-file parity
**Why:** The subsection structure is what makes the corpus answerable, and the prototype's
extraction rules were validated live: 42 of 42 quotes matched on the sump question.
Matching its output exactly means that validation carries over.
**What:** `CorpusExtractor` ports `extract.py`:
- **Scope.**
  - Read only the top level of `admincode/`. Enumerate `*` and filter by
    `Path.GetExtension` in `{".htm", ".html"}` using `OrdinalIgnoreCase`. On Windows the
    pattern `*.htm` also matches `.html` files, which would list them twice and produce a
    spurious label mismatch.
  - Read only `div.billtext-scale`, and require exactly one per page; there is no fallback
    to the whole page.
  - Strip `script`, `style` and `template`, and remove comments.
- **Output shape.**
  - The block-tag set is as in the prototype, including the table parts.
  - `<br>` is a line break.
  - A table is one paragraph, with one line per `<tr>` and cells joined by ` | `. Empty
    cells are kept; empty rows are dropped.
  - Inside a line, every whitespace run collapses to one space, and empty paragraphs are
    dropped.
  - Explicit checks confirm there is no CR and no U+00A0 in the output.
- **Labels.** Files and labels must agree in both directions; a mismatch is an error.
- **Encoding.** Parse from bytes, so the page's meta charset is honored. Use AngleSharp's
  `HtmlParser` with no loader configured, so it never fetches resources or runs scripts.
- **Golden files come from the prototype's function, not its command line.**
  - `python extract.py --show` prints through stdout, and Windows translates `\n` to `\r\n`
    there. PowerShell redirection can also re-encode the output. Either would produce a
    diff that has nothing to do with the parser.
  - Instead, a one-off script (kept in `tools/`, never inside the frozen prototype) imports
    the prototype's `extract_corpus()` and writes each document with
    `open(path, "w", encoding="utf-8", newline="\n")` to `tests/golden/<part>.txt`.
  - A test asserts that the golden bytes contain no `0x0D` and no BOM.
- **Golden-file parity.** The C# output must be byte-identical for all six parts. Any
  difference is investigated. It is either fixed, or documented here with a reason and
  accepted consciously, knowing it changes the corpus the prototype's measurements were
  taken on.
- **Test fixture.** Copy the prototype's table fixture `tests/fixtures/tables_175.html`.
**Depends on:** Item 0. Independent of item 1.
**Risks:**
- **Parser divergence.** For example, HTML5 closes an open `<p>` before a `<table>`, while
  libxml2 may nest it. The golden diff is the detector.
- **Whitespace classes.** .NET's `\s` includes `\p{Z}`, so it covers U+00A0; Python's `\s`
  also covers U+001C–U+001F. The golden diff catches any difference in practice.
**Open questions:** None.

### 3. Corpus assembly, fingerprint, size guard, and snapshots
**Why:** It prevents the worst failure in this class of app, which is answering confidently
from part of the corpus. It also makes a changed corpus visible instead of a silent $2
surprise.
**What:** `CorpusLoader` ports `corpus.py`:
- **Ordering.** Sort by filename using `StringComparer.Ordinal`. Python sorts by code point,
  so a culture-aware sort could reorder the documents, and with them every
  `document_index`.
- **Size guard, per selectable model.** Count tokens through an injectable `ITokenCounter`.
  The production counter wraps `client.Messages.CountTokens` with the system prompt and a
  placeholder question.
  - Count on **each selectable model** (two free calls). Both gave 199,749 on 2026-09-13,
    but a future tokenizer difference must not slip past the ceiling.
  - The guard applies to the largest count. Above the ceiling, throw
    `CorpusTooLargeException` listing the options; never truncate.
- **Fingerprint.** SHA-256 over a canonical serialization of the document blocks, written
  field by field in a fixed order with `Utf8JsonWriter` and covering every block field,
  TTL included. Do not rely on `System.Text.Json`'s property order. The C# fingerprint
  will not equal the Python one, and does not need to. Model and effort are not part of
  the fingerprint; they key the cache baseline instead (item 6).
  - **Since 2026-09-24 it also covers the system prompt,** hashed ahead of the blocks, since
    the prompt is part of the cached prefix. See **Assumptions**.
- **Parity with the assembled request, not just the extracted text.** The carried-over
  baseline also depends on the rendered `title` and `context` strings and on block order.
  - A golden file generated by the same `tools/` script records, per block, the title, the
    context and the SHA-256 of the text. A test asserts the C# blocks match it.
  - A live-gated test (`count_tokens` is free) asserts that the C# request counts exactly
    the token total recorded in the prototype's `.notrag_state.json`.
- **State file.** `.docqa_state.json` (gitignored) records the fingerprint, token counts per
  model, AngleSharp version, and each document's label, character count and text hash. On
  change it names the difference and the cache-write cost. A malformed file, including a
  field of the wrong type, is reported, never thrown. Writes go through a temp file and
  `File.Move(overwrite: true)`.
- **Per-document token counts.** Six extra `count_tokens` calls at load, which are free,
  because the design's Documents and Settings screens show them. They use the default
  model and are labeled as approximate: documents counted separately do not sum to the
  corpus total. *Nice-to-have: drop them if the rate limit bites.*
  - **Since 2026-09-24** the default is Opus 5.5, whose tokenizer differs slightly from
    the others', so the Settings registry note names the model the counts were made on.
- **Corpus snapshots.** A `Corpus` is immutable. It carries, for each document:
  - its text
  - its code-point index map (item 1)
  - its paragraph ranges, in UTF-16 coordinates (item 9)

  Every conversation keeps a reference to the snapshot its `messages[0]` was built from.
  It quote-checks and highlights against *that* snapshot, never against whatever corpus
  is current.
- **Reload.** A reload replaces the *current* corpus only if extraction, the size guard and
  the state write all succeed, as the prototype's `/reload` does. A conversation whose
  snapshot fingerprint differs from the current one becomes read-only, with a notice to
  start a new conversation. The prototype cleared its single conversation on reload; with
  many conversations in memory, clearing them silently would lose work, and continuing
  them would quote-check against the wrong text.
  **Enforced at item 8,** in `ConversationStore.Ask` and in the Conversation screen.
- **Loading.** A singleton `CorpusService` loads the corpus in the background at startup and
  exposes its state: `Loading`, `Ready(corpus)`, or `Failed(reason)`. The UI renders that
  state, with Ask disabled and the reason shown, instead of the app failing to start. For
  example, no network at startup means the count fails and the UI says so with a retry.
**Depends on:** Item 2.
**Risks:** None beyond the prototype's; the guard is the mitigation.
**Open questions:** None.

### 4. Answer service: one streamed turn
**Why:** This is the core. It is kept separate from conversation state, as the prototype
kept it, so each can be tested alone.
**What:**
```
AnswerService.StreamAsync(AnswerSettings settings,
                          IReadOnlyList<MessageParam> messages,
                          CancellationToken ct)
```
- **`AnswerSettings`** is an immutable `(Model, Effort)` pair validated against the
  `CacheAffecting` sets and exclusion list. It determines `model`,
  `output_config.effort`, `max_tokens` and the timeout view (per level, from options), and
  the price row used for cost.
- **It returns `IAsyncEnumerable<AnswerEvent>`.** The events are:
  - `UsageStarted`: the input-side usage from `message_start`, emitted as soon as it arrives
  - `ThinkingDelta`
  - `TextDelta`
  - `TextBlockCompleted`, with its citations
  - `Completed`, with the final message, elapsed time, stop warning and cost

  The stream is enumerated **once**, by the single-pass mechanism item 1 proved.
- **Request.** `messages[0]` is the corpus document blocks plus the first question; the
  top-level 5-minute tail breakpoint is on every request. The system prompt is sent as a
  plain string, as the prototype sends it. Thinking stays adaptive with summarized display
  at every level; there is no thinking-off option (see **What We're NOT Doing**).
  - **The tail breakpoint (added at item 1).** On a follow-up, the previous question also
    carries an explicit 5-minute breakpoint. `MessageRequestBuilder` adds it to a copy of
    the history on every request, so the marker moves forward each turn. Three of the four
    allowed breakpoints are then in use.
    - **Why:** the previous request's top-level breakpoint wrote its entry ending at that
      question. But a breakpoint looks back at most 20 block positions for an earlier entry,
      and a cited answer spans more: 26 blocks in the live check. Without the marker, every
      follow-up rewrites the whole history at the 5-minute rate.
    - **Measured:** turn 2 read the corpus plus turn 1's tail (3,250 tokens), and turn 3
      read everything turn 2 had written (5,169). (User decision, 2026-09-13.)
  - Follow-up questions are sent as a one-block list, not a bare string, so a question
    serializes the same way with or without the marker.
- **Timeout.** Each request gets a `CancellationTokenSource` linked to the caller's token,
  with `CancelAfter` set to the level's timeout. The SDK's own `Timeout` bounds only the wait
  for headers (item 1).
  - A timeout surfaces as a `TaskCanceledException`, just like Stop. The service tells them
    apart by checking which token was cancelled.
- **No thinking block is assumed.** For short questions, adaptive thinking often produces
  none: in item 1, at every level on Opus 5, and at every level but `max` on Sonnet 5.
- **Stop reasons.** Every stop reason is checked:
  - `end_turn`: complete; no warning
  - `max_tokens`: TRUNCATED. The message names this level's output limit (thinking counts
    toward it). It suggests a narrower question, or a new conversation at a level with a
    higher limit. A lower level is suggested only when the stop landed inside thinking.
  - `refusal`: REFUSED, with the `StopDetails` category
  - anything else: INCOMPLETE
- **Cost.** Priced by bucket from **the selected model's** price row: input, 1-hour write,
  5-minute write, read and output.
  - When the write breakdown is missing, the whole write is priced at the 1-hour rate, so
    the figure errs high.
  - `NOT CACHED` is flagged when a response shows no cache activity at all.
- **Cancellation (Stop).** Cancelling the token ends the enumeration and closes the HTTP
  stream. The service then emits no `Completed`; the caller receives an
  `OperationCanceledException` after any `UsageStarted` already delivered.
  - Output usage arrives only in the final `message_delta`, so a stopped turn's output
    tokens are never reported. The service does not guess them: thinking text is
    summarized, so the thinking tokens cannot be counted on the client.
  - Whether output generated before a disconnect is billed is not stated in any
    first-party documentation found so far. The UI therefore claims only that Stop ends the
    wait, not that it saves money.
- **Errors.** Map the SDK's typed exceptions in `Anthropic.Exceptions`, most specific first:
  - authentication
  - rate limit or quota
  - a 400 caused by exhausted credit, named separately
  - other API status errors
  - connection errors
  - timeouts

  Item 1 found three failures that do not surface as an `Anthropic.Exceptions` API type:
  - a body that breaks after the response started: a raw `IOException`
  - a body that ends without `message_stop`: `AnthropicInvalidDataException`
  - a timeout: a `TaskCanceledException`, with the caller's token not cancelled

  Log each with the full exception object and show the user a short message.
**Depends on:** Items 1 and 3.
**Risks:** Citations cannot be combined with `output_config.format`, so structured JSON
output is permanently unavailable on this path. This is inherited and accepted.
**Open questions:** None.

### 5. Quote checker
**Why:** This is the correctness story. It is a local string comparison, with no API call.
**What:** `QuoteChecker` ports `quotes.py`:
- **Statuses.** Five outcomes: `Matched`, `Mismatch`, `OutOfRange`, `Degenerate` and
  `NotCheckable`.
  - The document index and the span are validated *before* slicing. `Substring` would
    throw where Python's slicing silently clipped, and both are wrong answers.
  - A mismatch detail says where the quote departs from the span, and whether the quote
    appears verbatim elsewhere in the document and at what distance.
- **Code-point offsets, using item 1's index type.**
  - The map has `length + 1` entries in code points, so the valid exclusive end
    `end == length` converts.
  - Bounds are checked in code points *before* conversion. Checking against
    `string.Length` (UTF-16 units) would put the out-of-range boundary in the wrong place
    once any non-BMP character exists.
  - The degenerate threshold (8 non-whitespace characters) and every displayed character
    count are in code points, as in the prototype.
  - The current corpus has no non-BMP characters, so the map is the identity today. It is
    still always used, because a single emoji or math symbol in a future page would
    otherwise shift every span after it by one.
- **Duplicates.** Distinct spans are keyed by `(document_index, start, end)` and counted
  once.
- **Coverage.** Per cited block, the matched non-whitespace characters over the block's own
  non-whitespace characters; blocks under 50% are "thinly sourced". Per answer, the share
  of text in blocks with no citation.
- **Wording.** No output string contains "verified".
- The checker is independent of model and level; every answer is checked the same way.
**Depends on:** Items 1 (the index type), 3 and 4.
**Risks:** None beyond the stated provenance-not-entailment limit.
**Open questions:** None.

### 6. Conversation state and the in-memory store
**Why:** This is where the cache pays off, and where the prototype found the bugs that break
a session permanently. It must port `repl.py`'s `Session` exactly where the prototype
learned something the hard way.
**What:**
- **`Conversation`** holds:
  - its corpus snapshot
  - **its `AnswerSettings` (model and level)**
  - its `messages`
  - its per-bucket cost
  - its answer records: rendered segments, quote report, warnings, and the settings each
    answer used
- **Settings are editable until the conversation has something to protect.**
  - **When they lock.** Settings lock while a turn is in flight, or once `messages` is not
    empty (at least one committed turn). A first turn that is stopped, fails, or can't be
    replayed commits nothing, so the settings become editable again.
  - **Defaults.** "New conversation" copies the settings of the conversation active when it
    is clicked. On first run it uses Opus 5 at `high`.
  - **Changing them later** means a new conversation. "New conversation with different
    settings" does that in one step.
  - **Why.** This is not a limit for its own sake:
    - Switching model mid-conversation misses the model-scoped cache and cannot replay the
      conversation's thinking blocks.
    - Switching effort mid-conversation invalidates the messages cache, which holds the
      corpus.
    - Either would cost a hidden cold write, of $2.00 on Opus 5, on the very next question.
- **Commit only after success, and only if the reply can be replayed.** This ports
  `Session.commit`.
  - Build each request from `messages + [newUserMessage]`.
  - Commit the question and the reply only after `Completed`, **and only if the converted
    reply is non-empty**. A refusal with no text, or a `max_tokens` stop that lands inside
    thinking, can leave nothing sendable once empty text blocks are dropped. Committing
    that would make every later request a 400.
  - When a reply can't be replayed, keep the answer record visible with the notice "Not
    added to the conversation", and return the question to the input.
  - Any exception, and Stop, leaves `messages` exactly as it was, so the roles still
    alternate.
- **Replay through the item 1 converter.** Drop only empty text blocks. Thinking blocks keep
  their signatures and text blocks keep their citations. The conversation is append-only.
- **New conversation.** It builds `messages[0]` freshly from the corpus snapshot plus the
  new question. This is the web version of `/reset`, and it hit the cache in the
  prototype's live run.
- **Cache baseline.** This ports `Session.record` exactly.
  - It is held **per corpus fingerprint, model and level, at app level, and shared by every
    conversation with that combination**, as the prototype's baseline survived `/reset`.
  - It is set on the first turn where the 1-hour write plus the read is greater than zero,
    to that sum. A first turn can write the prefix, read an entry that is still warm, or
    split between the two, so the sum is an upper bound that the 1% tolerance absorbs.
  - **Expected cold writes are not misses.** When the warmth tracker predicted cold for this
    combination, a full write is reported as COLD CACHE WRITE. CACHE MISS is reserved for a
    shortfall the tracker did not predict. Its notice separates an expired entry (this turn
    rewrote the prefix) from a prefix that no longer matches (every turn now pays full
    price).
  - Baselines for a fingerprint are dropped when a reload changes it.
- **Cache warmth tracker.** At app level, keyed by fingerprint, model and level, using the
  injected `TimeProvider`.
  - **What it records.** The time of the last corpus cache write or read, taken at
    `UsageStarted`, when the server reports it, not at `Completed`. Stopped and failed
    turns whose `UsageStarted` showed cache activity update it too.
  - **Warm.** An entry is shown as *probably warm* until 5 minutes before its 1-hour TTL
    runs out, since each read refreshes the TTL. That margin covers a question sent close
    to the expiry.
  - **Write in progress.** When a request goes out on a combination predicted cold, the
    combination is marked "cold write in progress" until `UsageStarted` or failure.
    Another conversation selecting it sees: "A cold write is in progress in another
    conversation. Asking now may pay a second $2.00 write; wait until that answer starts
    streaming."
  - **Limits.** It only knows about this app's own requests, so the preview says
    "probably". A cache warmed by some other client simply turns a predicted write into a
    cheaper read.
  - **Consumer.** It feeds the selector's preview (item 8).
- **Context warning.** Warn at 70% of the context window, measured on the latest request's
  input + cache write + cache read, not a cumulative total.
- **Stop.** `ConversationStore.Stop(conversationId)` cancels that conversation's in-flight
  turn, and has to be safe against the turn's own timing:
  - **No lock is held across I/O.** The per-conversation lock covers only state changes and
    snapshots, never the stream. `Stop` takes the lock just long enough to read the turn's
    `CancellationTokenSource`, then calls `Cancel` outside it. That makes it immediate, even
    in the middle of a long `max` answer.
  - **Once `Completed` arrives, the turn stops watching the token.** The commit runs with
    `CancellationToken.None`, so a Stop that lands after the answer finished changes
    nothing: the answer is paid for, so it is kept.
  - **The outcome is published in the snapshot:** Completed, Stopped, Failed or Not
    replayable. The UI's "Stopping…" state resolves on whichever arrives.
  - **Token-source cleanup.** The token source is removed from the store's map under the
    lock before it is disposed. `Stop` on a missing or finished turn is a no-op, so a
    second Stop, or a Stop after completion, does nothing.
  - **After a Stop,** `messages` are unchanged. The partial answer (the text so far and the
    thinking summary) stays visible, labeled: "Stopped: not checked, not added to the
    conversation."
  - **Cost of a stopped turn, with only what is known:**
    - If `UsageStarted` arrived, the input-side cost is added to the conversation's and the
      app's totals, labeled "input billed about $X; output before Stop not reported".
    - If Stop came before `UsageStarted`, the cost is unknown. A cold prefill of 200K tokens
      takes several seconds, so the write may already have been billed. The label reads
      "cost unknown: up to about $X if the request had started", using the input-side
      figure (cold write or warm read) from the preview. Totals then say "excludes N turns
      of unknown cost".
  - Failed turns are recorded the same way.
- **The question comes back through the store, not the page.** A per-conversation
  `ReturnedQuestion` is set whenever a turn is stopped, fails, or can't be replayed. The
  input takes it when it renders and clears it. This works even when the tab that sent the
  question has been refreshed or closed, or Stop was clicked in another tab.
- **`ConversationStore`** is a DI singleton, for the app's lifetime, per the persistence
  decision. It is shared across tabs and circuits:
  - One question may be in flight per conversation, enforced on the server as well as by
    the UI.
  - Mutations are serialized per conversation, under the short lock described above.
  - The store hands out immutable snapshots, never live lists. A snapshot includes the
    **in-progress answer** (partial text, thinking summary, start time), the outcome, and
    any returned question. A tab opened or refreshed mid-answer therefore shows the answer
    in progress and the Stop button.
  - Change events are raised **outside** the lock. Each subscriber call is wrapped in
    catch-and-log, so a disposed or throwing component cannot fault the turn.
  - Components subscribe in `OnInitialized`, unsubscribe in `Dispose`, and re-render via
    `InvokeAsync(StateHasChanged)`, since events arrive on other threads.
- **A turn belongs to the conversation, not to the page.**
  - The store runs each turn with its own `CancellationTokenSource`, cancelled only by
    **Stop** or by `IHostApplicationLifetime.ApplicationStopping`.
  - The whole turn is wrapped in try/catch-and-log, so no task goes unobserved.
  - Refreshing or closing the tab mid-answer does not stop the answer or throw it away: it
    completes, commits, and appears when the page is reopened. Only Stop ends it early.
**Depends on:** Items 4 and 5.
**Risks:** Concurrency bugs between circuits and between Stop and completion. Mitigated by:
- the small store API and its immutable snapshots
- events raised outside the lock
- a Stop that never waits on the stream
- a commit that ignores cancellation
- item 11's tests for a disposed subscriber and for a Stop landing just before commit
**Open questions:** None.

### 7. UI shell and design-system port
**Why:** The design is the requested UI. Its components are React (`_ds_bundle.js`), which
Blazor cannot use, but its CSS is class-based (`.d1-btn`, `.d1-btn--primary`, and so on)
and can be used unchanged.
**What:**
- **Copy the design system's CSS unchanged** into `wwwroot/css/ds/`: `tokens/*.css`,
  `components/*/*.css` and `styles.css`. Pull them from the design project via DesignSync,
  and record the project id and pull date in a `README` beside them. Do not ship
  `_ds_bundle.js`.
  - The fonts load through a Google Fonts `@import`, which needs internet; the app already
    needs internet for the API.
  - Icons: copy the SVG paths of the glyphs used (`feather`, `book-open`, `settings`, and
    `x` for Stop) from the bundle's `ICON_PATHS` into an `Icon.razor`.
- **Razor primitives,** only the ones the three screens use: `Button`, `Badge`, `Card`,
  `TextInput`, **`Select`**, `Tag`, `Tooltip` and `Icon`. Each emits the design system's
  own class names, and each accepts a pass-through `class` parameter. `Select` wraps a
  native `<select>` with a visible label, styled by the design system's `forms.css`.
- **Layout.** A left nav rail (Conversation, Documents, Corpus settings, and app-lifetime
  total cost). The design's inline styles become scoped `.razor.css` using the design
  tokens. Scoped CSS does not reach markup rendered by child primitives, so styles aimed at
  them use `::deep` from a wrapper element or the primitive's `class` parameter.
- **Accessibility.** The design's gaps are fixed during the port:
  - Its clickable `<div>`s (session rows, citation rows, document rows) become `<button>`
    elements. A button may contain only phrasing content, so the rows use `<span>`s styled
    `display: block`.
  - Each citation marker `[n]` gets an `aria-label` naming its source.
  - The `Tooltip` appears on keyboard focus as well as hover, and dismisses with Escape
    (WCAG 1.4.13).
  - The streaming answer region sets `aria-busy` while streaming. Completion, and
    stopping, are announced through one always-present `aria-live="polite"` element whose
    text changes once per answer, not by making the streaming text itself live.
  - **Ask and Stop are one persistent `<button>`,** whose label and handler change: Ask,
    Stop, then Stopping….
    - In the Stopping state it sets `aria-disabled="true"` and ignores clicks, rather than
      becoming `disabled`. A focused button that becomes `disabled` drops keyboard focus to
      the page body, so this keeps focus in place (WCAG 2.4.3).
    - The same applies while Ask is unavailable.
  - The model and level selects point at the cost preview with `aria-describedby`, so a
    screen reader announces the new cost when a selection changes.
  - The design system's focus ring stays visible.
  - Status is always carried by text, never by color alone. The design already does this.
- **bUnit tests** for the primitives' rendered classes and states.
**Depends on:** Item 0 only. It can be built alongside items 1–6 against fake services.
**Risks:** Low. The CSS is self-contained.
**Open questions:** None.

### 8. Conversation screen
**Why:** The main screen.
**What:**
- **Layout, as in the design:**
  - a conversation list, with title, answer count and cost, now also showing each
    conversation's model and level
  - a "New conversation" button
  - a header with a model-and-level control (below) and a cache badge: NO QUESTIONS YET,
    COLD CACHE WRITE, CACHE READ or CACHE MISS
  - an empty state with the two suggestion cards
  - the question and answer history
  - the input and the Ask/Stop button
- **Model and level selector.** An addition to the design, which shows only a fixed
  `CLAUDE-OPUS-5` badge.
  - **While settings are editable** (item 6), the header shows two `Select`s:
    - **Model:** Claude Opus 5, Claude Sonnet 5.
    - **Level:** Low, Medium, High, Extra high, Max, defaulting to High.
    - Combinations on the exclusion list are shown disabled, with the reason.
  - **A cost preview line** sits under the two selects and is always visible. It comes from
    the price table, the output range and the warmth tracker, and shows the worst case too.
    For example:
    - *"Opus 5, High: cold, so $2.13–$2.16 for the first question (measured), then about
      $0.18–$0.30 each. Up to $2.80 if an answer runs to its output limit."*
    - *"Sonnet 5, Low: probably warm until 14:27, so about $0.05–$0.09 per question
      (estimated). Up to $1.12 if an answer runs to its output limit."*
    - Figures that are not measured carry "estimated" until item 12 measures them.
    - When a cold write is in progress elsewhere, the preview says so (item 6).
    - Levels below High show one short caution: *"Lower levels may miss related provisions.
      Quotes are still checked."*
  - **While settings are locked,** both selects become read-only badges (for example
    `OPUS 5 · HIGH`). A "New conversation with different settings" action sits beside
    them.
- **Input.** Enter sends and Shift+Enter inserts a newline.
  - This needs a small JS module on the textarea. Blazor's `@onkeydown:preventDefault` is
    fixed when the page renders and cannot be decided per key on the server, so without it
    Enter would both insert a newline and send.
  - The module prevents the default only for Enter without Shift, and not while an IME is
    composing. It then calls a .NET method with the current value.
  - bUnit cannot exercise this, so it is on the manual acceptance list (item 11).
  - The input shows the store's returned question when there is one (item 6).
  - Ask is unavailable while a question is in flight or the corpus is not `Ready`.
- **Stop button (required).** An addition to the design, and the web version of the
  prototype's Ctrl+C.
  - The one persistent Ask/Stop button (item 7) reads Stop whenever the active conversation
    has an answer in flight. That includes a tab opened or refreshed mid-answer, because
    the in-progress answer is part of the store's snapshot.
  - It is labeled "Stop", with the `x` icon, the design system's secondary style, and a
    visible focus ring.
  - Clicking it calls `ConversationStore.Stop`. The button reads "Stopping…" with
    `aria-disabled` until the store publishes the outcome. If the answer finished first,
    it resolves to the completed answer; otherwise to the stopped one.
  - After a Stop, the partial answer stays visible with the "Stopped" label and cost note
    from item 6, the question returns to the input, the button reads Ask again, and the
    aria-live region announces "Answer stopped".
  - **What Stop promises:** it ends the wait. Whether it also reduces the billed output is
    not verified (item 4), so the UI says nothing about savings. Item 11's manual session
    compares a stopped turn with the Claude Console's billed figure.
  - Stop is available on every model and level. It matters most at `xhigh` and `max`,
    where one answer can run for many minutes.
- **Streaming.**
  - "Assembling answer from documents" shows until the first text arrives.
  - The answer text then streams in, and each `[n]` marker is appended when its text block
    completes, as the prototype does.
  - Re-rendering is throttled to about every 100 ms, not once per text fragment, so a
    6,000-token answer does not trigger thousands of render batches over SignalR.
  - There is always one final render on `Completed` or Stop, so the throttle never drops
    the end of an answer.
- **Sources block, per the design, filled from the quote report:**
  - Each source shows its label, character range, excerpt and a status badge (success for
    matched; danger for a mismatch; warning for the other three), plus a detail line on
    failure.
  - A quality line: "n of m quotes matched", with the share of uncited text.
  - The list of thinly sourced blocks.
  - Warning badges: TRUNCATED, REFUSED, CACHE MISS, NOT CACHED, CONTEXT.
  - The disclaimer on the first answer of each conversation.
  - A meta line: model and level, cost, cache kind, elapsed time and token counts.
- **Errors and non-committed turns.** A failed or unreplayable turn shows its notice inline,
  and the question returns to the input.
- **Untrusted text is never rendered as `MarkupString`.** That covers model output, cited
  text and exception messages. Blazor's default encoding is the protection here, standing
  in for the prototype's terminal-escape filtering.
**Depends on:** Items 6 and 7.
**Risks:**
- Throttling must not drop the final render; the final render on `Completed` or Stop guards
  against that.
- **The cost preview is only as good as its estimates.** Every unmeasured figure is labeled
  "estimated", the worst case is always shown, and item 12 replaces the estimates with
  measurements.
**Open questions:**
- **Section labels.** The design shows a section per citation, such as "175.410(a)". The API
  returns no section, and the prototype found that spans start mid-sentence, including in
  the previous section's source note. So "nearest preceding `Section` heading" can
  mislabel a legal citation. Proposal: show the label and character range, as the
  prototype does, and leave the section out.
- **Thinking summary.** The design has no place for it. Proposal: stream it dimmed under
  the "Assembling" indicator, so a 60-second answer shows progress, then collapse it into
  a "Reasoning summary" disclosure once the answer completes. This matters more at
  `xhigh` and `max`, where the wait is longer.

**Resolved at item 8:** both proposals were accepted as written (user decision, 2026-09-14). See
the item 8 entry in the **Implementation Log**.

### 9. Documents screen
**Why:** A citation is only useful if it can be read in context. The design's click-through
from a citation to a highlighted span is that feature.
**What:**
- **Document list.** Label, character count, approximate token count, and a CITED badge for
  documents cited in the active conversation.
- **The full extracted text,** not the design's "excerpt", rendered as paragraph child
  components with `@key`.
- **Section navigation.** Tags built from paragraphs that start with `Section NNN.NNN`.
- **Paragraph ranges.** At corpus load (item 3), each paragraph's `[start, end)` range is
  recorded in UTF-16 coordinates, after the code-point map.
- **Highlighting.** A cited span is converted through the code-point map, then intersected
  with the paragraph ranges. Only the paragraphs it overlaps receive a highlight range and
  re-render; each paragraph's `ShouldRender` compares its own range.
  - A span may cross paragraph boundaries and absorb the blank separator, as the prototype
    observed with spans beginning in the previous section's source note. Each overlapped
    paragraph highlights its own portion, and the separator is not rendered.
- **Citation click-through.** Clicking a citation switches to the conversation's own corpus
  snapshot, applies the highlight, and scrolls the first highlighted paragraph into view
  through a small JS interop `scrollIntoView` call.
**Depends on:** Items 3, 5 and 7.
**Risks:** Rendering cost for Part 175. Paragraph components plus `ShouldRender` keep a
highlight change to a handful of re-renders. Do not use `Virtualize`: it would stop
scroll-to-highlight from reaching paragraphs that are not rendered. Measure before any
further optimization.
**Open questions:** None.

### 10. Corpus settings screen (read-only)
**Why:** It makes cost and corpus health visible. It is read-only by decision.
**What:** The design's four cards, with the inputs replaced by read-only values:
- **Corpus health.** The token gauge against the ceiling (per model), the fingerprint status
  with its change details, the time of the last check, and a Reload button.
- **Registry.** Labels, characters and approximate tokens, read-only.
- **Cache and pricing.**
  - TTLs and the ceiling.
  - **Both models' prices,** including the note that there is no long-context surcharge.
  - **The Cost Reference's cost-per-question and worst-case tables,** with each row's
    basis shown: measured, estimate or assumption.
  - **The warmth of each model and level combination:** probably warm until a given time,
    cold write in progress, or cold.
  - All of it is read-only. The design's editable TTL and ceiling inputs are replaced by
    values.
- **Session cost.** For the active conversation: its model and level, cost by bucket (input,
  1-hour write, 5-minute write, read, output) and a total. The total includes stopped and
  failed turns, with any unreported output and any turns of unknown cost stated.
**Depends on:** Items 3 and 7.
**Risks:** None.
**Open questions:** None.

### 11. Tests and live acceptance
**Why:** The prototype's item 8 used mutation testing and showed that 14 of 16 plausible bugs
survived the suite it had before item 8. Port the *intent* of the hardened suite, not its
line count.
**What:** Each test lands with its item, as in the prototype.
- **Core (xunit.v3), extraction and corpus:**
  - golden parity, and the golden bytes contain no CR and no BOM
  - the table fixture
  - CR and U+00A0 removal
  - the `_files` exclusion
  - `.html` files are not listed twice
  - the assembled-block golden (title, context, text hash)
  - the size guard at the ceiling and at ceiling+1, through the injected counter, including
    one model's count exceeding while the other's does not
  - fingerprint determinism, and that every block field changes it
  - a malformed state file, including a field of the wrong type, is reported, never thrown
  - the hashes of the cache-affecting constants are pinned, including the model and level
    sets and the exclusion list
- **Core (xunit.v3), configuration:**
  - the options validator fails startup on a missing price row, a missing or oversized
    `max_tokens`, a missing timeout, or an inverted output range
- **Core (xunit.v3), quotes:**
  - every outcome, including a span clipped at the end of a document
  - `end == length`, with and without a non-BMP character
  - a non-BMP offset shift
- **Core (xunit.v3), conversation state:**
  - the replay converter is lossless: re-serialized, the output equals the response minus
    its empty text blocks
  - a reply that can't be replayed is not committed, and the question is returned
  - after each of six failure kinds, and after a Stop, the roles still alternate and the
    next turn is still valid
- **Core (xunit.v3), model and level:**
  - `AnswerSettings` rejects a model or level outside the constant sets, and an excluded
    combination
  - each level maps to its `max_tokens` and its timeout view
  - all five effort wire strings serialize correctly, `xhigh` included
  - cost uses the selected model's price row: the same usage prices differently on Opus 5
    and Sonnet 5
  - the lock: settings are editable before the first question, locked while it is in
    flight and after a commit, and **editable again after a stopped, failed or
    unreplayable first turn**
  - "New conversation" copies the active conversation's settings
  - the preview's cold, warm and worst-case figures are **computed from the price table and
    output ranges**, and the test recomputes them from those inputs rather than copying
    numbers from the Cost Reference
- **Core (xunit.v3), cache warmth and baseline, with a fake `TimeProvider`:**
  - an entry is warm 54 minutes after its last activity and cold at 56 minutes
  - a read refreshes the TTL
  - the timestamp is taken at `UsageStarted`, and stopped or failed turns with cache
    activity update it
  - the "cold write in progress" state is set on send and cleared at `UsageStarted` or
    failure
  - entries are keyed by fingerprint, model and level: a warm Opus 5 `high` entry does not
    make Opus 5 `low`, or Sonnet 5 `high`, show as warm
  - a predicted cold write is reported as COLD CACHE WRITE, not CACHE MISS
  - a new conversation whose first turn is a pure read, followed by a turn that reads 0,
    reports CACHE MISS; a read exactly at the tolerance boundary does not
  - baselines are dropped when a reload changes the fingerprint
- **Core (xunit.v3), Stop:**
  - Stop mid-stream leaves `messages` unchanged and records the input-side cost
  - Stop before `UsageStarted` records the turn as "cost unknown", and the totals say it
    is excluded
  - **Stop landing between the final event and the commit:** the answer is committed and
    the outcome is Completed
  - a second Stop, or a Stop after completion, is a no-op and does not throw
  - Stop never waits on the stream: it returns while a fake stream is still blocked
  - Stop during a follow-up leaves the earlier turns intact and the next question valid
  - the returned question survives the sending subscriber being disposed
  - a disposed or throwing subscriber does not stop a turn from committing
- **Core (xunit.v3), cost and credentials:**
  - cost per bucket
  - key loading: BOM, CRLF, annotation lines, missing file, two keys, and no key text in
    any error
- **Offline SDK behavior (item 1, check 6):**
  - a stream that breaks after the response started is not retried
  - the `Timeout` fires while the body is being read
- **Web (bUnit):**
  - status badges and failure details
  - the disclaimer appears only once per conversation
  - Ask is unavailable while sending and while the corpus is not ready
  - **the Ask/Stop button is the same element before, during and after an answer,** with
    `aria-disabled` in the Stopping state, and the stopped label renders
  - the selects are editable or read-only following the lock rules; excluded combinations
    are disabled with a reason; the selects reference the preview through
    `aria-describedby`
  - the cost preview shows "estimated" for unmeasured combinations, and always shows the
    worst case
  - a freshly rendered component for a stopped turn shows the returned question in the
    input
  - errors return the question
  - no `MarkupString` for model or document text, checked by rendering a `<script>`
    payload and asserting it appears encoded
  - a highlight crossing paragraph boundaries re-renders only the overlapped paragraphs
- **Live, gated on `DOCQA_LIVE=1`,** using xunit.v3's dynamic `Assert.Skip`:
  - the item 1 spike checks, about $1. These include exactly one HTTP request, the
    credential and host assertions, all ten combinations, A → B → A coexistence, and a
    cancelled stream.
  - `count_tokens` parity with the prototype, free
  - the sump question on Opus 5 at `high`, asserting that Section 175.410 appears and every
    quote matched, about $2
- **Manual acceptance session,** mirroring the prototype's item 7 live run, **about $4**:
  - a cold question on Opus 5 at `high`
  - a follow-up in the same conversation that reads about 199,745 tokens
  - a new conversation at the same settings that reads the cache again
  - a new conversation on Sonnet 5 at `high`, where the preview says cold and the cost is
    about $0.85
  - Enter and Shift+Enter behavior, including with an IME if one is available
  - keyboard focus stays on the Ask/Stop button through Ask, Stop, Stopping… and Ask again
    (bUnit sees component instances, not the browser's focus; added at item 8)
  - refreshing the tab mid-answer shows the answer in progress and Stop, and the answer
    then completes
  - Stop mid-answer: the partial answer is kept and labeled, the input-side spend is
    recorded, the question returns, and the next question works
  - compare the follow-up's and the stopped turn's costs with the Claude Console's billed
    usage

  Record the costs in this plan next to the prototype's $2.16, $0.23 and $0.18.
**Depends on:** Items 1–10.
**Risks:** None.
**Open questions:** None.

### 12. Cost and quality measurement across models and levels *(nice-to-have — NOT RUN)*

> **Decision, 2026-09-15: nice-to-have, and not to be run.** (User decision.) The $16–$18.50
> is not worth what it buys: the preview's estimates stand, and every row on the Corpus
> settings screen already shows its own basis — measured, estimate or assumption — so a user
> is never shown an estimate dressed up as a measurement. Do not run this item, or any part
> of it, without the user asking for it first.
>
> **What stays unmeasured as a result:** every combination but Opus 5 at `high`. The
> `EstimatedOutputTokens` ranges in `appsettings.json` remain estimates and assumptions, and
> the **Cost Reference**'s bases stay as written. Nothing else in the app depends on them:
> they feed the cost preview and nothing more.
>
> **If it is ever run,** the method below is unchanged, and the plan's own advice holds — a
> single run per combination is calibration, not a benchmark.

**Why:** Every **Cost Reference** figure except Opus 5 at `high` is an estimate or an
assumption. The selector's preview is only honest once they are measured. Measuring also
shows what the cheaper settings give up on this corpus, which no published curve can.
**What:**
- **Run each of the 10 combinations** in a fresh conversation: the sump question cold, then
  one fixed follow-up immediately after. Record for each:
  - output tokens and total cost, for both turns
  - elapsed time
  - quotes matched
  - stop reason
  - which of the reference answer's provisions it names, against the prototype's Opus 5
    `high` answer: §175.410(a)–(n), 175.300(f), 175.420(b), 175.425(h), 175.450(e)(15),
    175.630(f), 175.700(f), 176.430(b)(6), 176.655(b)–(c) and 172.110(a)(2)(H)
- **Replace the estimates** in the **Cost Reference**, and in the options' output-token
  ranges, with the measured figures. Keep the ranges honest: two questions are a small
  sample.
- **Cost, from the Cost Reference's own ranges:**
  - **Expected: $15.83–$18.47.** That is each combination's cold question plus a warm
    follow-up with its tail write: $11.31–$13.19 on Opus 5 and $4.52–$5.28 on Sonnet 5.
  - **Worst case: about $31,** if every `xhigh` and `max` answer runs to its output limit.
  - The combinations never share a cache entry, so the order in which they run does not
    matter. What matters is that each combination's follow-up comes within the hour after
    its cold question, which asking immediately guarantees.
  - Running item 12 within an hour of item 11 makes Opus 5 `high` and Sonnet 5 `high`
    warm, which lowers the total.
**Depends on:** Items 8 and 11. Run it in the finished app, so the same code produces the
numbers the preview shows.
**Risks:** A single run per combination is noisy, especially at `max`, which the SDK guidance
calls prone to overthinking. Treat the results as calibration, not a benchmark.
**Open questions:** None. "Whether to run it at all, given the cost" was the open question,
and it is answered above: no.

## Sequencing

1. **Item 0.** Everything depends on it.
2. **Item 1 (the spike)** and **item 2 (extraction parity)**, in parallel. Item 1 is the
   gate: if the SDK cannot stream once, forward events live, and carry citations and
   signatures through to the final message and back, stop and re-plan (raw HTTP) before
   building items 4 and 6 on it. Item 1 also delivers the code-point index type that item 5
   reuses, and it decides which model and level combinations are selectable.
3. **Item 3,** which needs item 2.
4. **Item 4, then 5, then 6:** answer, then quotes, then conversation. This is the
   prototype's proven order. Model and level, Stop and the warmth tracker are built in
   these items, not bolted on afterward.
5. **Item 7** can start any time after item 0, against fake services; it is the only item
   that does not need the spike's result.
6. **Items 8, 9 and 10,** which need items 6 and 7.
7. **Item 11** runs throughout. The manual acceptance session is last among the required
   items.
8. ~~**Item 12,** only with your go-ahead, after item 11.~~ **Decided against, 2026-09-15.**
   Item 11 is therefore the last item, and the plan is complete at it.

## What We're NOT Doing (and Why)

- **Switching model or level once a conversation has a committed turn.**
  - A model switch misses the model-scoped corpus cache and cannot replay the
    conversation's thinking blocks.
  - A level switch invalidates the messages cache, which holds the corpus.
  - Either one costs a cold write the user did not see coming: $2.00 on Opus 5, $0.80 on
    Sonnet 5.

  Settings lock per conversation, and "New conversation with different settings" is one
  click.
- **The per-message effort beta** (`mid-conversation-output-config-2026-07-01`). It changes
  effort without invalidating the cache, but only on Opus 5, not Sonnet 5. It also moves
  every type into the beta namespace. Locking settings per conversation gets the same
  cost safety with no beta.
- **A thinking-off option.** Opus 5 rejects disabled thinking at `xhigh` and `max`. The SDK
  guidance also recommends low effort over disabling thinking, because of known failure
  modes with thinking off. Thinking stays adaptive at every level, and `low` is the cheap
  setting.
- **Models beyond Opus 5 and Sonnet 5.** Haiku 4.5's 200K context window cannot hold the
  199,749-token corpus plus a question. Fable 5.1 was not requested.
- **Calling the Python code from C#,** whether through Python.NET, a subprocess, or a
  FastAPI backend behind Blazor. That would mean two runtimes and two deployments, and the
  prototype is frozen by decision. A C# port of about 1,500 lines of Python is smaller than
  the integration. The one exception is the one-off `tools/` script in item 2, which
  generates the golden files and is not part of the app.
- **Blazor WebAssembly or the Auto render mode.** The key must stay on the server, so WASM
  would need a server API anyway. Interactive Server keeps everything in one process.
- **`Microsoft.Extensions.AI` `IChatClient`.** It abstracts away exactly what this app is
  built on: document blocks, citations with character offsets, cache breakpoints with
  TTLs, and thinking signatures.
- **Server-side refusal fallbacks** (`fallbacks: "default"`, beta
  `server-side-fallback-2026-07-01`). This is the SDK guidance's default for
  `claude-opus-5`, and it is rejected here for three reasons:
  - It moves every request and response type into the `Anthropic.Models.Beta.Messages`
    namespace.
  - A fallback answer comes from a different model, which misses the model-scoped corpus
    cache and cannot replay the conversation's thinking blocks. That is the same reason
    model switching is per conversation.
  - A refusal on UST regulations is improbable; the prototype saw none.

  Refusals are shown as REFUSED instead, and a refusal that can't be replayed is not
  committed (item 6). Revisit this if one actually happens.
- **Estimating the output tokens of a stopped answer.** Output usage arrives only at the end
  of a stream, and thinking is summarized, so any client-side count would be a guess. The
  UI says "not reported" instead.
- **Claiming that Stop saves money.** It is unverified what is billed after a disconnect.
  Stop ends the wait, and the manual session checks the Console.
- **Rendering answers as Markdown.** Model output can echo text from the documents, and the
  documents are data, not instructions. Markdown-to-HTML needs a sanitizer to be safe.
  Plain text with `white-space: pre-wrap` matches both the design and the prototype.
- **Deriving section numbers for citations.** It risks mislabeling a legal citation; see
  item 8.
- **Editable settings, persistence, authentication.** User decisions, 2026-09-13. They are
  cheap to add later because the store and options are behind interfaces.
- **An on-disk extraction cache.** Rejected in the prototype for the same reason: extraction
  takes a second or two and is deterministic, and the fingerprint already detects change.
- **Porting `fit_check.py` and `spike_request_shape.py` as scripts.** Their questions are
  answered. Item 1 replaces the spike with a repeatable live-gated test.
- **`Virtualize` for the document viewer.** It breaks scroll-to-highlight; see item 9.
- **A custom `HttpClient` for the SDK.** Its built-in 100-second timeout conflicts with the
  SDK's own timeout. Request capture and retry logging go through `ClientOptions.Handlers`,
  and per-level timeouts through `WithOptions`.

## Implementation Log

**2026-09-13, item 0.** Done on branch `item-0-scaffold`: 97 offline tests pass, and the
build has no warnings under `TreatWarningsAsErrors`. Before the commit, a second model ran
an adversarial review (Copilot CLI, `gpt-5.6-luna`, read-only). Its findings and their
outcomes are below.

- **Findings that affect later items:**
  - **The cache-affecting constants are verbatim.** The C# system prompt, labels and context
    template hash to the prototype's values, including its pinned `SYSTEM_PROMPT_SHA256`.
  - **The SDK sends an ambient `ANTHROPIC_AUTH_TOKEN` as a bearer token, even with an
    explicit API key.**
    - **How it was found:** an offline test that sets every `ANTHROPIC_*` variable the
      assembly names to a junk value.
    - **Why not `AuthToken = ""`:** setting it sends an empty bearer token instead.
    - **The fix:** `AnthropicClientProvider` installs an outermost `ApiKeyOnlyHandler` that
      removes the `Authorization` header. With it, requests go to `api.anthropic.com`,
      carry only the file's key, and carry no ambient value in any header.
    - **Still to do:** item 1's live check 1 asserts the same against the real API.
  - **`ClientOptions` is a sealed class, not a record.** An offline test confirms that the
    per-level `WithOptions` views leave the root client's timeout unchanged. It also confirms
    that all views share one handler chain without an "inner handler already set" conflict.
  - **The SDK sends no retry-count header.** The retry-logging handler therefore logs the
    retryable failure that precedes each retry (408, 409, 429, 5xx or a connection error,
    per the SDK's `MaxRetries` docs). An offline test sees one 503, one retry and one log
    line, with no key text in it.
  - **Other `ANTHROPIC_*` variables** the SDK reads are added to item 1, check 1.
- **Additions beyond the plan's text:**
  - **`LoopbackGuard`, in two layers.** Either layer stops the app with exit code 1. This
    makes the loopback rule hold against an `ASPNETCORE_URLS` or `--urls` override, not
    only in `launchSettings.json`.
    - **Before the server opens a socket,** it refuses any non-loopback address in
      configuration:
      - `urls`, which covers `ASPNETCORE_URLS` and `--urls`
      - `http_ports` and `https_ports`, which bind every interface
      - every `Kestrel:Endpoints:*:Url`
    - **After start-up,** it checks the addresses Kestrel actually bound, as a backstop for
      endpoints configured in code.
  - **The key must be printable ASCII with no spaces.** This is stricter than the
    prototype, which refused only whitespace. A control or non-ASCII character is invalid in
    an HTTP header, and would otherwise fail far from the key file.
  - **`IAnthropicClientProvider` is built on first use.** A missing or malformed key file
    therefore surfaces where the client is first needed (item 3's corpus load), which the
    UI can show, instead of stopping the app from starting.
  - **`.gitattributes` also sets `* text=auto`,** and exempts every `*.htm` and `*.html`
    rather than only `admincode/*.htm`. That covers item 2's `tables_175.html` fixture.
  - **`.gitignore` also covers `TestResults/`, `.claude/settings.local.json` and editor
    folders.**
- **Adversarial review: four findings, all confirmed and fixed.**
  1. **High:** the loopback check ran only after `StartAsync`, so a non-loopback bind could
     accept connections before the app stopped. Fixed by the pre-start configuration check
     above. A live run with `ASPNETCORE_URLS=http://0.0.0.0:5299` now exits with code 1
     before binding.
  2. **Medium:** a JSON `null` price row or output range made the options validator throw
     instead of failing validation. Fixed: null rows, ranges and dictionaries are reported
     as missing.
  3. **Low:** line splitting in the key loader did not match Python's `splitlines()`.
     Fixed to use the same set of line boundaries.
  4. **Low:** the environment test set no `ANTHROPIC_*` variable, so it proved nothing
     about overrides. It was replaced by the offline test that found the bearer-token leak
     above.

**2026-09-13, item 1.** Done on branch `item-1-request-shape-spike`. **The gate passes:**
the SDK serializes the request, streams it in one pass, and carries citations and signatures
through to the final message and back. No fallback is needed: neither the custom
accumulator nor raw HTTP.
- **Tests:** 132 offline tests pass. The 4 live tests run only with `DOCQA_LIVE=1`:
  - The credentials, round-trip and Stop tests pass.
  - The combination sweep's one failure was a test artifact. It was fixed but not re-run;
    see the test-only note.
- **Cost:** about $0.55 of billed calls in all, against the $1 estimate.
- **Delivered for later items:**
  - `CodePointIndex`, for item 5
  - `ReplayConverter`, for item 6
  - `MessageRequestBuilder` and `EffortLevel.ToSdkEffort`, for item 4

**Results by check.**
1. **Serialization and credentials.**
   - **Offline:** a test pins every field of the prototype's shape, and all five effort
     strings, against the captured request body.
   - **Live:** `count_tokens` succeeded with every `ANTHROPIC_*` variable set to junk. The
     request went to `api.anthropic.com` with the key file's key, and with no
     `Authorization`, workspace or organization header.
2. **One pass.** `MessageContentAggregator.CollectAsync` forwards every event except `ping`
   as it arrives. `Message()` then returns the final message, with citations and signatures
   intact. There was exactly one HTTP request per answer, offline and live.
3. **Offsets.** Live, every citation sliced exactly as code points: 13 in one run and 12 in
   the other. Every one of them started after the fixture's non-BMP character, so UTF-16
   offsets are ruled out.
4. **Round trip.** The converted reply is accepted as turn 2. The tail finding below led to
   an addition to the request shape.
5. **Caching.** A different first question read the full 1-hour prefix (3,214–3,218 tokens;
   the count differs by run id).
6. **Usage, stop details, Stop and retries.**
   - The 1-hour write is readable at `message_start`, and `StopReason` and `StopDetails` at
     the end.
   - Cancelling ended the live stream 2 ms after Stop, with a `TaskCanceledException`.
   - Offline, a body that breaks is not retried.
7. **Every combination.**
   - All ten were accepted. Each wrote its own 1-hour entry and read it back exactly.
   - A level change (Opus 5 `low`) and a model change (Sonnet 5 `high`) each wrote a new
     entry, and Opus 5 `high` still read its own after each.
   - No combination is excluded, and `ExcludedCombinations` stays empty.

**Findings that affect later items.**
- **Follow-ups need a breakpoint on the previous question.**
  - **The cause:** the reply in the live check had 26 content blocks, more than the 20
    positions a breakpoint looks back for an earlier entry. With the plan's original shape,
    turn 2 read only the corpus (3,218 tokens), and the whole history would have been
    rewritten on every follow-up.
  - **The fix:** an explicit 5-minute marker on the previous question. With it, turn 2 read
    3,250 tokens and turn 3 read 5,169, both exactly as expected (item 4).
  - **Why it matters:** without the fix, each follow-up would cost about
    $0.035 × (turn − 2) more on Opus 5 than the Cost Reference assumes. The Cost
    Reference's follow-up figures assume the fixed behavior.
  - User decision, 2026-09-13.
- **The SDK's `Timeout` does not cover a streamed body.**
  - With a 500 ms timeout, a body that stalled after the headers ran for the full
    3 seconds, then ended with an `AnthropicInvalidDataException`.
  - The timeout does bound the wait for headers: it gives up after one attempt, with a
    `TaskCanceledException`. The SDK's docs say timeouts are retried.
  - Item 4 enforces each level's timeout with its own token. Item 0's per-level views
    remain, for the header wait.
- **Some failures surface outside `Anthropic.Exceptions`:** a raw `IOException`, an
  `AnthropicInvalidDataException`, and a `TaskCanceledException` for a timeout. Tests pin
  all three (item 4, Errors).
- **Adaptive thinking often produces no thinking block** for a short question. The replay
  converter handles a reply without one.
- **SDK surface details for items 4–6:**
  - The parameter types are records with `init`-only and `required` members, so a changed
    block is made with `with`.
  - `Message.StopReason`, `Usage.CacheCreation` and `TextBlock.Citations` are nullable.
  - The SDK does not recognize a `char_location` citation whose `document_title` is null,
    which the API allows. `ReplayConverter` rebuilds such a citation from its JSON instead
    of refusing the reply.

**Test-only note.** The combination sweep's second coexistence re-read reused the first
re-read's question, so it also read that request's tail: 3,239 tokens, with nothing written.
It now uses a distinct question. It was not re-run, since the result already showed the full
prefix read.

**Adversarial review.** Two read-only reviews by a second model (Copilot CLI,
`gpt-5.6-luna`). Neither found a Critical or High issue.
- **Review 1, of the whole item.** It ran before the tail breakpoint was added.
  1. **Medium, fixed:** live spend had no upper bound, because `xhigh` and `max` send
     `max_tokens` of 64,000 and 128,000. Those values stay, since check 7 requires them.
     Instead, a live run now refuses further billed calls after $3.00, and stops any one
     request after 3 minutes, which bounds a runaway answer at about $0.45 on Opus 5. These
     guards have not been exercised live.
  2. **Low, fixed:** a citation with a null `document_title` was never tested. The new test
     exposed a real defect: the SDK does not recognize such a citation, so the converter
     refused a replayable reply. It now rebuilds the citation from its JSON, and refuses
     only one missing its text or offsets.
- **Review 2, of the tail breakpoint.**
  1. **Medium, fixed:** a previous question sent as a bare string was silently converted to
     a block list, which could change the bytes of a prefix already cached. It is now
     refused instead. The app's own builders always produce block lists.
- **Rejected:** none.

**2026-09-14, item 2.** Done on branch `item-2-extraction`. **Golden parity holds:** the C#
extraction of all six pages is byte-identical to the prototype's, on the first run. No
difference needed fixing or accepting for the real corpus.
- **Tests:** 187 offline Core tests (74 of them new) and 19 Web tests pass. Changing one byte of a
  golden file made the parity check fail, naming the first differing character in context.
- **How the goldens were made:** `tools/generate_golden.py` calls the prototype's
  `extract_corpus()` in the prototype's own `.venv` (lxml 6.1.3, libxml2 2.11.9). The global Python
  has lxml 6.1.1, which the prototype does not pin. The six pages are byte-identical in both
  repositories.
- **Delivered for later items:**
  - `CorpusExtractor` and `ExtractedDocument`, for item 3
  - `tools/generate_golden.py`, which item 3 extends for the assembled-block golden

**Findings.**
- **The parsers diverge on malformed markup that the six pages do not contain.** The adversarial
  review found two cases, and a differential probe through both extractors found six more. This
  port follows HTML5, which is what a browser shows a reader of the page. `ParserDivergenceTests`
  pins each case, with the prototype's measured output. (User decision, 2026-09-14.)

  | Input | Prototype (lxml) | This port (AngleSharp) |
  |---|---|---|
  | Text directly inside a `<table>`, or between rows | dropped | kept, as a paragraph before the table |
  | A stray `</br>` | ignored | a line break |
  | A stray `</p>` | ignored | a paragraph break |
  | `&amp`, `&copy` and other legacy entities with no semicolon | left as written | decoded |
  | `&#128;` to `&#159;` | C1 control characters | the windows-1252 characters, such as `€` |
  | `&#0;` | dropped | U+FFFD, which is refused (below) |
  | Raw C0 control characters other than tab, LF and CR | removed, as illegal in XML | form feed and U+001C–U+001F become spaces; the rest are kept |

  These match in both parsers, with and without a doctype:
  - an open `<p>` before a `<table>`
  - `<td>` and `<tr>` outside a table
  - nested tables and captions
  - unclosed `<p>` and `<li>`, and a `<div>` inside a `<p>`
  - `<noscript>`, CDATA, processing instructions and Word's `<o:p>`
  - `<pre>` and `&#13;`
- **Addition beyond the plan's text: U+FFFD is refused.**
  - **Why:** a wrong or missing charset, or invalid bytes, decode silently to U+FFFD. That text
    would be sent, cited and quote-checked with characters missing.
  - **What:** extraction now fails and names the file, as it already does for CR and U+00A0.
  - **Scope:** this is stricter than the prototype, which has no such check. None of the six
    pages contains U+FFFD. (User decision, 2026-09-14.)
- **Python's whitespace is not .NET's.** Python's `\s`, `split()` and `strip()` include
  U+001C–U+001F; `char.IsWhiteSpace` and .NET's regex `\s` do not. `PythonWhitespace` copies
  Python's set, and a test checks it against Python's for every BMP character. In practice libxml2
  removes those four characters before the prototype's pattern sees them (see the table).
- **The corpus pages have a doctype** (`<!DOCTYPE html>`), so they parse in no-quirks mode. The
  container is found by exact class token, not by a CSS selector. The match is therefore
  case-sensitive in either mode, as lxml's `find_class` is.
- **Every page has a second `billtext-scale`,** inside a script. Script text is not markup, so it
  never counts as a second container.

**Adversarial review.** One read-only review by a second model (Copilot CLI, `gpt-5.6-luna`).
Each finding was checked by running both extractors on the case it describes.
1. **High, confirmed, accepted as a divergence:** foster-parented table text (see the table).
2. **Medium, confirmed, accepted as a divergence:** a stray `</br>`.
3. **Medium, rejected:** "a parser or encoding exception can escape unwrapped."
   - **Why rejected:** nothing but `CorpusExtractionException` escaped on any probe: empty input,
     an unknown charset, invalid UTF-8, odd-length UTF-16, random binary, or NUL bytes.
   - **What it led to:** the probe found the silent U+FFFD, which is now refused (above).
4. **Low, rejected:** "the golden script leaves stale files, and its file-name pattern is
   case-sensitive."
   - A stale or missing golden file already fails `GoldenFiles_CoverExactlyTheLabeledParts`.
   - The script can only extract the prototype's six labeled files, since the prototype's
     `extract_corpus()` refuses any other. The pattern therefore never sees another name.

**2026-09-14, item 3.** Done on branch `item-3-corpus-assembly`. **Parity with the prototype's
request holds:** the assembled blocks match the prototype's field for field, and the C# request
counts exactly the prototype's 199,749 tokens, on both Opus 5 and Sonnet 5.
- **Tests:** 266 offline Core tests (79 of them new) and 19 Web tests pass. The live token-count
  test passes with `DOCQA_LIVE=1`; the other 4 live tests were not re-run.
- **Live, and free:** `count_tokens` calls only. Counted on its own, Part 175 is about 102,061
  tokens and Part 177 about 4,538. The six counts sum to 204,229, above the corpus total, because
  each one carries the system prompt.
- **Delivered for later items:**
  - `Corpus` and `CorpusDocument`, the immutable snapshot, with each document's `CodePointIndex`
    and paragraph ranges, for items 5, 6 and 9
  - `CorpusService`, with its `Loading`, `Ready` and `Failed` states and a `StateChanged` event,
    for items 8 and 10
  - `FingerprintCheck.Describe`, the change report with the write cost on each model, for item 10
- **Decisions (user, 2026-09-14):**
  - Per-document token counts are kept, but a failure there does not stop the load. The first
    failure ends the counting, and the documents from there on show no count.
  - Item 3 adds no UI. The screens render the corpus state in items 8 and 10.
- **Additions beyond the plan's text:**
  - **The fingerprint is checked against what the SDK sends.** It is written field by field, as
    planned, and a test compares that serialization with the blocks in a captured `count_tokens`
    request. A field added to `MessageRequestBuilder` without being fingerprinted fails the test.
  - **The golden token count is tied to its corpus.** `tools/generate_golden.py` copies the
    prototype's 199,749 into `tests/golden/blocks.json` only if the prototype's state file
    fingerprints the same blocks.
  - **A failed reload is reported without losing the corpus.** The state stays `Ready`, with the
    reason, and the current corpus stays in use.
  - **The state file counts characters in code points,** as the prototype does, and treats a file
    that names a document twice as malformed.
- **Tooling note:** do not pass `-nologo` to `dotnet test`. Microsoft Testing Platform forwards it
  to the test app, which then reports "Zero tests ran".

**Adversarial review.** One read-only review by a second model (Copilot CLI, `gpt-5.6-luna`). It
found no Critical or High issue, and both Medium findings were rejected after checking them against
the code.
1. **Medium, rejected:** "the state file accepts well-typed but invalid values, such as empty token
   counts or a negative character count, so a file with the right fingerprint compares Unchanged."
   - **Why rejected:** the comparison decides on the fingerprint alone, as the prototype's does. When
     the recorded fingerprint equals the current one, the corpus did not change, whatever the other
     fields say. They only feed the description of a change, where an odd value makes an odd message
     but cannot hide the change. Writes are atomic, so a partial write cannot produce such a file.
2. **Medium, rejected:** "a `StateChanged` handler can read a newer state than the one that raised
   it."
   - **Why rejected:** that is the contract. Every change raises a notification after it is made, and
     handlers read the current state, so the last notification always reads the final state.
     Delivering each state instead, as suggested, could hand a stale `Ready` to a handler after a newer
     `Loading`.
   - **What it led to:** the event's doc comment now states the contract.

**2026-09-14, item 4.** Done on branch `item-4-answer-service`. `AnswerService` streams one turn in a
single pass. It sends `UsageStarted` as soon as `message_start` arrives and `Completed` with the
final message, sources, usage, cost, elapsed time and stop warning. Stop and each level's timeout
are told apart by which token was cancelled.
- **Tests:** 333 offline Core tests (67 of them new) and 19 Web tests pass.
  - No live test for this item (user decision, 2026-09-14).
  - The $2 sump question stays in item 11, because it also needs item 5's quote checker.
- **Cost:** none. Nothing billed was called.
- **Delivered for later items:**
  - `IAnswerService` and `AnswerService`, registered in DI, for item 6
  - `AnswerSettings`, for items 6 and 8
  - `TokenUsage` and `TurnCost` (per bucket, with `Add` for totals), for items 6 and 10
  - `AnswerCitation`, the numbered `Sources`, and `TextBlockCompleted` with its `[n]` numbers, for
    items 5, 8 and 9
  - `StopWarning`, and `AnswerFailedException` with `AnswerFailureKind`, for items 6 and 8
  - `EffortLevel.ToDisplayName`, for item 8
- **Additions beyond the plan's text:**
  - **A sixth event, `ThinkingRedacted`,** so a redacted thinking block can be shown as one, as the
    prototype showed "(redacted)".
  - **The client is resolved when an answer is requested,** as the token counter resolves it. A
    missing or malformed key file becomes a `Credentials` failure the UI can show; it does not break
    startup.
  - **Citations are read into the app's own `AnswerCitation` record, from their JSON.** The SDK cannot
    read a citation with a null title (item 1). `ReplayConverter` now shares the same parser.
  - **Failures the plan does not list** are `Unexpected`. They include a cancellation that neither
    Stop nor a timeout asked for.
  - **User-facing failure messages are fixed short strings.** The full exception goes to the log.
- **Findings:**
  - **The SDK closes the HTTP response inside its final `MoveNextAsync`, after `message_stop`.**
    - **The risk:** a failure there would have discarded a complete, paid answer as a network error.
      A guard around the enumerator's disposal cannot catch it.
    - **The fix:** once `message_stop` has arrived, a failure is logged as a warning, and the answer
      completes from the aggregator, which already holds the whole message.
  - **The SDK's timeout on the response headers throws a bare `TaskCanceledException`,** with no inner
    exception or other marker. It is recognized by how long the request ran: at least 95% of the
    view's timeout.
  - **Abandoning the enumeration disposes the HTTP body.** A test pins this.
  - **A Stop that lands after the response finished still yields `Completed`.** This is deliberate,
    per item 6: a finished answer has been paid for, so it is kept. `IAnswerService` documents it.

**Adversarial review.** One read-only review by a second model (Copilot CLI, `gpt-5.6-luna`). It found
no Critical or High issue. Three findings were fixed and two rejected, and the test written for one
of them exposed the defect above.
1. **Medium, fixed:** a TTL breakdown missing one of its fields read that field as zero, which
   under-priced the write.
   - The breakdown is now read from its JSON.
   - Any write it does not account for is priced at the 1-hour rate, so the cost errs high.
2. **Medium, fixed:** an exception while disposing the stream could replace the answer's outcome.
   - Disposal failures are now logged as a warning.
   - Its test found that the SDK disposes the response earlier, inside the final read (see
     Findings), so that path is handled too.
3. **Medium, fixed:** every cancellation that neither Stop nor the service's timer caused was reported
   as a timeout.
   - Only one that lasted as long as the SDK's header timeout still counts as a timeout.
   - Any other is `Unexpected`.
4. **Medium, rejected:** "Stop or the timeout landing after the final event should prevent
   `Completed`."
   - **Why rejected:** item 6 decides the opposite. Once the response has finished, the answer is paid
     for, so it is kept.
5. **Low, rejected:** "stream creation sits outside the mapped `try`, so a setup failure escapes
   unmapped."
   - **Why rejected:** `MessageService.CreateStreaming` and `SseAggregator.CollectAsync` are both
     async iterators (checked in the 12.47.0 assembly). They do nothing until the first
     `MoveNextAsync`, which is inside the mapped `try`.

**2026-09-14, item 5.** Done on branch `item-5-quote-checker`. `QuoteChecker` checks each distinct cited span
against the conversation's corpus snapshot, character for character, and builds a `QuoteReport`: one outcome per
source, the coverage of each cited block, and the share of the answer that cites nothing.
- **Tests:** 401 offline Core tests (68 of them new) and 19 Web tests pass. Every case in the prototype's
  `test_quotes.py` is ported. The one exception is its check that the module never uses `assert`, which has no C#
  equivalent; a test that hostile offsets never throw takes its place.
- **Cost:** none. Nothing billed was called.
- **Delivered for later items:**
  - `QuoteChecker`, registered in DI, with `Check` and `BuildReport`, for item 6
  - `QuoteReport`, `QuoteCheck`, `BlockCoverage` and `MatchStatus.ToDisplayName`, for items 8 and 9
  - `QuoteChecker.MatchDisclaimer`, copied verbatim from the prototype, for item 8
  - `CodePointIndex.ToCodePoint`, the reverse of `ToUtf16`, so a quote found by `IndexOf` is reported in code points
- **Decision (user, 2026-09-14): `NotCheckable` is kept.** The checker reads citations from the message without
  throwing, and reports one it cannot read as a `char_location` citation as `NotCheckable`, keyed on its type and
  quote. It cannot happen through `AnswerService` today, which already fails such a turn as `Unexpected`.
- **Additions beyond the plan's text:**
  - **Offsets are validated as `long`, before any conversion.** An offset beyond the range of an `int` is
    `OutOfRange` and never overflows.
  - **A `char_location` citation missing its text or an offset is `NotCheckable`,** as one of another kind is.
  - **The degenerate detail names the rule, not a constant.** The prototype's names `config.MIN_CITATION_LENGTH`;
    this one says "under the minimum citation length (8)".
- **Finding: the canned answer's offsets do not point at its quotes.**
  - **What:** item 4's `CannedAnswer` cites 851–880 and 950–970 in the spike fixture's document A. Its two quotes
    are at code points 883 and 998.
  - **What the checker does:** it reports both as mismatches, and names each quote's real position, +32 and +48
    from the cited start. That is the offset error the mismatch detail exists to tell apart from a quote that is
    not in the document at all.
  - **What changed:** nothing in the canned answer, since `ReplayConverterTests` pins its JSON. A test now asserts
    the two mismatches, with the real positions counted independently of the checker.

**Adversarial review.** One read-only review by a second model (Copilot CLI, `gpt-5.6-luna`). It found one issue,
with no Critical or High finding, and it was fixed.
1. **Medium, fixed:** the mismatch detail could name a position where the quote does not occur.
   - **The cause:** the search for the quote elsewhere in the document was a UTF-16 `IndexOf`. A quote that starts
     with a lone low surrogate, or ends with a lone high one, could match half of a surrogate pair. `ToCodePoint`
     then rounded that position to the pair's start. Python's `str.find` works on code points and cannot match
     inside a character, so the prototype reports such a quote as appearing nowhere.
   - **The fix:** an occurrence counts only if it starts and ends on a code-point boundary; otherwise the search
     continues. Three tests cover a quote starting inside a pair, one ending inside a pair, and a later occurrence
     on a boundary. Traced by hand, not run: the unfixed code fails each of them, reporting a position inside a
     pair where the fixed code reports none, or character 1 instead of 13.
   - **Why it matters little in practice:** it needs a lone surrogate in `cited_text`, and it affects only the
     wording of a mismatch, never a status.

**2026-09-14, item 6.** Done on branch `item-6-conversation-state`. `ConversationStore` holds every conversation in memory
for the app's lifetime, runs each turn on its own task, and publishes immutable snapshots, including the answer in progress.
It commits a turn only after `Completed`, and only if the reply can be replayed.
- **Tests:** 486 offline Core tests (85 of them new) and 19 Web tests pass. The 84 conversation tests also passed ten
  repeated runs, since they exercise concurrency.
  - No live test, as for items 4 and 5. The $2 sump question stays in item 11.
- **Cost:** none. Nothing billed was called.
- **Delivered for later items:**
  - `IConversationStore` and `ConversationStore`, with `ConversationSnapshot`, `AnswerRecord`, `InProgressAnswer`,
    `ConversationCost` and `AskResult`, for items 8 and 10
  - `CacheWarmthTracker` and `CacheBaselines`, with `CacheReport`, for items 8 and 10
  - `CostEstimator` and `CostEstimate`: the preview's cold, warm and worst-case figures and their basis, for item 8
  - `ICorpusSource`, implemented by `CorpusService`, so the UI's tests can substitute a corpus
- **Decisions (user, 2026-09-14):**
  - `CostEstimator` is built here rather than in item 8. The label of a turn stopped before its usage arrived ("up to
    about $X") needs its figures, and item 11 lists its tests under Core. Item 8 only words the preview.
  - `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 is added to the Core tests, for `FakeTimeProvider`.
- **Additions beyond the plan's text:**
  - **An empty conversation has no corpus snapshot until a turn is committed.** Until then each question uses the corpus
    current when it is asked, so a reload between a failed first turn and its retry is not lost.
  - **A missing key file costs nothing.** No request was sent, so the turn records $0 rather than "cost unknown".
  - **Warnings are typed** (`TurnWarning` and its kind), so item 8 chooses a badge without parsing text.
  - **A snapshot is built once per change, not once per read.** Change events carry only the conversation id, so a
    streamed fragment does not copy the conversation once per tab.
  - **Stop uses `CancelAsync`.** It marks the token cancelled at once and runs the stream's cancellation callbacks on the
    thread pool, so Stop returns even if closing the stream is slow. A test holds a stream that ignores its token.
  - **A stream that ends without a final event fails the turn** as `Unexpected`.
  - **A last-resort guard releases the conversation** if recording an outcome itself throws, so a conversation can never
    be stuck in flight.
- **Finding:** item 4's canned answer writes the corpus on every response. Through the real `AnswerService`, its follow-up
  is therefore reported as CACHE MISS (expired), since the tracker predicted a read. That is the check working on the
  fixture, and a test pins it.

**Adversarial review.** One read-only review by a second model (Copilot CLI, `gpt-5.6-luna`). It found no Critical, High or
Medium issue.
1. **Low, fixed:** no test covered a turn that fails after its usage arrived. A test now checks that turn's input cost,
   its unreported output, the cache warmth it leaves, and that the next question is valid.
- It reported as checked and correct: the lock scope and event raising, the ordering of Stop and commit, token-source
  cleanup, message alternation, the replay rule, the returned question, the settings lock, the baseline's tolerance and
  classification, the tracker's TTL, margin and keys, the cost of a credentials failure, and the estimator's formulas.

**2026-09-14, item 7.** Done on branch `item-7-ui-shell`. The design system's CSS is in the app unchanged, the Razor
primitives emit its class names, the nav rail and layout follow the DocQA Prototype design, and the three screens are
placeholders at their routes. The Ask/Stop button is built here rather than in item 8.
- **Tests:** 486 offline Core tests and 121 Web tests pass. 103 of the Web tests are new, and the template's smoke
  test is gone.
  - **Mutation checks, two guards:** each was removed in turn, and a test failed each time.
    - never setting the Ask latch
    - dropping the rule that an answer's end is announced only in the conversation where it was seen
- **Smoke run:** the app was started with its key path pointing at a missing file, so no request was sent.
  - Every page and stylesheet returned 200: the three pages, `app.css`, the scoped CSS bundle, and all twelve
    design-system files that `styles.css` imports.
  - The fingerprinted `styles.css` resolves its relative imports.
  - The prerendered page carries the rail, `aria-current="page"` and "$0.00".
  - The corpus load reported the missing key, and the app stayed up.
- **Cost:** none. Nothing billed was called.
- **Delivered for later items:**
  - `Button`, `Badge`, `Card`, `Tag`, `TextInput`, `Select` (with `SelectOption`), `Tooltip`, `Icon` and
    `LiveRegion`, in `Components/Ds/`, for items 8 to 10
  - `AskStopButton`, with the screen's live region, for item 8
  - `NavRail`, showing the app's total cost, and `Formatting.Money`
  - `FakeConversationStore`, for the Web tests of items 8 to 10
- **Decisions (user, 2026-09-14):**
  - The design is Claude Design project `a364b4bf-ab9c-4b4a-8a69-bd156cff3e38`; its file is
    `plans/DocQA Prototype.dc.html`.
  - The Ask/Stop button's behavior is built in item 7. Item 8 places it on the Conversation screen, beside the input
    that calls `Store.Ask`.
- **The CSS copy:**
  - **Source:** 13 files from design-system project `741ce686-82d8-4ea2-9e43-990c8752d45a`, pulled 2026-09-14. The
    source is recorded in `wwwroot/css/ds/README.md`.
  - **Checked against the design:** six of the 13 were compared with the copies the prototype design embeds, and are
    identical.
  - **Layout:** the design system has one CSS file per group, not one per component. The Tabs, Dialog and Toast rules
    come along unused, so that the copy stays exact.
- **Additions beyond the plan's text:**
  - **The Ask/Stop button owns the screen's live region.**
    - **What it announces:** "Answer complete", "Answer stopped", "Answer failed" or "Answer not added to the
      conversation".
    - **When:** only when it sees this conversation's answer end, so never on a fresh render or on a switch of
      conversation.
    - **Repeats:** a repeated announcement gains a trailing no-break space, so that it is read again.
  - **Stopping comes from the store,** through `InProgressAnswer.IsStopping`, so every tab shows it.
    - A click acts on the state the button showed.
    - A Stop clicked just as the answer finishes therefore never becomes an Ask.
  - **Ask clicks are ignored while the page's `OnAsk` is running,** so a double click sends one question.
  - **A clickable `Card` or `Tag` is a `<button>`.** A clickable card holds only spans, and it cannot have a footer.
  - **No `MarkupString` anywhere.**
    - Icons are real SVG elements, chosen from an enum.
    - A test renders a `<script>` payload through every primitive that shows text, and finds it encoded.
  - **`Select` shows a disabled option's reason beside its label.** It also marks the chosen option `selected`, so the
    prerendered page shows the right choice before the circuit starts.
  - **Adapter rules in `app.css`,** since the copied CSS is never edited:
    - a card that is a button resets the browser's button styles
    - an `aria-disabled` button does not look pressed
    - `.visually-hidden`, for the live region
  - **The rail's focus ring is green-500,** as in the design system's night theme; green-600 does not show on ink.
    The ring is drawn inside the link, because the rail clips overflow.
- **Deviations from the design:**
  - The rail's eyebrow reads "DocQA", not the prototype's "NOTRAG".
  - The rail shows the app's total cost, labeled "Total cost", per this plan. The design's "Session cost" there is
    the active conversation's; item 10's Session cost card shows that.
- **Known limits, WCAG 1.4.13:**
  - Escape dismisses a tooltip only while its trigger has focus. Dismissing one opened by hover would need a
    document-level key listener in JS.
  - Moving the pointer across the 7px gap onto the bubble closes it.
  - The only planned tooltip is item 8's "Sources" label.
- **Tooling notes, from the mutation checks:**
  - A mutation that leaves a field assigned but never read fails the build under `TreatWarningsAsErrors` (CS0414).
    The test run then reports nothing, which can pass for a silent success.
  - The first version of the Ask-latch test awaited its second click before releasing the page's pending Ask. Without
    the latch it therefore hung instead of failing. It now awaits neither click until the Ask is released in a
    `finally`, and fails in milliseconds without the latch. Run a mutation check under `timeout`.

**Adversarial review.** One read-only review by a second model (Copilot CLI, `gpt-5.6-luna`). It reported two High and
three Medium findings, and no Critical or Low ones.
1. **High, rejected:** "the Conversation route never renders the Ask/Stop button."
   - **Why rejected:** that screen, with its input, `Store.Ask` and the Enter-key module, is item 8.
2. **High, fixed as Low:** "a store notification queued before disposal can render a disposed component."
   - **Why Low:** Blazor ignores a render request from a disposed component, so no exception results.
   - **The fix:** a disposed guard in the button and the rail makes that explicit.
3. **Medium, fixed:** Ask had no guard against a second click while the page's `OnAsk` was still running. It now has
   one, with a test.
4. **Medium, partly accepted:** "the same-element test compares component instances, not DOM nodes."
   - **Why the core claim is rejected:** bUnit rebuilds its DOM on each render, so node identity there proves nothing
     about the browser. What Blazor's diff preserves is the retained `Button` instance, whose template has one fixed
     `<button>`.
   - **What it led to:** the gaps it named that were real are now tested, a change of conversation and rapid clicks.
5. **Medium, fixed:** `LiveRegion` was built but announced nothing. The Ask/Stop button now holds the one region and
   announces each answer's end.

**2026-09-14, item 8.** Done on branch `item-8-conversation-screen`. The Conversation screen runs on the store. It
has the conversation list, the model and level selects with the cost preview, and the cache badge. It also has the
history, the streaming answer, and the input, with Enter to send, beside the Ask/Stop button.
- **Tests:** 487 offline Core tests (one more, net) and 198 Web tests (77 of them new) pass. The 5 live tests were not run.
- **Smoke run:** the app was started with its key path pointing at a missing file, so no request was sent.
  - Run in Development, as its launch profile runs it, `/` returned 200.
  - The list, the header, the preview and the input were all prerendered.
  - A second visit reused the one conversation and showed CORPUS NOT LOADED.
  - `QuestionInput.razor.js` was served at its fingerprinted path and at the path the import map maps.
- **Cost:** none. Nothing billed was called.
- **Decisions (user, 2026-09-14):**
  - **Sources show the document label and character range, and no section number.** This settles the first open
    question as proposed.
  - **The thinking summary streams dimmed under "Assembling answer from documents".** Once the text starts it becomes a
    "Reasoning summary so far" disclosure, and once the answer completes, a closed "Reasoning summary". This settles
    the second open question as proposed.
  - **A conversation whose corpus a reload replaced is read-only, enforced in the store and in the UI.** Item 3
    specified this, but the store did not do it.
    - **Store:** `ConversationStore.Ask` now refuses it with `CorpusReplacedRefusal`.
    - **UI:** a CORPUS RELOADED notice offers a new conversation, and Ask is unavailable.
    - **Tests:** item 6's test that asked a follow-up after such a reload now asserts the refusal. A new test checks
      that a reload finding the same corpus changes nothing.
  - **The open conversation is kept in the address, as `/?c={id}`.** A refresh reopens it, and two tabs can show
    different conversations. Without one, the newest opens, and on first run one is created.
    - In .NET 10, `NavLinkMatch.All` ignores the query string (confirmed in Microsoft's docs), so the rail's link stays
      marked.
- **Delivered for later items:**
  - `ICorpusSource.ReloadAsync`, for item 10's Reload button. Here the Retry button of a failed load uses it.
  - Each source row has the id `answer-{i}-source-{n}`, which item 9's click-through can target.
  - Test support for the Web tests of items 9 and 10:
    - `TestData`: a small corpus, answer records, and the options bound from the app's own `appsettings.json`
    - `FakeCorpusSource`
    - a `FakeConversationStore` that also creates conversations, changes settings, asks and returns questions
  - Core's internals are visible to `DocQaMs.Web.Tests`, which builds corpus snapshots.
- **Additions beyond the plan's text:**
  - **A suggestion fills the input; it does not ask.** The design asks at once, but a click that can spend $2 should be
    deliberate. This is a deviation from the design.
  - **Each `[n]` marker links to its source row on the same page.** Its `aria-label` names the source. Clicking through
    to the document is item 9.
  - **The cost preview:**
    - **It refreshes itself when a "probably warm" prediction expires.** A timer fires at the time the preview names,
      since nothing else changes then.
    - **The worst case always assumes a cold write,** since warmth is only ever probable. The plan's warm example
      gives the cold figure too.
    - **Until item 12, "estimated" covers estimates and assumptions alike.** Item 10's pricing table gives each row's
      basis.
    - **Once a conversation has an answer, a cold preview prices "the next question"** rather than the first.
  - **The returned question:**
    - **Only a live circuit takes it.** A prerendered page is thrown away, and would take the question with it.
    - **It fills only an empty input,** so a draft already being written is kept.
  - **A second Enter asks nothing.** The guard checks the store rather than the last render, which may not have caught
    up.
  - **Rendering while an answer streams:**
    - **The page:** a change that starts, stops or ends an answer, or brings its first text, renders at once. Other
      changes wait for the 100 ms throttle.
    - **The Ask/Stop button renders only when its label or announcement changes.** In item 7 it rendered on every
      streamed fragment.
    - **A finished answer skips re-rendering** (`ShouldRender`) while another streams.
  - **Why Ask is unavailable is read through `aria-describedby`, and kept visually hidden.** The notices above the
    history already show it on screen.
  - **The cache badge follows the design's tones:** neutral before any question, accent for a cold write, success for a
    read. It is danger for a miss or no caching, which the design does not show.
  - **A warning badge shows its kind, and the text beside it drops the repeated "KIND:" prefix.**
  - **Corpus state:**
    - a failed load shows its reason and a Retry button
    - a failed reload says the corpus loaded before stays in use
- **Findings:**
  - **Outside Development, the build output does not serve collocated JS.**
    - **What happens:** started from the build output in Production, as `--no-launch-profile` does, every `.razor.js`
      returns 500. That includes item 7's `ReconnectModal.razor.js`.
    - **Why:** `StaticAssetDevelopmentRuntimeHandler` looks for the files under `wwwroot`.
    - **In Development:** both are served, as the launch profile runs the app.
    - **A Release publish, run in Production, serves them too.** Checked for the review, below.
    - **What changed:** nothing, since deployment is out of scope.
- **Known limits:**
  - The history does not scroll itself to the streaming answer.
  - Enter, Shift+Enter and IME composition run in the browser. bUnit cannot exercise them, so they stay on item 11's
    manual list.

**Adversarial review.** One read-only review by a second model (Copilot CLI, `gpt-5.6-luna`). It reported one High,
one Medium and three Low findings, and no Critical one. One was fixed, one was fixed in documentation, one was partly
accepted, and two were rejected.
1. **High, rejected:** "the collocated JS module is not served in production, so Enter inserts a newline."
   - **Why rejected:** it holds only for the build output started outside Development (see Findings).
   - **What was checked:** a Release publish, run in Production, served `QuestionInput.razor.js` at both of its paths,
     and item 7's `ReconnectModal.razor.js`, with no exception.
   - Without the module, only Enter is lost; the Ask button still asks.
2. **Medium, rejected:** "a turn in flight across a reload recreates the old corpus's cache baseline, which could
   misclassify cache use if that corpus is loaded again."
   - **Why rejected:** a baseline is keyed by fingerprint, model and level, and a fingerprint names exactly one corpus.
     A recreated baseline for the old corpus is a correct measurement of that corpus's prefix, so it cannot
     misclassify a later turn on it.
   - The code is item 6's, and item 8 did not change it.
3. **Low, fixed in documentation:** `RenderThrottle` can call its render callback once after `Dispose`, when the two
   race. The class now states that contract. The page's callback already checks its own disposal.
4. **Low, partly accepted:** "the focus test compares component instances, not the browser's focus."
   - **Why the test stays:** item 7's review settled that bUnit cannot see the browser's focus.
   - **What it led to:** focus retention on the Ask/Stop button is now on item 11's manual acceptance list.
5. **Low, fixed:** the marker test used a fixed address, so it would pass even if the page dropped `?c={id}` from the
   links. A page-level test now checks that a marker's link keeps it.

**2026-09-15, item 9.** Done on branch `item-9-documents-screen`. The Documents screen shows each document's whole
extracted text, with the span behind a citation highlighted in the paragraphs it covers and scrolled to. The nav rail
now carries the open conversation between the three screens.
- **Tests:** 513 offline Core tests (26 of them new) and 239 Web tests (41 of them new) pass. The 5 live tests were not
  run.
  - **Mutation checks, four guards:** each was removed in turn.
    - `DocumentParagraph.ShouldRender`, the sections keeping the body heading, and CITED counting only citations
      checked against the corpus on screen each failed a test at once.
    - **The fourth exposed a gap:** dropping the code-point conversion of a span's *start* still passed, because a
      start before its paragraph is clamped to the paragraph's edge, which hid the error. A new test puts both ends of
      a span inside one paragraph, after a character outside the BMP; the check then failed as it should.
- **Smoke run, offline:** started with its key path pointing at a missing file, so no request was sent. `/`,
  `/documents`, `/settings`, a Documents address carrying every query parameter, and `Documents.razor.js` all returned
  200; the screen showed CORPUS NOT LOADED with Retry, and the rail marked Documents as current.
- **Smoke run, live, and free (user decision, 2026-09-15):** started with the real key, so the corpus loaded through
  eight `count_tokens` calls.
  - **Part 175 renders in about 6–8 ms,** as 325,604 bytes of prerendered HTML: 1,108 paragraphs, 48 section tags and
    the six document rows. The other five documents are 22,550 to 153,418 bytes.
  - Its counts match item 3's live figures: 246,682 characters and about 102,061 tokens.
  - **No further optimization was done,** as the plan asks: the cost is a few milliseconds.
- **Cost:** none. Only `count_tokens`, which is free.
- **Decisions (user, 2026-09-15):**
  - **The rail's three links carry `?c=`,** so Documents marks the conversation's citations and Conversation reopens it.
    `NavLinkMatch.All` ignores the query string in .NET 10, which a test pins.
  - **The click-through starts at the source rows.** A `[n]` marker still jumps to its source row, as item 8 decided,
    and the row carries a "Show in document" link.
  - **The section tags sit in a `<details>`,** open, with a capped height of their own. Parts 175 and 176 have 48 and 61
    sections, which as a plain wrapping row would fill the screen above the text.
- **Delivered for later items:** `CorpusDocument.FindHighlight` and `Sections`, `ParagraphSpan`, `SectionHeading`, and
  `SourceStatus`, which holds the source label, badge tone and span lookup that item 8 had inside `AnswerView`.
- **Finding: every document lists its sections twice.** A table of contents repeats each heading before the body does,
  and in Part 176 one contents entry is worded differently from its heading.
  - **What the code does:** headings are keyed by section number, and each section keeps its *last* heading, which is
    the body's. So a tag scrolls to the section, not to its contents entry. Part 176's 176.590 resolves to paragraph
    412, not 52, which a test pins against the golden text.
- **Additions beyond the plan's text:**
  - **A span that is cited but does not match is still highlighted,** with its status badge and the line "The highlight
    shows the text at the cited span, which is not the quote above." Seeing what the span really holds is the point of
    reading it in context.
  - **The screen states what it cannot show:** a source that is not in the conversation, a half link (an answer with no
    source number), and a span with nothing to highlight each get their own notice.
  - **A source shown in the snapshot its answer was checked against,** with a CORPUS RELOADED notice when a reload has
    replaced it. CITED likewise counts only citations checked against the corpus on screen.
  - **Scrolling moves focus to the paragraph** (`scrollIntoView`, then `focus`), so a keyboard or screen-reader user
    continues from the passage. The paragraph is made focusable by the JS module, not by the markup, so it stays out of
    the Tab order.
  - **Choosing a document drops the opened source,** as the design does, and keeps the conversation in the address.
- **Deviations from the design:**
  - The design scrolls the text inside a box with its own maximum height. Here the document pane scrolls as a whole, so
    there is one scroll bar and a section or citation scrolls into view in one place.
  - The design's "excerpt of the full section" is the whole extracted text, as this plan requires.
- **Known limits:**
  - `scrollIntoView` and focus run in the browser. bUnit checks only that the right element id is asked for, so the
    scroll itself stays on item 11's manual list.
  - A paragraph is a `<p>` per paragraph, with no `Virtualize`, by the plan's decision. 325,604 bytes reach the browser
    for Part 175, twice: once prerendered and once over the circuit.

**Adversarial review.** One read-only review by a second model (Copilot CLI, `gpt-5.6-luna`). It reported one Medium
finding, and no Critical, High or Low one. It was fixed.
1. **Medium, fixed:** a source that the store had not published when the screen opened was never scrolled to. The
   reveal was marked as handled even when nothing was found, so the highlight could appear later without the screen
   moving to it. The reveal is now left pending until the source resolves, and a test opens the screen at an answer
   that arrives afterwards, then checks that it is revealed once and only once.

**2026-09-15, item 10.** Done on branch `item-10-corpus-settings`. The Corpus settings screen shows the design's four
cards with its editable inputs replaced by read-only values: corpus health per model, the document registry, cache and
pricing with the whole Cost Reference computed live, and the open conversation's cost by bucket.
- **Tests:** 513 offline Core tests and 262 Web tests (23 of them new) pass. The 5 live tests were not run.
  - **Mutation checks, five guards:** each was removed in turn, and four failed a test at once — the active
    conversation's row marking (dropping the effort comparison marked all five of that model's rows), the warmth
    expiry timer, the re-render guard, and the "cold write in progress" wording.
    - **The fifth exposed a gap twice over:** dropping the conversation title from the re-render guard still passed,
      because the test's two snapshots also differed in cost. With the cost held equal, the mutation failed as it
      should. That gap was itself the adversarial review's first finding.
- **Smoke run, live, and free:** started with the real key, so the corpus loaded through eight `count_tokens` calls.
  - Every figure on the screen matches the plan's **Cost Reference** exactly, having been computed from the price
    table and output ranges rather than copied: the measured Opus 5 at High row ($2.08–$2.16 cold, $0.18–$0.30 warm)
    and all ten worst-case rows ($2.80/$1.10 through $5.20/$4.10, and $1.12/$0.44 through $2.08/$1.64).
  - The corpus reads 199,749 tokens on both models, 50% of the 400,000 ceiling, with the six documents' counts
    matching items 3 and 9.
  - An earlier run with the key path pointing at a missing file returned 200 from all three routes and showed CORPUS
    NOT LOADED with Retry, the registry and cost tables replaced by notices, and the price table still shown.
- **Cost:** none. Only `count_tokens`, which is free.
- **Delivered for later items:** `CostTables`, which builds the ten rows from the estimator and the warmth tracker and
  words their basis and warmth, and `Formatting.Timestamp`.
- **Additions beyond the plan's text:**
  - **A gauge per selectable model,** since each has its own count and the guard applies to the largest. A model
    whose count failed reads "tokens not counted" and "UNKNOWN SHARE OF CEILING".
  - **The cost card also totals every conversation of the run,** below the open one's breakdown, because the card is
    where cost is read and the rail shows only the total.
  - **The open conversation's own combination is marked** in both cost tables, so the row that applies is findable
    among ten.
  - **The screen states what it cannot show:** each card says what is missing while the corpus is loading or failed,
    and the cost tables say they appear once it is counted.
  - **A row's own token count is named when it is borrowed.** The estimator prices a model with no count of its own
    on the corpus's largest count; a note says so, so the cost table cannot contradict the health gauge.
- **Deviations from the design:**
  - **The cards are ordered Corpus health, Registry, Session cost, Cache and pricing,** not the design's order. Cache
    and pricing carries three tables and spans the whole grid, so it sits last and the three narrow cards pack above
    it.
  - The design's editable cache TTL, token ceiling and per-document label inputs are values. Each is part of the
    cached prefix, so an edit would cost a fresh corpus write on every model and level.
- **Known limits:**
  - Warmth is only ever "probably": the tracker knows this app's own requests and no other client's.
  - Every row but Opus 5 at High is an estimate or an assumption until item 12 measures them, which each row's basis
    column says.

**Adversarial review.** One read-only review by a second model (Copilot CLI, `gpt-5.6-luna`). It reported four Medium
findings and one Low, and no Critical or High one. Two were fixed, one partly accepted, one fixed differently, and one
rejected.
1. **Medium, fixed:** the re-render guard omitted the conversation's title, so a first question asked in another tab
   left the cost card reading "New conversation". The title is now part of the guard, with the test above.
2. **Medium, partly accepted:** "the worst-case table does not show each row's basis."
   - **Why not a basis column:** an answer capped at its output limit is exact arithmetic on that limit, not an
     estimate of how long an answer runs. The plan's own Cost Reference gives that table no basis column either.
   - **What it led to:** the table now says why it carries none.
3. **Medium, fixed differently:** "a model with no token count is silently priced on the largest count." True, and the
   health gauge calls that count unknown, so the two cards could contradict each other. Rather than change the
   estimator, the cache card now names any such model and says its rows are priced on the largest count.
4. **Medium, rejected:** "missing prices, output ranges or `max_tokens` throw `KeyNotFoundException` while building the
   tables."
   - **Why rejected:** item 0's `DocQaOptionsValidator` runs under `ValidateOnStart` and fails start-up on a missing
     price row, `max_tokens`, timeout or output range for any selectable model or level. The app cannot reach this
     screen with such a gap.
   - **What it led to:** the price table's own "no price row is configured" branch was unreachable for the same
     reason, and was removed.
5. **Low, fixed:** the tables had no accessible name of their own; a section's heading names the section, not the
   table inside it. Each table now carries `aria-labelledby` pointing at its heading, which a test pins.

**2026-09-15, item 11.** Done on branch `item-11-tests-acceptance`. Item 11 is an audit: each
earlier item landed with its own tests, so the work was to check the plan's checklist line by
line and fill what was missing, then spend the live acceptance.
- **The suite before the audit:** 780 tests, all passing. After it, **782**.
- **Two gaps, both closed:**
  - **The live sump acceptance test did not exist.** `SumpAnswerLiveTests` asks the
    prototype's question verbatim on the real corpus, through the production path end to end
    — `CorpusLoader`, the real system prompt, `AnswerService` and `QuoteChecker` — and
    asserts that Section 175.410 appears, that the corpus prefix was cached, and that every
    distinct cited span matched. Unlike the item 1 spike, which varies its system prompt to
    stay cold, it writes and reads the production cache entry.
  - **`CreditExhausted`** was the one failure kind that can reach the store from a request
    already sent and was not in the roles-still-alternate theory. Added, making seven rows.
- **`LiveApi` hardening,** from the adversarial review below:
  - The run budget is checked against what a call is **expected** to cost rather than against
    zero, so a run cannot start a $2 answer with $0.10 of budget left.
  - A request that never completes now counts its input-side usage against the budget, so a
    stopped or failed answer cannot be paid for twice in one run.
  - The budget rises to **$4.00**, covering the item 1 spike plus this answer, and the log is
    `TestResults/live.log` rather than `item1-live.log`.

**Live acceptance: passed, and it reproduces the prototype.** The full live suite (6 tests)
ran for **$2.55**, against the ~$3.16 estimate.

| | Prototype (2026-09-12) | This app (2026-09-15) |
|---|---|---|
| 1-hour prefix written | 199,745 tokens | **199,745 tokens** |
| Cold sump question | $2.16 and $2.13 | **$2.15** |
| Output tokens | 5,313 and 6,358 | **6,171** |
| Elapsed | 55–67 s | **64 s** |
| Quotes matched | 42 of 42 | **41 of 41** distinct spans, 44 citations |
| Stop reason | `end_turn` | **`end_turn`** |

The answer names §175.410(a)–(n) and cross-references §175.300(f), 175.420(b), 175.425(h),
175.630(f), 175.640(a), 175.700(f) and 176.430(b)(6) — the reference answer's provisions.
19% of it is in blocks with no citation. The carried-over baseline therefore holds: the
default combination is the prototype's shape, and it behaves as the prototype measured.

**Adversarial review.** One read-only review by a second model (Copilot CLI, `gpt-5.6-luna`)
of the new live test and the `LiveApi` change. Three findings, all confirmed and fixed.
1. **High:** the run budget was not a cap on what a run may start — it compared the total
   spent against the budget and ignored the cost of the call about to be made, and no
   failed, stopped or timed-out request ever counted at all. Both fixed as above.
2. **Medium:** `Assert.Empty` over a projection discards the projection, so a failed quote
   would have reported only that a list was not empty. It is now `Assert.True` carrying the
   failing quotes, their statuses and their mismatch details into the message.
3. **Low:** the timeout catch relabeled any `OperationCanceledException` as the 3-minute cap.
   It now checks that the cap's own token is the one that was cancelled.

**Manual acceptance session: run in part, and closed by decision.** (User decision,
2026-09-15: the remaining checks were not worth their cost, and the estimates stand.)

- **Run in the app, on Opus 5 at `high`, against the entry the live test had written:**

  | Check | Cost |
  |---|---|
  | First question in the app, reading the corpus entry | **$0.12** |
  | Refreshing the tab mid-answer; the answer completed and committed | **$0.17** |
  | Enter and Shift+Enter, and focus through Ask → Stop → Stopping… → Ask | **$0.14** |

- **What the $0.12 proves, and it is the useful result:** the app read the corpus entry that
  the live test's process wrote. The 1-hour entry is shared across processes, not per client
  instance, so the cache survives an app restart within its TTL.
- **Warm answers can cost less than the preview's low end.** The corpus read is a fixed $0.10,
  so $0.12 is about 800 output tokens and $0.14 about 1,600. The $0.18–$0.30 warm band came
  from the sump question's 3,200–6,400-token answers, and a short question lands under it. The
  band is an estimate of this corpus's *typical* answer, not a floor. Item 12 would widen its
  low end if it ever measures short questions.
- **Not run, by decision:** the follow-up tail read, the new-conversation read, the stopped
  turn, and the comparison against the Claude Console's billed usage. The follow-up and
  new-conversation reads are covered by the prototype's live run ($0.23 and $0.18) and by
  item 1's live A → B → A check; the stopped turn is covered offline by the Stop tests.
- **One question stays open, and the Console comparison was the only way to close it:**
  whether output generated before a Stop is billed. No first-party documentation states it.
  This changes nothing in the app, which already claims only that Stop ends the wait and
  never that it saves money, and labels a stopped turn "input billed about $X; output before
  Stop not reported".
- **Not a defect, but worth recording:** the app was first started with `--no-launch-profile`,
  which drops `ASPNETCORE_ENVIRONMENT=Development`. Run as Production against unpublished
  output, Static Web Assets are off, so `blazor.web.js` and the scoped stylesheet 404: the
  page renders unstyled and the circuit never starts. That is standard ASP.NET behavior, and
  `dotnet run` uses the `http` profile, but a Production run needs `dotnet publish` first.

## Plan Review Log

**2026-09-13, subagent review of the first draft.** No critical findings; four high, seven
medium, seven low. Each high finding was checked against the prototype's source or the
SDK's XML docs before being applied.

- **High, all applied:**
  1. The cache baseline must be the 1-hour write plus the read, shared across conversations
     for a given fingerprint. Confirmed in `repl.py` `Session.record` and in
     `reload_corpus`. Item 6.
  2. A reply that can't be replayed must not be committed. Confirmed in `Session.commit`,
     which returns `False` when there is no replayable content. Item 6.
  3. Golden files cannot come from `--show` on Windows: `print` translates line endings to
     CRLF. Confirmed from `extract.py` and `display.py`. Item 2, plus `.gitattributes` in
     item 0.
  4. The spike must test the single-pass stream the answer service actually uses. The SDK
     documents `CollectAsync` and `Message()`, which suggests a pass-through, but this is
     unverified. Item 1, check 2.
- **Medium, all applied:**
  - the cost of cancelled turns (items 4 and 6)
  - environment overrides of credentials and host; confirmed that the SDK reads
    `ANTHROPIC_BASE_URL` and `ANTHROPIC_PROFILE` (items 0 and 1)
  - CS1591 with `TreatWarningsAsErrors` (item 0)
  - Enter and Shift+Enter need JS (item 8)
  - lifecycle of the singleton store's subscribers (item 6)
  - offset-to-paragraph mapping for highlights (item 9)
  - parity of the assembled request (item 3)
- **Low, all applied:**
  - the code-point map's `length + 1` entries and bounds checked in code points (item 5)
  - moving the code-point index type into item 1
  - `AllowedHosts` including `127.0.0.1` and `[::1]`
  - explicit timeout and retries (item 0)
  - `*.htm` also matching `.html` on Windows (item 2)
  - scoped CSS and `::deep` (item 7)
  - accessibility details (item 7)
- **Rejected:** none.

**2026-09-13, revision 2 (user request).** Added model selection (Opus 5 or Sonnet 5), effort
level selection (`low`–`max`), the **Cost Reference**, a required Stop button, and optional
item 12. The facts behind it:
- **Prices:** confirmed against the published pricing page.
- **Corpus token count:** measured on both models with the free `count_tokens`, 199,749
  each.
- **Cache behavior:** the invalidation rules are taken from the SDK skill's caching
  reference. Caches are scoped to the model, and a change to `thinking` or `effort`
  invalidates the messages cache.

**2026-09-13, focused subagent review of revision 2.** No critical findings; two high, nine
medium, ten low.

- **High, both applied:**
  1. **The 20-minute timeout was shorter than a ceiling-length `max` answer,** which takes
     about 22.5 minutes at the measured rate, and retries could bill it again. The SDK's
     docs confirm that `Timeout` covers the response body. Fix: timeouts per level through
     `WithOptions`, an offline check that there is no retry after the response starts, and
     the `MaxRetries = 0` rule if there is (items 0 and 1).
  2. **The spike did not send every combination live, or check that cache entries
     coexist.** Fix: check 7 now covers all ten combinations, runs A → B → A, and excludes
     failing combinations one at a time.
- **Medium, eight applied:**
  - Stop's concurrency: no lock held across I/O, a commit that ignores cancellation, the
    outcome published in the snapshot, and token-source disposal (item 6)
  - the lock trigger, and the wording of the TRUNCATED advice (items 4 and 6)
  - the warmth tracker's timing: stamped at `UsageStarted`, a 5-minute margin, a
    write-in-progress state, and a fake clock for tests (items 0, 6 and 11)
  - the Stop cost claims: an unknown pre-`UsageStarted` cost, and no promise of savings
    (items 4, 6 and 8)
  - the estimate basis: the multipliers now follow from their stated sources, and every row
    has a basis column (Cost Reference)
  - options validation against the cache-affecting sets (item 0)
  - the returned question kept in the store (item 6)
  - Ask/Stop as one persistent button using `aria-disabled` (items 7 and 8)
- **Medium, one rejected:** a possible price tier above 200K input tokens. The pricing page
  states that Claude 4.6 and later models are billed at standard rates across the full 1M
  context window. That sentence was added to the Cost Reference instead.
- **Low, all applied:**
  - every cost figure recomputed exactly from 199,745 tokens rather than from the rounded
    corpus figures
  - the claim of exactness softened
  - the worst case given as absolute totals, including the follow-up after a capped answer
  - item 12's budget and its reasoning corrected
  - the manual session's cost, about $4
  - the measured figure used in the preview example
  - COLD CACHE WRITE shown instead of CACHE MISS for predicted writes
  - "last used" defined as the active conversation's settings
  - enlarging the fixtures before concluding that a combination does not cache
  - `aria-describedby` from the selects to the preview
  - a junk `ANTHROPIC_PROFILE` in check 1
