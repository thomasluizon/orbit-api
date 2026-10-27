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

    [Theory]
    [InlineData("en", "Change my habit.", "Which habit would you like to change, and what would you like to change?")]
    [InlineData("pt-BR", "Altere meu hábito.", "Qual hábito você quer mudar e o que quer mudar nele?")]
    public async Task HonestyReplay_UnspecifiedChangeAsksForTargetAndChange(string language, string message, string question)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        SetupAiResponse(HonestyReplay("ambiguity-en-r1"));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, message,
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue();
        var choice = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>().Subject;
        choice.Question.Should().Be(question);
        choice.AllowFreeText.Should().BeTrue();
        choice.Options.Select(option => option.Label).Should().Equal("Water", "Walk");
        await _blockStore.Received(1).AddAsync(Arg.Is<IssuedAstraBlock>(block => block.ReadDefinition().Title == question
            && block.ReadDefinition().AllowFreeText), Arg.Any<CancellationToken>());
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("en", "How many days in a row did I play piano?", "No matching habit was found, so its streak is unavailable.")]
    [InlineData("pt-BR", "Quantos dias seguidos eu toquei piano?", "Não encontrei um hábito correspondente para consultar a sequência.")]
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
    [InlineData("query_habits", true)]
    [InlineData("delete_habit", true)]
    [InlineData("query_habits", false)]
    [InlineData("delete_habit", false)]
    public async Task UnspecifiedChange_ReplacesListOrWritePlanWithCompletePlainQuestion(string toolName, bool parts)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = ReadTool(toolName, new(true, Payload: new HabitListCard("all", [])));
        if (toolName == "delete_habit") tool.IsReadOnly.Returns(false);
        SetupAiResponse(ToolResponse(toolName, "unrequested", "{}"));

        var result = await CreateHandler(tool).Handle(new(UserId, "Change my habit.",
            ClientContext: new(SupportsParts: parts, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Which habit would you like to change, and what would you like to change?");
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
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
