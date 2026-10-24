using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en", "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)", false)]
    [InlineData("en", "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)", true)]
    [InlineData("pt-BR", "Post lunch: tomar vitamina D (uma vez por semana) Before bed: tomar rosuvastatina (todo dia)", false)]
    public async Task CompoundHeadings_ModelWithoutCreationStillProducesOneBoundPreview(string language, string message, bool empty)
    {
        ParentAccount(language);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(empty ? Result.Failure<AiResponse>(ErrorMessages.AiEmptyResponse)
                : Result.Success(new AiResponse { TextMessage = "The parent already exists. Create another?", ConversationContext = new() }));
        var result = await CreateHandler().Handle(new(_userId, message), default);
        result.IsSuccess.Should().BeTrue();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().HaveCount(2);
        pending.Items![0].Fields.Should().Contain(field => field.Field == "parent_habit_id" && field.NewValue == "Post Lunch");
        pending.Items[1].Fields.Should().Contain(field => field.Field == "parent_habit_id" && field.NewValue == "Before Bed");
        pending.Items[0].Fields.Should().Contain(field => field.Field == "frequency_unit" && field.NewValue == "Week");
        pending.Items[1].Fields.Should().Contain(field => field.Field == "frequency_unit" && field.NewValue == "Day");
        result.Value.AiMessage.Should().BeNull();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
