using OpenClaw.Shared.Audio;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class VoicePreviewTextTests
{
    private const string UiFallback = "ui-localized";

    [Theory]
    [InlineData("es-ES", "¡Hola! Esta es la voz de tu Companion.")]
    [InlineData("zh-CN", "您好!我是您的 Companion。")]
    [InlineData("en-GB", "Hello! This is your Companion speaking.")]
    [InlineData("ES", "¡Hola! Esta es la voz de tu Companion.")]
    [InlineData("xx-XX", UiFallback)]
    [InlineData("", UiFallback)]
    [InlineData(null, UiFallback)]
    public void For_PicksSentenceByVoiceLanguageAndFallsBackToUiText(string? languageTag, string expected)
    {
        Assert.Equal(expected, VoicePreviewText.For(languageTag, UiFallback));
    }

    [Fact]
    public void EveryKokoroVoiceLanguage_HasPreviewText()
    {
        var missing = KokoroModelManager.AvailablePacks
            .SelectMany(p => p.Voices)
            .Select(v => v.LanguageTag)
            .Distinct()
            .Where(tag => VoicePreviewText.For(tag, UiFallback) == UiFallback)
            .ToList();

        Assert.Empty(missing);
    }
}
