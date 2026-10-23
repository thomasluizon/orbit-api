using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Application.Tags.Queries;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("query_habits", "habitList", "habits", "habits")]
    [InlineData("query_goals", "goalList", "goals", "goals")]
    [InlineData("get_habit_metrics", "metrics", "metrics", null)]
    [InlineData("get_period_analytics", "periodTotal", "total", "progress")]
    [InlineData("get_period_analytics", "periodComparison", "comparison", "progress")]
    [InlineData("get_daily_summary", "daySummary", "summary", "today")]
    [InlineData("get_gamification_overview", "streak", "streak", "progress")]
    [InlineData("get_calendar_overview", "calendar", "calendar", "calendar")]
    [InlineData("get_retrospective", "periodInsight", "recap", "progress")]
    [InlineData("get_notifications", "recordList", "notifications", "notifications")]
    [InlineData("list_tags", "recordList", "tags", null)]
    [InlineData("get_checklist_templates", "recordList", "templates", null)]
    [InlineData("get_api_keys", "recordList", "keys", "profile")]
    [InlineData("get_habit_history", "recordList", "history", null)]
    [InlineData("get_profile", "accountRows", "profile", "profile")]
    [InlineData("get_subscription_overview", "accountRows", "subscription", "profile")]
    [InlineData("get_referral_overview", "accountRows", "referrals", "profile")]
    public async Task Composition_ContextAndReplayStayBoundToEveryProducedRead(string toolName, string kind,
        string subject, string? target)
    {
        SetupUserAndPayGate();
        EnableComposition();
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var tool = CompositionTool(toolName, kind);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(toolName, "answer", JsonSerializer.Serialize(new
        { habit_id = Guid.NewGuid(), date_from = Today.AddDays(-6).ToString("yyyy-MM-dd"), date_to = Today.ToString("yyyy-MM-dd") })));
        var question = $"Can you show my {subject} again using the same scope?";
        ContinueWithReadDirective("Duplicated prose.\n[[orbit:followups]]\n" + question);
        var client = new AgentClientContext(SupportsParts: true, PartKinds: [kind],
            SupportsHabitListCard: true, SupportsGoalListCard: true, SupportsMetricsCard: true,
            SupportsDaySummaryCard: true, SupportsStreakCard: true, SupportsCalendarCard: true,
            SupportsPeriodInsightCard: true, SupportsRecordListCard: true, SupportsAccountRowsCard: true,
            SupportsHabitHistoryCard: true, SupportsFollowUps: true, SupportsFollowUpContext: true);

        var result = await handler.Handle(new(UserId, "Show my " + subject, ClientContext: client), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.FollowUps.Should().Equal(question);
        var context = result.Value.FollowUpContext.Should().ContainSingle().Subject;
        context.Question.Should().Be(question);
        context.Label.Should().Be(question[..37].TrimEnd() + "...");
        context.Context.Should().Be(toolName);
        context.Target.Should().Be(target);
        var block = result.Value.Parts.Should().ContainSingle().Subject.Should().BeOfType<ChatBlockPart>().Subject;
        context.QueryId.Should().Be(ReadBlockTelemetry.Describe(block.Block).Read!.QueryId);
        var json = JsonSerializer.Serialize(result.Value, ChatStreamEvent.SerializerOptions);
        var replay = JsonSerializer.Deserialize<ChatResponse>(json, ChatStreamEvent.SerializerOptions)!;
        ChatStreamEvent.Final(ChatResponseNegotiation.ForClient(replay, client, [])).ToJson()
            .Should().Be(ChatStreamEvent.Final(result.Value).ToJson());
        var fallback = ChatResponseNegotiation.ForClient(replay, client with { PartKinds = [] }, []);
        fallback.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>();
        fallback.AiMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("Duplicated prose");
        var legacy = ChatResponseNegotiation.ForClient(replay, null, []);
        legacy.Parts.Should().BeNull();
        legacy.AiMessage.Should().Be(fallback.AiMessage);
        legacy.FollowUps.Should().BeNull();
        legacy.FollowUpContext.Should().BeNull();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("invalid")]
    [InlineData("duplicate")]
    public async Task Composition_RankingFallbackAndDeduplicationUseProducedResult(string mode)
    {
        SetupUserAndPayGate();
        EnableComposition();
        if (mode == "disabled")
            _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
                .Returns([FeatureFlagKeys.AstraBlockFirst, FeatureFlagKeys.AstraPeriodBlocksDisabled]);
        var ranking = await ProduceRankingAsync();
        if (mode == "invalid") ranking = ranking with { Query = ranking.Query with { Metric = "unsupported" } };
        var tool = ReadTool("get_habit_ranking", new ToolResult(true, Payload: ranking));
        var handler = CreateHandler(tool);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = mode == "duplicate"
                ? [new(tool.Name, "first", ParseArguments("{}")), new(tool.Name, "again", ParseArguments("{}"))]
                : [new(tool.Name, "first", ParseArguments("{}"))],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("Everything succeeded. Read leads.");
        var client = new AgentClientContext(SupportsParts: true, PartKinds: mode == "missing" ? [] : ["habitRanking"]);

        var result = await handler.Handle(new(UserId, "Rank habits", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue();
        if (mode == "duplicate")
        {
            result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>();
            var replay = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result.Value,
                ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
            ChatResponseNegotiation.ForClient(replay, client, []).Parts.Should().ContainSingle()
                .Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("habitRanking");
            ChatResponseNegotiation.ForClient(replay, client with { PartKinds = [] }, []).AiMessage
                .Should().Be(HabitRankingCardBuilder.Readable(ranking, null));
        }
        else
        {
            result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>();
            result.Value.AiMessage.Should().Be(mode == "invalid" ? "I couldn't complete that. Please try again."
                : HabitRankingCardBuilder.Readable(ranking, null));
        }
        _productAnalytics.Received().CaptureUserEvent(UserId, "astra_answer_composed", "Free",
            Arg.Is<IReadOnlyDictionary<string, object>>(properties =>
                properties["build_failure"].Equals(mode == "invalid")
                && properties["duplicate_suppression"].Equals(mode == "duplicate" ? 1 : 0)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Composition_FailureNeverBecomesAnEmptySuccess(bool missingProjection)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = ReadTool("query_habits", new ToolResult(missingProjection, Error: missingProjection ? null : "offline"));
        var handler = CreateHandler(tool);
        SetupAiResponse(ToolResponse(tool.Name, "answer", "{}"));
        ContinueWithReadDirective("You have no habits. Everything succeeded.");

        var result = await handler.Handle(new(UserId, "Show habits", ClientContext: new(SupportsParts: true,
            PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>();
        result.Value.AiMessage.Should().Be("I couldn't complete that. Please try again.");
        _productAnalytics.Received().CaptureUserEvent(UserId, "astra_answer_composed", "Free",
            Arg.Is<IReadOnlyDictionary<string, object>>(properties => properties["build_failure"].Equals(missingProjection)));
    }

    [Fact]
    public async Task Composition_TruncationKeepsTheBlockBeforeOneStatusLine()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = CompositionTool("query_habits");
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "answer", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "You have no habits.", IsTruncated = true }));

        var result = await handler.Handle(new(UserId, "Show habits", ClientContext: new(SupportsParts: true,
            PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().HaveCount(2);
        result.Value.Parts![0].Should().BeOfType<ChatBlockPart>();
        result.Value.Parts[1].Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(result.Value.AiMessage);
        result.Value.AiMessage.Should().NotContain("no habits").And.NotContain("\n");
    }

    [Theory]
    [InlineData("goals", true)]
    [InlineData("records", true)]
    [InlineData("records", false)]
    public async Task Composition_PrerequisiteSelectionUsesTypedEntityIds(string family, bool validId)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var id = Guid.NewGuid();
        var entityId = validId ? id.ToString() : "record-without-entity-id";
        object payload = family == "goals"
            ? new GoalListCard([new(entityId, "Reading", 0, 5, "books", null)])
            : new TagResponse[] { new(id, "Reading", "blue") };
        var lookup = ReadTool(family == "goals" ? "query_goals" : "list_tags", new ToolResult(true, Payload: payload));
        var metrics = CompositionTool("get_habit_metrics");
        var handler = CreateHandler(lookup, metrics);
        var factory = UseProductionReadExecutor(lookup, metrics);
        if (family == "records")
        {
            var read = factory.Capture(lookup.Name, ParseArguments("{}"), UserId, payload)
                ?? throw new InvalidOperationException("Tag read fixture is required.");
            var records = (RecordListCard)read.Block;
            var recordRead = read with { Block = records with { Items = [records.Items[0] with { Id = entityId }] } };
            var executor = Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(metrics, factory);
            var lookupName = lookup.Name;
            _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<AgentExecuteOperationRequest>().OperationId == lookupName
                    ? Task.FromResult(new AgentExecuteOperationResponse(new(lookupName, lookupName, AgentRiskClass.Low,
                        AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, ReadBlock: recordRead)))
                    : executor.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>()));
        }
        SetupAiResponse(ToolResponse(lookup.Name, "lookup", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success(call.Arg<IReadOnlyList<AiToolCallResult>>()[0].Name == lookup.Name
                ? ToolResponse(metrics.Name, "answer", JsonSerializer.Serialize(new { filters = new[] { new { entity_id = id } }, enabled = true }))
                : new AiResponse { TextMessage = "Model omitted the blocks." }));

        var result = await handler.Handle(new(UserId, "Show the requested result", ClientContext: new(SupportsParts: true,
            PartKinds: ["goalList", "recordList", "metrics"], SupportsGoalListCard: true, SupportsRecordListCard: true,
            SupportsMetricsCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts!.OfType<ChatBlockPart>().Select(part => part.Kind).Should().Equal(validId
            ? ["metrics"] : ["recordList", "metrics"]);
        result.Value.AiMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Composition_IndependentReadAcrossRoundsKeepsTheFirstAnswer()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var metrics = CompositionTool("get_habit_metrics");
        var habits = CompositionTool("query_habits");
        var handler = CreateHandler(metrics, habits);
        UseProductionReadExecutor(metrics, habits);
        SetupAiResponse(ToolResponse(metrics.Name, "metrics", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success(call.Arg<IReadOnlyList<AiToolCallResult>>()[0].Name == metrics.Name
                ? ToolResponse(habits.Name, "habits", "{}") : new AiResponse { TextMessage = "Duplicated prose." }));

        var result = await handler.Handle(new(UserId, "Show metrics and habits", ClientContext: new(SupportsParts: true,
            PartKinds: ["metrics", "habitList"], SupportsMetricsCard: true, SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts!.OfType<ChatBlockPart>().Select(part => part.Kind).Should().Equal("metrics", "habitList");
        result.Value.AiMessage.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("records")]
    [InlineData("account")]
    public async Task Composition_UnknownReadKindsAndSubjectsNeverEmitUnsupportedBlocksOrFollowUps(string unknown)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = CompositionTool("query_habits");
        var handler = CreateHandler(tool);
        var factory = UseProductionReadExecutor(tool);
        var read = factory.Capture(tool.Name, ParseArguments("{}"), UserId, new HabitListCard("all", []))
            ?? throw new InvalidOperationException("Read fixture is required.");
        read = unknown switch
        {
            "records" => read with { Kind = "recordList", Block = new RecordListCard("futureRecords", 0, [], Read: read.Read) },
            "account" => read with { Kind = "accountRows", Block = new AccountRowsCard("futureAccount", [], Read: read.Read) },
            _ => read with { Kind = "futureKind" }
        };
        var toolName = tool.Name;
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new(toolName, toolName, AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, ReadBlock: read)));
        SetupAiResponse(ToolResponse(toolName, "answer", "{}"));
        ContinueWithReadDirective("Duplicated prose.\n[[orbit:followups]]\nShow habits?");

        var result = await handler.Handle(new(UserId, "Show the requested result", ClientContext: new(SupportsParts: true,
            PartKinds: [], SupportsFollowUps: true, SupportsFollowUpContext: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>();
        result.Value.AiMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("Duplicated prose");
        result.Value.FollowUps.Should().BeNull();
        result.Value.FollowUpContext.Should().BeNull();
    }

    [Fact]
    public async Task Composition_UnresolvedHabitNameAsksInsteadOfReportingExecutionFailure()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = FakeTool("log_habit");
        tool.IsReadOnly.Returns(false);
        SetupAiResponse(ToolResponse(tool.Name, "log", "{\"habit_id\":\"Missing\"}"));
        ContinueWithReadDirective("Everything succeeded.");

        var result = await CreateHandler(tool).Handle(new(UserId, "Log Missing", ClientContext: new(SupportsParts: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text
            .Should().Contain("Missing").And.Contain("?").And.NotContain("couldn't complete");
        result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.Success);
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Composition_BuilderExceptionReturnsOnlyTruthfulStatus()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = CompositionTool("query_habits");
        var handler = CreateHandler(tool);
        var factory = UseProductionReadExecutor(tool);
        var read = factory.Capture(tool.Name, ParseArguments("{}"), UserId, new HabitListCard("all", []))!;
        var brokenCard = JsonSerializer.Deserialize<HabitListCard>("{\"scope\":\"all\",\"items\":null}",
            ChatStreamEvent.SerializerOptions) ?? throw new InvalidOperationException("Malformed card fixture is required.");
        var brokenRead = read with { Block = brokenCard };
        var toolName = tool.Name;
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new(toolName, toolName, AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, ReadBlock: brokenRead)));
        SetupAiResponse(ToolResponse(tool.Name, "answer", "{}"));
        ContinueWithReadDirective("You have no habits. Everything succeeded.");

        var result = await handler.Handle(new(UserId, "Show habits", ClientContext: new(SupportsParts: true,
            PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text
            .Should().Be("I couldn't complete that. Please try again.");
        result.Value.AiMessage.Should().Be("I couldn't complete that. Please try again.");
        _productAnalytics.Received().CaptureUserEvent(UserId, "astra_answer_composed", "Free",
            Arg.Is<IReadOnlyDictionary<string, object>>(properties => properties["build_failure"].Equals(true)
                && properties["fallback_reason"].Equals("build_failure")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composition_CachedHelpUsesTheNegotiatedTextPolicy(bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var answer = "# Heading\n- First line.\nSecond line.\nThird line.\nExtra caveat.";
        ChatFaqCache.StoreAnswer("bad_habits", "pt", answer);
        try
        {
            var events = new List<ChatStreamEvent>();
            var result = await CreateHandler().Handle(new(UserId, "O que e um mau habito?",
                ClientContext: new(SupportsParts: true), StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);
            result.IsSuccess.Should().BeTrue();
            result.Value.AiMessage.Should().Be(composition ? "First line.\nSecond line.\nThird line." : answer);
            if (composition) result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>();
            else result.Value.Parts.Should().BeNull();
            events.Should().BeEmpty();
            await _aiIntentService.DidNotReceive().SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            ChatFaqCache.StoreAnswer("bad_habits", "pt", "Resposta em cache");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composition_ShortTextRespectsRequestedSteps(bool steps)
    {
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(new AiResponse { TextMessage = "# Heading\n- " + new string('a', 200) + "\n2. Second step.\nThird step.\nExtra caveat." });
        var result = await CreateHandler().Handle(new(UserId, steps ? "Give me steps" : "Help me plan",
            ClientContext: new(SupportsParts: true)), default);
        result.Value.AiMessage.Should().Be((steps ? "- " + new string('a', 178) : new string('a', 180))
            + (steps ? "\n2. Second step." : "\nSecond step.") + "\nThird step.");
    }
}
