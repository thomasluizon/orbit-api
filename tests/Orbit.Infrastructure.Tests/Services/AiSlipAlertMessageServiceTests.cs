using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure/deterministic logic in AiSlipAlertMessageService.
/// The GenerateFallback method (private static) is exercised through the public API
/// by verifying expected fallback patterns.
/// The AI client interaction is an integration concern.
/// </summary>
public class AiSlipAlertMessageServiceTests
{
    // We test the GenerateFallback logic via reflection since it's a private static method.

    [Fact]
    public void GenerateFallback_English_ReturnsEnglishMessage()
    {
        var method = typeof(AiSlipAlertMessageService)
            .GetMethod("GenerateFallback",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (Domain.Common.Result<(string Title, string Body)>)
            method.Invoke(null, ["Smoking", "en"])!;

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Heads up: Smoking");
        result.Value.Body.Should().Contain("Stay strong");
    }

    [Fact]
    public void GenerateFallback_Portuguese_ReturnsPortugueseMessage()
    {
        var method = typeof(AiSlipAlertMessageService)
            .GetMethod("GenerateFallback",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (Domain.Common.Result<(string Title, string Body)>)
            method.Invoke(null, ["Fumar", "pt-BR"])!;

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Contain("Fique atento: Fumar");
        result.Value.Body.Should().Contain("consegue");
    }

    [Fact]
    public void GenerateFallback_PtShort_ReturnsPortugueseMessage()
    {
        var method = typeof(AiSlipAlertMessageService)
            .GetMethod("GenerateFallback",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (Domain.Common.Result<(string Title, string Body)>)
            method.Invoke(null, ["Biting nails", "pt"])!;

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Contain("Fique atento");
    }

    [Fact]
    public void GenerateFallback_UnknownLanguage_ReturnsEnglishMessage()
    {
        var method = typeof(AiSlipAlertMessageService)
            .GetMethod("GenerateFallback",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (Domain.Common.Result<(string Title, string Body)>)
            method.Invoke(null, ["Smoking", "fr"])!;

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Contain("Heads up");
    }

    [Fact]
    public void GenerateFallback_IncludesHabitTitleInTitle()
    {
        var method = typeof(AiSlipAlertMessageService)
            .GetMethod("GenerateFallback",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (Domain.Common.Result<(string Title, string Body)>)
            method.Invoke(null, ["Doom scrolling", "en"])!;

        result.Value.Title.Should().Contain("Doom scrolling");
    }
}
