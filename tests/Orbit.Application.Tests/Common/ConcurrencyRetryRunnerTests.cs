using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Common;

public class ConcurrencyRetryRunnerTests
{
    [Fact]
    public async Task Conflict_ResetsBetweenAttemptsAndStopsAtTheSharedCap()
    {
        var attempts = 0;
        var resets = 0;
        var action = () => ConcurrencyRetry.RunAsync<int>(_ =>
        {
            attempts++;
            throw new DbUpdateConcurrencyException();
        }, _ => { resets++; return Task.CompletedTask; }, CancellationToken.None);
        await action.Should().ThrowAsync<DbUpdateConcurrencyException>();
        attempts.Should().Be(ConcurrencyRetry.DefaultMaxAttempts);
        resets.Should().Be(attempts - 1);
    }

    [Theory]
    [InlineData(true, 3, 2)]
    [InlineData(false, 1, 0)]
    public async Task Predicate_ControlsWhichFailuresRetry(bool accepts, int expectedAttempts, int expectedResets)
    {
        var attempts = 0;
        var resets = 0;
        var action = () => ConcurrencyRetry.RunAsync<int>(_ =>
        {
            attempts++;
            throw new DbUpdateException();
        }, _ => { resets++; return Task.CompletedTask; }, CancellationToken.None, exception => accepts);
        await action.Should().ThrowAsync<DbUpdateException>();
        attempts.Should().Be(expectedAttempts);
        resets.Should().Be(expectedResets);
    }

    [Fact]
    public async Task ConflictThenSuccess_ReturnsTheSuccessfulValue()
    {
        var attempts = 0;
        var result = await ConcurrencyRetry.RunAsync(_ =>
        {
            if (++attempts == 1) throw new DbUpdateConcurrencyException();
            return Task.FromResult(42);
        }, _ => Task.CompletedTask, CancellationToken.None);
        result.Should().Be(42);
        attempts.Should().Be(2);
    }
}
