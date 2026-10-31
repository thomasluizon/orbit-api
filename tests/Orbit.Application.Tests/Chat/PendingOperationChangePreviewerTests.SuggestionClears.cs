using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed partial class PendingOperationChangePreviewerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SuggestionClears_AllBulkPreviewPathsBindVisibleRemovals(bool nested, bool placement)
    {
        var today = await _userDateService.GetUserTodayAsync(UserId);
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId,
            "Read", FrequencyUnit.Day, 1, today, EndDate: today.AddDays(30), Emoji: "📖")).Value;
        var parent = CreateHabit("Routine", null);
        Setup([existing, parent]);
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { existing, parent }.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToArray());
        var draft = new HabitSuggestionDraft(Guid.NewGuid(), existing.Emoji!, existing.Title,
            "Task", null, null, [], today, new(8, 5), new(8, 10), null, [], "Adjust the cadence.", existing.Id);
        var source = JsonNode.Parse(PreviewHabitSuggestionCommandHandler.Arguments(draft, existing).GetRawText())!;
        var items = new JsonArray(nested
            ? new JsonObject { ["title"] = "Routine", ["sub_habits"] = new JsonArray(source) } : source);
        if (placement)
            items.Add(new JsonObject { ["title"] = "Stretch", ["parent_habit_id"] = parent.Id.ToString() });
        var arguments = JsonSerializer.SerializeToElement(new JsonObject { ["habits"] = items });
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var tool = new BulkCreateHabitsTool(mediator);
        var previewer = new PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService, new AiToolRegistry([tool]));

        var preview = (await previewer.PreviewAsync(UserId, tool.Name, arguments,
            clientContext: new(SupportsPendingOperationPlacement: placement))).Value!;

        preview.Items.Should().NotBeNull();
        if (nested)
        {
            var children = preview.Items![0].Fields.Single(field => field.Field == "sub_habits");
            using var previous = JsonDocument.Parse(children.OldValue!);
            using var proposed = JsonDocument.Parse(children.NewValue!);
            previous.RootElement[0].GetProperty("end_date").GetString().Should().Be(today.AddDays(30).ToString("yyyy-MM-dd"));
            previous.RootElement[0].GetProperty("frequency_unit").GetString().Should().Be("Day");
            proposed.RootElement[0].GetProperty("end_date").ValueKind.Should().Be(JsonValueKind.Null);
            proposed.RootElement[0].GetProperty("frequency_unit").ValueKind.Should().Be(JsonValueKind.Null);
        }
        else
        {
            var fields = preview.Items![0].Fields;
            fields.Should().ContainSingle(field => field.Field == "end_date" && field.OldValue == today.AddDays(30).ToString("yyyy-MM-dd") && field.NewValue == null);
            fields.Should().ContainSingle(field => field.Field == "frequency_unit" && field.OldValue == "Day" && field.NewValue == null);
        }
        var changed = JsonNode.Parse(arguments.GetRawText())!;
        var target = nested ? changed["habits"]![0]!["sub_habits"]![0]! : changed["habits"]![0]!;
        target["clear_fields"] = new JsonArray("frequency_unit");
        var preserved = (await previewer.PreviewAsync(UserId, tool.Name, JsonSerializer.SerializeToElement(changed),
            clientContext: new(SupportsPendingOperationPlacement: placement))).Value!;
        preserved.PreviewFingerprint.Should().NotBe(preview.PreviewFingerprint);
        preserved.Items![0].StateFingerprint.Should().NotBe(preview.Items![0].StateFingerprint);
        (await tool.CheckArgumentsAsync(arguments, UserId, default)).IsSuccess.Should().BeTrue();
        target["clear_fields"] = new JsonArray("title");
        (await tool.CheckArgumentsAsync(JsonSerializer.SerializeToElement(changed), UserId, default)).IsFailure.Should().BeTrue();
        target["clear_fields"] = new JsonArray("end_date");
        target["end_date"] = today.AddDays(60).ToString("yyyy-MM-dd");
        (await tool.CheckArgumentsAsync(JsonSerializer.SerializeToElement(changed), UserId, default)).IsFailure.Should().BeTrue();
    }
}
