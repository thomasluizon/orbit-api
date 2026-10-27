using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitRankingReadTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Ranking_TranslatesGroupedCountsForPostgres()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=translation_only").Options);
        var repository = Substitute.For<IGenericRepository<Habit>>();
        string? sql = null;
        repository.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<HabitRankingRow>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.ArgAt<Expression<Func<Habit, bool>>>(0);
                var projection = call.ArgAt<Func<IQueryable<Habit>, IQueryable<HabitRankingRow>>>(1);
                sql = projection(context.Habits.AsNoTracking().Where(predicate)).ToQueryString();
                return Array.Empty<HabitRankingRow>();
            });
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTimeZoneAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var handler = new GetHabitRankingQueryHandler(repository, dates, CreateTokens(),
            Substitute.For<IProductAnalytics>(), NullLogger<GetHabitRankingQueryHandler>.Instance);
        var from = new DateOnly(2024, 1, 1);

        (await handler.Handle(new GetHabitRankingQuery(Guid.NewGuid(), from, from.AddDays(30)), default))
            .IsSuccess.Should().BeTrue();

        sql.Should().Contain("GROUP BY").And.NotContain("DISTINCT").And.Contain("LEFT JOIN")
            .And.Contain("EXISTS")
            .And.NotContain("\"Note\"").And.NotContain("\"ChecklistItems\"");
        output.WriteLine(sql);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(24, 50)]
    [InlineData(2000, 50)]
    public async Task Ranking_ReadsDecryptedNamesAndGroupedCounts_InOneQueryWithoutWrites(int count, int logsPerHabit)
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
        var habits = Enumerable.Range(0, count).Select(i => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            user.Id, $"Private habit {i}", FrequencyUnit.Day, 1, DueDate: from)).Value).ToArray();
        foreach (var habit in habits)
        {
            for (var day = 0; day < logsPerHabit; day++)
                habit.Log(from.AddDays(day), advanceDueDate: false);
        }
        context.Habits.AddRange(habits);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTimeZoneAsync(user.Id, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var handler = new GetHabitRankingQueryHandler(new GenericRepository<Habit>(context), dates, CreateTokens(),
            Substitute.For<IProductAnalytics>(), NullLogger<GetHabitRankingQueryHandler>.Instance);
        counter.Reset();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        var result = await handler.Handle(new GetHabitRankingQuery(user.Id, from, from.AddDays(logsPerHabit),
            HabitIds: count <= 100 ? habits.Select(h => h.Id).ToArray() : null, PageSize: 2), default);
        stopwatch.Stop();
        output.WriteLine($"Habits={count}; logs={count * logsPerHabit}; ranking_ms={stopwatch.Elapsed.TotalMilliseconds:F2}; allocated_bytes={GC.GetTotalAllocatedBytes() - allocatedBefore}; returned_rows={counter.Commands.Sum(command => command.Rows)}");

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(count);
        result.Value.Rows.Should().HaveCount(2).And.OnlyContain(row => row.CompletedCount == logsPerHabit
            && row.Name.StartsWith("Private habit "));
        counter.SelectCommandCount.Should().Be(1);
        counter.Commands.Should().ContainSingle().Which.Sql.Should().Contain("GROUP BY").And.Contain("COUNT(");
        counter.Commands.Sum(command => command.Rows).Should().Be(count);
        context.ChangeTracker.HasChanges().Should().BeFalse();
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    private static HabitRankingTokenService CreateTokens() => new(Options.Create(new JwtSettings
    {
        SecretKey = "ranking-query-shape-test-signing-secret",
        Issuer = "test",
        Audience = "test"
    }));
}
