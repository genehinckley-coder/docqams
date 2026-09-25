using System.Text.RegularExpressions;
using DocQaMs.Web.Tests.TestSupport;

namespace DocQaMs.Web.Tests;

/// <summary>The design system's CSS is copied unchanged, so these check only that the copy is whole and wired in.</summary>
public sealed partial class StylesheetTests
{
    private static readonly string DsFolder = RepoPaths.Combine("src", "DocQaMs.Web", "wwwroot", "css", "ds");

    [Fact]
    public void EveryImportInTheEntryPoint_Exists()
    {
        string[] imports = Imports(File.ReadAllText(Path.Combine(DsFolder, "styles.css")));

        Assert.Equal(12, imports.Length);
        Assert.All(imports, path => Assert.True(File.Exists(Path.Combine(DsFolder, path)), $"Missing {path}"));
    }

    [Fact]
    public void TheOnlyRemoteImport_IsTheGoogleFontsStylesheet()
    {
        string[] remote =
        [
            .. Directory.EnumerateFiles(DsFolder, "*.css", SearchOption.AllDirectories)
                .SelectMany(file => Imports(File.ReadAllText(file)))
                .Where(url => url.Contains("://", StringComparison.Ordinal)),
        ];

        Assert.StartsWith("https://fonts.googleapis.com/", Assert.Single(remote), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePage_LoadsTheDesignSystemBeforeTheAppsOwnRules()
    {
        string app = File.ReadAllText(RepoPaths.Combine("src", "DocQaMs.Web", "Components", "App.razor"));

        int designSystem = app.IndexOf("@Assets[\"css/ds/styles.css\"]", StringComparison.Ordinal);
        int appCss = app.IndexOf("@Assets[\"app.css\"]", StringComparison.Ordinal);
        Assert.True(designSystem >= 0, "App.razor does not link css/ds/styles.css.");
        Assert.True(designSystem < appCss, "css/ds/styles.css must come before app.css, which builds on it.");
    }

    private static string[] Imports(string css) => [.. ImportPattern().Matches(css).Select(match => match.Groups[1].Value)];

    [GeneratedRegex("""@import url\("([^"]+)"\)""")]
    private static partial Regex ImportPattern();
}
