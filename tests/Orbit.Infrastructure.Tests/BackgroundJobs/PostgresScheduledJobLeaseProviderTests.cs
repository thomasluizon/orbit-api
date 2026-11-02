using System.Data.Common;
using FluentAssertions;
using NSubstitute;
using Npgsql;
using Orbit.Infrastructure.BackgroundJobs;

namespace Orbit.Infrastructure.Tests.BackgroundJobs;

public sealed class PostgresScheduledJobLeaseProviderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryAcquireAsync_ReleasesConnectionAndUsesSameKeyForUnlock(bool acquired)
    {
        var connection = Substitute.For<DbConnection>();
        var command = Command();
        connection.CreateCommand().Returns(command);
        command.ExecuteScalarAsync(Arg.Any<CancellationToken>()).Returns(acquired);
        var provider = new PostgresScheduledJobLeaseProvider(() => connection);
        using var cancellation = new CancellationTokenSource();

        var lease = await provider.TryAcquireAsync("reminder-scheduler", cancellation.Token);

        await connection.Received(1).OpenAsync(cancellation.Token);
        command.CommandText.Should().Contain("pg_try_advisory_lock");
        command.Parameters[0].Value.Should().Be("orbit-scheduled:reminder-scheduler");
        if (acquired)
        {
            lease.Should().NotBeNull();
            await connection.DidNotReceive().DisposeAsync();
            await lease!.DisposeAsync();
            command.CommandText.Should().Contain("pg_advisory_unlock");
            command.Parameters[1].Value.Should().Be(command.Parameters[0].Value);
        }
        else lease.Should().BeNull();
        await connection.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryAcquireAsync_OpenOrCommandThrows_DisposesOnceAndPropagates(bool openFails)
    {
        var connection = Substitute.For<DbConnection>();
        var command = Command();
        connection.CreateCommand().Returns(command);
        var failure = new InvalidOperationException("database failed");
        if (openFails) connection.OpenAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        else command.ExecuteScalarAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<object?>(failure));
        var provider = new PostgresScheduledJobLeaseProvider(() => connection);

        var run = () => provider.TryAcquireAsync("job", default);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("database failed");
        await connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_UnacquiredConnectionDisposalThrows_DoesNotDisposeTwice()
    {
        var connection = Substitute.For<DbConnection>();
        var command = Command();
        connection.CreateCommand().Returns(command);
        command.ExecuteScalarAsync(Arg.Any<CancellationToken>()).Returns(false);
        connection.DisposeAsync().Returns(_ => ValueTask.FromException(new InvalidOperationException("dispose failed")));
        var provider = new PostgresScheduledJobLeaseProvider(() => connection);

        var run = () => provider.TryAcquireAsync("job", default);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("dispose failed");
        await connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Lease_UnlockThrows_StillDisposesConnection()
    {
        var connection = Substitute.For<DbConnection>();
        var command = Command();
        connection.CreateCommand().Returns(command);
        command.ExecuteScalarAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<object?>(true),
            Task.FromException<object?>(new InvalidOperationException("unlock failed")));
        var provider = new PostgresScheduledJobLeaseProvider(() => connection);
        var lease = await provider.TryAcquireAsync("job", default);

        var release = async () => await lease!.DisposeAsync();

        await release.Should().ThrowAsync<InvalidOperationException>().WithMessage("unlock failed");
        await connection.Received(1).DisposeAsync();
    }

    private static DbCommand Command()
    {
        var command = Substitute.For<DbCommand>();
        command.CreateParameter().Returns(_ => new NpgsqlParameter());
        command.Parameters.Returns(new NpgsqlCommand().Parameters);
        return command;
    }
}
