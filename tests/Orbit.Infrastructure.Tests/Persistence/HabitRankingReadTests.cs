using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitRankingReadTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(24)]
    public async Task Ranking_ReadsDecryptedNamesAndFilteredLogs_InTwoQueriesWithoutWrites(int count)
    {
        var encryption = new EncryptionService(Options.Create(new EncryptionSettings
        {
            Key = Convert.ToBase64String(new byte[32])
        }), NullLogger<EncryptionService>.Instance);
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(encryption, counter);
        var context = factory.Context;
        var user = User.Create("Ranking user", "ranking@example.test").Value;
        context.Users.Add(user);
        var from = new DateOnly(2024, 1, 1);
        var habits = Enumerable.Range(0, count).Select(i => Habit.Create(new HabitCreateParams(
            user.Id, $"Private habit {i}", FrequencyUnit.Day, 1, DueDate: from)).Value).ToArray();
        foreach (var habit in habits)
        {
            habit.Log(from.AddDays(-1), advanceDueDate: false);
            habit.Log(from, advanceDueDate: false);
            habit.Log(from.AddDays(1), advanceDueDate: false);
        }
        context.Habits.AddRange(habits);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTimeZoneAsync(user.Id, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var tokens = new HabitRankingTokenService(Options.Create(new JwtSettings
        {
            SecretKey = "ranking-query-shape-test-signing-secret",
            Issuer = "test",
            Audience = "test"
        }));
        var handler = new GetHabitRankingQueryHandler(new GenericRepository<Habit>(context), dates, tokens,
            Substitute.For<IProductAnalytics>(), NullLogger<GetHabitRankingQueryHandler>.Instance);
        counter.Reset();

        var result = await handler.Handle(new GetHabitRankingQuery(user.Id, from, from.AddDays(1),
            HabitIds: habits.Select(h => h.Id).ToArray(), PageSize: 2), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(count);
        result.Value.Rows.Should().HaveCount(2).And.OnlyContain(row => row.CompletedCount == 1
            && row.Name.StartsWith("Private habit "));
        counter.SelectCommandCount.Should().Be(2);
        context.ChangeTracker.HasChanges().Should().BeFalse();
        context.ChangeTracker.Entries().Should().BeEmpty();
    }
}
