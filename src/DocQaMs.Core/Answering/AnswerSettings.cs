using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Answering;

/// <summary>
/// The model and effort level one conversation's answers are requested at.
/// </summary>
/// <remarks>
/// The pair selects the corpus cache entry: caches are scoped to the model, and a change of effort
/// invalidates the messages cache, which holds the corpus. It also selects the request's
/// <c>max_tokens</c>, the timeout and the price row. An instance can only be made by
/// <see cref="Create(string, EffortLevel)"/>, so every instance is a combination the app offers.
/// </remarks>
public sealed record AnswerSettings
{
    private AnswerSettings(SelectableModel model, EffortLevel effort)
    {
        Model = model;
        Effort = effort;
    }

    /// <summary>The first-run settings, Opus 5 at <c>high</c>: exactly the prototype's request shape.</summary>
    public static AnswerSettings Default { get; } = Create(CacheAffecting.DefaultModelId, CacheAffecting.DefaultEffort);

    /// <summary>The model.</summary>
    public SelectableModel Model { get; }

    /// <summary>The API model id, such as <c>claude-opus-5</c>.</summary>
    public string ModelId => Model.Id;

    /// <summary>The effort level.</summary>
    public EffortLevel Effort { get; }

    /// <summary>
    /// Validates a model and level against the combinations the app offers.
    /// </summary>
    /// <param name="modelId">A selectable model id.</param>
    /// <param name="effort">A selectable effort level.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentException">
    /// The model is not selectable, or the combination is on <see cref="CacheAffecting.ExcludedCombinations"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The effort level is not selectable.</exception>
    public static AnswerSettings Create(string modelId, EffortLevel effort) =>
        Create(modelId, effort, CacheAffecting.ExcludedCombinations);

    /// <summary>As <see cref="Create(string, EffortLevel)"/>, against a given exclusion list.</summary>
    internal static AnswerSettings Create(string modelId, EffortLevel effort, IReadOnlyList<ExcludedCombination> excluded)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(excluded);

        SelectableModel model = CacheAffecting.SelectableModels.FirstOrDefault(candidate => candidate.Id == modelId)
            ?? throw new ArgumentException($"\"{modelId}\" is not a selectable model.", nameof(modelId));

        if (!CacheAffecting.EffortLevels.Contains(effort))
        {
            throw new ArgumentOutOfRangeException(nameof(effort), effort, "Not a selectable effort level.");
        }

        ExcludedCombination? exclusion = excluded.FirstOrDefault(
            combination => combination.ModelId == modelId && combination.Effort == effort);
        if (exclusion is not null)
        {
            throw new ArgumentException(
                $"{model.DisplayName} at {effort.ToDisplayName()} effort is not offered: {exclusion.Reason}", nameof(effort));
        }

        return new AnswerSettings(model, effort);
    }

    /// <summary>The settings as the UI names them, such as <c>Claude Opus 5, High</c>.</summary>
    /// <returns>The model's display name and the level's.</returns>
    public override string ToString() => $"{Model.DisplayName}, {Effort.ToDisplayName()}";
}
