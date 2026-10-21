using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Tests.Controllers;

public class HabitTagUpdateContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData(",\"tagIds\":null")]
    [InlineData(",\"tagIds\":[]")]
    [InlineData(",\"tagIds\":[\"11111111-1111-1111-1111-111111111111\"]")]
    public async Task Update_DeserializesAndMapsOptionalTagsWithoutChangingNoContentContract(string tags)
    {
        var json = $$"""
            {"title":"Read","description":"Details","frequencyUnit":"Day","frequencyQuantity":1,
             "days":["Monday"],"isBadHabit":false,"dueDate":"2026-04-06","dueTime":"09:00:00","dueEndTime":"10:00:00",
             "reminderEnabled":true,"reminderTimes":[15],"scheduledReminders":[{"when":"day_before","time":"08:00:00"}],
             "slipAlertEnabled":false,"checklistItems":[{"text":"Same","isChecked":true},{"text":"Other","isChecked":false},{"text":"Same","isChecked":false}],
             "isGeneral":false,"endDate":"2026-04-30","clearEndDate":false,"isFlexible":false,
             "goalIds":["22222222-2222-2222-2222-222222222222"],"emoji":"book","intervalWeeks":2,
             "relativeReminders":[{"minutesBefore":30,"when":null,"time":null}],"startDate":"2026-04-06"{{tags}}}
            """;
        var options = ReminderInputContractTests.MvcOptions();
        var request = JsonSerializer.Deserialize<HabitsController.UpdateHabitRequest>(json, options)!;
        var sender = Substitute.For<IMediator>();
        UpdateHabitCommand? actual = null;
        sender.Send(Arg.Any<UpdateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            actual = call.Arg<UpdateHabitCommand>();
            return Result.Success();
        });
        var userId = Guid.NewGuid();
        var habitId = Guid.NewGuid();
        var controller = Controller(sender, userId);
        using var cancellation = new CancellationTokenSource();

        var response = await controller.UpdateHabit(habitId, request, cancellation.Token);

        response.Should().BeOfType<NoContentResult>().Which.StatusCode.Should().Be(204);
        var expected = new UpdateHabitCommand(userId, habitId, "Read", "Details", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 4, 6), IsGeneral: false, ClearEndDate: false,
            Options: new(Days: [DayOfWeek.Monday], DueTime: new TimeOnly(9, 0), DueEndTime: new TimeOnly(10, 0),
                ReminderEnabled: true, ReminderTimes: [15], SlipAlertEnabled: false,
                ChecklistItems: [new("Same", true), new("Other", false), new("Same", false)],
                ScheduledReminders: [new(ScheduledReminderWhen.DayBefore, new TimeOnly(8, 0))],
                EndDate: new DateOnly(2026, 4, 30), IsFlexible: false, RelativeReminders: [new(30)]),
            GoalIds: [Guid.Parse("22222222-2222-2222-2222-222222222222")], Emoji: "book", IntervalWeeks: 2,
            StartDate: new DateOnly(2026, 4, 6), TagIds: request.TagIds);
        await sender.Received(1).Send(Arg.Any<UpdateHabitCommand>(), cancellation.Token);
        actual.Should().BeEquivalentTo(expected, config => config.WithStrictOrdering()
            .ComparingByMembers<UpdateHabitCommand>().ComparingByMembers<UpdateHabitCommandOptions>());
        if (tags.Contains("11111111", StringComparison.Ordinal))
            actual!.TagIds.Should().Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        else if (tags.Contains("[]", StringComparison.Ordinal))
            actual!.TagIds.Should().BeEmpty();
        else
            actual!.TagIds.Should().BeNull();
    }

    [Fact]
    public void Update_MalformedTagId_IsRejectedByActualMvcSerializer()
    {
        const string json = """{"title":"Read","tagIds":["invalid"]}""";
        var deserialize = () => JsonSerializer.Deserialize<HabitsController.UpdateHabitRequest>(json, ReminderInputContractTests.MvcOptions());
        deserialize.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task Update_UnresolvedAssociation_RetainsNotFoundErrorContract()
    {
        var sender = Substitute.For<IMediator>();
        sender.Send(Arg.Any<UpdateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Failure(ErrorMessages.TagNotFound));
        var result = await Controller(sender, Guid.NewGuid()).UpdateHabit(Guid.NewGuid(),
            new("Read", null, null, null, TagIds: [Guid.NewGuid()]), CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void PublishedRequest_AppendsOptionalNullableUuidArray(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("UpdateHabitRequest");
        schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().NotContain("tagIds");
        var tags = schema.GetProperty("properties").GetProperty("tagIds");
        tags.GetProperty("type").EnumerateArray().Select(value => value.GetString()).Should().BeEquivalentTo("array", "null");
        tags.GetProperty("items").GetProperty("type").GetString().Should().Be("string");
        tags.GetProperty("items").GetProperty("format").GetString().Should().Be("uuid");
    }

    private static HabitsController Controller(IMediator sender, Guid userId) =>
        new(sender, Substitute.For<ILogger<HabitsController>>(), Substitute.For<IUserDateService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"))
                }
            }
        };
}
