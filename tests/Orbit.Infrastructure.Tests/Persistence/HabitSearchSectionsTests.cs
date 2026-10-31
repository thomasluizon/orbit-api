using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitSearchSectionsTests
{
    private static readonly DateOnly Today = new(2026, 6, 30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_OrdersInSqlBeforePaging_WithStableTiesAndHistoricalCompletions(bool dated)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var db = factory.Context;
        var user = User.Create("Search", "search-pages@example.com").Value;
        var olderDate = Today.AddDays(dated ? -4 : -400);
        var newerDate = Today.AddDays(dated ? -3 : -300);
        var older = Task(user, "Search older", olderDate, -5);
        var newer = Task(user, "Search newer", newerDate, -10);
        older.Log(olderDate).IsSuccess.Should().BeTrue();
        newer.Log(newerDate).IsSuccess.Should().BeTrue();
        var openA = Task(user, "Search open A", Today, 1);
        var openB = Task(user, "Search open B", Today, 1);
        var undated = Task(user, "Search undated", Today, -100);
        undated.Log(Today).Value.SoftDelete();
        var createdAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Users.Add(user);
        db.Habits.AddRange(older, newer, openA, openB, undated);
        foreach (var habit in new[] { older, newer, openA, openB, undated })
            db.Entry(habit).Property(h => h.CreatedAtUtc).CurrentValue = createdAtUtc;
        var foreignUser = User.Create("Other", "search-other@example.com").Value;
        db.Users.Add(foreignUser);
        db.Habits.Add(Task(foreignUser, "Search foreign", Today, -100));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handler = Handler(db, Dates(user.Id));
        var query = new GetHabitScheduleQuery(user.Id,
            dated ? Today.AddDays(-7) : null, dated ? Today : null, Search: "Search", PageSize: 2);
        counter.Reset();
        var first = await handler.Handle(query, default);
        var second = await handler.Handle(query with { Page = 2 }, default);
        var repeated = await handler.Handle(query, default);
        var third = await handler.Handle(query with { Page = 3 }, default);

        first.IsSuccess.Should().BeTrue();
        first.Value.TotalCount.Should().Be(5);
        var openIds = new[] { openA.Id, openB.Id }.OrderBy(id => id).ToArray();
        first.Value.Items.Select(item => item.Id).Should().Equal(openIds);
        repeated.Value.Items.Select(item => item.Id).Should().Equal(openIds);
        first.Value.Items.Should().OnlyContain(item => item.CompletedOn == null && item.IsSkippedOnDate == false);
        second.Value.Items.Select(item => item.Id).Should().Equal(newer.Id, older.Id);
        second.Value.Items.Select(item => item.CompletedOn).Should()
            .Equal(newerDate, olderDate);
        third.Value.Items.Should().ContainSingle().Which.CompletedOn.Should().BeNull();
        var pageSql = counter.Commands.Where(command => command.Sql.Contains("LIMIT")
            && command.Sql.Contains("ORDER BY") && command.Sql.Contains("MAX(")).ToList();
        pageSql.Should().HaveCount(4);
        pageSql.Take(3).Should().OnlyContain(command => command.Rows == 2);
        foreach (var command in pageSql)
            command.Sql.IndexOf("ORDER BY", StringComparison.Ordinal).Should()
                .BeLessThan(command.Sql.IndexOf("LIMIT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_UsesAccountLocalDay_AndReportsSkipReceiptsAndChildCompletion()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Search", "search-fields@example.com").Value;
        user.SetTimeZone("America/Sao_Paulo");
        var users = new GenericRepository<User>(db);
        var utcInstant = new DateTimeOffset(2026, 7, 1, 1, 30, 0, TimeSpan.Zero);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var dates = new UserDateService(users, cache, new FixedClock(utcInstant), Substitute.For<IUnitOfWork>());
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var localDay = await dates.GetUserTodayAsync(user.Id);
        localDay.Should().Be(Today);
        var completed = Task(user, "Search completed", Today.AddDays(-5), 1);
        var completion = completed.Log(localDay).Value;
        var open = Task(user, "Search open", Today, 2);
        var child = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user.Id,
            "Child", null, null, DueDate: Today.AddDays(-300), ParentHabitId: open.Id)).Value;
        child.Log(Today.AddDays(-300)).IsSuccess.Should().BeTrue();
        var skipped = Task(user, "Search skipped", Today, 3);
        var receipt = HabitSkipUndo.Create(Guid.NewGuid(), skipped).Value;
        skipped.PostponeTo(Today.AddDays(1));
        receipt.Seal(skipped, null, "[]");
        db.Habits.AddRange(completed, open, skipped, child);
        db.HabitSkipUndos.Add(receipt);
        db.Entry(completion).Property(log => log.CreatedAtUtc).CurrentValue = utcInstant.UtcDateTime;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handler = Handler(db, dates);
        var result = await handler.Handle(new GetHabitScheduleQuery(user.Id, Search: "Search"), default);
        var items = result.Value.Items.ToDictionary(item => item.Id);
        items[completed.Id].CompletedOn.Should().Be(Today);
        items[completed.Id].IsSkippedOnDate.Should().BeFalse();
        items[open.Id].CompletedOn.Should().BeNull();
        items[open.Id].IsSkippedOnDate.Should().BeFalse();
        items[open.Id].Children.Should().ContainSingle().Which.CompletedOn.Should().Be(Today.AddDays(-300));
        items[skipped.Id].CompletedOn.Should().BeNull();
        items[skipped.Id].IsSkippedOnDate.Should().BeTrue();
        var json = JsonSerializer.SerializeToElement(items[completed.Id], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("completedOn").GetString().Should().Be("2026-06-30");
        json.GetProperty("isSkippedOnDate").GetBoolean().Should().BeFalse();
        var otherDay = await handler.Handle(new GetHabitScheduleQuery(user.Id,
            Today.AddDays(1), Today.AddDays(1), Search: "Search"), default);
        otherDay.Value.Items.Single(item => item.Id == skipped.Id).IsSkippedOnDate.Should().BeFalse();
    }

    [Fact]
    public async Task Search_McpAndAstraUseTheSameSqlOrderingAndPaging()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var db = factory.Context;
        var user = User.Create("Search", "search-tools@example.com").Value;
        var older = Task(user, "Search older", Today, -10);
        var newer = Task(user, "Search newer", Today, -20);
        older.Log(Today.AddDays(-1)).IsSuccess.Should().BeTrue();
        newer.Log(Today).IsSuccess.Should().BeTrue();
        var open = Task(user, "Search open", Today, 1);
        db.Users.Add(user);
        db.Habits.AddRange(older, newer, open);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var dates = Dates(user.Id);
        var handler = Handler(db, dates);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<GetHabitScheduleQuery>(), default));
        var mcp = new HabitTools(mediator, dates, new McpExecutorBridge(Substitute.For<IAgentOperationExecutor>()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"));
        counter.Reset();
        var mcpText = await mcp.ListHabits(principal, Today.AddDays(-1).ToString("yyyy-MM-dd"),
            Today.ToString("yyyy-MM-dd"), search: "Search", page: 2, pageSize: 1);
        mcpText.Should().Contain(newer.Title).And.NotContain(older.Title).And.NotContain(open.Title);
        var mcpSql = counter.Commands.Single(command => command.Sql.Contains("LIMIT") && command.Sql.Contains("MAX(")).Sql;
        var astra = new QueryHabitsTool(new GenericRepository<Habit>(db), new GenericRepository<User>(db),
            dates, new HabitScheduleLogReader(db));
        counter.Reset();
        var chat = await astra.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            search = "Search",
            date = Today.ToString("yyyy-MM-dd"),
            offset = 1,
            limit = 1
        }), user.Id, default);
        chat.Payload.Should().BeOfType<HabitListCard>().Which.Items.Should().ContainSingle()
            .Which.Id.Should().Be(newer.Id.ToString());
        chat.EntityName.Should().Contain(newer.Title).And.NotContain(open.Title);
        var astraSql = counter.Commands.Single(command => command.Sql.Contains("LIMIT") && command.Sql.Contains("MAX(")).Sql;
        astraSql[astraSql.IndexOf("ORDER BY", StringComparison.Ordinal)..].Should()
            .Be(mcpSql[mcpSql.IndexOf("ORDER BY", StringComparison.Ordinal)..]);
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void SearchContract_AddsOptionalNullableFields(string artifact)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", artifact));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "HabitScheduleItem", "HabitScheduleChildItem" })
        {
            var schema = schemas.GetProperty(name);
            var properties = schema.GetProperty("properties");
            properties.GetProperty("completedOn").GetProperty("format").GetString().Should().Be("date");
            properties.GetProperty("completedOn").GetProperty("type").EnumerateArray()
                .Select(type => type.GetString()).Should().BeEquivalentTo("null", "string");
            properties.GetProperty("isSkippedOnDate").GetProperty("type").EnumerateArray()
                .Select(type => type.GetString()).Should().BeEquivalentTo("null", "boolean");
            if (schema.TryGetProperty("required", out var required))
                required.EnumerateArray().Select(field => field.GetString())
                    .Should().NotContain("completedOn").And.NotContain("isSkippedOnDate");
        }
    }

    private static Habit Task(User user, string title, DateOnly due, int position)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user.Id, title, null, null, DueDate: due)).Value;
        habit.SetPosition(position);
        return habit;
    }

    private static IUserDateService Dates(Guid userId)
    {
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(userId, Arg.Any<CancellationToken>()).Returns(1);
        return dates;
    }

    private static GetHabitScheduleQueryHandler Handler(OrbitDbContext db, IUserDateService dates) =>
        new(new GenericRepository<Habit>(db), new HabitScheduleLogReader(db), new HabitSchedulePageLoader(db),
            dates, Substitute.For<IUnitOfWork>(), Substitute.For<IRepeatReminderReader>());

    private sealed class FixedClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
