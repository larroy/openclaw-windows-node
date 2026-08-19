using System;
using System.Linq;
using OpenClaw.Shared.Inference;
using Xunit;

namespace OpenClaw.Shared.Tests.Inference;

public class LlamaServerArgumentsTests
{
    private static readonly string[] Recipe = ["--temp", "1.0", "-dio"];

    [Fact]
    public void PutsLauncherOwnedFlagsFirstAndAppendsTheRecipe()
    {
        var args = LlamaServerArguments.Build(@"C:\models\m.gguf", 8080, Recipe);

        Assert.Equal(
            ["-m", @"C:\models\m.gguf", "--host", "127.0.0.1", "--port", "8080", "--temp", "1.0", "-dio"],
            args);
    }

    [Fact]
    public void BindsLoopbackByDefault()
    {
        var args = LlamaServerArguments.Build(@"C:\m.gguf", 1234, Recipe);

        var host = args[args.ToList().IndexOf("--host") + 1];
        Assert.Equal("127.0.0.1", host);
    }

    [Fact]
    public void BindsAllInterfacesOnlyWhenExplicitlyRequested()
    {
        // Beyond-loopback exposes an unauthenticated endpoint to the network, so
        // it must never be reachable by accident.
        var args = LlamaServerArguments.Build(@"C:\m.gguf", 1234, Recipe, bindBeyondLoopback: true);

        var host = args[args.ToList().IndexOf("--host") + 1];
        Assert.Equal("0.0.0.0", host);
    }

    [Theory]
    [InlineData("-m")]
    [InlineData("--model")]
    [InlineData("--host")]
    [InlineData("--port")]
    public void RejectsARecipeThatSetsALauncherOwnedFlag(string flag)
    {
        // Such a recipe would either duplicate the flag or silently win.
        var ex = Assert.Throws<ArgumentException>(() =>
            LlamaServerArguments.Build(@"C:\m.gguf", 8080, ["--temp", "1.0", flag, "x"]));

        Assert.Contains(flag, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCatalogRecipeIsAcceptedByTheBuilder()
    {
        // Guards the catalog against a recipe that cannot actually be launched.
        foreach (var model in LocalModelCatalog.Models)
        {
            var args = LlamaServerArguments.Build(@"C:\m.gguf", 8080, model.RecipeArgs);
            Assert.All(model.RecipeArgs, a => Assert.Contains(a, args));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void RejectsAnOutOfRangePort(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LlamaServerArguments.Build(@"C:\m.gguf", port, Recipe));
    }

    [Fact]
    public void RejectsAnEmptyModelPath()
    {
        Assert.Throws<ArgumentException>(() => LlamaServerArguments.Build("  ", 8080, Recipe));
    }

    [Fact]
    public void HealthUrlAlwaysTargetsLoopback()
    {
        // The health poll runs on this machine even when the server binds wider.
        Assert.Equal("http://127.0.0.1:9000/health", LlamaServerArguments.BuildHealthUrl(9000));
    }

    [Fact]
    public void BaseUrlCarriesTheOpenAiCompatibleSuffix()
    {
        Assert.Equal("http://127.0.0.1:9000/v1", LlamaServerArguments.BuildBaseUrl("127.0.0.1", 9000));
        Assert.Equal("http://host.docker.internal:9000/v1",
            LlamaServerArguments.BuildBaseUrl("host.docker.internal", 9000));
    }
}
