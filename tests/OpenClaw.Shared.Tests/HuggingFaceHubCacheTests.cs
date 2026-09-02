using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class HuggingFaceHubCacheTests
{
    [Fact]
    public void ResolveCacheRoot_PrefersHfHubCacheOverEverything()
    {
        string root = HuggingFaceHubCache.ResolveCacheRoot(name => name switch
        {
            "HF_HUB_CACHE" => @"C:\explicit\hub-cache",
            "HUGGINGFACE_HUB_CACHE" => @"C:\legacy\hub-cache",
            "HF_HOME" => @"C:\hf-home",
            _ => null,
        });

        Assert.Equal(@"C:\explicit\hub-cache", root);
    }

    [Fact]
    public void ResolveCacheRoot_FallsBackToLegacyHuggingFaceHubCache()
    {
        string root = HuggingFaceHubCache.ResolveCacheRoot(name => name switch
        {
            "HUGGINGFACE_HUB_CACHE" => @"C:\legacy\hub-cache",
            "HF_HOME" => @"C:\hf-home",
            _ => null,
        });

        Assert.Equal(@"C:\legacy\hub-cache", root);
    }

    [Fact]
    public void ResolveCacheRoot_FallsBackToHfHomeSlashHub()
    {
        string root = HuggingFaceHubCache.ResolveCacheRoot(name => name switch
        {
            "HF_HOME" => @"C:\hf-home",
            _ => null,
        });

        Assert.Equal(Path.Combine(@"C:\hf-home", "hub"), root);
    }

    [Fact]
    public void ResolveCacheRoot_DefaultsUnderUserProfile()
    {
        string root = HuggingFaceHubCache.ResolveCacheRoot(_ => null);

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "huggingface",
            "hub");
        Assert.Equal(expected, root);
    }

    [Fact]
    public void TryGetSnapshotPaths_MatchesStandardHubCacheLayout()
    {
        using var temp = new TempDirectory();

        bool resolved = HuggingFaceHubCache.TryGetSnapshotPaths(
            temp.Path,
            "unsloth/Qwen3.8-27B-GGUF",
            new string('a', 40),
            "model.gguf",
            out string modelPath,
            out string partialPath,
            out string error);

        Assert.True(resolved, error);
        string expectedDirectory = Path.Combine(
            temp.Path,
            "models--unsloth--Qwen3.8-27B-GGUF",
            "snapshots",
            new string('a', 40));
        Assert.Equal(Path.Combine(expectedDirectory, "model.gguf"), modelPath);
        Assert.Equal(Path.Combine(expectedDirectory, "model.gguf.partial"), partialPath);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    public void TryGetSnapshotPaths_RejectsMalformedRepositoryId(string repositoryId)
    {
        bool resolved = HuggingFaceHubCache.TryGetSnapshotPaths(
            @"C:\cache",
            repositoryId,
            new string('a', 40),
            "model.gguf",
            out string modelPath,
            out string partialPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(modelPath);
        Assert.Empty(partialPath);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryGetSnapshotPaths_RejectsNonGgufFileName()
    {
        bool resolved = HuggingFaceHubCache.TryGetSnapshotPaths(
            @"C:\cache",
            "owner/repo",
            new string('a', 40),
            "model.bin",
            out _,
            out _,
            out string error);

        Assert.False(resolved);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryGetSnapshotPaths_RejectsShortRevision()
    {
        bool resolved = HuggingFaceHubCache.TryGetSnapshotPaths(
            @"C:\cache",
            "owner/repo",
            new string('a', 39),
            "model.gguf",
            out _,
            out _,
            out string error);

        Assert.False(resolved);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryValidateManagedPath_AcceptsPathContainedWithinCacheRoot()
    {
        using var temp = new TempDirectory();
        string candidate = Path.Combine(temp.Path, "models--owner--repo", "snapshots", new string('a', 40), "model.gguf");

        bool resolved = HuggingFaceHubCache.TryValidateManagedPath(
            temp.Path,
            candidate,
            out string validatedPath,
            out string error);

        Assert.True(resolved, error);
        Assert.Equal(candidate, validatedPath);
    }

    [Fact]
    public void TryValidateManagedPath_RejectsPathOutsideCacheRoot()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        string candidate = Path.Combine(outside.Path, "model.gguf");

        bool resolved = HuggingFaceHubCache.TryValidateManagedPath(
            temp.Path,
            candidate,
            out string validatedPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(validatedPath);
        Assert.Contains("not contained within", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateManagedPath_RejectsRelativePath()
    {
        bool resolved = HuggingFaceHubCache.TryValidateManagedPath(
            @"C:\cache",
            Path.Combine("models--owner--repo", "snapshots", new string('a', 40), "model.gguf"),
            out string validatedPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(validatedPath);
        Assert.Contains("fully qualified", error, StringComparison.Ordinal);
    }
}
