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
    [InlineData("harm myself", CrisisLocales.English)]
    [InlineData("self harm", CrisisLocales.English)]
    [InlineData("self-harm", CrisisLocales.English)]
    [InlineData("kill myself", CrisisLocales.English)]
    [InlineData("end my life", CrisisLocales.English)]
    [InlineData("take my life", CrisisLocales.English)]
    [InlineData("want to die", CrisisLocales.English)]
    [InlineData("don't want to live", CrisisLocales.English)]
    [InlineData("suicide", CrisisLocales.English)]
    [InlineData("suicidal", CrisisLocales.English)]
    [InlineData("killing myself", CrisisLocales.English)]
    [InlineData("don’t want to be alive", CrisisLocales.English)]
    [InlineData("end it all", CrisisLocales.English)]
    [InlineData("KILL MYSELF", CrisisLocales.English)]
    [InlineData("me ferir", CrisisLocales.Portuguese)]
    [InlineData("me matar", CrisisLocales.Portuguese)]
    [InlineData("tirar minha vida", CrisisLocales.Portuguese)]
    [InlineData("acabar com minha vida", CrisisLocales.Portuguese)]
    [InlineData("quero morrer", CrisisLocales.Portuguese)]
    [InlineData("não quero viver", CrisisLocales.Portuguese)]
    [InlineData("suicida", CrisisLocales.Portuguese)]
    [InlineData("automutilação", CrisisLocales.Portuguese)]
    [InlineData("não quero mais viver", CrisisLocales.Portuguese)]
    [InlineData("não quero mais estar aqui", CrisisLocales.Portuguese)]
    [InlineData("vou acabar com a minha vida", CrisisLocales.Portuguese)]
    [InlineData("Me Matar", CrisisLocales.Portuguese)]
    [InlineData("Não quero viver", CrisisLocales.Portuguese)]
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
