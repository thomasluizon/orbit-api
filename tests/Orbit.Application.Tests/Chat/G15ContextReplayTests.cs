using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private const string SharedTitleGuidance = "For an existing habit target with sharesTitle=true in habitTargets, read query_habits with for_selection=true and wait for the person's choice before calling any write tool. Never emit the write alongside that read or use every matching ID for a singular target. An explicitly requested full set uses a fresh unfiltered read with for_selection=false.";
    private const string TouchRefinementGuidance = "If previousStartingStep.step already consists of a single touch or contact, another touch, tap or finger placement is equal effort and is not smaller. Reduce to looking at the same object without touching or moving it. Compare with the actual typed previous step. Do not repeat contact as a refinement of contact.";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task G15_DuplicateReplayMarksTypedTargetsBeforeTheFirstModelResponse(int repeat)
    {
        SetupExistingParents("pt-BR");
        EnableComposition();
        var habits = new[] { CreateHabit("Caminhada"), CreateHabit("Caminhada"), CreateHabit("Water") };
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var write = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var handler = CreateHandler(RealAskTool(), query, write);
        UseProductionReadExecutor(query);
        var captured = CapturedCompositionPlan($"prod-r7-ambiguity-pt-r{repeat}") with { ConversationContext = new() };
        var ask = captured.ToolCalls!.Single(call => call.Name == "ask");
        var arguments = JsonNode.Parse(ask.Args.GetRawText())!.AsObject();
        for (var index = 0; index < 2; index++) arguments["input"]!["options"]![index]!["id"] = habits[index].Id.ToString();
        SetupAiResponse(captured with { ToolCalls = [captured.ToolCalls!.Single(call => call.Name == query.Name)] });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(captured with { ToolCalls = [ask with { Args = JsonSerializer.SerializeToElement(arguments) }] }));

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: new(SupportsParts: true,
            SupportsHabitListCard: true, PartKinds: ["habitList", "choice"], Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var request = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>()).Single();
        request.AccountContext.Should().Contain("\"habitTargets\"");
        var line = request.AccountContext!.Split(Environment.NewLine).Single(value => value.StartsWith("{\"habitTargets\":"));
        using var context = JsonDocument.Parse(line);
        var targets = context.RootElement.GetProperty("habitTargets").EnumerateArray().ToArray();
        foreach (var target in targets)
            target.GetProperty("sharesTitle").GetBoolean().Should().Be(target.GetProperty("title").GetString() == "Caminhada");
        targets.Select(target => target.GetProperty("id").GetString()).Should().BeEquivalentTo(habits.Take(2).Select(habit => habit.Id.ToString()));
        request.AccountContext.Should().Contain(SharedTitleGuidance);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Block.Should()
            .BeOfType<AstraChoiceBlock>().Which.Options.Select(option => option.Id).Should().Equal(habits.Take(2).Select(habit => habit.Id.ToString()));
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(value => value.OperationId == write.Name), Arg.Any<CancellationToken>());
        result.Value.PendingOperations.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task G15_CapturedTouchRefinementReceivesTheTypedContactBaseline(string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        var tool = new SuggestBreakdownTool();
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(CapturedCompositionPlan("prod-r7-composition-one-small-step-smaller-pt-BR-r1"));
        var previous = new StartingStep(language == "en" ? "Study" : "Estudar",
            language == "en" ? "Place one finger on the closed study material" : "Colocar um dedo sobre o material de estudo fechado", language);

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: new(SupportsParts: true,
            PartKinds: ["oneSmallStep"], Locale: language, SupportsStartingStepContext: true, PreviousStartingStep: previous)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var request = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>()).Single();
        request.DynamicContext.Should().Contain(JsonSerializer.Serialize(new { previousStartingStep = previous }, ChatStreamEvent.SerializerOptions))
            .And.Contain(TouchRefinementGuidance);
        JsonSerializer.SerializeToElement(request.ToolDeclarations.Single()).GetProperty("description").GetString().Should()
            .NotContain(TouchRefinementGuidance, "the typed previous-step context already supplies the complete guidance");
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("oneSmallStep");
        await AssertNoCompositionContinuation();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
    }
}
