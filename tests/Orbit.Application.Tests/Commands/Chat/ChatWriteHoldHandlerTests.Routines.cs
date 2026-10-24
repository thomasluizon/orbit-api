using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
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
    [InlineData("en", false, false)]
    [InlineData("pt-BR", false, false)]
    [InlineData("en", true, false)]
    [InlineData("pt-BR", true, false)]
    [InlineData("en", false, true)]
    [InlineData("pt-BR", false, true)]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", true, true)]
    public async Task RoutineFirstTurn_ModelProposalHoldsEditableParentAndChildrenUntilApproval(
        string language, bool explicitCadence, bool editCadence)
    {
        _user.SetLanguage(language);
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraBlockFirst });
        var title = language == "pt-BR" ? "Rotina tranquila da noite" : "Calm evening routine";
        var read = language == "pt-BR" ? "Ler 20 minutos" : "Read 20 minutes";
        var stretch = language == "pt-BR" ? "Alongar" : "Stretch";
        var lightsOut = language == "pt-BR" ? "Apagar as luzes" : "Lights out";
        var arguments = new JsonObject
        {
            ["title"] = title,
            ["frequency_unit"] = "Day",
            ["frequency_quantity"] = 1,
            ["sub_habits"] = new JsonArray(
                new JsonObject { ["title"] = read },
                new JsonObject { ["title"] = explicitCadence ? stretch + " 10 min" : stretch },
                new JsonObject { ["title"] = lightsOut, ["due_time"] = "23:00" })
        };
        if (explicitCadence) arguments["due_time"] = "21:30";
        ScriptToolCall(arguments.ToJsonString());
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse()));
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
        routine.GetProperty("title").GetString().Should().Be(title);
        var children = routine.GetProperty("sub_habits");
        children.GetArrayLength().Should().Be(3);
        children[0].GetProperty("title").GetString().Should().Be(read);
        children[1].GetProperty("title").GetString().Should().Be(explicitCadence ? stretch + " 10 min" : stretch);
        children[2].GetProperty("due_time").GetString().Should().Be("23:00");
        if (explicitCadence)
            routine.GetProperty("due_time").GetString().Should().Be("21:30");
        else
            routine.TryGetProperty("due_time", out _).Should().BeFalse();
        var fields = pending.Items.Should().ContainSingle().Which.Fields;
        fields.Should().Contain(field => field.Field == "sub_habits" && field.ProposedValue!.Value.GetArrayLength() == 3);
        fields.Should().Contain(field => field.Field == "frequency_quantity" && field.IsEditable);
        pending.Items.Should().OnlyContain(item => item.ValidationErrors == null || item.ValidationErrors.Count == 0);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeEmpty();
        result.Value.Actions.Should().BeEmpty();
        _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());

        if (editCadence)
        {
            var revision = await ParentRevisionService().ReviseAsync(_userId, pending.Id,
                new(pending.PreviewFingerprint!, [new(pending.Items!.Single().ItemId,
                    JsonSerializer.SerializeToElement(new { frequency_quantity = 2 }))]), default);
            revision.IsSuccess.Should().BeTrue(revision.Error);
            revision.PendingOperationId.Should().Be(pending.Id);
            stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
            var revised = RoutineArguments(stored.Arguments);
            revised.GetProperty("frequency_quantity").GetInt32().Should().Be(2);
            revised.GetProperty("title").GetString().Should().Be(title);
            revised.GetProperty("sub_habits").GetRawText().Should().Be(children.GetRawText());
            stored.PreviewFingerprint.Should().NotBe(pending.PreviewFingerprint);
            await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        }

        var saved = new List<Habit>();
        await _habitRepo.AddAsync(Arg.Do<Habit>(saved.Add), Arg.Any<CancellationToken>());
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken), default);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        saved.Should().HaveCount(4);
        var parent = saved.Should().ContainSingle(habit => habit.ParentHabitId == null).Which;
        parent.Title.Should().Be(title);
        parent.FrequencyUnit.Should().Be(Orbit.Domain.Enums.FrequencyUnit.Day);
        parent.FrequencyQuantity.Should().Be(editCadence ? 2 : 1);
        parent.DueTime.Should().Be(explicitCadence ? new TimeOnly(21, 30) : null);
        saved.Where(habit => habit.ParentHabitId == parent.Id).Should().HaveCount(3)
            .And.Contain(habit => habit.Title == read)
            .And.Contain(habit => habit.Title == lightsOut && habit.DueTime == new TimeOnly(23, 0));
        _pendingOperationStore.GetExecution(_userId, pending.Id).Should().BeNull();
    }
}
