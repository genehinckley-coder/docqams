using System.Globalization;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Answering;

/// <summary>Why an answer cannot be taken as complete.</summary>
public enum StopWarningKind
{
    /// <summary>The response reached its <c>max_tokens</c> limit and stops partway.</summary>
    Truncated,

    /// <summary>The model declined to answer.</summary>
    Refused,

    /// <summary>The response ended for any other reason than <c>end_turn</c>.</summary>
    Incomplete,
}

/// <summary>
/// A warning about how a response ended. Every stop reason is checked: an answer cut off at
/// <c>max_tokens</c> still carries citations, so without the check it would pass for a complete one.
/// </summary>
/// <param name="Kind">What kind of ending it was.</param>
/// <param name="Message">The warning as the UI shows it, starting with the kind in capitals.</param>
public sealed record StopWarning(StopWarningKind Kind, string Message)
{
    /// <summary>
    /// The warning for how <paramref name="response"/> ended, or <see langword="null"/> for <c>end_turn</c>.
    /// </summary>
    /// <param name="response">The final response.</param>
    /// <param name="effort">The level the answer was requested at.</param>
    /// <param name="maxTokens">The <c>max_tokens</c> of every level, for the truncation advice.</param>
    /// <returns>The warning, or <see langword="null"/> when the answer ended normally.</returns>
    internal static StopWarning? For(
        ChatResponse response,
        EffortLevel effort,
        IReadOnlyDictionary<EffortLevel, int> maxTokens)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(maxTokens);

        string? stopReason = StopReasonOf(response);
        return stopReason switch
        {
            "end_turn" => null,
            "max_tokens" => Truncated(Contents(response), effort, maxTokens),
            "refusal" => Refused(response.AdditionalProperties),
            null => new StopWarning(
                StopWarningKind.Incomplete,
                "INCOMPLETE: the response ended with no stop_reason, so the answer may stop partway."),
            _ => new StopWarning(
                StopWarningKind.Incomplete,
                $"INCOMPLETE: the response ended with stop_reason '{stopReason}' rather than 'end_turn', " +
                "so the answer may stop partway."),
        };
    }

    /// <summary>
    /// The provider's raw stop reason (<see cref="ChatKeys.StopReason"/>) when it reported one; otherwise the
    /// stop reason <see cref="ChatResponse.FinishReason"/> stands for, or <see langword="null"/> when there is
    /// neither.
    /// </summary>
    internal static string? StopReasonOf(ChatResponse response)
    {
        if (response.AdditionalProperties is { } properties
            && properties.TryGetValue(ChatKeys.StopReason, out string? raw)
            && raw is not null)
        {
            return raw;
        }

        if (response.FinishReason is not { } finish)
        {
            return null;
        }

        if (finish == ChatFinishReason.Stop)
        {
            return "end_turn";
        }

        if (finish == ChatFinishReason.Length)
        {
            return "max_tokens";
        }

        return finish == ChatFinishReason.ContentFilter ? "refusal" : finish.Value;
    }

    /// <summary>Every content of the response's messages, in order.</summary>
    private static List<AIContent> Contents(ChatResponse response) =>
        [.. response.Messages.SelectMany(message => message.Contents)];

    private static StopWarning Truncated(IReadOnlyList<AIContent> content, EffortLevel effort, IReadOnlyDictionary<EffortLevel, int> maxTokens)
    {
        int limit = maxTokens[effort];
        bool stoppedInThinking = StoppedInThinking(content);

        string text =
            $"TRUNCATED: the response reached the {Count(limit)}-token output limit for {effort.ToDisplayName()} " +
            "effort (thinking counts toward it), so it stops partway.";
        text += stoppedInThinking
            ? " The limit was reached while the model was still thinking, before it finished answering: do not rely on any text above."
            : " Its citations point at real text, but the answer is incomplete: do not rely on it.";

        text += " Ask a narrower question";

        // The lowest level above this one whose output limit is higher, if there is one.
        EffortLevel? higher = CacheAffecting.EffortLevels
            .Where(level => level > effort && maxTokens.TryGetValue(level, out int other) && other > limit)
            .Select(level => (EffortLevel?)level)
            .FirstOrDefault();
        if (higher is { } roomier)
        {
            text += $", or start a new conversation at {roomier.ToDisplayName()} effort, whose limit is {Count(maxTokens[roomier])} tokens";
        }

        // A lower level thinks less, which helps only when thinking used up the limit.
        if (stoppedInThinking && CacheAffecting.EffortLevels.Any(level => level < effort))
        {
            text += ", or start a new conversation at a lower level, which thinks less and leaves more of the limit for the answer";
        }

        return new StopWarning(StopWarningKind.Truncated, text + ".");
    }

    private static StopWarning Refused(AdditionalPropertiesDictionary? details)
    {
        string? category = Detail(details, ChatKeys.RefusalCategory);
        string? explanation = Detail(details, ChatKeys.RefusalExplanation);
        string text = category is null
            ? "REFUSED: the model declined to answer (stop_reason 'refusal')."
            : $"REFUSED: the model declined to answer (stop_reason 'refusal', category '{category}').";
        if (!string.IsNullOrWhiteSpace(explanation))
        {
            text += $" Explanation: {explanation}";
        }

        return new StopWarning(StopWarningKind.Refused, text + " Any text above is not an answer.");
    }

    private static string? Detail(AdditionalPropertiesDictionary? details, string key) =>
        details is not null && details.TryGetValue(key, out string? value) ? value : null;

    /// <summary>
    /// Whether generation stopped inside thinking: the last content with anything in it is reasoning, or
    /// there is none at all. Empty text contents are skipped; they carry nothing.
    /// </summary>
    private static bool StoppedInThinking(IReadOnlyList<AIContent> content)
    {
        for (int i = content.Count - 1; i >= 0; i--)
        {
            AIContent block = content[i];
            if (block is TextContent text)
            {
                if (!string.IsNullOrEmpty(text.Text))
                {
                    return false;
                }

                continue;
            }

            return block is TextReasoningContent;
        }

        return true;
    }

    private static string Count(int tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);
}
