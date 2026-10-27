using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private static AiResponse HonestyReplay(string name)
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "HonestyReads", name + ".json")));
        return new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = capture.RootElement.GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
                new AiToolCall(call.GetProperty("name").GetString()!, "captured-" + index,
                    call.GetProperty("arguments").Clone())).ToArray()
        };
    }

    [Fact]
    public async Task HonestyReplay_AskPreservesCapturedQuestionAndOptions()
    {
        const string question = "Which habit would you like to change?";
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(HonestyReplay("ambiguity-en-r1"));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Change my habit.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue();
        var choice = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>().Subject;
        choice.Question.Should().Be(question);
        choice.AllowFreeText.Should().BeFalse();
        choice.Options.Select(option => option.Label).Should().Equal("Water", "Walk");
        await _blockStore.Received(1).AddAsync(Arg.Is<IssuedAstraBlock>(block => block.ReadDefinition().Title == question
            && !block.ReadDefinition().AllowFreeText), Arg.Any<CancellationToken>());
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("en", "Which habit would you like to change, and what would you like to change?")]
    [InlineData("pt-BR", "Qual hábito você quer mudar e o que quer mudar nele?")]
    public async Task HonestyReplay_StructuredClarificationAsksForTargetAndChange(string language, string question)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        SetupAiResponse(ToolResponse("ask", "clarification", JsonSerializer.Serialize(new
        {
            form = "choice",
            question,
            allowFreeText = true,
            options = new[] { new { id = "water", label = "Water" }, new { id = "walk", label = "Walk" } }
        })));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me make a change.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue();
        var choice = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>().Subject;
        choice.Question.Should().Be(question);
        choice.AllowFreeText.Should().BeTrue();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("en", "Show my piano progress.", "No matching habit was found, so its streak is unavailable.")]
    [InlineData("pt-BR", "E o piano?", "Não encontrei um hábito correspondente para consultar a sequência.")]
    public async Task HonestyReplay_MissingNamedHabitReportsUnavailableWithoutFigures(string language, string message, string expected)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Habit>());
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query);
        UseProductionReadExecutor(query);
        SetupAiResponse(HonestyReplay("missing-query"));
        ContinueWithReadDirective("No data.");

        var result = await handler.Handle(new(UserId, message,
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(expected);
        result.Value.AiMessage.Should().Be(expected);
        result.Value.Operations.Should().ContainSingle().Which.OperationId.Should().Be("query_habits");
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("en", "Which habit should I edit, and what should change?")]
    [InlineData("pt-BR", "Qual hábito devo editar e o que deve mudar?")]
    public async Task HonestyReplay_PreservesModelClarification(string language, string question)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        SetupAiResponse(new AiResponse { TextMessage = question });

        var result = await CreateHandler().Handle(new(UserId, "Change my habit."), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(question);
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task HonestyReplay_NonterminalCompoundReadsContinueToModelQuestion()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var goals = CompositionTool("query_goals");
        var handler = CreateHandler(query, goals, RealAskTool());
        UseProductionReadExecutor(query, goals);
        SetupAiResponse(HonestyReplay("missing-query") with
        {
            ToolCalls = [.. HonestyReplay("missing-query").ToolCalls!,
                new("query_goals", "goals", ParseArguments("{\"orbit_read_complete\":false}"))]
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success(ToolResponse("ask", "schedule", ChoiceArguments)));

        var result = await handler.Handle(new(UserId, "Check my piano streak, then create a habit.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Block
            .Should().BeOfType<AstraChoiceBlock>().Which.Question.Should().Be("When?");
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "Caminhada: skipped today.")]
    [InlineData("pt-BR", "Caminhada: hábito pulado hoje.")]
    public async Task HonestyReplay_SkippedHabitUsesDatedDataAndLocalizedStatus(string language, string expected)
    {
        var today = new DateOnly(2026, 10, 27);
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, UserId, "Caminhada", FrequencyUnit.Day, 1, today)).Value;
        var skip = habit.SkipFlexible(today).Value;
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(new[] { habit });
        _scheduleLogReader.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns(new[] { new HabitScheduleLogDay(habit.Id, skip.Date, 0, 1, true) });
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query);
        UseProductionReadExecutor(query);
        SetupAiResponse(HonestyReplay("edge-skipped-pt-r1"));

        var result = await handler.Handle(new(UserId, "O que aconteceu com a Caminhada hoje? Eu tinha pulado.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        var card = result.Value.Operations.Should().ContainSingle().Which.ReadBlock!.Block.Should().BeOfType<HabitListCard>().Subject;
        card.Date.Should().Be(today);
        card.Items.Should().ContainSingle().Which.Status.Should().Be("none");
        card.SkippedIds.Should().Contain(habit.Id.ToString());
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(expected);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }
}
