using System.Diagnostics;
using System.Text.Json;
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
using Orbit.Domain.Models;
using Orbit.Tests.Shared;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitRankingReadTests(ITestOutputHelper output)
{
    [Fact]
    public void Ranking_TranslatesGroupedCountsForPostgres()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=translation_only").Options);
        var from = new DateOnly(2024, 1, 1);
        var definition = HabitRankingDefinition.Create(Guid.NewGuid(), from, from.AddDays(30),
            "UTC", HabitRankingDefinition.CompletionLogRecords, [Guid.NewGuid()], 20).Value;
        var query = new HabitRankingReader(context, Substitute.For<IEncryptionService>()).BuildQuery(definition, 0);
        var sql = context.Database.SqlQuery<SqlShapeRow>(query).ToQueryString();
        sql.Should().Contain("GROUP BY").And.NotContain("DISTINCT").And.Contain("LEFT JOIN")
            .And.Contain("LIMIT").And.Contain("OFFSET").And.Contain("JOIN \"HabitLogs\"")
            .And.Contain("jsonb_array_elements_text")
            .And.NotContain("\"Note\"").And.NotContain("\"ChecklistItems\"");
        output.WriteLine(sql);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(24, 50)]
    [InlineData(2000, 50)]
    public async Task Ranking_ReadsDecryptedNamesAndGroupedCounts_InOneQueryWithoutWrites(int count, int logsPerHabit)
    {
        var configuredEncryption = CreateEncryption();
        var encryption = new MeasuringEncryption(configuredEncryption);
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(configuredEncryption, counter);
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
        var reader = Substitute.For<IHabitRankingReader>();
        var readMilliseconds = 0d;
        reader.ReadAsync(Arg.Any<HabitRankingDefinition>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var read = Stopwatch.StartNew();
                var page = await new HabitRankingReader(context, encryption).ReadAsync(
                    call.Arg<HabitRankingDefinition>(), call.Arg<int>(), call.Arg<CancellationToken>());
                readMilliseconds = read.Elapsed.TotalMilliseconds;
                return page;
            });
        var handler = new GetHabitRankingQueryHandler(reader, dates, CreateTokens(),
            Substitute.For<IProductAnalytics>(), NullLogger<GetHabitRankingQueryHandler>.Instance);
        counter.Reset();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        var result = await handler.Handle(new GetHabitRankingQuery(user.Id, from, from.AddDays(logsPerHabit),
            HabitIds: count <= 100 ? habits.Select(h => h.Id).ToArray() : null, PageSize: 2), default);
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
        var serialization = Stopwatch.StartNew();
        var json = JsonSerializer.SerializeToUtf8Bytes(result.Value);
        serialization.Stop();
        output.WriteLine($"Habits={count}; logs={count * logsPerHabit}; ranking_ms={stopwatch.Elapsed.TotalMilliseconds:F2}; read_ms={readMilliseconds:F2}; decrypt_ms={encryption.DecryptMilliseconds:F2}; decrypt_calls={encryption.DecryptCalls}; handler_other_ms={stopwatch.Elapsed.TotalMilliseconds - readMilliseconds:F2}; serialize_ms={serialization.Elapsed.TotalMilliseconds:F2}; json_bytes={json.Length}; allocated_bytes={allocated}; returned_rows={counter.Commands.Sum(command => command.Rows)}");
        foreach (var command in counter.Commands) output.WriteLine(command.Sql);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(count);
        result.Value.Rows.Should().HaveCount(2).And.OnlyContain(row => row.CompletedCount == logsPerHabit
            && row.Name.StartsWith("Private habit "));
        counter.CommandCount.Should().Be(1);
        counter.Commands.Should().ContainSingle().Which.Sql.Should().Contain("GROUP BY").And.Contain("COUNT(");
        counter.Commands.Sum(command => command.Rows).Should().Be(result.Value.Rows.Count);
        encryption.DecryptCalls.Should().Be(result.Value.Rows.Count);
        context.ChangeTracker.HasChanges().Should().BeFalse();
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    private sealed class SqlShapeRow
    {
        public Guid? HabitId { get; set; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ranking_MatchesReferenceAcrossPagesAndFilters(bool filtered)
    {
        var encryption = CreateEncryption();
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(encryption, counter);
        var db = factory.Context;
        var user = User.Create("Ranking user", "ranking@example.test").Value;
        var other = User.Create("Other user", "other@example.test").Value;
        db.Users.AddRange(user, other);
        var from = new DateOnly(2024, 1, 1);
        Habit Create(string name, HabitSchedule schedule = HabitSchedule.Recurring,
            bool bad = false, Guid? parent = null, Guid? owner = null, DateOnly? end = null) =>
            Habit.Create(new HabitCreateParams(schedule, owner ?? user.Id, name,
                schedule == HabitSchedule.OneTime ? null : FrequencyUnit.Day,
                schedule == HabitSchedule.OneTime ? null : schedule == HabitSchedule.Flexible ? 5 : 1, DueDate: from,
                Emoji: "\ud83c\udfc3", IsBadHabit: bad, ParentHabitId: parent, EndDate: end)).Value;
        var parent = Create("Parent");
        var child = Create("Child", parent: parent.Id);
        var flexible = Create("Flexible", HabitSchedule.Flexible);
        var ended = Create("Ended", end: from);
        var deleted = Create("Deleted");
        var undone = Create("Undone");
        var bad = Create("Bad", bad: true);
        var task = Create("Task", HabitSchedule.OneTime);
        var crossUser = Create("Someone else", owner: other.Id);
        var zero = Create("Zero", HabitSchedule.Flexible);
        var habits = new[] { parent, child, flexible, ended, deleted, undone, bad, task, crossUser, zero };
        foreach (var habit in habits.Where(h => h != zero)) habit.Log(from, advanceDueDate: habit == ended);
        child.Log(from.AddDays(1), advanceDueDate: false);
        flexible.Log(from, advanceDueDate: false);
        zero.SkipFlexible(from);
        flexible.Log(from.AddDays(1), advanceDueDate: false).Value.SoftDelete();
        parent.Log(from.AddDays(-1), advanceDueDate: false);
        parent.Log(from.AddDays(2), advanceDueDate: false);
        ended.IsCompleted.Should().BeTrue();
        deleted.SoftDelete();
        undone.Unlog(from);
        db.Habits.AddRange(habits);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var definition = HabitRankingDefinition.Create(user.Id, from, from.AddDays(2),
            "America/Sao_Paulo", HabitRankingDefinition.CompletionLogRecords,
            filtered ? [parent.Id, child.Id, ended.Id, zero.Id] : null, 2).Value;
        var reader = new HabitRankingReader(db, encryption);
        var reference = new InMemoryHabitRankingReader(habits);
        var all = new List<HabitRankingReadRow>();
        for (var offset = 0; ; offset += definition.PageSize)
        {
            counter.Reset();
            var expected = await reference.ReadAsync(definition, offset);
            var actual = await reader.ReadAsync(definition, offset);
            actual.Rows.Should().Equal(expected.Rows);
            actual.EligibleCount.Should().Be(expected.EligibleCount);
            actual.TotalCount.Should().Be(expected.TotalCount);
            counter.Commands.Should().ContainSingle().Which.Rows.Should().Be(Math.Max(1, actual.Rows.Count));
            all.AddRange(actual.Rows);
            if (offset + actual.Rows.Count >= actual.TotalCount) break;
        }
        all.Single(h => h.HabitId == parent.Id).CompletedCount.Should().Be(1);
        all.Single(h => h.HabitId == child.Id).ParentHabitId.Should().Be(parent.Id);
        if (!filtered) all.Single(h => h.HabitId == flexible.Id).CompletedCount.Should().Be(2);
        all.Should().Contain(h => h.HabitId == ended.Id);
        db.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task Ranking_CursorDetectsChangesOutsidePage_AndRefreshDoesNotRestoreDeletedTargets()
    {
        var encryption = CreateEncryption();
        using var factory = new SqliteOrbitDbContextFactory(encryption);
        var db = factory.Context;
        var user = User.Create("Ranking user", "cursor@example.test").Value;
        db.Users.Add(user);
        var from = new DateOnly(2024, 1, 1);
        var habits = Enumerable.Range(0, 4).Select(i => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            user.Id, $"Habit {i}", FrequencyUnit.Day, 1, DueDate: from)).Value).ToArray();
        foreach (var habit in habits) habit.Log(from, advanceDueDate: false);
        db.Habits.AddRange(habits);
        await db.SaveChangesAsync();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTimeZoneAsync(user.Id, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var handler = new GetHabitRankingQueryHandler(new HabitRankingReader(db, encryption), dates,
            CreateTokens(), Substitute.For<IProductAnalytics>(), NullLogger<GetHabitRankingQueryHandler>.Instance);
        var request = new GetHabitRankingQuery(user.Id, from, from.AddDays(2), PageSize: 1);
        var first = (await handler.Handle(request, default)).Value;
        var offPage = habits.Single(h => h.Id == habits.OrderBy(h => h.Id.ToString("D"), StringComparer.Ordinal).Last().Id);
        offPage.Update(new HabitUpdateParams(HabitSchedule.Recurring, "Changed name", null,
            FrequencyUnit.Day, 1, null, false, from)).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        var stale = await handler.Handle(new GetHabitRankingQuery(user.Id, QueryToken: first.QueryToken,
            Cursor: first.NextCursor), default);
        stale.ErrorCode.Should().Be("HABIT_RANKING_STALE");
        var refreshed = (await handler.Handle(new GetHabitRankingQuery(user.Id, QueryToken: first.QueryToken,
            Refresh: true), default)).Value;
        refreshed.Revision.Should().NotBe(first.Revision);
        refreshed.Query.Should().BeEquivalentTo(first.Query);
        db.HabitLogs.Add(offPage.Log(from.AddDays(2), advanceDueDate: false).Value);
        await db.SaveChangesAsync();
        var unchanged = (await handler.Handle(new GetHabitRankingQuery(user.Id, QueryToken: first.QueryToken,
            Cursor: refreshed.NextCursor), default)).Value;
        unchanged.Revision.Should().Be(refreshed.Revision);
        db.HabitLogs.Add(offPage.Log(from.AddDays(1), advanceDueDate: false).Value);
        await db.SaveChangesAsync();
        (await handler.Handle(new GetHabitRankingQuery(user.Id, QueryToken: first.QueryToken,
            Cursor: refreshed.NextCursor), default)).ErrorCode.Should().Be("HABIT_RANKING_STALE");

        var target = (await handler.Handle(request with { HabitIds = [offPage.Id] }, default)).Value;
        offPage.SoftDelete();
        await db.SaveChangesAsync();
        var removed = (await handler.Handle(new GetHabitRankingQuery(user.Id, QueryToken: target.QueryToken,
            Refresh: true), default)).Value;
        removed.Rows.Should().BeEmpty();
        removed.TotalCount.Should().Be(0);
        (await handler.Handle(request with { HabitIds = [offPage.Id] }, default)).IsFailure.Should().BeTrue();
    }

    private static EncryptionService CreateEncryption() => new(Options.Create(new EncryptionSettings
    {
        Key = Convert.ToBase64String(new byte[32])
    }), NullLogger<EncryptionService>.Instance);

    [Fact]
    public async Task Ranking_UsesItsEncryptionKey_WhenAnotherEncryptedModelIsCached()
    {
        using var first = new SqliteOrbitDbContextFactory(CreateEncryption());
        var encryption = new EncryptionService(Options.Create(new EncryptionSettings
        {
            Key = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray())
        }), NullLogger<EncryptionService>.Instance);
        using var second = new SqliteOrbitDbContextFactory(encryption);
        var user = User.Create("Ranking user", "keys@example.test").Value;
        var from = new DateOnly(2024, 1, 1);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id,
            "Different key", FrequencyUnit.Day, 1, DueDate: from)).Value;
        habit.Log(from, advanceDueDate: false);
        second.Context.Users.Add(user);
        second.Context.Habits.Add(habit);
        await second.Context.SaveChangesAsync();
        var definition = HabitRankingDefinition.Create(user.Id, from, from.AddDays(1), "UTC",
            HabitRankingDefinition.CompletionLogRecords, null, 20).Value;

        var page = await new HabitRankingReader(second.Context, encryption).ReadAsync(definition, 0);

        page.Rows.Should().ContainSingle().Which.Name.Should().Be("Different key");
    }

    private sealed class MeasuringEncryption(IEncryptionService inner) : IEncryptionService
    {
        internal int DecryptCalls { get; private set; }
        internal double DecryptMilliseconds { get; private set; }
        public bool IsConfigured => inner.IsConfigured;
        public string Encrypt(string value) => inner.Encrypt(value);
        public string? EncryptNullable(string? value) => inner.EncryptNullable(value);
        public string Decrypt(string value)
        {
            var clock = Stopwatch.StartNew();
            var result = inner.Decrypt(value);
            DecryptMilliseconds += clock.Elapsed.TotalMilliseconds;
            DecryptCalls++;
            return result;
        }
        public string? DecryptNullable(string? value) => value is null ? null : Decrypt(value);
    }

    private static HabitRankingTokenService CreateTokens() => new(Options.Create(new JwtSettings
    {
        SecretKey = "ranking-query-shape-test-signing-secret",
        Issuer = "test",
        Audience = "test"
    }));
}
