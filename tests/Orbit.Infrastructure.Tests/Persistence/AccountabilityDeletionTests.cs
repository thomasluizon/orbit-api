using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Events;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AccountabilityDeletionTests
{
    public static TheoryData<AccountabilityPairStatus, bool, bool> DeletionCases()
    {
        var cases = new TheoryData<AccountabilityPairStatus, bool, bool>();
        foreach (var status in Enum.GetValues<AccountabilityPairStatus>())
            foreach (var isRequester in new[] { true, false })
                foreach (var permanentlyDelete in new[] { true, false })
                    cases.Add(status, isRequester, permanentlyDelete);
        return cases;
    }

    [Theory]
    [MemberData(nameof(DeletionCases))]
    public async Task DeleteAccount_RemovesPairsAndDependents_PreservesOtherParticipantsData(
        AccountabilityPairStatus status, bool isRequester, bool permanentlyDelete)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys;";
            command.ExecuteScalar().Should().Be(1L);
        }

        var target = User.Create("Target", "target@example.com").Value;
        var buddy = User.Create("Buddy", "buddy@example.com").Value;
        var unrelatedRequester = User.Create("Unrelated requester", "requester@example.com").Value;
        var unrelatedAddressee = User.Create("Unrelated addressee", "addressee@example.com").Value;
        if (permanentlyDelete)
            target.Deactivate(DateTime.UtcNow.AddDays(-7));
        context.Users.AddRange(target, buddy, unrelatedRequester, unrelatedAddressee);

        var targetHabit = CreateHabit(target.Id);
        var buddyHabit = CreateHabit(buddy.Id);
        var unrelatedHabit = CreateHabit(unrelatedRequester.Id);
        context.Habits.AddRange(targetHabit, buddyHabit, unrelatedHabit);

        var pair = CreatePair(
            isRequester ? target.Id : buddy.Id,
            isRequester ? buddy.Id : target.Id,
            status);
        var unrelatedPair = CreatePair(unrelatedRequester.Id, unrelatedAddressee.Id, AccountabilityPairStatus.Accepted);
        context.AccountabilityPairs.AddRange(pair, unrelatedPair);
        if (status != AccountabilityPairStatus.Pending)
        {
            AddPairData(context, pair, targetHabit);
            AddPairData(context, pair, buddyHabit);
        }
        AddPairData(context, unrelatedPair, unrelatedHabit);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var repository = new AccountResetRepository(context, Substitute.For<IAccountEventCollector>());
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());
        await using var provider = new ServiceCollection()
            .AddSingleton(context)
            .AddSingleton<IObjectStorageDeletionService>(new FakeObjectStore())
            .AddSingleton<IUnitOfWork>(unitOfWork)
            .AddSingleton<IAccountResetRepository>(repository)
            .AddSingleton<IGenericRepository<MarketingContact>>(new GenericRepository<MarketingContact>(context))
            .BuildServiceProvider();
        if (permanentlyDelete)
        {
            var logger = Substitute.For<ILogger<AccountDeletionService>>();
            logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
            using var service = new AccountDeletionService(
                provider.GetRequiredService<IServiceScopeFactory>(), logger, new ConfigurationBuilder().Build());

            await service.RunAsync(CancellationToken.None);

            var errors = logger.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<Exception>();
            errors.Should().BeEmpty();
        }
        else
        {
            await unitOfWork.ExecuteInTransactionAsync(ct => repository.DeleteAllUserDataAsync(target.Id, ct));
        }

        (await context.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == target.Id))
            .Should().Be(!permanentlyDelete);
        (await context.Users.IgnoreQueryFilters().Select(user => user.Id).ToListAsync())
            .Should().Contain([buddy.Id, unrelatedRequester.Id, unrelatedAddressee.Id]);
        (await context.Habits.IgnoreQueryFilters().Select(habit => habit.Id).ToListAsync())
            .Should().BeEquivalentTo([buddyHabit.Id, unrelatedHabit.Id]);
        (await context.AccountabilityPairs.Select(remaining => remaining.Id).ToListAsync())
            .Should().BeEquivalentTo([unrelatedPair.Id]);
        (await context.AccountabilityPairHabits.Select(link => link.PairId).ToListAsync())
            .Should().BeEquivalentTo([unrelatedPair.Id]);
        (await context.AccountabilityCheckIns.Select(checkIn => checkIn.PairId).ToListAsync())
            .Should().BeEquivalentTo([unrelatedPair.Id]);
    }

    private static Habit CreateHabit(Guid userId) => Habit.Create(new HabitCreateParams(
        userId, "Habit", FrequencyUnit.Day, 1, DueDate: new DateOnly(2030, 1, 1))).Value;

    private static AccountabilityPair CreatePair(Guid requesterId, Guid addresseeId, AccountabilityPairStatus status)
    {
        var pair = AccountabilityPair.Create(requesterId, addresseeId, AccountabilityCadence.Daily).Value;
        if (status != AccountabilityPairStatus.Pending)
            pair.Accept().IsSuccess.Should().BeTrue();
        if (status == AccountabilityPairStatus.Ended)
            pair.End();
        return pair;
    }

    private static void AddPairData(OrbitDbContext context, AccountabilityPair pair, Habit habit)
    {
        context.AccountabilityPairHabits.Add(AccountabilityPairHabit.Create(pair.Id, habit.UserId, habit.Id));
        context.AccountabilityCheckIns.Add(AccountabilityCheckIn.Create(
            pair.Id, habit.UserId, new DateOnly(2030, 1, 1), "Check-in").Value);
    }
}
