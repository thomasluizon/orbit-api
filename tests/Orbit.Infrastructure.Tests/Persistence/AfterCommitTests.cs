using FluentAssertions;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AfterCommitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Callback_RunsOnlyAfterTheOwnedTransactionCommits(bool singleAttempt)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var unit = new UnitOfWork(factory.Context, new DatabaseConnectionSettings());
        var callbacks = 0;
        Task Operation(CancellationToken ct)
        {
            unit.HasActiveTransaction.Should().BeTrue();
            unit.AfterCommit(() =>
            {
                unit.HasActiveTransaction.Should().BeFalse();
                callbacks++;
            });
            callbacks.Should().Be(0);
            return Task.CompletedTask;
        }
        if (singleAttempt) await unit.ExecuteOnceInTransactionAsync(Operation);
        else await unit.ExecuteInTransactionAsync(Operation);
        callbacks.Should().Be(1);
    }

    [Fact]
    public async Task FailedTransaction_DiscardsCallbacksBeforeTheNextOperation()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var unit = new UnitOfWork(factory.Context, new DatabaseConnectionSettings());
        var callbacks = 0;
        var action = () => unit.ExecuteInTransactionAsync(_ =>
        {
            unit.AfterCommit(() => callbacks++);
            throw new InvalidOperationException("Simulated rollback");
        });
        await action.Should().ThrowAsync<InvalidOperationException>();
        await unit.ExecuteInTransactionAsync(_ => Task.CompletedTask);
        callbacks.Should().Be(0);
    }

    [Fact]
    public async Task FailedSavepoint_DiscardsOnlyItsCallbacks()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var unit = new UnitOfWork(factory.Context, new DatabaseConnectionSettings());
        var callbacks = new List<string>();
        await unit.ExecuteInTransactionAsync(async ct =>
        {
            unit.AfterCommit(() => callbacks.Add("outer"));
            var savepoint = () => unit.ExecuteInSavepointAsync<bool>(_ =>
            {
                unit.AfterCommit(() => callbacks.Add("rolled back"));
                throw new InvalidOperationException("Simulated rollback");
            }, ct);
            await savepoint.Should().ThrowAsync<InvalidOperationException>();
            callbacks.Should().BeEmpty();
        });
        callbacks.Should().Equal("outer");
    }
}
