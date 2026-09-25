using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Quotes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Web.Tests.TestSupport;

/// <summary>A small corpus, the app's own options, and answer records as the store publishes them.</summary>
internal static class TestData
{
    public const string Label = "41 Ill. Adm. Code 175: Technical Requirements for Underground Storage Tanks";

    public const string Question = "What must a sump hold?";

    public const string AnswerText = "A sump must hold at least 5 gallons.";

    public const string CitedText = "minimum capacity of 5 gallons";

    /// <summary>The token count on every model: the real corpus's cached prefix.</summary>
    public const long Tokens = 199_745;

    public static readonly DateTimeOffset Start = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    public static readonly CorpusSnapshot Corpus = CreateCorpus();

    public static CorpusSnapshot CreateCorpus(string fingerprint = "fingerprint-1") => new(
        [
            new CorpusDocument(
                "ADMINISTRATIVE CODE 41 175.htm",
                new RequestDocument(Label, "Source file: ADMINISTRATIVE CODE 41 175.htm", "Section 175.410 Sumps\n\nA sump must have a minimum capacity of 5 gallons."),
                1_000),
        ],
        fingerprint,
        CacheAffecting.SelectableModels.ToDictionary(model => model.Id, _ => Tokens));

    /// <summary>The options the app runs with, bound from its own appsettings.json: the real prices and output ranges.</summary>
    public static DocQaOptions Options()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(RepoPaths.Combine("src", "DocQaMs.Web", "appsettings.json"))
            .Build();
        return configuration.GetSection(DocQaOptions.SectionName).Get<DocQaOptions>()
            ?? throw new InvalidOperationException("The DocQaMs section is missing.");
    }

    public static AnswerSettings Sonnet(EffortLevel effort) => AnswerSettings.Create(ModelIds.ClaudeSonnet5, effort);

    public static AnswerCitation Citation(string citedText = CitedText) => new(0, 36, 65, citedText, Label);

    public static QuoteReport Report(params QuoteCheck[] checks) =>
        new(checks, checks.Length, [new BlockCoverage(AnswerText, [1], 28, 24, 0.5)], 28, 0);

    public static QuoteCheck Matched(string citedText = CitedText) =>
        new(Citation(citedText), "char_location", citedText, MatchStatus.Matched, null);

    /// <summary>A completed response. Its message is never shown; the view reads the usage, cost and time.</summary>
    public static AnswerResult Result(AnswerSettings? settings = null, TokenUsage? usage = null)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, []) { MessageId = "msg_test" })
        {
            ResponseId = "msg_test",
            ModelId = "claude-opus-5",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 5_313 },
            AdditionalProperties = new() { [ChatKeys.StopReason] = "end_turn" },
        };

        settings ??= AnswerSettings.Default;
        usage ??= new TokenUsage(12, Tokens, 20, 0, 5_313);
        return new AnswerResult(
            settings, response, [Citation()], usage, TurnCost.For(usage, Options().Prices[settings.ModelId]), TimeSpan.FromSeconds(58), null);
    }

    public static AnswerRecord Completed(
        string question = Question,
        string text = AnswerText,
        QuoteReport? quotes = null,
        IReadOnlyList<TurnWarning>? warnings = null,
        CacheReport? cache = null,
        string thinking = "")
    {
        AnswerResult result = Result();
        return new AnswerRecord
        {
            Question = question,
            Settings = AnswerSettings.Default,
            Corpus = Corpus,
            AskedAt = Start,
            Outcome = TurnOutcome.Completed,
            Segments = [new AnswerSegment(text, [1])],
            Thinking = thinking,
            HasRedactedThinking = false,
            Result = result,
            Quotes = quotes ?? Report(Matched()),
            Cache = cache ?? new CacheReport(CacheStatus.ColdWrite),
            Warnings = warnings ?? [],
            Cost = result.Cost,
        };
    }

    public static AnswerRecord Stopped(string question = Question, string partial = "A sump must") => new()
    {
        Question = question,
        Settings = AnswerSettings.Default,
        Corpus = Corpus,
        AskedAt = Start,
        Outcome = TurnOutcome.Stopped,
        Segments = [new AnswerSegment(partial, [])],
        Thinking = "",
        HasRedactedThinking = false,
        Notice = "Stopped: not checked, not added to the conversation.",
        Cost = new TurnCost(0m, 2m, 0m, 0m, 0m),
        CostNote = "Input billed about $2.00; output before Stop not reported.",
    };
}
