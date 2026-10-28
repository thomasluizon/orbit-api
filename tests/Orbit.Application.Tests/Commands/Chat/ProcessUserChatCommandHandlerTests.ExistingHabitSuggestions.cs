using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", false)]
    [InlineData("pt-BR", true)]
    public async Task ExistingHabitSuggestion_StoredTitleReachesCardAndApproval(string language, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var existing = CreateHabit(language == "en" ? "Drink water" : "Beber água");
        var modelTitle = language == "en" ? "Drink a glass of water on waking" : "Beber um copo de água ao acordar";
        var other = CreateHabit(modelTitle);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([existing, other]);
        var modelDraft = new HabitSuggestionDraft(Guid.NewGuid(), "💧", modelTitle, "Habit", FrequencyUnit.Day, 1,
            [], Today, new(7, 0), new(7, 5), 10, [language == "en" ? "Fill the glass" : "Encher o copo"],
            language == "en" ? "Make water easy to reach." : "Deixe a água por perto.", existing.Id);
        var generator = Substitute.For<IHabitDraftGenerator>();
        generator.GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitSuggestionDraft>>([modelDraft]));
        var service = new HabitSuggestionService(_habitRepo, _goalRepo, _userDateService, generator);
        var query = new SuggestHabitDraftsQueryHandler(service);
        _mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => query.Handle(call.Arg<SuggestHabitDraftsQuery>(), call.Arg<CancellationToken>()));
        var tool = new SuggestHabitsTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var message = language == "en" ? "Suggest an adjustment to Drink water." : "Sugira um ajuste em Beber água.";
        SetupAiResponse(ToolResponse(tool.Name, "suggestions", JsonSerializer.Serialize(new { instruction = message, language, count = 1 })));
        ContinueWithReadDirective("Prepared.");

        var result = await handler.Handle(new(UserId, message,
            ClientContext: new(SupportsParts: true, PartKinds: ["habitSuggestions"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var card = result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Block
            .Should().BeOfType<HabitSuggestionsCard>().Subject;
        var draft = card.Drafts.Should().ContainSingle().Which;
        draft.Should().BeEquivalentTo(modelDraft with { Title = existing.Title });
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        var executor = Substitute.For<IAgentOperationExecutor>();
        var preview = new PreviewHabitSuggestionCommandHandler(service, executor);

        var approval = await preview.Handle(new(UserId, draft, language), default);

        approval.IsSuccess.Should().BeTrue(approval.Error);
        await executor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == "update_habit" && request.PreviewOnly && request.IncludeChangePreview
            && request.Arguments.GetProperty("habit_id").GetGuid() == existing.Id
            && request.Arguments.GetProperty("title").GetString() == existing.Title
            && request.Arguments.GetProperty("due_time").GetString() == "07:00"), Arg.Any<CancellationToken>());
        existing.Title.Should().Be(language == "en" ? "Drink water" : "Beber água");
        other.Title.Should().Be(modelTitle);
        await generator.Received(1).GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>());
    }
}
