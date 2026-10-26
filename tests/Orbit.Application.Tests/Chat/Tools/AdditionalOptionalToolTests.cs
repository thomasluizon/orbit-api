using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Commands;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Notifications.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class AdditionalOptionalToolTests
{
    public static IEnumerable<object[]> OptionalActions()
    {
        (Type Type, string Json)[] cases =
        [
            (typeof(ManageAccountTool), """{"action":"confirm_deletion","code":"000000"}"""),
            (typeof(ManageSubscriptionTool), """{"action":"create_checkout","interval":"monthly"}"""),
            (typeof(ManageApiKeysTool), """{"action":"create","name":"Key","scopes":["read_habits"],"is_read_only":false}"""),
            (typeof(ManageFeatureFlagsTool), """{"action":"create","key":"test","description":"Test","emails":["reader@example.com"]}"""),
            (typeof(UpdateNotificationsTool), """{"action":"subscribe_push","endpoint":"https://example.com/push","p256dh":"key","auth":"secret"}"""),
            (typeof(DeleteNotificationsTool), """{"action":"delete_all"}"""),
            (typeof(UpdateProfilePreferencesTool), """{"action":"set_name","name":"Reader"}"""),
            (typeof(ManageCalendarSyncTool), """{"action":"set_selected_calendars","calendar_ids":["primary"]}"""),
            (typeof(UpdateChecklistTemplateTool), """{"template_id":"11111111-1111-1111-1111-111111111111","name":"Read","items":["Read"]}""")
        ];
        foreach (var (type, json) in cases)
            foreach (var row in OptionalParameterCases.Fields(type, ""))
                yield return [type, json, row[0]];
    }

    [Theory]
    [MemberData(nameof(OptionalActions))]
    public async Task OptionalNull_ActionDispatchesTheSameCommandOrErrorAsAbsent(Type type, string json, string field)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        mediator.Send(Arg.Any<SetNameCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        mediator.Send(Arg.Any<SetSelectedCalendarsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        mediator.Send(Arg.Any<UpdateChecklistTemplateCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        mediator.Send(Arg.Any<ManageFeatureFlagsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AdminFeatureFlag("test", "Test", false, null, [])));
        using var context = new HeldWriteTestContext();
        context.Mediator.Send(Arg.Any<DeleteAllNotificationsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var tool = type == typeof(ManageFeatureFlagsTool) || type == typeof(UpdateProfilePreferencesTool)
            || type == typeof(ManageCalendarSyncTool) || type == typeof(UpdateChecklistTemplateTool)
                ? (IAiTool)Activator.CreateInstance(type, mediator)!
                : (IAiTool)Activator.CreateInstance(type, context.Mediator)!;
        var args = JsonNode.Parse(json)!.AsObject();
        args.Remove(field);
        var absent = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(args), context.UserId, default);
        var commands = context.Commands.Select(command => JsonSerializer.Serialize(command, command.GetType())).ToArray();
        var dispatched = mediator.ReceivedCalls().Select(call => call.GetArguments()[0])
            .Where(command => command is not null).Select(command => JsonSerializer.Serialize(command, command!.GetType())).ToArray();
        context.Commands.Clear();
        mediator.ClearReceivedCalls();
        args[field] = null;
        var explicitNull = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(args), context.UserId, default);
        explicitNull.Should().BeEquivalentTo(absent);
        context.Commands.Select(command => JsonSerializer.Serialize(command, command.GetType())).Should().Equal(commands);
        mediator.ReceivedCalls().Select(call => call.GetArguments()[0]).Where(command => command is not null)
            .Select(command => JsonSerializer.Serialize(command, command!.GetType())).Should().Equal(dispatched);
        if (tool is IArgumentCheckTool check)
        {
            args.Remove(field);
            var absentCheck = await check.CheckArgumentsAsync(JsonSerializer.SerializeToElement(args), context.UserId, default);
            args[field] = null;
            var nullCheck = await check.CheckArgumentsAsync(JsonSerializer.SerializeToElement(args), context.UserId, default);
            nullCheck.Should().BeEquivalentTo(absentCheck);
        }
    }

    [Theory]
    [InlineData(typeof(GetApiKeysTool))]
    [InlineData(typeof(GetReferralCodeTool))]
    [InlineData(typeof(GetReferralOverviewTool))]
    [InlineData(typeof(CreateChecklistTemplateTool))]
    [InlineData(typeof(DeleteChecklistTemplateTool))]
    [InlineData(typeof(SetProactiveAstraTool))]
    [InlineData(typeof(SendSupportRequestTool))]
    public void ToolsWithoutOptionalFields_HaveNoNullVersusAbsentCases(Type type)
    {
        OptionalParameterCases.Fields(type, "").Should().BeEmpty();
    }
}
