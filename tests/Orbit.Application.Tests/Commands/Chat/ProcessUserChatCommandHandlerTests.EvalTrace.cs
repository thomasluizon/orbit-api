using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Commands;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("subscribe_push", true, false)]
    [InlineData("subscribe_push", true, true)]
    [InlineData("subscribe_push", false, false)]
    [InlineData("subscribe_push", false, true)]
    [InlineData("unsubscribe_push", true, false)]
    [InlineData("unsubscribe_push", true, true)]
    public async Task Handle_EvalTrace_OmitsPushCredentialsFromRealNotificationTool(string action, bool execute, bool stream)
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        _mediator.Send(Arg.Any<SubscribePushCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _mediator.Send(Arg.Any<UnsubscribePushCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        const string endpoint = "private-fcm-registration";
        const string p256dh = "private-push-key";
        const string auth = "private-push-auth";
        var arguments = JsonSerializer.SerializeToElement(new { action, endpoint, p256dh, auth, release_other_account = true });
        SetupReadToolReply("update_notifications", "Push subscription updated.");
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new("update_notifications", "push-call", arguments)],
            ConversationContext = TestConversationContext,
            TextMessage = "Push subscription update requested.",
            ReportedTokenCount = execute ? 0 : AppConstants.MaxAiRequestTotalTokens + 1
        });

        var result = await CreateHandler(new UpdateNotificationsTool(_mediator)).Handle(new ProcessUserChatCommand(UserId,
            "Update push subscription", EvalTraceRequested: true, StreamSink: stream ? _ => Task.CompletedTask : null), default);

        result.IsSuccess.Should().BeTrue();
        var trace = EvalResponseJson(result.Value, stream).GetProperty("evalTrace");
        var call = trace.GetProperty("toolCalls")[0];
        call.GetProperty("status").GetString().Should().Be(execute ? "Succeeded" : "NotExecuted");
        var tracedArguments = call.GetProperty("arguments");
        tracedArguments.TryGetProperty("auth", out _).Should().BeFalse();
        tracedArguments.TryGetProperty("p256dh", out _).Should().BeFalse();
        tracedArguments.TryGetProperty("endpoint", out _).Should().BeFalse();
        tracedArguments.GetProperty("action").GetString().Should().Be(action);
        tracedArguments.GetProperty("release_other_account").GetBoolean().Should().BeTrue();
        trace.GetRawText().Should().NotContain(endpoint).And.NotContain(p256dh).And.NotContain(auth);
        if (execute)
        {
            var payload = call.GetProperty("result").GetProperty("payload");
            payload.TryGetProperty("endpoint", out _).Should().BeFalse();
            payload.GetProperty("action").GetString().Should().Be(action);
            if (action == "subscribe_push")
                await _mediator.Received(1).Send(Arg.Is<SubscribePushCommand>(command =>
                    command.Endpoint == endpoint && command.P256dh == p256dh && command.Auth == auth), Arg.Any<CancellationToken>());
            else
                await _mediator.Received(1).Send(Arg.Is<UnsubscribePushCommand>(command =>
                    command.Endpoint == endpoint && command.P256dh == p256dh && command.Auth == auth && command.ReleaseOtherAccount),
                    Arg.Any<CancellationToken>());
        }
        else
        {
            await _mediator.DidNotReceive().Send(Arg.Any<SubscribePushCommand>(), Arg.Any<CancellationToken>());
            result.Value.EvalTrace!.ToolCalls[0].Result.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_EvalTrace_RecordsModelOrderArgumentsAndResultsAcrossRounds(bool stream)
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        var first = ReadTool("query_habits", new ToolResult(true, Payload: new { count = 2 }));
        var second = ReadTool("query_goals", new ToolResult(true, Payload: new { count = 1 }));
        first.Order.Returns(2);
        second.Order.Returns(1);
        SetupAiResponse(new AiResponse
        {
            ToolCalls =
            [
                new("query_habits", "habit-call", ParseArguments("""{"filter":{"completed":false},"limit":2}""")),
                new("query_goals", "goal-call", ParseArguments("""{"status":"Active"}"""))
            ],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse("query_habits", "follow-up", "{}")),
                Result.Success(new AiResponse { TextMessage = "Here are your records." }));

        var result = await CreateHandler(first, second).Handle(new ProcessUserChatCommand(UserId,
            "Find my habits and goals", CorrelationId: "request-eval", EvalTraceRequested: true,
            StreamSink: stream ? _ => Task.CompletedTask : null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var response = EvalResponseJson(result.Value, stream);
        response.TryGetProperty("evalTrace", out var trace).Should().BeTrue();
        trace.GetProperty("traceId").GetString().Should().Be("request-eval");
        var timing = trace.GetProperty("timing");
        var phases = timing.GetProperty("phases").EnumerateArray().ToArray();
        phases.Select(phase => phase.GetProperty("name").GetString()).Should()
            .Contain(["moderation", "context_load", "prompt_build", "tool_call", "save", "composition"]);
        phases.Sum(phase => phase.GetProperty("milliseconds").GetDouble()).Should()
            .BeApproximately(timing.GetProperty("totalMilliseconds").GetDouble(), 0.001);
        var calls = trace.GetProperty("toolCalls").EnumerateArray().ToArray();
        calls.Select(call => call.GetProperty("id").GetString()).Should().Equal("habit-call", "goal-call", "follow-up");
        calls.Select(call => call.GetProperty("name").GetString()).Should().Equal("query_habits", "query_goals", "query_habits");
        calls[0].GetProperty("arguments").GetProperty("filter").GetProperty("completed").GetBoolean().Should().BeFalse();
        calls[0].GetProperty("arguments").GetProperty("limit").GetInt32().Should().Be(2);
        calls[1].GetProperty("arguments").GetProperty("status").GetString().Should().Be("Active");
        calls.Should().OnlyContain(call => call.GetProperty("status").GetString() == "Succeeded");
        calls[0].GetProperty("result").GetProperty("payload").GetProperty("count").GetInt32().Should().Be(2);
        calls[1].GetProperty("result").GetProperty("payload").GetProperty("count").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Handle_EvalTrace_RequiresBothRequestAndAccountFlag(bool requested, bool flagged)
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(flagged ? new[] { "astra_eval_trace" } : Array.Empty<string>());
        var tool = ReadTool("query_habits", new ToolResult(true, Payload: new { count = 1 }));
        SetupReadToolReply("query_habits", "Here are your habits.");

        var result = await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Find my habits",
            EvalTraceRequested: requested), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        EvalResponseJson(result.Value, false).TryGetProperty("evalTrace", out _).Should().BeFalse();
        EvalResponseJson(result.Value, true).TryGetProperty("evalTrace", out _).Should().BeFalse();

        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        var traced = await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Find my habits",
            EvalTraceRequested: true), CancellationToken.None);
        EvalResponseJson(result.Value, false).GetRawText().Should()
            .Be(EvalResponseJson(traced.Value with { EvalTrace = null }, false).GetRawText());
        ChatStreamEvent.Final(result.Value).ToJson().Should()
            .Be(ChatStreamEvent.Final(traced.Value with { EvalTrace = null }).ToJson());
    }

    [Theory]
    [InlineData(AgentOperationStatus.Denied)]
    [InlineData(AgentOperationStatus.UnsupportedByPolicy)]
    [InlineData(AgentOperationStatus.Failed)]
    [InlineData(AgentOperationStatus.PendingConfirmation)]
    public async Task Handle_EvalTrace_PreservesRefusalStatus(AgentOperationStatus status)
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        var tool = ReadTool("query_habits", new ToolResult(true));
        SetupReadToolReply("query_habits", "Unable to run.");
        var handler = CreateHandler(tool);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult("query_habits", "query_habits",
                AgentRiskClass.Low, AgentConfirmationRequirement.None, status, PolicyReason: "scope_missing")));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Find my habits",
            EvalTraceRequested: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var call = EvalResponseJson(result.Value, false).GetProperty("evalTrace").GetProperty("toolCalls")[0];
        call.GetProperty("status").GetString().Should().Be(status.ToString());
        call.GetProperty("result").GetProperty("success").GetBoolean().Should().Be(status == AgentOperationStatus.PendingConfirmation);
        if (status == AgentOperationStatus.PendingConfirmation)
            call.GetProperty("result").GetProperty("payload").GetProperty("status").GetString().Should().Be("awaiting_confirmation");
    }

    private static JsonElement EvalResponseJson(ChatResponse response, bool stream)
    {
        using var json = JsonDocument.Parse(stream ? ChatStreamEvent.Final(response).ToJson()
            : JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return (stream ? json.RootElement.GetProperty("response") : json.RootElement).Clone();
    }

    [Fact]
    public async Task Handle_EvalTrace_RedactsApiKeyFromRealToolResultAndNestedSensitiveArguments()
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        _mediator.Send(Arg.Any<CreateApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new CreateApiKeyResponse(Guid.NewGuid(), "Eval key", "credential-value",
                "prefix", ["habits:read"], true, null, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc))));
        SetupReadToolReply("manage_api_keys", "Key created.");
        SetupAiResponse(ToolResponse("manage_api_keys", "key-call",
            """{"action":"create","name":"Eval key","scopes":["habits:read"],"metadata":[{"access_token":"private-token","password":"private-password"}]}"""));

        var result = await CreateHandler(new ManageApiKeysTool(_mediator)).Handle(new ProcessUserChatCommand(UserId,
            "Create an evaluation API key", EvalTraceRequested: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var trace = EvalResponseJson(result.Value, false).GetProperty("evalTrace");
        trace.GetRawText().Should().NotContain("credential-value").And.NotContain("private-token").And.NotContain("private-password");
        trace.GetProperty("toolCalls")[0].GetProperty("arguments").GetProperty("name").GetString().Should().Be("Eval key");
        var payload = trace.GetProperty("toolCalls")[0].GetProperty("result").GetProperty("payload");
        payload.TryGetProperty("key", out _).Should().BeFalse();
        payload.GetProperty("keyPrefix").GetString().Should().Be("prefix");
    }

    [Fact]
    public async Task Handle_EvalTrace_ListsCallsAfterAskAsNotExecuted()
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        var later = ReadTool("query_habits", new ToolResult(true));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new("ask", "question", ParseArguments(ChoiceArguments)),
                new("query_habits", "skipped", ParseArguments("{}"))],
            ConversationContext = TestConversationContext
        });

        var result = await CreateHandler(RealAskTool(), later).Handle(new ProcessUserChatCommand(UserId,
            "Help me plan a walk", ClientContext: new(SupportsParts: true, PartKinds: ["choice"]),
            EvalTraceRequested: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var calls = result.Value.EvalTrace!.ToolCalls;
        calls.Select(call => call.Id).Should().Equal("question", "skipped");
        calls.Select(call => call.Status).Should().Equal("Succeeded", "NotExecuted");
        calls[1].Result.Should().BeNull();
        await later.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default, default);
    }

    [Fact]
    public async Task Handle_EvalTrace_IsNotPersistedOrReplayedAcrossRequests()
    {
        var quota = CreateRealPayGate(User.Create("Alex", "eval@example.com").Value);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        var block = RetryChoice();
        var tool = ReadTool("query_habits", new ToolResult(true, Payload: new { count = 1 }));
        SetupReadToolReply("query_habits", "Here are your habits.");
        var handler = CreateHandler(quota, tool);
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"]),
            CorrelationId: "original-request", EvalTraceRequested: true);

        var first = await handler.Handle(request, default);
        first.IsSuccess.Should().BeTrue();
        first.Value.EvalTrace!.ToolCalls.Should().ContainSingle();
        block.ResponseJson.Should().NotBeNull().And.NotContain("evalTrace").And.NotContain("toolCalls");
        var normalReplay = await handler.Handle(request with { EvalTraceRequested = false }, default);
        normalReplay.Value.EvalTrace.Should().BeNull();
        var tracedReplay = await handler.Handle(request with { CorrelationId = "replay-request" }, default);
        tracedReplay.Value.EvalTrace!.TraceId.Should().Be("replay-request");
        tracedReplay.Value.EvalTrace.ToolCalls.Should().BeEmpty();
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EvalTrace_NoToolTurnReturnsEmptyTrace()
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        SetupAiResponse(new AiResponse { TextMessage = "Hello." });

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "Hello",
            CorrelationId: "greeting-request", EvalTraceRequested: true), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.EvalTrace!.TraceId.Should().Be("greeting-request");
        result.Value.EvalTrace.ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_EvalTrace_OmitsMcpOnlyAdministrativeData()
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        _mediator.Send(Arg.Any<ManageFeatureFlagsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AdminFeatureFlag>("Unknown account emails: other@example.com",
                FeatureFlagActions.UnknownAccounts));
        SetupReadToolReply("manage_feature_flags", "Flag updated.");
        SetupAiResponse(ToolResponse("manage_feature_flags", "admin-call",
            """{"action":"enable_for_accounts","key":"redesign","emails":["other@example.com"]}"""));

        var result = await CreateHandler(new ManageFeatureFlagsTool(_mediator)).Handle(new ProcessUserChatCommand(UserId,
            "Update a flag", EvalTraceRequested: true), default);

        result.IsSuccess.Should().BeTrue();
        var trace = result.Value.EvalTrace!;
        trace.ToolCalls.Should().ContainSingle().Which.Name.Should().Be("manage_feature_flags");
        trace.ToolCalls[0].Arguments.GetString().Should().Be("[redacted]");
        trace.ToolCalls[0].Result.Should().BeNull();
        JsonSerializer.Serialize(trace).Should().NotContain("other@example.com");
    }

    [Fact]
    public async Task Handle_EvalTrace_RecordsCallsPreventedByTheTokenCeiling()
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        var tool = ReadTool("query_habits", new ToolResult(true));
        SetupAiResponse(ToolResponse("query_habits", "budget-call", "{}") with
        {
            TextMessage = "The request budget was reached.",
            ReportedTokenCount = AppConstants.MaxAiRequestTotalTokens + 1
        });

        var result = await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId,
            "Find my habits", EvalTraceRequested: true), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.EvalTrace!.ToolCalls.Should().ContainSingle().Which.Status.Should().Be("NotExecuted");
        await tool.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default, default);
    }

    [Fact]
    public async Task Handle_EvalTrace_RedactsAccountDeletionCodeFromRealToolArguments()
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        _mediator.Send(Arg.Any<ConfirmAccountDeletionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)));
        SetupReadToolReply("manage_account", "Deletion scheduled.");
        SetupAiResponse(ToolResponse("manage_account", "deletion-call",
            """{"action":"confirm_deletion","code":"private-deletion-code"}"""));

        var result = await CreateHandler(new ManageAccountTool(_mediator)).Handle(new ProcessUserChatCommand(UserId,
            "Confirm deletion", EvalTraceRequested: true), default);

        result.IsSuccess.Should().BeTrue();
        var trace = result.Value.EvalTrace!;
        trace.ToolCalls.Should().ContainSingle().Which.Arguments.TryGetProperty("code", out _).Should().BeFalse();
        JsonSerializer.Serialize(trace).Should().NotContain("private-deletion-code");
        await _mediator.Received(1).Send(Arg.Is<ConfirmAccountDeletionCommand>(command =>
            command.Code == "private-deletion-code"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EvalTrace_PreservesPublicReferralCodeFromRealToolResult()
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "astra_eval_trace" });
        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("shareable-code"));
        SetupReadToolReply("get_referral_code", "Here is your referral code.");

        var result = await CreateHandler(new GetReferralCodeTool(_mediator)).Handle(new ProcessUserChatCommand(UserId,
            "Get my referral code", EvalTraceRequested: true), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.EvalTrace!.ToolCalls.Should().ContainSingle().Which.Result!.Value
            .GetProperty("payload").GetProperty("code").GetString().Should().Be("shareable-code");
    }
}
