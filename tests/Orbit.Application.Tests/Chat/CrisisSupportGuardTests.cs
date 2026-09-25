using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public class CrisisSupportGuardTests
{
    [Theory]
    [InlineData("I want to hurt myself", CrisisLocales.English)]
    [InlineData("Quero me machucar", CrisisLocales.Portuguese)]
    [InlineData("Tenho pensamentos de suicídio", CrisisLocales.Portuguese)]
    [InlineData("I want to die. Quero morrer.", CrisisLocales.English | CrisisLocales.Portuguese)]
    [InlineData("I don’t want to live", CrisisLocales.English)]
    public void Detect_RecognizesCuratedPhrases(string message, CrisisLocales expected)
    {
        CrisisSupportGuard.Detect(message).Should().Be(expected);
    }

    [Fact]
    public void Detect_DoesNotTreatGeneralFrustrationAsDisclosure()
    {
        CrisisSupportGuard.Detect("This app is killing me").Should().Be(CrisisLocales.None);
    }

    [Fact]
    public void EnsureResources_PreservesReplyAndAddsBothLines()
    {
        var reply = CrisisSupportGuard.EnsureResources(
            "I hear you.", CrisisLocales.English | CrisisLocales.Portuguese, null);

        reply.Should().StartWith("I hear you.");
        reply.Should().Contain(CrisisSupportGuard.EnglishResource);
        reply.Should().Contain(CrisisSupportGuard.PortugueseResource);
    }

    [Fact]
    public void EnsureResources_DoesNotRepeatLineFromLastAssistantMessage()
    {
        var history = new List<ChatHistoryMessage>
        {
            new(ChatHistoryMessage.AssistantRole, CrisisSupportGuard.EnglishResource)
        };

        CrisisSupportGuard.EnsureResources("I'm here.", CrisisLocales.English, history)
            .Should().Be("I'm here.");
    }
}
