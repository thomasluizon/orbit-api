using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Server;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class McpGatewayErrorTests
{
    private static readonly (Type Family, string[] Methods)[] GatewayTools =
    [
        (typeof(AccountTools), [nameof(AccountTools.ManageAccount)]),
        (typeof(ApiKeyTools), [nameof(ApiKeyTools.GetApiKeys), nameof(ApiKeyTools.ManageApiKeys)]),
        (typeof(CalendarTools), [nameof(CalendarTools.SetSelectedCalendars), nameof(CalendarTools.ManageCalendarSync)]),
        (typeof(ChecklistTemplateTools), [nameof(ChecklistTemplateTools.CreateChecklistTemplate),
            nameof(ChecklistTemplateTools.UpdateChecklistTemplate), nameof(ChecklistTemplateTools.DeleteChecklistTemplate)]),
        (typeof(GoalTools), [nameof(GoalTools.CreateGoal), nameof(GoalTools.UpdateGoal), nameof(GoalTools.DeleteGoal),
            nameof(GoalTools.UpdateGoalProgress), nameof(GoalTools.UpdateGoalStatus), nameof(GoalTools.ReorderGoals),
            nameof(GoalTools.LinkHabitsToGoal)]),
        (typeof(HabitTools), [nameof(HabitTools.CreateHabit), nameof(HabitTools.UpdateHabit), nameof(HabitTools.DeleteHabit),
            nameof(HabitTools.LogHabit), nameof(HabitTools.UnlogHabit), nameof(HabitTools.SkipHabit),
            nameof(HabitTools.UndoSkipHabit), nameof(HabitTools.UpdateChecklist), nameof(HabitTools.CreateSubHabit),
            nameof(HabitTools.DuplicateHabit), nameof(HabitTools.BulkCreateHabits), nameof(HabitTools.BulkDeleteHabits),
            nameof(HabitTools.BulkUpdateHabits), nameof(HabitTools.BulkUpdateHabitEmojis), nameof(HabitTools.BulkRescheduleHabits),
            nameof(HabitTools.BulkLogHabits), nameof(HabitTools.BulkSkipHabits), nameof(HabitTools.ReorderHabits),
            nameof(HabitTools.MoveHabitParent), nameof(HabitTools.LinkGoalsToHabit)]),
        (typeof(NotificationTools), [nameof(NotificationTools.MarkNotificationRead), nameof(NotificationTools.MarkAllNotificationsRead),
            nameof(NotificationTools.SubscribePush), nameof(NotificationTools.UnsubscribePush), nameof(NotificationTools.TestPush),
            nameof(NotificationTools.DeleteNotification)]),
        (typeof(ProfileTools), [nameof(ProfileTools.SetName), nameof(ProfileTools.SetTimezone), nameof(ProfileTools.SetLanguage),
            nameof(ProfileTools.SetAiSummary), nameof(ProfileTools.SetProactiveAstra), nameof(ProfileTools.SetWeekStartDay),
            nameof(ProfileTools.SetClockFormat)]),
        (typeof(SubscriptionTools), [nameof(SubscriptionTools.ManageSubscription)]),
        (typeof(SupportTools), [nameof(SupportTools.SendSupportRequest)]),
        (typeof(TagTools), [nameof(TagTools.CreateTag), nameof(TagTools.UpdateTag), nameof(TagTools.DeleteTag), nameof(TagTools.AssignTags)])
    ];

    public static IEnumerable<object[]> ErrorCases() => GatewayTools.SelectMany(family => family.Methods
        .SelectMany(method => new[] { false, true }.Select(json => new object[] { family.Family, method, json })));

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task GatewayInputFailure_IsFriendlySdkError(Type family, string method, bool invalidJson)
    {
        var executor = Substitute.For<IAgentOperationExecutor>();
        Exception error = invalidJson ? new JsonException("Invalid operation document")
            : new ArgumentException("Invalid operation arguments");
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AgentExecuteOperationResponse>(error));
        var bridge = new McpExecutorBridge(executor);
        var target = Activator.CreateInstance(family, family.GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(McpExecutorBridge) ? (object)bridge
                : Substitute.For([parameter.ParameterType], [])).ToArray())!;
        var arguments = family.GetMethod(method)!.GetParameters()
            .Where(parameter => !parameter.HasDefaultValue && parameter.ParameterType != typeof(System.Security.Claims.ClaimsPrincipal))
            .ToDictionary(parameter => parameter.Name!, RequiredArgument);

        var result = await McpInputSafetyTests.Invoke(target, method, arguments);

        result.IsError.Should().BeTrue();
        result.AsText().Should().Be(invalidJson ? "Invalid JSON input: " + error.Message : error.Message);
        await executor.Received(1).ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
    }

    private static object? RequiredArgument(ParameterInfo parameter)
    {
        if (parameter.ParameterType == typeof(string[]))
            return new[] { "primary" };
        if (parameter.ParameterType != typeof(string))
            return Activator.CreateInstance(parameter.ParameterType);
        if (parameter.Name!.EndsWith("Id", StringComparison.Ordinal) || parameter.Name.EndsWith("Ids", StringComparison.Ordinal))
            return "f387e12f-445f-452a-a31a-cd8ee66aa561";
        return parameter.Name switch
        {
            "positionsJson" or "checklistItemsJson" or "habitsJson" => "[]",
            "filterJson" or "updatesJson" => "{}",
            "dueDate" => "2026-04-03",
            _ => "test"
        };
    }
}
