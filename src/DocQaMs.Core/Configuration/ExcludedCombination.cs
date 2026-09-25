namespace DocQaMs.Core.Configuration;

/// <summary>
/// A model and effort combination that is not offered, because it failed the request-shape spike.
/// </summary>
/// <param name="ModelId">The API model id.</param>
/// <param name="Effort">The effort level.</param>
/// <param name="Reason">Why it is excluded; shown beside the disabled option in the UI.</param>
public sealed record ExcludedCombination(string ModelId, EffortLevel Effort, string Reason);
