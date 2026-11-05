using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Connections;
using Orbit.Application.Goals.Services;
using Orbit.Application.Profile.Models;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class StreamingExportUnitTests
{
    [Fact]
    public async Task Handler_WritesLargeAccountWithoutSynchronousIoAndPreservesEveryByte()
    {
        var user = User.Create("Export", "export-unit@example.test").Value;
        var today = new DateOnly(2026, 6, 1);
        var habits = Enumerable.Range(0, 150).Select(index => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            user.Id, "Read " + index, FrequencyUnit.Day, 1, today)).Value).ToArray();
        var logs = habits.SelectMany(habit => Enumerable.Range(0, 10)
            .Select(day => habit.Log(today.AddDays(day), new string('n', 1024)).Value)).ToArray();
        var turns = Enumerable.Range(0, 2000).Select(index => ConversationTurn.Create(user.Id, Guid.NewGuid(), "user",
            DateTime.UtcNow.AddSeconds(index), null, new string('x', 1024), "[]").Value).ToArray();
        var repos = new ExportUserDataRepositories(Repo<User>(), Repo<Habit>(), Repo<HabitLog>(), Repo<Goal>(),
            Repo<GoalProgressLog>(), Repo<Tag>(), Repo<Notification>(), Repo<ChecklistTemplate>(), Repo<UserAchievement>(),
            Repo<StreakFreeze>(), Repo<Referral>(), Repo<ApiKey>(), Repo<ProactiveItem>(), Repo<NotionConnection>(),
            Repo<NotionTask>(), Repo<ConversationTurn>(), Repo<ConversationOffer>());
        repos.Users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        Project<Habit, Habit>(repos.Habits, habits);
        Project<Goal, Goal>(repos.Goals);
        Project<NotionConnection, ExportedNotionConnection>(repos.NotionConnections);
        Stream<HabitLog, ExportedHabitLog>(repos.HabitLogs, logs);
        Stream<GoalProgressLog, ExportedGoalProgressLog>(repos.GoalProgressLogs);
        Stream<Tag, ExportedTag>(repos.Tags);
        Stream<Notification, ExportedNotification>(repos.Notifications);
        Stream<ChecklistTemplate, ExportedChecklistTemplate>(repos.ChecklistTemplates);
        Stream<UserAchievement, ExportedAchievement>(repos.UserAchievements);
        Stream<StreakFreeze, ExportedStreakFreeze>(repos.StreakFreezes);
        Stream<Referral, ExportedReferral>(repos.Referrals);
        Stream<ApiKey, ExportedApiKey>(repos.ApiKeys);
        Stream<ProactiveItem, ExportedProactiveItem>(repos.ProactiveItems);
        Stream<NotionTask, NotionTaskResponse>(repos.NotionTasks);
        Stream<ConversationTurn, ExportedConversationTurn>(repos.ConversationTurns, turns);
        Stream<ConversationOffer, ExportedConversationOffer>(repos.ConversationOffers!,
            ConversationOffer.Create(user.Id, Guid.NewGuid(), "daily-check-in").Value);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(user.Id, today, Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var result = await new StreamUserDataQueryHandler(repos, dates, progress).Handle(new(user.Id), default);
        result.IsSuccess.Should().BeTrue();
        using var expected = new MemoryStream();
        await result.Value.WriteAsync(expected, default);
        using var destination = new MemoryStream();
        using var transport = new AsyncOnlyWriteStream(destination);

        await result.Value.WriteAsync(transport, default);

        destination.ToArray().Should().Equal(expected.ToArray());
        destination.Length.Should().BeGreaterThan(3 * 1024 * 1024);
        transport.AsyncWrites.Should().BeGreaterThan(3500);
        transport.LargestWrite.Should().BeLessThan(64 * 1024);
        using var document = JsonDocument.Parse(destination.ToArray());
        document.RootElement.GetProperty("habits").GetArrayLength().Should().Be(150);
        document.RootElement.GetProperty("habits")[149].GetProperty("logs").GetArrayLength().Should().Be(10);
        document.RootElement.GetProperty("conversations").GetArrayLength().Should().Be(2000);
    }

    private static IGenericRepository<T> Repo<T>() where T : Entity => Substitute.For<IGenericRepository<T>>();

    private static void Project<T, TResult>(IGenericRepository<T> repository, params T[] rows) where T : Entity =>
        repository.ProjectAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<Func<IQueryable<T>, IQueryable<TResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<TResult>)call.Arg<Func<IQueryable<T>, IQueryable<TResult>>>()(
                rows.AsQueryable().Where(call.Arg<Expression<Func<T, bool>>>())).ToArray());

    private static void Stream<T, TResult>(IGenericRepository<T> repository, params T[] rows) where T : Entity =>
        repository.StreamAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<Func<IQueryable<T>, IQueryable<TResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Enumerate(call.Arg<Func<IQueryable<T>, IQueryable<TResult>>>()(
                rows.AsQueryable().Where(call.Arg<Expression<Func<T, bool>>>())), call.Arg<CancellationToken>()));

    private static async IAsyncEnumerable<T> Enumerate<T>(IEnumerable<T> rows, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            yield return row;
        }
    }
}
