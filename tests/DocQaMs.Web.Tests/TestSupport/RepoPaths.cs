namespace DocQaMs.Web.Tests.TestSupport;

/// <summary>Locates files in the repository from the test's output folder.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocQaMs.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"DocQaMs.slnx not found above {AppContext.BaseDirectory}.");
    }
}
