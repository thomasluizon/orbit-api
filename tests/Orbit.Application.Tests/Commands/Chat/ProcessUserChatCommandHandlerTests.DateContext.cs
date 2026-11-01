using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private readonly IChatConversationStore _chatConversationStore = Substitute.For<IChatConversationStore>();

    [Theory]
    [InlineData("en", "pending")]
    [InlineData("pt-BR", "pending")]
    [InlineData("en", "approved")]
    [InlineData("pt-BR", "edited")]
    [InlineData("pt-BR", "rejected")]
    public async Task ReservationReplay_FollowUpKeepsImageAndLastPreview(string language, string outcome)
    {
        SetupUserAndPayGate();
        var user = await _userRepo.GetByIdAsync(UserId, default);
        user!.SetLanguage(language);
        user.SetTimeZone("America/Sao_Paulo");
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 11, 1));
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "AstraDates", language + ".json")));
        var script = fixture.RootElement;
        var create = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(create);
        var modelArgs = script.GetProperty("create_arguments");
        var previewer = new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService,
            new AiToolRegistry([create]));
        var preview = (await previewer.PreviewAsync(UserId, create.Name, modelArgs)).Value!;
        var pending = new PendingAgentOperation(Guid.NewGuid(), "habits.write", "Reservation", "Reservation",
            AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(5),
            preview.Changes, preview.ChangeTargetCount, preview.Items, preview.PreviewFingerprint);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new(create.Name, create.Name, AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), PendingOperation: pending));
        SetupAiResponse(new AiResponse { ToolCalls = [new(create.Name, "reservation", modelArgs)], ConversationContext = TestConversationContext });
        var conversation = Guid.NewGuid();
        var client = new AgentClientContext(ConversationId: conversation, SupportsParts: true,
            SupportsPendingOperationChanges: true, Locale: language);
        var firstText = script.GetProperty("first_message").GetString()!;
        byte[] image = [137, 80, 78, 71];
        var first = await handler.Handle(new(UserId, firstText, ImageData: image, ImageMimeType: "image/png",
            ClientContext: client), default);
        first.IsSuccess.Should().BeTrue(first.Error);
        first.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle().Which.Fields
            .Should().Contain(field => field.Field == "due_date" && field.NewValue == "2026-12-31" && field.Provenance == "requested");
        await _chatConversationStore.Received(1).SaveAsync(Arg.Is<ChatConversationCapture>(capture =>
            capture.ConversationId == conversation && capture.ImageData == image && capture.Previews.Count == 1), Arg.Any<CancellationToken>());
        _chatConversationStore.LoadAsync(UserId, conversation, Arg.Any<CancellationToken>()).Returns(new ChatConversationMemory(
            [new("user", firstText)], [new(firstText, image, "image/png")], [new(pending, outcome, outcome == "edited", modelArgs)]));
        var reply = script.GetProperty("follow_up_answer").GetString()!;
        AiToolRequest? followUpRequest = null;
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(request => followUpRequest = request),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = reply }));
        var second = await handler.Handle(new(UserId, script.GetProperty("follow_up").GetString()!, ClientContext: client), default);
        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value.AiMessage.Should().Be(reply);
        second.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        followUpRequest!.ConversationAttachments.Should().ContainSingle().Which.ImageData.Should().Equal(image);
        followUpRequest.DynamicContext.Should().ContainAll("conversation_last_previews", "2026-12-31", "20:30", outcome,
            "America/Sao_Paulo");
        followUpRequest.History.Should().ContainSingle().Which.Content.Should().Be(firstText);
    }
}
