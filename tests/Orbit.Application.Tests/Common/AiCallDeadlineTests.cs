using FluentAssertions;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Common;

public class AiCallDeadlineTests
{
    [Fact]
    public void Start_ElapsedBudget_CancelsTheTokenAsAnExpiredDeadline()
    {
        using var caller = new CancellationTokenSource();

        using (var budget = AiCallDeadline.Start(TimeSpan.Zero, TimeProvider.System, caller.Token))
        {
            budget.Token.IsCancellationRequested.Should().BeTrue("an answered block turn past its budget has no time left");
            AiCallDeadline.Current.Should().NotBeNull();
            AiCallDeadline.Current!.Expired.Should().BeTrue();
        }

        caller.IsCancellationRequested.Should().BeFalse();
        AiCallDeadline.Current.Should().BeNull();
    }

    [Fact]
    public void Start_CallerCancellation_CancelsTheTokenWithoutAnExpiredDeadline()
    {
        using var caller = new CancellationTokenSource();
        using var budget = AiCallDeadline.Start(TimeSpan.FromHours(1), TimeProvider.System, caller.Token);

        caller.Cancel();

        budget.Token.IsCancellationRequested.Should().BeTrue();
        AiCallDeadline.Current!.Expired.Should().BeFalse("the caller, not the budget, ended the call");
    }

    [Fact]
    public void Start_InsideAnEnclosingBinding_ReadsTheEnclosingExpiry()
    {
        using var turn = new CancellationTokenSource();
        using var enclosing = AiCallDeadline.Bind(turn.Token);
        using var budget = AiCallDeadline.Start(TimeSpan.FromHours(1), TimeProvider.System, turn.Token);

        turn.Cancel();

        budget.Token.IsCancellationRequested.Should().BeTrue();
        AiCallDeadline.Current.Should().NotBeSameAs(enclosing);
        AiCallDeadline.Current!.Expired.Should().BeTrue("the enclosing turn deadline stays in force");
    }
}
