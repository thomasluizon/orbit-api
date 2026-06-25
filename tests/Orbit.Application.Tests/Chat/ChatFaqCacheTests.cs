using FluentAssertions;
using Orbit.Application.Chat;

namespace Orbit.Application.Tests.Chat;

public class ChatFaqCacheTests
{
    [Theory]
    [InlineData("How do streaks work?", "streaks", "en")]
    [InlineData("como funciona a sequencia?", "streaks", "pt")]
    [InlineData("what is a streak freeze", "streak_freeze", "en")]
    [InlineData("o que vem no pro", "free_vs_pro", "pt")]
    [InlineData("how does xp work", "xp_levels", "en")]
    [InlineData("what is a bad habit", "bad_habits", "en")]
    public void TryMatchFaqKey_RecognisedQuestion_ReturnsKeyAndQuestionLocale(string message, string expectedKey, string expectedLocale)
    {
        var match = ChatFaqCache.TryMatchFaqKey(message);

        match.Should().NotBeNull();
        match!.Value.Key.Should().Be(expectedKey);
        match.Value.Locale.Should().Be(expectedLocale);
    }

    [Theory]
    [InlineData("log my run")]
    [InlineData("create a habit called read")]
    [InlineData("what's my streak today")]
    [InlineData("")]
    public void TryMatchFaqKey_NonFaqOrUserSpecific_ReturnsNull(string message)
        => ChatFaqCache.TryMatchFaqKey(message).HasValue.Should().BeFalse();

    [Fact]
    public void StoreAndGet_RoundTripsByFaqKeyAndLocale()
    {
        ChatFaqCache.StoreAnswer("xp_levels", "en", "XP is earned by logging habits.");

        ChatFaqCache.TryGetAnswer("xp_levels", "en", out var answer).Should().BeTrue();
        answer.Should().Be("XP is earned by logging habits.");
        ChatFaqCache.TryGetAnswer("xp_levels", "pt-BR", out _).Should().BeFalse();
    }
}
