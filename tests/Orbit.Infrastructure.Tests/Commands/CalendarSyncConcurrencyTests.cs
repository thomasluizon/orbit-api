using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Middleware;
using Orbit.Application.Behaviors;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Commands;

public class CalendarSyncConcurrencyTests
{
    [Fact]
    public async Task RunSync_ConcurrentUserWriteAtTransactionBoundary_CommitsWithoutHttp409()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var user = User.Create("Calendar user", "calendar@example.com").Value;
        user.SetStripeSubscription("sub_calendar", DateTime.UtcNow.AddDays(30));
        user.SetGoogleTokens("access", "refresh");
        user.EnableCalendarAutoSync();
        await using (var seed = new ConcurrencyContext(options))
        {
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
        }

        await using var context = new ConcurrencyContext(options);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var writes = 0;
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await using var writer = new ConcurrencyContext(options);
                var competingUser = await writer.Users.SingleAsync(u => u.Id == user.Id);
                competingUser.SetTimeZone("America/Sao_Paulo");
                writer.Entry(competingUser).Property<uint>("xmin").CurrentValue = (uint)++writes;
                await writer.SaveChangesAsync();
                await call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1));
            });
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => context.SaveChangesAsync(call.Arg<CancellationToken>()));
        unitOfWork.When(u => u.ResetTracking()).Do(_ => context.ChangeTracker.Clear());

        var gate = Substitute.For<IPayGateService>();
        gate.CanManageCalendar(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var tokens = Substitute.For<IGoogleTokenService>();
        tokens.TryRefreshAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome("refreshed", GoogleTokenRefreshResult.Success, null));
        var fetcher = Substitute.For<ICalendarEventFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>());
        var handler = new RunCalendarAutoSyncCommandHandler(
            new CalendarAutoSyncDependencies(
                new GenericRepository<User>(context), new GenericRepository<Habit>(context),
                new GenericRepository<GoogleCalendarSyncSuggestion>(context), new GenericRepository<Notification>(context),
                tokens, fetcher, unitOfWork), gate, TimeProvider.System,
            NullLogger<RunCalendarAutoSyncCommandHandler>.Instance);
        var retry = new ConcurrencyRetryBehavior<RunCalendarAutoSyncCommand, Result<CalendarAutoSyncResult>>(unitOfWork);
        var command = new RunCalendarAutoSyncCommand(user.Id, IsOpportunistic: true);
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();

        try
        {
            var result = await retry.Handle(command, ct => handler.Handle(command, ct), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }
        catch (DbUpdateConcurrencyException exception)
        {
            var mapper = new ConcurrencyExceptionHandler(NullLogger<ConcurrencyExceptionHandler>.Instance);
            await mapper.TryHandleAsync(http, exception, CancellationToken.None);
        }

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        writes.Should().Be(1);
        await using var verify = new ConcurrencyContext(options);
        var saved = await verify.Users.SingleAsync(u => u.Id == user.Id);
        saved.TimeZone.Should().Be("America/Sao_Paulo");
        saved.GoogleCalendarLastSyncedAt.Should().NotBeNull();
    }

    private sealed class ConcurrencyContext(DbContextOptions<OrbitDbContext> options) : OrbitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<User>().Property<uint>("xmin").IsConcurrencyToken().ValueGeneratedNever();
        }
    }
}
