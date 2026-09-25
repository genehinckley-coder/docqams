namespace DocQaMs.Claude.Tests.TestSupport;

/// <summary>
/// Tests that read or change process-wide environment variables. They run alone, after the
/// parallel tests, so no other test observes a half-changed environment.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
