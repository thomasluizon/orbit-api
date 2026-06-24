using FluentAssertions;
using Orbit.Application.Chat;

namespace Orbit.Application.Tests.Chat;

public class ChatFaqCacheTests
{
    [Theory]
    [InlineData("How do streaks work?", "streaks")]
    [InlineData("como funciona a sequencia?", "streaks")]
    [InlineData("what is a streak freeze", "streak_freeze")]
    [InlineData("o que vem no pro", "free_vs_pro")]
    [InlineData("how does xp work", "xp_levels")]
    [InlineData("what is a bad habit", "bad_habits")]
    public void TryMatchFaqKey_RecognisedQuestion_ReturnsKey(string message, string expected)
        => ChatFaqCache.TryMatchFaqKey(message).Should().Be(expected);

    [Theory]
    [InlineData("log my run")]
    [InlineData("create a habit called read")]
    [InlineData("what's my streak today")]
    [InlineData("")]
    public void TryMatchFaqKey_NonFaqOrUserSpecific_ReturnsNull(string message)
        => ChatFaqCache.TryMatchFaqKey(message).Should().BeNull();

    [Fact]
    public void StoreAndGet_RoundTripsByFaqKeyAndLocale()
    {
        ChatFaqCache.StoreAnswer("xp_levels", "en", "XP is earned by logging habits.");

        ChatFaqCache.TryGetAnswer("xp_levels", "en", out var answer).Should().BeTrue();
        answer.Should().Be("XP is earned by logging habits.");
        ChatFaqCache.TryGetAnswer("xp_levels", "pt-BR", out _).Should().BeFalse();
    }
}
