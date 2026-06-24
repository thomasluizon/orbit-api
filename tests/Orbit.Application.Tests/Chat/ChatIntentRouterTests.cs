using FluentAssertions;
using Orbit.Application.Chat;

namespace Orbit.Application.Tests.Chat;

public class ChatIntentRouterTests
{
    [Theory]
    [InlineData("thanks")]
    [InlineData("Thanks!")]
    [InlineData("thank you so much")]
    [InlineData("ok")]
    [InlineData("got it")]
    [InlineData("good morning")]
    [InlineData("Good Morning!")]
    [InlineData("obrigado")]
    [InlineData("valeu")]
    [InlineData("olá")]
    [InlineData("bom dia")]
    [InlineData("👍")]
    [InlineData("😊😊")]
    public void IsNoToolTurn_TrivialSocialTurns_ReturnsTrue(string message)
        => ChatIntentRouter.IsNoToolTurn(message).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("create a habit")]
    [InlineData("log my run")]
    [InlineData("how do streaks work?")]
    [InlineData("show my goals")]
    [InlineData("delete the gym habit")]
    [InlineData("what's my streak")]
    [InlineData("thanks, can you also add a habit")]
    [InlineData("remind me at 8")]
    public void IsNoToolTurn_ActionableOrUnknownTurns_ReturnsFalse(string message)
        => ChatIntentRouter.IsNoToolTurn(message).Should().BeFalse();
}
