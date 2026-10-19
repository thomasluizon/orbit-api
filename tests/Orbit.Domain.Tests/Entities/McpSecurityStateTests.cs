using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class McpSecurityStateTests
{
    [Fact]
    public void BudgetReservation_RejectsInvalidUserAndTokens()
    {
        var today = new DateOnly(2026, 1, 1);
        Action emptyUser = () => AiBudgetCall.Reserve(Guid.Empty, today, 10);
        Action emptyTokens = () => AiBudgetCall.Reserve(Guid.NewGuid(), today, 0);
        emptyUser.Should().Throw<ArgumentException>();
        emptyTokens.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BudgetSettlement_IsOnceOnlyAndRejectsNegativeTokens()
    {
        var call = AiBudgetCall.Reserve(Guid.NewGuid(), new DateOnly(2026, 1, 1), 120000);
        Action negative = () => call.Settle(-1);
        negative.Should().Throw<ArgumentOutOfRangeException>();
        call.Settle(30);
        call.Settle(60);
        call.ChargedTokens.Should().Be(30);
        call.IsSettled.Should().BeTrue();
    }

    [Fact]
    public void OAuthState_RequiresHashAndUtcExpiry()
    {
        Action invalidHash = () => OAuthState.Create("raw-secret", "code", "{}", DateTime.UtcNow.AddMinutes(5));
        Action invalidExpiry = () => OAuthState.Create(new string('A', 64), "code", "{}", DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
        invalidHash.Should().Throw<ArgumentException>();
        invalidExpiry.Should().Throw<ArgumentException>();
    }
}
