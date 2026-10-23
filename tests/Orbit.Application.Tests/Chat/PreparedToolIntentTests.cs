using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat;

public class PreparedToolIntentTests
{
    public static IEnumerable<object[]> IntentCases() =>
    [
        ["manage_calendar_sync", """{"action":"set_auto_sync","enabled":true}""", """{"action":"set_auto_sync","enabled":false}"""],
        ["manage_calendar_sync", """{"action":"set_selected_calendars","calendar_ids":["work"]}""", """{"action":"set_selected_calendars","calendar_ids":["personal"]}"""],
        ["manage_api_keys", """{"action":"create","name":"Review","scopes":["read_habits"]}""", """{"action":"create","name":"Review","scopes":["read_habits"],"is_read_only":true}"""],
        ["manage_account", """{"action":"request_deletion"}""", """{"action":"reset_account"}"""],
        ["manage_subscription", """{"action":"create_checkout","interval":"monthly"}""", """{"action":"create_checkout","interval":"yearly"}"""],
        ["update_profile_preferences", """{"action":"set_clock_format","uses_24_hour_clock":true}""", """{"action":"set_clock_format","uses_24_hour_clock":false}"""],
        ["update_notifications", """{"action":"mark_read","notification_id":"$notification"}""", """{"action":"mark_read","notification_id":"$other"}"""],
        ["delete_notifications", """{"action":"delete_selected","notification_ids":["$notification"]}""", """{"action":"delete_selected","notification_ids":["$notification","$other"]}"""]
    ];

    [Theory]
    [MemberData(nameof(IntentCases))]
    public async Task PreparationUsesCompleteCommandValuesAndDoesNotDispatch(string name, string original, string changed)
    {
        using var context = new HeldWriteTestContext();
        var notification = Notification.Create(context.UserId, "First", "Body");
        var other = Notification.Create(context.UserId, "Second", "Body");
        context.Records.AddRange([notification, other]);
        JsonElement Expand(string value) => JsonSerializer.Deserialize<JsonElement>(value
            .Replace("$notification", notification.Id.ToString(), StringComparison.Ordinal)
            .Replace("$other", other.Id.ToString(), StringComparison.Ordinal));
        var tool = (IPreparedIntentTool)context.Tool(name);

        var first = await tool.PrepareIntentAsync(Expand(original), context.UserId, default);
        var same = await tool.PrepareIntentAsync(Expand(original), context.UserId, default);
        var different = await tool.PrepareIntentAsync(Expand(changed), context.UserId, default);

        first.Should().NotBeNull();
        same.Should().NotBeNull();
        first!.HasSameMeaning(same!).Should().BeTrue();
        if (different is not null)
            first.HasSameMeaning(different).Should().BeFalse();
        context.Commands.Should().BeEmpty();
        context.Added.Should().BeEmpty();
        await context.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void EveryExposedMultiplexerDeclaresItsPreparationOwner()
    {
        var tools = typeof(IAiTool).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IAiTool).IsAssignableFrom(type))
            .Select(type => (IAiTool)RuntimeHelpers.GetUninitializedObject(type))
            .Where(tool => !tool.IsReadOnly && tool is not IMcpOnlyAiTool)
            .Where(tool => JsonSerializer.SerializeToElement(tool.GetParameterSchema()).GetProperty("properties")
                .TryGetProperty("action", out var action) && action.TryGetProperty("enum", out var values) && values.GetArrayLength() > 1);

        foreach (var tool in tools)
            tool.Should().BeAssignableTo<IPreparedIntentTool>(tool.Name);
    }

    [Theory]
    [InlineData("update_notifications", "{\"action\":\"mark_all_read\"}")]
    [InlineData("update_notifications", "{\"action\":\"test_push\"}")]
    [InlineData("delete_notifications", "{\"action\":\"delete_all\"}")]
    [InlineData("manage_calendar_sync", "{\"action\":\"run_sync\"}")]
    [InlineData("manage_calendar_sync", "{\"action\":\"set_selected_calendars\",\"calendar_ids\":[]}")]
    [InlineData("manage_account", "{\"action\":\"reset_account\"}")]
    [InlineData("manage_account", "{\"action\":\"confirm_deletion\",\"code\":\"wrong\"}")]
    public async Task UnboundedTargetsAndSecurityChecksDoNotProduceIntentProofOrRunChecks(string name, string arguments)
    {
        using var context = new HeldWriteTestContext();
        var tool = (IPreparedIntentTool)context.Tool(name);

        var prepared = await tool.PrepareIntentAsync(JsonSerializer.Deserialize<JsonElement>(arguments), context.UserId, default);

        prepared.Should().BeNull();
        context.Commands.Should().BeEmpty();
        context.Mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("21:00:00", true)]
    [InlineData("21:00:01", false)]
    [InlineData("tomorrow night", false)]
    public async Task HabitCorrectionPreservesMinutePrecisionRatherThanGuessingTime(string time, bool supported)
    {
        using var context = new HeldWriteTestContext();
        var tool = (IPreparedIntentTool)context.Tool("create_habit");
        var original = JsonSerializer.SerializeToElement(new { title = "Read", frequency_unit = "Day", due_time = time });
        var candidate = JsonSerializer.SerializeToElement(new { title = "Read", frequency_unit = "Day", due_time = "21:00" });

        var before = await tool.PrepareIntentAsync(original, context.UserId, default);
        var after = await tool.PrepareIntentAsync(candidate, context.UserId, default);

        if (supported)
        {
            before.Should().NotBeNull();
            before!.HasSameMeaning(after!).Should().BeTrue();
            (await tool.CheckArgumentsAsync(original, context.UserId, default)).IsFailure.Should().BeTrue();
            (await tool.CheckArgumentsAsync(before.ExecutionArguments, context.UserId, default)).IsSuccess.Should().BeTrue();
        }
        else
            before.Should().BeNull();
        context.Added.Should().BeEmpty();
    }

    [Theory]
    [InlineData("title", "Different")]
    [InlineData("frequency_unit", "Week")]
    [InlineData("due_time", "22:00")]
    [InlineData("is_bad_habit", true)]
    public async Task HabitPreparedDomainParametersIncludeEveryMutationValue(string field, object value)
    {
        using var context = new HeldWriteTestContext();
        var tool = (IPreparedIntentTool)context.Tool("create_habit");
        var fields = new Dictionary<string, object> { ["title"] = "Read", ["frequency_unit"] = "Day", ["due_time"] = "21:00" };
        var original = await tool.PrepareIntentAsync(JsonSerializer.SerializeToElement(fields), context.UserId, default);
        fields[field] = value;
        var changed = await tool.PrepareIntentAsync(JsonSerializer.SerializeToElement(fields), context.UserId, default);

        original.Should().NotBeNull();
        changed.Should().NotBeNull();
        original!.HasSameMeaning(changed!).Should().BeFalse();
    }

    [Fact]
    public async Task HabitPreparationFreezesInheritedChildDatesAndCadenceUsingTheExecutionProducer()
    {
        using var context = new HeldWriteTestContext();
        var tool = (IPreparedIntentTool)context.Tool("create_habit");
        var arguments = JsonSerializer.Deserialize<JsonElement>("""
            {"title":"Read","frequency_unit":"Day","days":["Wednesday","Sunday"],
             "due_date":"2026-10-04","sub_habits":[{"title":"Take notes","due_time":"21:00:00"}]}
            """);
        var prepared = await tool.PrepareIntentAsync(arguments, context.UserId, default);
        prepared.Should().NotBeNull();
        var repeated = await tool.PrepareIntentAsync(prepared!.ExecutionArguments, context.UserId, default);
        prepared.HasSameMeaning(repeated!).Should().BeTrue();

        var result = await tool.ExecuteAsync(prepared.ExecutionArguments, context.UserId, default);

        result.Success.Should().BeTrue();
        var child = context.Added.OfType<Habit>().Single(habit => habit.ParentHabitId is not null);
        child.NextDueDate.Should().Be(context.Today);
        child.Days.Should().BeEquivalentTo([DayOfWeek.Wednesday, DayOfWeek.Sunday]);
        child.DueTime.Should().Be(new TimeOnly(21, 0));
    }
}
