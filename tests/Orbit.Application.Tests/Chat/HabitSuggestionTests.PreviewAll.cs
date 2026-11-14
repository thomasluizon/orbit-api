using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Chat;

public partial class HabitSuggestionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void PreviewAllValidator_UsesSuggestionBlockBounds(int count, bool valid)
    {
        var drafts = Enumerable.Range(0, count).Select(index => Draft($"Habit {index}")).ToArray();
        new PreviewAllHabitSuggestionsCommandValidator().Validate(new PreviewAllHabitSuggestionsCommand(_userId, drafts)).IsValid.Should().Be(valid);
    }

    [Fact]
    public void PreviewAllValidator_RejectsMissingOrInvalidFieldsWithIndexedDraftErrors()
    {
        var validator = new PreviewAllHabitSuggestionsCommandValidator();
        validator.Validate(new PreviewAllHabitSuggestionsCommand(_userId, null!)).IsValid.Should().BeFalse();
        validator.Validate(new PreviewAllHabitSuggestionsCommand(Guid.Empty, [Draft()])).IsValid.Should().BeFalse();
        validator.Validate(new PreviewAllHabitSuggestionsCommand(_userId, [Draft()], "fr")).IsValid.Should().BeFalse();
        validator.Validate(new PreviewAllHabitSuggestionsCommand(_userId, [Draft(), null!])).Errors.Should().Contain(error => error.PropertyName == "Drafts[1]");
        validator.Validate(new PreviewAllHabitSuggestionsCommand(_userId, [Draft(), Draft() with { DueEndTime = new(20, 0) }])).Errors
            .Should().Contain(error => error.PropertyName == "Drafts[1]" && error.ErrorCode == "HABIT_SUGGESTION_INVALID");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PreviewAll_ChecksEveryDraftBeforeCallingExecutor(int failingIndex)
    {
        var drafts = Enumerable.Range(0, 3).Select(index => Draft($"Habit {index}") with
        { DueTime = new(18 + index, 0), DueEndTime = new(18 + index, 15) }).ToArray();
        drafts[failingIndex] = drafts[failingIndex] with { ExistingHabitId = Guid.NewGuid() };
        var executor = Substitute.For<IAgentOperationExecutor>();
        var store = Substitute.For<IPendingAgentOperationStore>();
        var changes = new AccountChangeSetService(store, Substitute.For<IPendingOperationChangePreviewer>(),
            Substitute.For<IAgentCatalogService>(), new AiToolRegistry([]));
        var result = await new PreviewAllHabitSuggestionsCommandHandler(Service(), executor, changes, store)
            .Handle(new(_userId, drafts), default);
        result.Should().BeOfType<HabitSuggestionPreviewFailure>().Which.DraftIndex.Should().Be(failingIndex);
        result.ErrorCode.Should().Be("HABIT_SUGGESTION_UNAVAILABLE");
        executor.ReceivedCalls().Should().BeEmpty();
        store.ReceivedCalls().Should().BeEmpty();
    }

    private static AgentCapability Capability(string id) => new(id, id, id, "chat", "test_scope", AgentRiskClass.Low,
        IsMutation: true, IsPhaseOneReadOnly: false, AgentConfirmationRequirement.FreshConfirmation);

    /// <summary>
    /// Two drafts prepare two previews in the real store, and a concurrent write makes the store refuse the bind of
    /// their change set. The call created both members, so it closes both, and the change set closes too.
    /// </summary>
    [Fact]
    public async Task PreviewAll_AFailedChangeSetBindClosesEveryMemberThatTheCallCreated()
    {
        var binding = new ConcurrentChangeSetBind();
        using var database = binding.CreateContext();
        var store = new PendingAgentOperationStore(database, Options.Create(new AgentPlatformSettings()));
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<AgentExecuteOperationRequest>();
            var arguments = request.Arguments.GetRawText();
            var fingerprint = AgentOperationFingerprint.Compute(request.OperationId, arguments);
            var pending = store.Create(_userId, Capability(request.OperationId), request.OperationId, arguments,
                request.OperationId, fingerprint, AgentExecutionSurface.Chat);
            store.Revise(_userId, pending.Id, fingerprint, arguments, fingerprint, "member-preview").Should().BeTrue();
            return new AgentExecuteOperationResponse(new AgentOperationResult(request.OperationId, request.OperationId,
                AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation),
                PendingOperation: pending);
        });
        var previewer = Substitute.For<IPendingOperationChangePreviewer>();
        previewer.PreviewAsync(_userId, Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>(),
                Arg.Any<AgentClientContext?>())
            .Returns(Result.Success<PendingOperationChangePreview?>(new PendingOperationChangePreview([], 1,
                [new PendingOperationItem("0", null, "Read", [], "state")], "member-preview")));
        var catalog = Substitute.For<IAgentCatalogService>();
        catalog.GetCapability(AccountChangeSet.CapabilityId).Returns(Capability(AccountChangeSet.CapabilityId));
        var changes = new AccountChangeSetService(store, previewer, catalog,
            new AiToolRegistry([new BulkCreateHabitsTool(Substitute.For<IMediator>())]));
        var drafts = new[] { Draft(), Draft("Stretch") with { DueTime = new(21, 0), DueEndTime = new(21, 15) } };

        var preview = () => new PreviewAllHabitSuggestionsCommandHandler(Service(), executor, changes, store)
            .Handle(new(_userId, drafts), default);

        await preview.Should().ThrowAsync<InvalidOperationException>().WithMessage("The account change preview could not be bound.");
        var rows = await database.PendingAgentOperations.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle(row => row.OperationId == AccountChangeSet.OperationName)
            .Which.ConsumedAtUtc.Should().NotBeNull("the store refused the bind, so the change set closed");
        rows.Where(row => row.OperationId == "bulk_create_habits").Should().HaveCount(2)
            .And.OnlyContain(row => row.ConsumedAtUtc != null, "the call created both members, so it closes both");
    }
}
