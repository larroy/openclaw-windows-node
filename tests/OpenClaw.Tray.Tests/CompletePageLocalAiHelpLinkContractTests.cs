using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Security-boundary regression: llama-server's own error text (identified by a non-null
/// <c>CompletePageArgs.Detail</c>) must never be scanned for a URL to render as a clickable help
/// link. Unlike OpenClaw's own curated failure messages, that text is server-controlled
/// diagnostic evidence; scanning it for a URL could let a compromised or malicious local model
/// server plant a navigable link in the completion UI. Source-text contract test because
/// <c>CompletePage</c> is a WinUI <c>Page</c> that requires a XAML host to instantiate.
/// </summary>
public sealed class CompletePageLocalAiHelpLinkContractTests
{
    [Fact]
    public void CompletePage_NeverExtractsHelpLinkFromLocalAiFailureText()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));

        Assert.Contains(
            "var helpUrl = args.Detail is null ? ExtractHelpUrl(errorMessage) : null;",
            source);
    }

    /// <summary>
    /// The displayed/tooltipped Local AI log directory must go through the same MSIX-container
    /// path translation as the setup log link, or the text shown to the user (and copied via the
    /// tooltip) will not match the real on-disk location that <c>RevealInExplorer</c> opens.
    /// </summary>
    [Fact]
    public void CompletePage_ResolvesRealPathForServerLogDirectoryDisplay()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));

        Assert.Contains(
            "var displayDirectory = LogFileLauncher.ResolveRealPath(detail.LogDirectory);",
            source);
        Assert.Contains("ViewServerLogLink.Content = $\"Open Local AI logs → {displayDirectory}\";", source);
    }
}
