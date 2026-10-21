using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Validators;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Chat.Tools;

public class CapabilityParityToolTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UpdateChecklistTemplate_DispatchesPartialUpdate(bool rename, bool replaceItems)
    {
        var userId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<UpdateChecklistTemplateCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await new UpdateChecklistTemplateTool(mediator).ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                template_id = templateId,
                name = rename ? "Evening" : null,
                items = replaceItems ? new[] { "Read" } : null
            }), userId, default);
        result.Success.Should().BeTrue(result.Error);
        await mediator.Received().Send(Arg.Is<UpdateChecklistTemplateCommand>(c => c.UserId == userId
            && c.TemplateId == templateId && c.Name == (rename ? "Evening" : null)
            && (replaceItems ? c.Items!.SequenceEqual(new[] { "Read" }) : c.Items == null)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetName_DispatchesExistingCommand()
    {
        var userId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetNameCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await new UpdateProfilePreferencesTool(mediator).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { action = "set_name", name = "Alex" }), userId, default);
        result.Success.Should().BeTrue(result.Error);
        await mediator.Received().Send(Arg.Is<SetNameCommand>(c => c.UserId == userId && c.Name == "Alex"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetSelectedCalendars_DispatchesSelectionIncludingReset(bool reset)
    {
        var userId = Guid.NewGuid();
        string[] ids = reset ? [] : ["primary", "work@example.com"];
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetSelectedCalendarsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await new ManageCalendarSyncTool(mediator).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { action = "set_selected_calendars", calendar_ids = ids }), userId, default);
        result.Success.Should().BeTrue(result.Error);
        await mediator.Received().Send(Arg.Is<SetSelectedCalendarsCommand>(c => c.UserId == userId && c.CalendarIds.SequenceEqual(ids)), Arg.Any<CancellationToken>());
    }
    [Theory]
    [InlineData("update_checklist_template")]
    [InlineData("update_profile_preferences")]
    [InlineData("manage_calendar_sync")]
    public async Task ArgumentCheck_ValidatesWithoutDispatchingWrite(string operation)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        IAiTool tool = operation switch
        {
            "update_checklist_template" => new UpdateChecklistTemplateTool(mediator),
            "update_profile_preferences" => new UpdateProfilePreferencesTool(mediator),
            _ => new ManageCalendarSyncTool(mediator)
        };
        var args = operation switch
        {
            "update_checklist_template" => JsonSerializer.SerializeToElement(new { template_id = Guid.NewGuid(), name = "Evening" }),
            "update_profile_preferences" => JsonSerializer.SerializeToElement(new { action = "set_name", name = "Alex" }),
            _ => JsonSerializer.SerializeToElement(new { action = "set_selected_calendars", calendar_ids = Array.Empty<string>() })
        };
        var result = await ((IArgumentCheckTool)tool).CheckArgumentsAsync(args, Guid.NewGuid(), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        mediator.ReceivedCalls().Should().ContainSingle().Which.GetArguments()[0].Should().BeOfType<CheckChatCommandQuery>();
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task SetName_ArgumentCheckUsesCommandValidatorBeforePreview(int length, bool valid)
    {
        using var context = new HeldWriteTestContext();
        var name = new string('a', length);
        var validation = new SetNameCommandValidator().Validate(new SetNameCommand(context.UserId, name));
        var result = await ((IArgumentCheckTool)context.Tool("update_profile_preferences")).CheckArgumentsAsync(
            JsonSerializer.SerializeToElement(new { action = "set_name", name }), context.UserId, default);

        result.IsSuccess.Should().Be(valid).And.Be(validation.IsValid);
        if (!valid)
            result.ErrorCode.Should().Be(ValidationErrorCodes.ProfileNameLength)
                .And.Be(validation.Errors.Single().ErrorCode);
        context.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, ValidationErrorCodes.TemplateItemLimit)]
    [InlineData(false, ValidationErrorCodes.TemplateItemLength)]
    public async Task UpdateChecklistTemplate_ArgumentCheckPreservesCommandValidationCode(bool tooMany, string code)
    {
        using var context = new HeldWriteTestContext();
        var template = ChecklistTemplate.Create(context.UserId, "Morning", ["Water"]).Value;
        context.Records.Add(template);
        var items = tooMany ? Enumerable.Repeat("Item", AppConstants.MaxChecklistItems + 1).ToArray()
            : [new string('a', AppConstants.MaxChecklistItemTextLength + 1)];
        var validation = new UpdateChecklistTemplateCommandValidator().Validate(
            new UpdateChecklistTemplateCommand(context.UserId, template.Id, Items: items));

        var result = await ((IArgumentCheckTool)context.Tool("update_checklist_template")).CheckArgumentsAsync(
            JsonSerializer.SerializeToElement(new { template_id = template.Id, items }), context.UserId, default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(code).And.Be(validation.Errors.Single().ErrorCode);
        context.Commands.Should().BeEmpty();
        template.Items.Should().Equal("Water");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"calendar_ids\":null}")]
    [InlineData("{\"calendar_ids\":[3]}")]
    [InlineData("{\"calendar_ids\":[\" \"]}")]
    public async Task Selection_RejectsMalformedInputInsteadOfResetting(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        values["action"] = JsonSerializer.SerializeToElement("set_selected_calendars");
        var mediator = Substitute.For<IMediator>();
        var result = await new ManageCalendarSyncTool(mediator).ExecuteAsync(JsonSerializer.SerializeToElement(values), Guid.NewGuid(), default);
        result.Success.Should().BeFalse();
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Overview_ExposesSelectedAndUnselectedCalendars()
    {
        var mediator = Substitute.For<IMediator>();
        var calendars = new List<UserCalendarItem>
        {
            new("primary", "Personal", "owner", true, null, true),
            new("work", "Work", "owner", false, null, false)
        };
        mediator.Send(Arg.Any<GetUserCalendarsQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(calendars));
        var result = await new GetCalendarOverviewTool(mediator).ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            include_events = false,
            include_auto_sync_state = false,
            include_suggestions = false
        }), Guid.NewGuid(), default);
        result.Success.Should().BeTrue();
        result.Payload.Should().BeOfType<CalendarOverviewPayload>().Which.Calendars.Should().Equal(calendars);
    }

}
