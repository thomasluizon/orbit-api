using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Calendar.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Mcp;

public class CapabilityParityToolsTests
{
    [Theory]
    [InlineData(typeof(ChecklistTemplateTools), "UpdateChecklistTemplate")]
    [InlineData(typeof(ProfileTools), "SetName")]
    [InlineData(typeof(CalendarTools), "SetSelectedCalendars")]
    public void MissingWrites_AreExposedOnMcp(Type tools, string method)
    {
        tools.GetMethod(method).Should().NotBeNull("MCP must expose " + method);
    }
    [Theory]
    [InlineData("template", AgentOperationStatus.Succeeded)]
    [InlineData("template", AgentOperationStatus.Denied)]
    [InlineData("name", AgentOperationStatus.Succeeded)]
    [InlineData("name", AgentOperationStatus.Denied)]
    [InlineData("selection", AgentOperationStatus.PendingConfirmation)]
    [InlineData("selection", AgentOperationStatus.Succeeded)]
    public async Task Writes_RouteArgumentsAndConfirmationThroughExecutor(string action, AgentOperationStatus status)
    {
        var userId = Guid.NewGuid();
        var templateId = Guid.NewGuid().ToString();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult("operation", "operation", AgentRiskClass.Low,
                AgentConfirmationRequirement.None, status, PolicyReason: "denied", PendingOperationId: Guid.NewGuid())));
        var mediator = Substitute.For<IMediator>();
        var bridge = new McpExecutorBridge(executor);
        var result = action switch
        {
            "template" => await new ChecklistTemplateTools(mediator, bridge).UpdateChecklistTemplate(user, templateId, "Evening", ["Read"], "token"),
            "name" => await new ProfileTools(mediator, bridge).SetName(user, "Alex", "token"),
            _ => await new CalendarTools(mediator, bridge).SetSelectedCalendars(user, [], "token")
        };
        var request = (AgentExecuteOperationRequest)executor.ReceivedCalls().Single().GetArguments()[0]!;
        request.UserId.Should().Be(userId);
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        request.ConfirmationToken.Should().Be("token");
        request.OperationId.Should().Be(action switch { "template" => "update_checklist_template", "name" => "update_profile_preferences", _ => "manage_calendar_sync" });
        if (action == "template")
        {
            request.Arguments.GetProperty("template_id").GetString().Should().Be(templateId);
            request.Arguments.GetProperty("name").GetString().Should().Be("Evening");
            request.Arguments.GetProperty("items").EnumerateArray().Select(item => item.GetString()).Should().Equal("Read");
        }
        else if (action == "name")
        {
            request.Arguments.GetProperty("action").GetString().Should().Be("set_name");
            request.Arguments.GetProperty("name").GetString().Should().Be("Alex");
        }
        else
        {
            request.Arguments.GetProperty("action").GetString().Should().Be("set_selected_calendars");
            request.Arguments.GetProperty("calendar_ids").GetArrayLength().Should().Be(0);
        }
        if (status == AgentOperationStatus.Denied)
            result.Should().Be("Error: denied");
        else if (status == AgentOperationStatus.PendingConfirmation)
            result.Should().Contain("Confirmation required");
        else
            result.Should().NotContain("Error:");
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task GetCalendarOverview_ShowsCalendarSelection()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new List<CalendarEventItem>()));
        mediator.Send(Arg.Any<GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure<CalendarAutoSyncStateResponse>("gated"));
        mediator.Send(Arg.Any<GetCalendarSyncSuggestionsQuery>(), Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure<List<CalendarSyncSuggestionItem>>("gated"));
        mediator.Send(Arg.Any<GetUserCalendarsQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new List<UserCalendarItem>
        {
            new("primary", "Personal", "owner", true, null, true),
            new("work", "Work", "owner", false, null, false)
        }));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test"));
        var text = await new CalendarTools(mediator, new McpExecutorBridge(Substitute.For<IAgentOperationExecutor>())).GetCalendarOverview(user);
        var calendars = JsonDocument.Parse(text).RootElement.GetProperty("Calendars");
        calendars[0].GetProperty("IsSynced").GetBoolean().Should().BeTrue();
        calendars[1].GetProperty("IsSynced").GetBoolean().Should().BeFalse();
    }

}
