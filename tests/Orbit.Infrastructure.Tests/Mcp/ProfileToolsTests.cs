using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class ProfileToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly ProfileTools _tools;
    private readonly ClaimsPrincipal _user;

    public ProfileToolsTests()
    {
        _tools = new ProfileTools(_mediator, new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private void StubExecutor(AgentOperationStatus status, string? policyReason = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "operation", "operation", AgentRiskClass.Low, AgentConfirmationRequirement.None,
            status, PolicyReason: policyReason));

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private async Task<AgentExecuteOperationRequest> CapturedRequestAsync(Func<Task> act)
    {
        await act();
        var calls = _executor.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .ToList();
        calls.Should().NotBeEmpty();
        return (AgentExecuteOperationRequest)calls[^1].GetArguments()[0]!;
    }

    [Fact]
    public async Task GetProfile_Success_ReturnsFormattedProfile()
    {
        var profile = new ProfileResponse(
            "Thomas", "thomas@example.com", "America/Sao_Paulo",
            true, true, true, true, true, true, true, true, "pt-BR", "Pro", true, false, null, null,
            5, 100, false, false, false, null, null, false, 1, 500, 5, "Achiever",
            0, 10, 12, 2, null, null,
            false, GoogleCalendarAutoSyncStatus.Idle, null, true, null, false);

        _mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(profile));

        var result = await _tools.GetProfile(_user);

        result.Should().Contain("Thomas");
        result.Should().Contain("thomas@example.com");
        result.Should().Contain("Pro");
        result.Should().Contain("Monday");
    }

    [Fact]
    public async Task GetProfile_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ProfileResponse>("User not found"));

        var result = await _tools.GetProfile(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SetTimezone_Success_RoutesToUpdatePreferencesAndReturnsConfirmation()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.SetTimezone(_user, "America/New_York"));

        request.OperationId.Should().Be("update_profile_preferences");
        request.Arguments.GetRawText().Should().Contain("set_timezone");
        result.Should().Contain("Timezone set to America/New_York");
    }

    [Fact]
    public async Task SetTimezone_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Invalid timezone");

        var result = await _tools.SetTimezone(_user, "Invalid/Zone");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SetLanguage_Success_RoutesToUpdatePreferencesAndReturnsConfirmation()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.SetLanguage(_user, "pt-BR"));

        request.OperationId.Should().Be("update_profile_preferences");
        request.Arguments.GetRawText().Should().Contain("set_language");
        result.Should().Contain("Language set to pt-BR");
    }

    [Fact]
    public async Task SetLanguage_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Invalid language");

        var result = await _tools.SetLanguage(_user, "xx");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SetAiMemory_Enabled_RoutesThroughExecutorAndReturnsEnabledMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.SetAiMemory(_user, true));

        request.OperationId.Should().Be("set_ai_memory");
        result.Should().Be("AI memory enabled");
    }

    [Fact]
    public async Task SetAiMemory_Disabled_ReturnsDisabledMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var result = await _tools.SetAiMemory(_user, false);

        result.Should().Be("AI memory disabled");
    }

    [Fact]
    public async Task SetAiMemory_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Not found");

        var result = await _tools.SetAiMemory(_user, true);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SetAiSummary_Enabled_RoutesThroughExecutorAndReturnsEnabledMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.SetAiSummary(_user, true));

        request.OperationId.Should().Be("set_ai_summary");
        result.Should().Be("AI summary enabled");
    }

    [Fact]
    public async Task SetAiSummary_Disabled_ReturnsDisabledMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var result = await _tools.SetAiSummary(_user, false);

        result.Should().Be("AI summary disabled");
    }

    [Fact]
    public async Task SetAiSummary_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Not found");

        var result = await _tools.SetAiSummary(_user, true);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SetColorScheme_Success_RoutesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.SetColorScheme(_user, "blue"));

        request.OperationId.Should().Be("set_color_scheme");
        result.Should().Contain("Color scheme set to blue");
    }

    [Fact]
    public async Task SetColorScheme_Null_SendsExplicitNullColorScheme()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var request = await CapturedRequestAsync(async () => await _tools.SetColorScheme(_user, null));

        request.OperationId.Should().Be("set_color_scheme");
        request.Arguments.GetRawText().Should().Contain("color_scheme");
    }

    [Fact]
    public async Task SetWeekStartDay_Sunday_RoutesToUpdatePreferencesAndReturnsSundayMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.SetWeekStartDay(_user, 0));

        request.OperationId.Should().Be("update_profile_preferences");
        request.Arguments.GetRawText().Should().Contain("set_week_start_day");
        result.Should().Contain("Sunday");
    }

    [Fact]
    public async Task SetWeekStartDay_Monday_ReturnsMondayMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var result = await _tools.SetWeekStartDay(_user, 1);

        result.Should().Contain("Monday");
    }

    [Fact]
    public async Task SetWeekStartDay_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Invalid");

        var result = await _tools.SetWeekStartDay(_user, 5);

        result.Should().StartWith("Error: ");
    }
}
