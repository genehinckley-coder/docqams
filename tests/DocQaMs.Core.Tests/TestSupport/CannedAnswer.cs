using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>
/// A streamed answer under the app's streaming contract, covering every content kind this app receives: a
/// thinking block with a signature, a redacted thinking block, uncited and cited text blocks, and an empty text
/// block (which must be dropped on replay).
/// </summary>
internal static class CannedAnswer
{
    public const string DocumentTitle = "41 Ill. Adm. Code 175: Technical Requirements for Underground Storage Tanks";

    public const long Write1h = 2_080;

    public const long Write5m = 20;

    public const string Thinking = "The user asks about sumps. Section 175.410 applies.";

    public const string Signature = "c2lnbmF0dXJlLTE=";

    public const string RedactedData = "cmVkYWN0ZWQ=";

    /// <summary>The first citation's quoted text; the space before "gallons" is U+00A0, as in the corpus.</summary>
    public const string CitedText1 = "minimum capacity of\n5 gallons";

    public const string CitedText2 = "at least 3.5 gallons";

    public static InputTokens Tokens { get; } = new(12, Write1h, Write5m, 0);

    public static CitationAnnotation Citation1 => NeutralUpdates.Citation(851, 880, CitedText1, title: DocumentTitle);

    public static CitationAnnotation Citation2 => NeutralUpdates.Citation(950, 970, CitedText2, title: DocumentTitle);

    /// <summary>Up to and including the first thinking delta: what arrives before the model writes much.</summary>
    public static IReadOnlyList<ChatResponseUpdate> Opening =>
    [
        NeutralUpdates.Start(Tokens),
        NeutralUpdates.ThinkingDelta(0, "The user asks about sumps. "),
    ];

    public static IReadOnlyList<ChatResponseUpdate> Updates =>
    [
        .. Opening,
        NeutralUpdates.ThinkingDelta(0, "Section 175.410 applies."),
        NeutralUpdates.Signature(0, Signature),
        NeutralUpdates.RedactedThinking(1, RedactedData),
        .. NeutralUpdates.Text(2, "Sumps must hold "),
        NeutralUpdates.CitationDelta(3, Citation1),
        NeutralUpdates.TextDelta(3, "a minimum of 5 gallons"),
        NeutralUpdates.CitationDelta(3, Citation2),
        .. NeutralUpdates.Text(4, ""),
        .. NeutralUpdates.Text(5, "."),
        .. NeutralUpdates.End(tokens: Tokens),
    ];
}
