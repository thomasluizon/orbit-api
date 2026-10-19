using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public class HabitScheduleContractTests
{
    private static readonly DateOnly Start = new(2026, 4, 3);

    [Theory]
    [InlineData(HabitSchedule.OneTime)]
    [InlineData(HabitSchedule.Recurring)]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.General)]
    public async Task HabitResponses_AppendScheduleAndOriginalStartWithoutChangingLegacyFields(HabitSchedule type)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var user = User.Create("Alex", "alex@example.com").Value;
        var parent = Create(user.Id, type);
        var child = Create(user.Id, type, parent.Id);
        foreach (var habit in new[] { parent, child })
            for (var offset = 0; offset < (type == HabitSchedule.OneTime ? 1 : 10); offset++)
                habit.Log(Start.AddDays(offset)).IsSuccess.Should().BeTrue();
        context.Users.Add(user);
        context.Habits.AddRange(parent, child);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Start.AddDays(9));
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        var habits = new GenericRepository<Habit>(context);
        var logs = new GenericRepository<HabitLog>(context);
        var detail = await new GetHabitByIdQueryHandler(habits, logs, dates)
            .Handle(new GetHabitByIdQuery(user.Id, parent.Id), CancellationToken.None);
        detail.IsSuccess.Should().BeTrue();
        AssertContract(detail.Value, type);
        AssertContract(detail.Value.Children.Should().ContainSingle().Subject, type);

        var full = await new GetHabitFullDetailQueryHandler(habits, logs, new GenericRepository<User>(context), dates)
            .Handle(new GetHabitFullDetailQuery(user.Id, parent.Id), CancellationToken.None);
        full.IsSuccess.Should().BeTrue();
        AssertContract(full.Value.Habit, type);

        var schedule = await new GetHabitScheduleQueryHandler(habits,
                new HabitScheduleLogReader(context), new HabitSchedulePageLoader(context), dates,
                Substitute.For<IUnitOfWork>())
            .Handle(new GetHabitScheduleQuery(user.Id, Start, Start.AddDays(10),
                IsGeneral: type == HabitSchedule.General ? true : null), CancellationToken.None);
        schedule.IsSuccess.Should().BeTrue();
        var item = schedule.Value.Items.Should().ContainSingle().Subject;
        AssertContract(item, type);
        AssertContract(item.Children.Should().ContainSingle().Subject, type);

        if (type != HabitSchedule.General)
        {
            var calendar = await new GetCalendarMonthQueryHandler(habits, dates, Substitute.For<IUnitOfWork>())
                .Handle(new GetCalendarMonthQuery(user.Id, Start, Start.AddDays(10)), CancellationToken.None);
            calendar.IsSuccess.Should().BeTrue();
            var calendarItem = calendar.Value.Habits.Should().ContainSingle().Subject;
            AssertContract(calendarItem, type);
            AssertContract(calendarItem.Children.Should().ContainSingle().Subject, type);
        }

        var controller = new SyncController(context, Substitute.For<ILogger<SyncController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                }
            }
        };
        var changes = await controller.GetChangesV2(DateTime.UtcNow.AddMinutes(-1), CancellationToken.None);
        var sync = changes.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SyncController.SyncChangesV2Response>().Subject;
        sync.Habits.Updated.Should().HaveCount(2);
        foreach (var habit in sync.Habits.Updated)
            AssertContract(habit, type);
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void OpenApi_AppendsOptionalScheduleAndStartDateOnBothVersions(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "HabitDetailResponse", "HabitChildResponse", "HabitScheduleItem", "HabitScheduleChildItem", "SyncHabitDto" })
        {
            var schema = schemas.GetProperty(name);
            var required = schema.GetProperty("required").EnumerateArray().Select(item => item.GetString());
            required.Should().NotContain("schedule").And.NotContain("startDate");
            var properties = schema.GetProperty("properties");
            properties.GetProperty("schedule").GetProperty("oneOf").GetArrayLength().Should().Be(2);
            properties.GetProperty("startDate").GetProperty("format").GetString().Should().Be("date");
            properties.GetProperty("dueDate").GetProperty("type").GetString().Should().Be("string");
            properties.GetProperty("isGeneral").GetProperty("type").GetString().Should().Be("boolean");
            properties.GetProperty("isFlexible").GetProperty("type").GetString().Should().Be("boolean");
        }
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void OpenApi_HabitScheduleMatchesSerializedNamesAndFrequencyUnitPreservesMainSchema(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var schema = schemas.GetProperty("HabitSchedule");
        schema.GetProperty("type").GetString().Should().Be("string");
        schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("OneTime", "Recurring", "Flexible", "General");

        using var mainFrequencyUnit = JsonDocument.Parse("""{"type":"integer"}""");
        JsonElement.DeepEquals(schemas.GetProperty("FrequencyUnit"), mainFrequencyUnit.RootElement)
            .Should().BeTrue("FrequencyUnit must preserve the complete schema from main");
    }

    private static Habit Create(Guid userId, HabitSchedule type, Guid? parentId = null) =>
        Habit.Create(new HabitCreateParams(userId, "Habit",
            type is HabitSchedule.Recurring or HabitSchedule.Flexible ? FrequencyUnit.Day : null,
            type is HabitSchedule.Recurring or HabitSchedule.Flexible ? 1 : null, Start,
            IsGeneral: type == HabitSchedule.General, IsFlexible: type == HabitSchedule.Flexible,
            ParentHabitId: parentId)).Value;

    private static void AssertContract(object response, HabitSchedule type)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var json = JsonSerializer.SerializeToElement(response, options);
        json.GetProperty("schedule").GetString().Should().Be(type.ToString());
        json.GetProperty("startDate").GetString().Should().Be("2026-04-03");
        var legacy = JsonSerializer.Serialize(new
        {
            isGeneral = json.GetProperty("isGeneral").GetBoolean(),
            isFlexible = json.GetProperty("isFlexible").GetBoolean(),
            dueDate = json.GetProperty("dueDate").GetString()
        });
        legacy.Should().Be(type switch
        {
            HabitSchedule.General => """{"isGeneral":true,"isFlexible":false,"dueDate":"2026-04-03"}""",
            HabitSchedule.Flexible => """{"isGeneral":false,"isFlexible":true,"dueDate":"2026-04-03"}""",
            HabitSchedule.Recurring => """{"isGeneral":false,"isFlexible":false,"dueDate":"2026-04-13"}""",
            _ => """{"isGeneral":false,"isFlexible":false,"dueDate":"2026-04-03"}"""
        });
    }
}
