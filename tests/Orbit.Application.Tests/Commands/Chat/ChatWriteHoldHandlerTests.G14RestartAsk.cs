using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task G14_CapturedRestartQuestionRecoversToOneCompleteUnappliedPreview(string language)
    {
        var capture = FreshRestartCapture(1);
        var habits = await SetupFreshRestartAsync(capture, language);
        var before = await RestartReadbackAsync();
        var calls = FreshRestartCalls(capture, habits);
        var ask = new AskTool((BulkCreateHabitsTool)_toolRegistry.GetTool("bulk_create_habits")!,
            new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService, new BulkHabitReplayPlanner(
                Substitute.For<Orbit.Application.Common.IIdempotencyContext>(), Substitute.For<Orbit.Application.Common.IIdempotencyStore>(), _unitOfWork)),
            (BulkSkipHabitsTool)_toolRegistry.GetTool("bulk_skip_habits")!, _userDateService);
        var tools = new AiToolRegistry([.. _toolRegistry.GetAll(), ask]);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "CompositionFailures", "prod-r6-composition-restart-approve-pt-BR-r2.json")));
        var initial = document.RootElement.GetProperty("toolCalls")[0];
        var question = new AiToolCall(initial.GetProperty("name").GetString()!, "captured-question", initial.GetProperty("arguments").Clone());
        var rounds = 0;
        SetupFreshRestartModel(question, feedback =>
        {
            var outcome = feedback.Should().ContainSingle().Which;
            if (rounds == 0)
            {
                outcome.Success.Should().BeFalse();
                outcome.Error.Should().Contain("typed missing-field result");
            }
            else
                outcome.Payload.Should().BeOfType<HabitListCard>();
            return FreshRestartResponse(calls[rounds++]);
        });

        var result = await CreateHandler(tools: tools).Handle(new(_userId, "Continue.", ClientContext: new(
            SupportsParts: true, SupportsPendingOperationChanges: true, SupportsHabitListCard: true,
            PartKinds: ["habitList", "choice", "question"], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        rounds.Should().Be(2);
        AssertFreshRestartPreview(result.Value, habits);
        (await RestartReadbackAsync()).Should().BeEquivalentTo(before);
    }
}
