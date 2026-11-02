using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Sync.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Events;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Sync;

public class SyncBatchRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAttempt_ClearsCollectedEventsAndRetriesTheOwnedMutation(bool relational)
    {
        var collector = Substitute.For<IAccountEventCollector>();
        var pending = false;
        collector.When(events => events.MarkResync(Arg.Any<Guid>())).Do(_ => pending = true);
        collector.When(events => events.Clear()).Do(_ => pending = false);
        var conflict = new ConflictOnce(collector);
        using var sqlite = relational ? new SqliteOrbitDbContextFactory(conflict) : null;
        await using var db = sqlite?.Context ?? new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(conflict).Options);
        var user = User.Create("Sync user", "sync@example.com").Value;
        var notification = Notification.Create(user.Id, "Reminder", "Body");
        db.AddRange(user, notification);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        conflict.Enabled = true;
        var handler = new ProcessSyncBatchCommandHandler(new GenericRepository<Habit>(db), new GenericRepository<Goal>(db),
            new GenericRepository<Tag>(db), new GenericRepository<Notification>(db),
            new UnitOfWork(db, new DatabaseConnectionSettings(), collector), collector,
            NullLogger<ProcessSyncBatchCommandHandler>.Instance);

        var result = await handler.Handle(new(user.Id, [new("notification", "read", notification.Id, null)]), CancellationToken.None);

        result.Value.Processed.Should().Be(1);
        conflict.Attempts.Should().Be(2);
        pending.Should().BeFalse();
        collector.Received().Clear();
        db.ChangeTracker.Clear();
        (await db.Notifications.SingleAsync()).IsRead.Should().BeTrue();
    }

    private sealed class ConflictOnce(IAccountEventCollector collector) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && ++Attempts == 1)
            {
                collector.MarkResync(Guid.NewGuid());
                throw new DbUpdateConcurrencyException("Simulated conflict");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
