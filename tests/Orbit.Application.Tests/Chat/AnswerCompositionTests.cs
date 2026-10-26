using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Profile.Queries;
using Orbit.Application.Referrals.Queries;
using Orbit.Application.Tags.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Entities;
using System.Linq.Expressions;
using System.Text.Json;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("query_habits", "habitList")]
    [InlineData("query_goals", "goalList")]
    [InlineData("get_habit_metrics", "metrics")]
    [InlineData("get_period_analytics", "periodTotal")]
    [InlineData("get_period_analytics", "periodComparison")]
    [InlineData("get_daily_summary", "daySummary")]
    [InlineData("get_gamification_overview", "streak")]
    [InlineData("get_calendar_overview", "calendar")]
    [InlineData("get_retrospective", "periodInsight")]
    [InlineData("get_notifications", "recordList")]
    [InlineData("list_tags", "recordList")]
    [InlineData("get_checklist_templates", "recordList")]
    [InlineData("get_api_keys", "recordList")]
    [InlineData("get_habit_history", "recordList")]
    [InlineData("get_profile", "accountRows")]
    [InlineData("get_subscription_overview", "accountRows")]
    [InlineData("get_referral_overview", "accountRows")]
    public async Task Composition_EveryReadFamilyNegotiatesGroundedBlockAndFallback(string toolName, string kind)
    {
        foreach (var mode in new[] { "parts", "legacy", "missing" })
        {
            SetupUserAndPayGate();
            EnableComposition();
            _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
            var tool = CompositionTool(toolName, kind);
            var handler = CreateHandler(tool);
            UseProductionReadExecutor(tool);
            SetupAiResponse(ToolResponse(toolName, "family", JsonSerializer.Serialize(new
            { habit_id = Guid.NewGuid(), date_from = Today.AddDays(-6).ToString("yyyy-MM-dd"), date_to = Today.ToString("yyyy-MM-dd") })) with
            { TextMessage = TerminalReadDirective.Marker });
            ContinueWithReadDirective("Model prose before the answer.\n[[orbit:read:family]]");
            var client = new AgentClientContext(SupportsParts: mode != "legacy",
                PartKinds: mode == "missing" ? ["unknown"] : [kind],
                SupportsHabitListCard: true, SupportsGoalListCard: true, SupportsMetricsCard: true,
                SupportsDaySummaryCard: true, SupportsStreakCard: true, SupportsCalendarCard: true,
                SupportsPeriodInsightCard: true, SupportsRecordListCard: true, SupportsAccountRowsCard: true,
                SupportsHabitHistoryCard: true);

            var result = await handler.Handle(new(UserId, "Show the requested result", ClientContext: client), default);

            result.IsSuccess.Should().BeTrue(toolName + ": " + mode);
            if (mode == "parts")
            {
                result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Kind.Should().Be(kind);
                if (kind == "daySummary")
                    result.Value.Parts![0].Should().BeOfType<ChatTextPart>().Which.Text.Should().NotMatchRegex(@"\d");
                else
                    result.Value.AiMessage.Should().BeNullOrEmpty();
            }
            else if (mode == "legacy")
            {
                result.Value.Parts.Should().BeNull();
                if (kind == "daySummary")
                    result.Value.AiMessage.Should().Be("Here is what you finished and what is left for today.");
                else
                    result.Value.AiMessage.Should().Contain("Model prose");
            }
            else
            {
                result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
                result.Value.AiMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("Model prose");
            }
        }
    }

    [Theory]
    [InlineData("en", "legacy")]
    [InlineData("pt-BR", "legacy")]
    [InlineData("en", "disabled")]
    [InlineData("pt-BR", "disabled")]
    [InlineData("en", "parts")]
    public async Task Composition_RealPreviewHasReadableFallbackWithoutModelClaims(string language, string mode)
    {
        SetupExistingParents(language);
        EnableComposition();
        if (mode == "disabled")
            _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
                .Returns(["astra_block_first", "astra_parts_disabled", "astra_conversation_recording"]);
        var tool = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(tool);
        var held = HoldReplayWrites(tool);
        SetupAiResponse(ReplayCall(tool.Name, """{"habits":[{"title":"Reading","frequency_unit":"Day","frequency_quantity":1}]}""").Value);
        ReplyToReplay("Saved Reading 99 times.");
        var events = new List<ChatStreamEvent>();
        var result = await handler.Handle(new(UserId, "Create a Reading habit", ClientContext: new(
            SupportsParts: mode != "legacy", SupportsPendingOperationChanges: true),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
            .Which.EntityName.Should().Be("Reading");
        held.Should().ContainSingle();
        result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.Success);
        if (mode == "parts")
        {
            result.Value.AiMessage.Should().BeNullOrEmpty();
            events.Should().NotContain(item => item.Type == "delta");
        }
        else
        {
            result.Value.AiMessage.Should().Contain("Reading").And.NotContain("99").And.NotContain("Saved");
            result.Value.AiMessage.Should().Contain(language == "en" ? "approving" : "aprovar");
            result.Value.Parts.Should().BeNull();
            events.Last(item => item.Type == "delta").Text.Should().Be(result.Value.AiMessage);
        }
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn =>
            turn.ResponseJson == JsonSerializer.Serialize(result.Value, ChatStreamEvent.SerializerOptions)));
    }

    private IAiTool CompositionTool(string name, string? kind = null)
    {
        if (name == "get_daily_summary") return SummaryReadTool();
        var period = new PeriodHabitMetrics(new(Today.AddDays(-6), Today), 3, 2, 1, 7, 100m / 7, 0, 1);
        object payload = name switch
        {
            "query_habits" => new HabitListCard("all", []),
            "query_goals" => new GoalListCard([]),
            "get_habit_metrics" => new HabitMetrics(3, 8, 50, 60, 20, Today, Guid.NewGuid(), "Read") { Period = period },
            "get_period_analytics" => new PeriodAnalyticsResponse(new("positiveLogRecords", "records", 3, period, new("day", [])),
                kind == "periodComparison" ? new("positiveLogRecords", "records", period, period, 3, 3, 0, 0) : null, "revision"),
            "get_habit_history" => new HabitHistoryRead(0, [], "revision"),
            "get_retrospective" => new RetrospectiveResponse("week", Metrics(totalScheduled: 7),
                new("Highlights", "Missed", "Trends", "Suggestion"), true, Today.AddDays(-6), Today),
            "get_calendar_overview" => new CalendarOverviewPayload([], null, []),
            "get_gamification_overview" => new GamificationOverviewPayload(
                new GamificationProfileResponse(120, 3, "Level", "level", 100, 200, 80, 0, 0, [], [],
                    7, 9, Today, true, false, new NextRewardCarrot(4, "Next", 80, null)), null,
                new StreakInfoResponse(7, 9, Today, 1, 2, 3, false, [], 0, 3, 0, 2, true, false, null, 0)),
            "get_notifications" => new GetNotificationsResponse([], 0),
            "list_tags" => Array.Empty<TagResponse>(),
            "get_checklist_templates" => Array.Empty<ChecklistTemplateResponse>(),
            "get_api_keys" => Array.Empty<ApiKeyResponse>(),
            "get_profile" => JsonSerializer.Deserialize<ProfileResponse>("{\"Name\":\"Reader\",\"Email\":\"reader@example.test\",\"Plan\":\"Free\",\"LevelTitle\":\"Level\"}")
                ?? throw new InvalidOperationException("Profile fixture is required."),
            "get_subscription_overview" => new SubscriptionOverviewPayload(new("Free", false, false, null, null, 0, 10, false, null, null), null, null),
            "get_referral_overview" => new ReferralDashboardResponse("CODE", "https://example.test/r/CODE",
                new("CODE", "https://example.test/r/CODE", 1, 0, 10, "discount", 20)),
            _ => throw new ArgumentException("Unknown composition fixture.", nameof(name))
        };
        return ReadTool(name, new ToolResult(true, Payload: payload));
    }

    private void EnableComposition() => _featureFlagService
        .GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
        .Returns(["astra_block_first", "astra_conversation_recording"]);

    [Fact]
    public async Task Composition_OneContextualFollowUpPreservesLegacyQuestionAndStoredParts()
    {
        var result = await ComposeHabitAnswer("[[orbit:followups]]\nShow my habits?", context: true);

        result.FollowUps.Should().Equal("Show my habits?");
        result.FollowUpContext.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Label = "Show my habits?", Question = "Show my habits?", Context = "query_habits", Target = "habits" });
        result.RelatedSurfaces.Should().Contain("habits");
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn =>
            turn.ResponseJson == JsonSerializer.Serialize(result, ChatStreamEvent.SerializerOptions)));
        var replay = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result, ChatStreamEvent.SerializerOptions),
            ChatStreamEvent.SerializerOptions)!;
        ChatStreamEvent.Final(replay).ToJson().Should().Be(ChatStreamEvent.Final(result).ToJson());
    }

    [Theory]
    [InlineData("Here is your habit ranking.")]
    [InlineData("Aqui está o ranking dos seus hábitos.")]
    public async Task Composition_RankingOmissionUsesProducedCompleteRanking(string sentence)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var ranking = await ProduceRankingAsync();
        var tool = ReadTool("get_habit_ranking", new ToolResult(true, Payload: ranking));
        SetupAiResponse(ToolResponse(tool.Name, "ranking", "{}") with
        {
            TextMessage = sentence + "\n[[orbit:complete]]"
        });
        ContinueWithReadDirective("Read has 1 completion.\nRead leads the ranking.");
        var result = await CreateHandler(tool).Handle(new(UserId, "Rank my habits", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitRanking"])), default);
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        result.Value.Parts.Should().HaveCount(2);
        result.Value.Parts![0].Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(sentence);
        result.Value.Parts[1].Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<HabitRankingCard>().Which.Ranking.Should().Be(ranking);
        result.Value.AiMessage.Should().Be(sentence);
    }

    [Fact]
    public async Task Composition_ClarificationLivesInChoice()
    {
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(new AiResponse
        {
            TextMessage = "Choose a time.",
            ToolCalls = [new("ask", "choice", ParseArguments(ChoiceArguments))],
            ConversationContext = TestConversationContext
        });
        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me choose", ClientContext: new(
            SupportsParts: true, PartKinds: ["choice"])), default);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("choice");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Composition_SuccessfulReadSurvivesNarrativeOutage()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = CompositionTool("query_habits");
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "answer", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>("Provider offline"));
        var result = await handler.Handle(new(UserId, "Show habits", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>();
        result.Value.AiMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Composition_UnrelatedFollowUpIsDropped()
    {
        var result = await ComposeHabitAnswer("[[orbit:followups]]\nShow calendar events?");
        result.FollowUps.Should().BeNull();
    }

    [Fact]
    public async Task Composition_PreservesIndependentReadsWithoutSubjectKeywords()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = CompositionTool("query_habits");
        var goals = CompositionTool("query_goals");
        var metrics = CompositionTool("get_habit_metrics");
        var handler = CreateHandler(habits, goals, metrics);
        UseProductionReadExecutor(habits, goals, metrics);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(habits.Name, "habits", ParseArguments("{}")),
                new(goals.Name, "goals", ParseArguments("{}")), new(metrics.Name, "metrics", ParseArguments("{}"))],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("[[orbit:read:metrics]]\nExtra dashboard prose.\n[[orbit:read:goals]]");

        var result = await handler.Handle(new(UserId, "Help me check everything", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList", "goalList", "metrics"], SupportsHabitListCard: true,
            SupportsGoalListCard: true, SupportsMetricsCard: true)), default);

        result.Value.Parts!.OfType<ChatBlockPart>().Select(part => part.Kind).Should().Equal("habitList", "goalList", "metrics");
    }

    [Fact]
    public async Task Composition_StreakAnswerExcludesItsPrerequisiteHabitLookup()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habit = CreateHabit("Reading");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        var lookup = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var metrics = ScopedCompositionTool("get_habit_metrics");
        var handler = CreateHandler(lookup, metrics);
        UseProductionReadExecutor(lookup, metrics);
        SetupAiResponse(ToolResponse(lookup.Name, "lookup", "{\"search\":\"Reading\"}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success(call.Arg<IReadOnlyList<AiToolCallResult>>()[0].Name == lookup.Name
                ? ToolResponse(metrics.Name, "answer", JsonSerializer.Serialize(new { habit_id = habit.Id }))
                : new AiResponse { TextMessage = "The streak is 3." }));

        var result = await handler.Handle(new(UserId, "What is the streak for my Reading habit?", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList", "metrics"], SupportsHabitListCard: true,
            SupportsMetricsCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        var part = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        part.Kind.Should().Be("metrics");
        part.Block.Should().BeOfType<MetricsCard>().Which.HabitId.Should().Be(habit.Id);
        result.Value.AiMessage.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("get_habit_metrics", "Show metrics for Reading and Running")]
    [InlineData("get_habit_metrics", "Show metrics for both Reading and Running")]
    [InlineData("get_habit_history", "Show history for Reading and Running")]
    [InlineData("get_habit_history", "Show history for both Reading and Running")]
    public async Task Composition_KeepsDistinctHabitScopes(string toolName, string message)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var tool = ScopedCompositionTool(toolName);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var from = Today.AddDays(-6).ToString("yyyy-MM-dd");
        var to = Today.ToString("yyyy-MM-dd");
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(toolName, "first", JsonSerializer.SerializeToElement(new { habit_id = first, date_from = from, date_to = to })),
                new(toolName, "second", JsonSerializer.SerializeToElement(new { habit_id = second, date_from = from, date_to = to }))],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("Model prose without directives.");

        var result = await handler.Handle(new(UserId, message, ClientContext: new(SupportsParts: true,
            PartKinds: ["metrics", "recordList"], SupportsMetricsCard: true, SupportsRecordListCard: true,
            SupportsHabitHistoryCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        var reads = result.Value.Parts!.OfType<ChatBlockPart>().Select(part => ReadBlockTelemetry.Describe(part.Block).Read!).ToArray();
        reads.Should().HaveCount(2);
        reads.Select(read => read.Scope.GetProperty("habit_id").GetGuid()).Should().Equal(first, second);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn =>
            turn.ResponseJson == JsonSerializer.Serialize(result.Value, ChatStreamEvent.SerializerOptions)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Composition_PreviewKeepsIndependentRead(bool previewFirst)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var write = FakeTool("create_habit");
        var read = CompositionTool("query_habits");
        var handler = CreateHandler(write, read);
        var blocks = UseProductionReadExecutor(read);
        var executor = Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(read, blocks);
        var pending = new PendingAgentOperation(Guid.NewGuid(), "habits.write", "Create", "Create Reading",
            AgentRiskClass.High, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(5));
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<AgentExecuteOperationRequest>().OperationId == write.Name
                ? Task.FromResult(new AgentExecuteOperationResponse(new(write.Name, write.Name, AgentRiskClass.High,
                    AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), pending))
                : executor.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>()));
        var createCall = new AiToolCall(write.Name, "preview", ParseArguments("{\"title\":\"Reading\"}"));
        var readCall = new AiToolCall(read.Name, "list", ParseArguments("{}"));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = previewFirst ? [createCall, readCall] : [readCall, createCall],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("Created Reading. Here are your habits.");

        var result = await handler.Handle(new(UserId, "Create a Reading habit and show my existing habits", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.PendingOperations.Should().ContainSingle().Which.Id.Should().Be(pending.Id);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("habitList");
        result.Value.Operations.Should().ContainSingle().Which.OperationId.Should().Be(read.Name);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn =>
            turn.ResponseJson == JsonSerializer.Serialize(result.Value, ChatStreamEvent.SerializerOptions)));
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == write.Name && request.IncludeChangePreview), Arg.Any<CancellationToken>());
        await write.DidNotReceive().ExecuteAsync(Arg.Any<JsonElement>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("get_habit_metrics", false)]
    [InlineData("get_habit_metrics", true)]
    [InlineData("get_habit_history", false)]
    [InlineData("get_habit_history", true)]
    public async Task Composition_DeduplicatesOnlyEquivalentDateScopes(string toolName, bool equivalent)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var id = Guid.NewGuid();
        var tool = ScopedCompositionTool(toolName);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var first = JsonSerializer.SerializeToElement(new
        { habit_id = id, date_from = Today.AddDays(-6).ToString("yyyy-MM-dd"), date_to = Today.ToString("yyyy-MM-dd") });
        var second = JsonSerializer.SerializeToElement(new
        { date_to = Today.ToString("yyyy-MM-dd"), date_from = Today.AddDays(equivalent ? -6 : -13).ToString("yyyy-MM-dd"), habit_id = id });
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(toolName, "first", first), new(toolName, "second", second)],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("No directives.");
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Show history and metrics", ClientContext: new(SupportsParts: true,
            PartKinds: ["metrics", "recordList"], SupportsMetricsCard: true, SupportsRecordListCard: true, SupportsHabitHistoryCard: true),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue();
        var reads = result.Value.Parts!.OfType<ChatBlockPart>().Select(part => ReadBlockTelemetry.Describe(part.Block).Read!).ToArray();
        reads.Should().HaveCount(equivalent ? 1 : 2);
        reads.Select(read => read.Scope.GetProperty("date_from").GetString()).Should().Equal(equivalent
            ? [first.GetProperty("date_from").GetString()] : [first.GetProperty("date_from").GetString(), second.GetProperty("date_from").GetString()]);
        events.Where(item => item.Type == "delta").Should().BeEmpty();
        var json = JsonSerializer.Serialize(result.Value, ChatStreamEvent.SerializerOptions);
        var replay = JsonSerializer.Deserialize<ChatResponse>(json, ChatStreamEvent.SerializerOptions)!;
        ChatStreamEvent.Final(replay).ToJson().Should().Be(ChatStreamEvent.Final(result.Value).ToJson());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Composition_IndependentRankingAndReadKeepProducedOrder(bool rankingFirst)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var ranking = await ProduceRankingAsync();
        var rankingTool = ReadTool("get_habit_ranking", new ToolResult(true, Payload: ranking));
        var read = CompositionTool("query_habits");
        var handler = CreateHandler(read, rankingTool);
        UseProductionReadExecutor(read, rankingTool);
        var rankingCall = new AiToolCall(rankingTool.Name, "ranking", ParseArguments("{}"));
        var readCall = new AiToolCall(read.Name, "list", ParseArguments("{}"));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = rankingFirst ? [rankingCall, readCall] : [readCall, rankingCall],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("Model omitted the blocks.");

        var result = await handler.Handle(new(UserId, "Help me check everything", ClientContext: new(SupportsParts: true,
            PartKinds: ["habitList", "habitRanking"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts!.OfType<ChatBlockPart>().Select(part => part.Kind).Should().Equal(rankingFirst
            ? ["habitRanking", "habitList"] : ["habitList", "habitRanking"]);
    }

    private IAiTool ScopedCompositionTool(string toolName)
    {
        if (toolName == "get_habit_metrics")
        {
            _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>()).Returns(call => Result.Success(
                new HabitMetrics(3, 8, 50, 60, 20, Today, call.Arg<GetHabitMetricsQuery>().HabitId, "Reading")));
            return new GetHabitMetricsTool(_mediator);
        }
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitLogResponse>>([]));
        return new GetHabitHistoryTool(_mediator);
    }

    [Fact]
    public async Task Composition_DeduplicatesEquivalentQueryScopes()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = CompositionTool("query_habits");
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(tool.Name, "first", ParseArguments("{\"offset\":0,\"limit\":50}")),
                new(tool.Name, "again", ParseArguments("{\"limit\":50,\"offset\":0}"))],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("Here are both lists.");
        var result = await handler.Handle(new(UserId, "Show both habits", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);
        result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("create_habit")]
    [InlineData("bulk_update_habits")]
    public async Task Composition_PreviewReportsProposalOnceWithoutRepeatingModelSuccess(string name)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = FakeTool(name);
        var handler = CreateHandler(tool);
        var pending = new PendingAgentOperation(Guid.NewGuid(), "habits.write", "Update", "Update habits",
            AgentRiskClass.High, AgentConfirmationRequirement.FreshConfirmation,
            DateTime.UtcNow.AddMinutes(5));
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new(name, name, AgentRiskClass.High,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), pending));
        SetupAiResponse(ToolResponse(name, "preview", name == "create_habit" ? "{\"title\":\"Read\"}" : "{}"));
        ContinueWithReadDirective("Done. Updated everything.\n[[orbit:followups]]\nShow my habits?");

        var result = await handler.Handle(new(UserId, "Update habits", ClientContext: new(SupportsParts: true,
            PartKinds: ["habitList"], SupportsPendingOperationChanges: true, SupportsFollowUps: true)), default);

        result.Value.PendingOperations.Should().ContainSingle().Which.Id.Should().Be(pending.Id);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.FollowUps.Should().BeNull();
        await tool.DidNotReceive().ExecuteAsync(Arg.Any<JsonElement>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Composition_NoDataTextUsesThreeShortLines()
    {
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(new AiResponse { TextMessage = "# Heading\n- First line.\n* Second line.\nThird line.\nExtra caveat." });
        var result = await CreateHandler().Handle(new(UserId, "Help me plan", ClientContext: new(SupportsParts: true)), default);
        result.Value.AiMessage.Should().Be("First line.\nSecond line.\nThird line.");
    }

    [Theory]
    [InlineData("Here are your habits.\n[[orbit:read:answer]]")]
    [InlineData("Here are your habits.")]
    public async Task Composition_LeadsWithTypedReadDespiteModelPositionOrOmission(string reply)
    {
        var result = await ComposeHabitAnswer(reply);

        result.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Kind.Should().Be("habitList");
        result.AiMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Composition_SuppressesRepeatedFiguresAndFacts()
    {
        var result = await ComposeHabitAnswer("You have 0 habits.\n[[orbit:read:answer]]\nThere are zero habits today.");

        result.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>();
        result.AiMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Composition_ReducesThreeFollowUpsWithoutAnotherCall()
    {
        var result = await ComposeHabitAnswer("[[orbit:read:answer]]\n[[orbit:followups]]\nShow my habits?\nWhat habits remain?\nShow all habits?");

        result.FollowUps.Should().Equal("Show my habits?", "What habits remain?");
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Composition_WithholdsPreBlockDeltas()
    {
        var events = new List<ChatStreamEvent>();
        var result = await ComposeHabitAnswer("You have 0 habits.\n[[orbit:read:answer]]", events);

        events.Where(item => item.Type == "delta").Should().BeEmpty();
        events.Add(ChatStreamEvent.Final(result));
        events.Last().Response!.Parts![0].Should().BeOfType<ChatBlockPart>();
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn => turn.AssistantContent == result.AiMessage));
    }

    private async Task<ChatResponse> ComposeHabitAnswer(string reply, List<ChatStreamEvent>? events = null, bool context = false)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "answer", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (call.ArgAt<Func<AiStreamEvent, Task>?>(2) is { } sink)
                    foreach (var character in reply)
                        await sink(AiStreamEvent.Delta(character.ToString()));
                return Result.Success(new AiResponse { TextMessage = reply });
            });
        var result = await handler.Handle(new(UserId, "Show my habits", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true, SupportsFollowUps: true, SupportsFollowUpContext: context),
            StreamSink: events is null ? null : item => { events.Add(item); return Task.CompletedTask; }), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }
}
