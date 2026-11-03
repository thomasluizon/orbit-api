using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Sync.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Events;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Sync;

internal static class SyncBatchTestSender
{
    internal static ISender Create(OrbitDbContext db, IAccountEventCollector? collector = null)
    {
        var events = collector ?? Substitute.For<IAccountEventCollector>();
        var handler = new ProcessSyncBatchCommandHandler(new GenericRepository<Habit>(db),
            new GenericRepository<Goal>(db), new GenericRepository<Tag>(db), new GenericRepository<Notification>(db),
            new UnitOfWork(db, new DatabaseConnectionSettings(), events), events,
            NullLogger<ProcessSyncBatchCommandHandler>.Instance);
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<ProcessSyncBatchCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<ProcessSyncBatchCommand>(), call.Arg<CancellationToken>()));
        return sender;
    }
}
