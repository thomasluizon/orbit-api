using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Events;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Events;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Events;

public sealed class AccountEventSavepointTests : IDisposable
{
    private const string OriginHeader = "X-Orbit-Event-Origin";
    private readonly IAccountEventBus _bus = Substitute.For<IAccountEventBus>();
    private readonly HttpContextAccessor _accessor = new() { HttpContext = new DefaultHttpContext() };
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly AuthValidationCache _authCache;
    private readonly AccountEventCollector _collector;
    private readonly SqliteOrbitDbContextFactory _factory;
    private readonly UnitOfWork _unitOfWork;
    private readonly User _user;

    public AccountEventSavepointTests()
    {
        _authCache = new AuthValidationCache(_memory, Options.Create(new AuthCacheSettings()), TimeProvider.System);
        _collector = new AccountEventCollector(_bus, _accessor, _authCache);
        _factory = new SqliteOrbitDbContextFactory(_collector, new AccountEventTransactionInterceptor(_collector));
        _unitOfWork = new UnitOfWork(_factory.Context, new DatabaseConnectionSettings(), _collector);
        _user = User.Create("Owner", "owner@example.com").Value;
        _factory.Context.Users.Add(_user);
        _factory.Context.SaveChanges();
        _bus.ClearReceivedCalls();
        SetOrigin("origin-1");
    }

    [Fact]
    public async Task RolledBackSavepoint_KeepsTheEventsOfAnEarlierSave()
    {
        var kept = NewHabit("Kept");
        var rolledBack = NewHabit("Rolled back");

        await InTransactionAsync(async ct =>
        {
            _factory.Context.Habits.Add(kept);
            await _factory.Context.SaveChangesAsync(ct);
        }, async ct =>
        {
            _factory.Context.Habits.Add(rolledBack);
            await _factory.Context.SaveChangesAsync(ct);
        });

        var payload = PublishedPayload();
        payload.Changes.Should().ContainSingle(change => change.Kind == "habit" && change.Op == "create")
            .Which.Ids.Should().Equal(kept.Id);
    }

    [Fact]
    public async Task RolledBackSavepoint_KeepsAnEarlierResync()
    {
        await InTransactionAsync(_ =>
        {
            _collector.MarkResync(_user.Id);
            return Task.CompletedTask;
        }, async ct =>
        {
            _factory.Context.Habits.Add(NewHabit("Rolled back"));
            await _factory.Context.SaveChangesAsync(ct);
        });

        _bus.Received(1).Publish(_user.Id, Arg.Any<AccountEventPayload>(), true);
    }

    [Fact]
    public async Task RolledBackSavepoint_KeepsAnEarlierSessionRevocation()
    {
        var session = UserSession.Create(_user.Id, "token-hash", null).Value;
        _factory.Context.UserSessions.Add(session);
        await _factory.Context.SaveChangesAsync();
        _bus.ClearReceivedCalls();

        await InTransactionAsync(async ct =>
        {
            session.Revoke(DateTime.UtcNow);
            await _factory.Context.SaveChangesAsync(ct);
        }, async ct =>
        {
            _factory.Context.Habits.Add(NewHabit("Rolled back"));
            await _factory.Context.SaveChangesAsync(ct);
        });

        _bus.Received(1).CloseSessions(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { session.Id })));
    }

    [Fact]
    public async Task RolledBackSavepoint_KeepsAnEarlierAccountDeletion()
    {
        var deleted = User.Create("Deleted", "deleted@example.com").Value;
        _factory.Context.Users.Add(deleted);
        await _factory.Context.SaveChangesAsync();
        _bus.ClearReceivedCalls();

        await InTransactionAsync(async ct =>
        {
            _factory.Context.Users.Remove(deleted);
            await _factory.Context.SaveChangesAsync(ct);
        }, async ct =>
        {
            _factory.Context.Habits.Add(NewHabit("Rolled back"));
            await _factory.Context.SaveChangesAsync(ct);
        });

        _bus.Received(1).CloseAccount(deleted.Id);
    }

    [Fact]
    public async Task RolledBackSavepoint_KeepsAnEarlierAuthChange()
    {
        var epoch = _authCache.Epoch;

        await InTransactionAsync(async ct =>
        {
            _user.StartTrial(DateTime.UtcNow.AddDays(7));
            await _factory.Context.SaveChangesAsync(ct);
        }, async ct =>
        {
            _factory.Context.Habits.Add(NewHabit("Rolled back"));
            await _factory.Context.SaveChangesAsync(ct);
        });

        _authCache.Epoch.Should().Be(epoch + 1);
    }

    [Fact]
    public async Task RolledBackSavepoint_KeepsTheOriginOfAnEarlierSave()
    {
        var kept = NewHabit("Kept");

        await InTransactionAsync(async ct =>
        {
            _factory.Context.Habits.Add(kept);
            await _factory.Context.SaveChangesAsync(ct);
        }, async ct =>
        {
            SetOrigin("origin-2");
            _factory.Context.Habits.Add(NewHabit("Rolled back"));
            await _factory.Context.SaveChangesAsync(ct);
        });

        PublishedPayload().Origin.Should().Be("origin-1");
    }

    [Fact]
    public async Task TwoSavepoints_KeepTheReleasedOneAndDropTheRolledBackOne()
    {
        var first = NewHabit("First");
        var released = NewHabit("Released");
        var rolledBack = NewHabit("Rolled back");

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            _factory.Context.Habits.Add(first);
            await _factory.Context.SaveChangesAsync(ct);
            await _unitOfWork.ExecuteInSavepointAsync(async inner =>
            {
                _factory.Context.Habits.Add(released);
                await _factory.Context.SaveChangesAsync(inner);
                return true;
            }, ct);
            await FailingSavepointAsync(async inner =>
            {
                _factory.Context.Habits.Add(rolledBack);
                await _factory.Context.SaveChangesAsync(inner);
            }, ct);
        });

        PublishedPayload().Changes.Should().ContainSingle(change => change.Kind == "habit")
            .Which.Ids.Should().BeEquivalentTo([first.Id, released.Id]);
    }

    [Fact]
    public async Task SavepointWithNoEarlierSave_RestoresAnEmptyState()
    {
        var later = NewHabit("Later");

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await FailingSavepointAsync(async inner =>
            {
                _collector.MarkResync(_user.Id);
                _factory.Context.Habits.Add(NewHabit("Rolled back"));
                await _factory.Context.SaveChangesAsync(inner);
            }, ct);
            _factory.Context.Habits.Add(later);
            await _factory.Context.SaveChangesAsync(ct);
        });

        _bus.Received(1).Publish(_user.Id, Arg.Any<AccountEventPayload>(), false);
        PublishedPayload().Changes.Should().ContainSingle(change => change.Kind == "habit")
            .Which.Ids.Should().Equal(later.Id);
    }

    [Fact]
    public async Task SavepointThatFailsInsideASave_LeavesNoStagedChange()
    {
        var kept = NewHabit("Kept");

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            _factory.Context.Habits.Add(kept);
            await _factory.Context.SaveChangesAsync(ct);
            await FailingSavepointAsync(async inner =>
            {
                var duplicate = NewHabit("Duplicate");
                _factory.Context.Habits.Add(duplicate);
                await _factory.Context.SaveChangesAsync(inner);
                _factory.Context.ChangeTracker.Clear();
                _factory.Context.Habits.Add(duplicate);
                await _factory.Context.SaveChangesAsync(inner);
            }, ct);
            await _factory.Context.SaveChangesAsync(ct);
        });

        PublishedPayload().Changes.Should().ContainSingle(change => change.Kind == "habit")
            .Which.Ids.Should().Equal(kept.Id);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _memory.Dispose();
    }

    private Task InTransactionAsync(Func<CancellationToken, Task> before, Func<CancellationToken, Task> failingSavepoint) =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await before(ct);
            await FailingSavepointAsync(failingSavepoint, ct);
        });

    private async Task FailingSavepointAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        var savepoint = () => _unitOfWork.ExecuteInSavepointAsync<bool>(async inner =>
        {
            await operation(inner);
            throw new InvalidOperationException("Simulated effect failure");
        }, ct);
        await savepoint.Should().ThrowAsync<Exception>();
    }

    private AccountEventPayload PublishedPayload()
    {
        var call = _bus.ReceivedCalls().Should()
            .ContainSingle(received => received.GetMethodInfo().Name == nameof(IAccountEventBus.Publish)).Subject;
        call.GetArguments()[0].Should().Be(_user.Id);
        return (AccountEventPayload)call.GetArguments()[1]!;
    }

    private Habit NewHabit(string title) =>
        Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, _user.Id, title, null, null, new DateOnly(2026, 1, 1))).Value;

    private void SetOrigin(string origin) => _accessor.HttpContext!.Request.Headers[OriginHeader] = origin;
}
