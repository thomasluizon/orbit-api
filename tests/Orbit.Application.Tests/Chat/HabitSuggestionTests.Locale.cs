using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public partial class HabitSuggestionTests
{
    [Theory]
    [InlineData("en", 1)]
    [InlineData("pt-BR", 1)]
    [InlineData("en", 2)]
    [InlineData("pt-BR", 2)]
    public async Task SuggestionPreviews_BindWithAContextThatHoldsOnlyTheLocaleOfTheirLanguage(string language, int count)
    {
        var expected = new AgentClientContext(Locale: language);
        var requests = new List<AgentExecuteOperationRequest>();
        var store = Substitute.For<IPendingAgentOperationStore>();
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Do<AgentExecuteOperationRequest>(requests.Add), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<AgentExecuteOperationRequest>();
            var pending = new PendingAgentOperation(Guid.NewGuid(), "habits", request.OperationId, "Preview", AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10));
            store.GetExecution(_userId, pending.Id).Returns(new PendingAgentOperationExecution(pending.Id, "habits",
                request.OperationId, request.Arguments, AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation,
                "member-preview"));
            return new AgentExecuteOperationResponse(new(request.OperationId, request.OperationId, AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), pending);
        });
        var previewer = Substitute.For<IPendingOperationChangePreviewer>();
        previewer.PreviewAsync(_userId, AccountChangeSet.OperationName, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>(),
                Arg.Any<AgentClientContext?>())
            .Returns(Result.Success<PendingOperationChangePreview?>(null));
        var changes = new AccountChangeSetService(store, previewer, Substitute.For<IAgentCatalogService>(),
            new AiToolRegistry([new BulkCreateHabitsTool(Substitute.For<IMediator>())]));
        var drafts = Enumerable.Range(0, count).Select(index => Draft($"Habit {index}") with
        { DueTime = new(18 + index, 0), DueEndTime = new(18 + index, 15) }).ToArray();

        if (count == 1)
            await new PreviewHabitSuggestionCommandHandler(Service(), executor, SameTitleDetailWriters.NoHabits()).Handle(new(_userId, drafts[0], language), default);
        await new PreviewAllHabitSuggestionsCommandHandler(Service(), executor, changes, store, SameTitleDetailWriters.NoHabits())
            .Handle(new(_userId, drafts, language), default);

        requests.Should().NotBeEmpty().And.OnlyContain(request => request.ClientContext == expected);
        if (count > 1)
            await previewer.Received(1).PreviewAsync(_userId, AccountChangeSet.OperationName, Arg.Any<JsonElement>(),
                Arg.Any<CancellationToken>(), expected);
    }
}
