# DocQaMs

DocQA rebuilt on the **Microsoft .NET AI libraries** ([Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)).
The application talks to the model only through `IChatClient`; Claude is plugged in behind that
abstraction by the `DocQaMs.Claude` project. See [docs/MIGRATION.md](docs/MIGRATION.md) for the design
and for what the abstraction does not model.

A local, single-user Blazor app that answers questions about **Illinois Administrative Code
Title 41, Parts 172 and 174–178** — underground storage tank regulations.

It does not use RAG. It sends the **whole corpus** (about 200,000 tokens) to Claude as cached,
citable document blocks, then checks every quoted passage **character for character** against
the text that was sent.

## What "matched" means, and what it does not

A matched quote shows **provenance**: the quote appears at the cited location in the source
file. It does **not** show that the quote supports the claim beside it — that is *entailment*,
and nothing here checks it.

The UI says "matched", never "verified", and that wording is deliberate. An answer can have
every quote matched and still reason poorly, especially at the lower effort levels. Read the
cited text before relying on any conclusion drawn from it.

## Requirements

| | |
|---|---|
| .NET SDK | **10.0.400** (pinned in `global.json`; .NET 11 is STS — do not target it) |
| An Anthropic API key | at `D:\claude\apikeys\notrag_key.txt` — see below |
| Internet | for the API, and for the design system's Google Fonts import |

**The API key is never copied into this repository.** It is read from the path in
`appsettings.json` (`DocQaMs:ApiKeyPath`). The loader requires exactly one line beginning
`sk-ant-`, strips a BOM, and never puts key text in an error message. Point `ApiKeyPath`
somewhere else if your key lives elsewhere.

## Run it

```bash
dotnet run --project src/DocQaMs.Web
```

Then open <http://localhost:5266>.

**Use the `http` launch profile — which the line above does.** Running as Production from an
unpublished source tree leaves the Blazor runtime and the scoped stylesheet unserved: the page
renders unstyled and the circuit never starts. A real Production run needs `dotnet publish`
first, which copies those assets into `wwwroot/`. This is standard ASP.NET behavior, not a
defect.

The app binds to **loopback only** and has no authentication. Anyone who can reach the port
spends money on your API key, so that binding is the access control. A `LoopbackGuard` refuses
to start if configuration points anywhere else, including via `ASPNETCORE_URLS`.

Conversations live **in memory** for the app's lifetime. They survive a page refresh and are
lost on restart.

## What it costs

The corpus is about 200,000 tokens, and **each combination of model and effort level has its
own cache entry**, so the first question on each is a cold write.

| On Claude Opus 5 at `high` (the default) | |
|---|---|
| First question (cold corpus write) | **about $2.15** |
| Every question after, within the hour | **about $0.12–$0.30** |
| The same on Claude Sonnet 5 | about $0.85 cold, $0.05–$0.12 warm |

The 1-hour entry is **shared across processes** and each read refreshes its TTL, so restarting
the app inside the hour does not pay the write again.

Every screen shows costs before you spend them, and each figure states its basis: **measured**,
**estimate** or **assumption**. Only Opus 5 at `high` is measured; the rest were never measured
by decision (see plan item 12). Treat the others as the guidance they claim to be.

**Stop** ends the wait for an answer in progress. It does not necessarily save money — whether
output generated before a disconnect is billed is not documented anywhere first-party, so the
app declines to claim either way.

## The three screens

- **Conversation** — ask, watch the answer stream, see each quote's match status and its `[n]`
  markers, pick the model and effort level, and Stop.
- **Documents** — the extracted text of all six parts, with cited spans highlighted in place.
- **Corpus settings** — read-only: corpus health and fingerprint, the document registry, both
  models' prices, the cost tables with their bases, cache warmth per combination, and the
  current conversation's spend by bucket. Read-only by design: each cache-affecting edit would
  force a fresh ~$2 corpus write.

## Tests

```bash
dotnet test
```

1,022 tests, all offline (plus 7 live tests, skipped by default). The runner is **Microsoft Testing Platform** (selected in
`global.json`), not VSTest — xunit.v3 4.x drops VSTest on the .NET 10 SDK.

### Live tests cost real money

Seven tests call the real API and are **skipped unless `DOCQA_LIVE=1`**:

```bash
DOCQA_LIVE=1 dotnet test --filter-namespace DocQaMs.Claude.Tests.Live   # about $2.55
```

They are guarded: a `$4.00` per-run budget refuses a call that would exceed it, and each
request is capped at 3 minutes. Usage and cost are appended to `TestResults/live.log`
(gitignored). The expensive one is the sump acceptance test (~$2.15), which asks the
prototype's question against the real corpus and asserts every quote matched.

## Layout

```
src/DocQaMs.Core      extraction, corpus, answering, quote checking, conversations, cost
                    (provider-neutral: Microsoft.Extensions.AI abstractions only)
src/DocQaMs.Claude    the Claude provider: IChatClient pipeline over the official Anthropic
                    package's AsIChatClient(), token counting, API key, error translation
src/DocQaMs.Web       Blazor Web App (Interactive Server), design-system CSS in wwwroot/css/ds/
tests/              xunit.v3; DocQaMs.Claude.Tests holds the wire-level and live tests;
                    DocQaMs.Web.Tests adds bUnit
admincode/          the six source HTML pages (public ILGA pages; tracked deliberately)
tests/golden/       byte-for-byte extraction goldens from the Python prototype
tools/              generate_golden.py, which produced them
plans/              the implementation plan and its full log — the real design document
```

**Cache-affecting values are compiled constants** in `DocQaMs.Core`'s `CacheAffecting` class, not
settings: the system prompt, document labels, context template, TTLs, thinking configuration,
and the model and level sets. A test pins the SHA-256 of each. They are deliberately not in
`appsettings.json`, where a stray value would silently cost $2 per question.

## Origin

DocQaMs is a copy of DocQA (`D:\code\docqa`) migrated from direct use of the Anthropic SDK
to Microsoft.Extensions.AI. DocQA itself is a port of the Python prototype at `D:\code\NotRag\`, which is **frozen as the reference
and not kept in sync**. Its measurements are this app's acceptance baseline, and they carry
over: the live sump question reproduced the prototype's 199,745-token prefix at $2.15 against
its $2.16, matching 41 of 41 cited spans.

`plans/2026-09-13_docqa-blazor.md` is the design document — the plan, every decision and its
reasoning, and the implementation log for all twelve items. Read it before changing anything
that touches the cached prefix.
