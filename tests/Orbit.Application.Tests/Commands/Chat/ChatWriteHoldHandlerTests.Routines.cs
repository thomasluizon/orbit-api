using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("provider")]
    [InlineData("truncated")]
    [InlineData("budget")]
    public async Task RoutineRecovery_RespectsProviderAndTurnLimits(string reason)
    {
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>()).Returns(reason == "provider"
                    ? Result.Failure<AiResponse>(ErrorMessages.AiUnavailable)
                    : Result.Success(new AiResponse
                    {
                        TextMessage = "How often should it repeat?",
                        IsTruncated = reason == "truncated",
                        ReportedTokenCount = reason == "budget" ? AppConstants.MaxAiRequestTotalTokens + 1 : 0
                    }));

        var result = await CreateHandler().Handle(new(_userId, "Every evening: read, stretch",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        if (reason == "provider") result.IsFailure.Should().BeTrue();
        _pendingOperationStore.GetAwaiting(_userId).Should().BeEmpty();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", false, false)]
    [InlineData("pt-BR", false, false)]
    [InlineData("en", true, false)]
    [InlineData("pt-BR", true, false)]
    [InlineData("en", false, true)]
    [InlineData("pt-BR", false, true)]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", true, true)]
    [InlineData("en", false, null)]
    [InlineData("pt-BR", false, null)]
    public async Task RoutineFirstTurn_ClarificationBecomesHeldParentAndChildren(
        string language, bool explicitCadence, bool? askTool)
    {
        _user.SetLanguage(language);
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraBlockFirst });
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse
                {
                    TextMessage = language == "pt-BR"
                        ? "Você prefere uma única rotina com uma lista de etapas, ou hábitos separados?"
                        : "How often should this evening routine repeat?",
                    ToolCalls = askTool == true ? [new("ask", "question", JsonSerializer.SerializeToElement(new
                    {
                        question = "Checklist or sub-habits?", options = new[] { "Checklist", "Sub-habits" }
                    }))] : askTool is null ? [new("create_habit", "routine", JsonSerializer.SerializeToElement(new
                    {
                        title = language == "pt-BR" ? "Rotina da noite" : "Evening routine"
                    }))] : null
                }));
        var message = (language, explicitCadence) switch
        {
            ("pt-BR", true) => "Toda noite às 21h30: ler 20 minutos, alongar por 10 minutos e apagar as luzes às 23h, como uma rotina da noite",
            ("pt-BR", false) => "Crie uma rotina tranquila da noite: ler 20 minutos, alongar e apagar as luzes até as 23h",
            (_, true) => "Every evening at 9:30 PM: read 20 minutes, stretch for 10 minutes, and lights out at 11 PM, as one evening routine",
            _ => "Build me a calm evening routine: read 20 minutes, stretch, and lights out by 11"
        };

        var result = await CreateHandler().Handle(new(_userId, message,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var routine = RoutineArguments(stored.Arguments);
        routine.GetProperty("frequency_unit").GetString().Should().Be("Day");
        routine.GetProperty("frequency_quantity").GetInt32().Should().Be(1);
        routine.GetProperty("title").GetString().Should().Be(language == "pt-BR" ? "Rotina da noite" : "Evening routine");
        var children = routine.GetProperty("sub_habits");
        children.GetArrayLength().Should().Be(3);
        children[0].GetProperty("title").GetString().Should().Contain(language == "pt-BR" ? "ler" : "read");
        children[1].GetProperty("title").GetString().Should().Contain(language == "pt-BR" ? "alongar" : "stretch");
        children[2].GetProperty("due_time").GetString().Should().Be("23:00");
        if (explicitCadence)
            routine.GetProperty("due_time").GetString().Should().Be("21:30");
        else
            routine.TryGetProperty("due_time", out _).Should().BeFalse();
        pending.Items.Should().ContainSingle().Which.Fields.Should().Contain(field =>
            field.Field == "sub_habits" && field.ProposedValue!.Value.GetArrayLength() == 3);
        pending.Items.Should().OnlyContain(item => item.ValidationErrors == null || item.ValidationErrors.Count == 0);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeEmpty();
        result.Value.Actions.Should().BeEmpty();
        _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());

        var saved = new List<Habit>();
        await _habitRepo.AddAsync(Arg.Do<Habit>(saved.Add), Arg.Any<CancellationToken>());
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken), default);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        saved.Should().HaveCount(4);
        var parent = saved.Should().ContainSingle(habit => habit.ParentHabitId == null).Which;
        parent.FrequencyUnit.Should().Be(Orbit.Domain.Enums.FrequencyUnit.Day);
        saved.Where(habit => habit.ParentHabitId == parent.Id).Should().HaveCount(3)
            .And.Contain(habit => habit.DueTime == new TimeOnly(23, 0));
    }
}
