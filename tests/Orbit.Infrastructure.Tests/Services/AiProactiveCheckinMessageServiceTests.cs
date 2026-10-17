using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AiProactiveCheckinMessageServiceTests
{
    public static TheoryData<ProactiveTrigger, string> LocalesAndTriggers
    {
        get
        {
            var data = new TheoryData<ProactiveTrigger, string>();
            foreach (var trigger in Enum.GetValues<ProactiveTrigger>())
                foreach (var locale in new[] { "en", "pt-BR" }) data.Add(trigger, locale);
            return data;
        }
    }

    private static ProactiveFacts Facts(ProactiveTrigger trigger) => new(trigger, ["Read"], 2, 3, true,
        Guid.NewGuid(), "Learn", "behind", 4, 240, new(2030, 10, 7), new(2030, 10, 13), 5, 7, 0);

    [Theory]
    [MemberData(nameof(LocalesAndTriggers))]
    public async Task EveryTriggerAndLocale_HasGroundedTemplateAndLocalizedAction(ProactiveTrigger trigger, string locale)
    {
        var facts = Facts(trigger);
        var service = Service(null);
        var result = await service.GenerateAsync(facts, locale, default);
        result.Text.Length.Should().BeLessThanOrEqualTo(140);
        result.ActionLabel.Should().NotBeNullOrWhiteSpace();
        result.ActionPrompt.Should().NotBeNullOrWhiteSpace();
        AiProactiveCheckinMessageService.IsGrounded(result.Text, facts, locale).Should().BeTrue();
        if (locale == "pt-BR") result.Text.Should().NotContain("pronto");
    }

    [Theory]
    [InlineData("You have 99 habits still open today.")]
    [InlineData("Read and Swim are still open today.")]
    [InlineData("Your goal Marathon needs attention today.")]
    [InlineData("Upgrade your plan today.")]
    [InlineData("Seu plano Pro está pronto hoje.")]
    [InlineData("A habit is open. Pick one.")]
    [InlineData("A habit is open\nPick one.")]
    public async Task UngroundedOrInvalidSentence_UsesWholeTemplate(string candidate)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        var result = await Service(candidate).GenerateAsync(facts, "en", default);
        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Fact]
    public async Task LongSentence_UsesTemplate()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        (await Service(new string('a', 141) + ".").GenerateAsync(facts, "en", default)).Should()
            .Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Theory]
    [InlineData("en", "Read is still open today.")]
    [InlineData("pt-BR", "Você tem 2 hábitos pendentes hoje.")]
    public async Task GroundedSentence_UsesModelWithLowTemperature(string locale, string sentence)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        double? observedTemperature = null;
        var service = new AiProactiveCheckinMessageService((system, prompt, temperature, ct) =>
        {
            observedTemperature = temperature;
            system.Should().Contain("feminine");
            prompt.Should().Contain("Read");
            return Task.FromResult<string?>(sentence);
        }, NullLogger<AiProactiveCheckinMessageService>.Instance);
        (await service.GenerateAsync(facts, locale, default)).Text.Should().Be(sentence);
        observedTemperature.Should().BeLessThanOrEqualTo(0.3);
    }

    [Fact]
    public async Task ModelFailure_UsesTemplate()
    {
        var facts = Facts(ProactiveTrigger.RestartAfterMiss);
        var service = new AiProactiveCheckinMessageService((_, _, _, _) => throw new InvalidOperationException("Unavailable"),
            NullLogger<AiProactiveCheckinMessageService>.Instance);
        (await service.GenerateAsync(facts, "pt-BR", default)).Should().Be(AiProactiveCheckinMessageService.Template(facts, "pt-BR"));
    }

    private static AiProactiveCheckinMessageService Service(string? text) => new(
        (_, _, _, _) => Task.FromResult(text), NullLogger<AiProactiveCheckinMessageService>.Instance);
}
