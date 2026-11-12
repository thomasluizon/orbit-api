using FluentAssertions;
using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class OrbitPersonaTests
{
    [Fact]
    public void SummaryPrompt_NamesOrbitAsThePersona()
    {
        AiSummaryService.SystemPrompt.Should().StartWith("You are Orbit, a warm, perceptive friend")
            .And.NotContain("Astra");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void CheckinPrompt_NamesOrbitAsThePersona(string language)
    {
        var prompt = AiProactiveCheckinMessageService.BuildSystemPrompt(language);

        prompt.Should().Contain("You are Orbit.").And.NotContain("Astra");
        prompt.Should().Contain("Orbit, or the language's weekday");
    }

    [Fact]
    public void CheckinPrompt_Portuguese_StatesThatOrbitIsMasculine()
    {
        AiProactiveCheckinMessageService.BuildSystemPrompt("pt-BR")
            .Should().Contain("Orbit is masculine in Brazilian Portuguese (o Orbit, do Orbit, ele); use native, warm você phrasing.")
            .And.NotContain("feminine");
    }

    [Fact]
    public void SlipAlertPrompt_NamesOnlyOrbitAsAProductName()
    {
        var prompt = AiSlipAlertMessageService.BuildPrompt("Smoking", DayOfWeek.Monday, 20, "en");

        prompt.Should().Contain("product names (Orbit)").And.NotContain("Astra");
    }

    [Fact]
    public async Task ProactivePushJob_SendsThePushWithTheTitleOrbit()
    {
        var push = Substitute.For<IPushNotificationService>();
        var userId = Guid.NewGuid();

        await new ProactivePushJob(push).ExecuteAsync(userId, "Body", CancellationToken.None);

        await push.Received(1).SendToUserAsync(userId, "Orbit", "Body", "/chat", CancellationToken.None);
    }
}
