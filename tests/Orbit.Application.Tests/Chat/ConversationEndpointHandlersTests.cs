using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat;

public class ConversationEndpointHandlersTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Latest_RequiresRecordingGate_AndScopesRequestToAccount(bool enabled)
    {
        var user = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var store = Substitute.For<IConversationTurnRecorder>();
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user, Arg.Any<CancellationToken>()).Returns(enabled ? new[] { FeatureFlagKeys.AstraConversationRecording } : []);
        store.GetLatestAsync(user, conversation, 9, 4, Arg.Any<CancellationToken>()).Returns(new ConversationPage(conversation, [], null));
        var result = await new GetLatestConversationQueryHandler(store, flags).Handle(new(user, conversation, 9, 4), default);
        result.IsSuccess.Should().Be(enabled);
        if (enabled) await store.Received(1).GetLatestAsync(user, conversation, 9, 4, Arg.Any<CancellationToken>());
        else
        {
            result.ErrorCode.Should().Be(ConversationErrors.NotFoundCode);
            await store.DidNotReceiveWithAnyArgs().GetLatestAsync(default, default, default, default);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_MapsMissingAndDeletedConversations(bool exists)
    {
        var user = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var store = Substitute.For<IConversationTurnRecorder>();
        store.DeleteAsync(user, conversation, Arg.Any<CancellationToken>()).Returns(exists);
        var result = await new DeleteConversationCommandHandler(store).Handle(new(user, conversation), default);
        result.IsSuccess.Should().Be(exists);
        if (!exists) result.ErrorCode.Should().Be(ConversationErrors.NotFoundCode);
        await store.Received(1).DeleteAsync(user, conversation, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, null, false)]
    [InlineData(101, null, false)]
    [InlineData(40, 0L, false)]
    [InlineData(40, -1L, false)]
    [InlineData(1, null, true)]
    [InlineData(100, 3L, true)]
    public void LatestValidator_BoundsPageAndCursor(int size, long? before, bool accepted) =>
        new GetLatestConversationQueryValidator().Validate(new GetLatestConversationQuery(Guid.NewGuid(), null, before, size)).IsValid.Should().Be(accepted);

    [Fact]
    public void Validators_RequireAccountConversationAndImageTurnIdentifiers()
    {
        new GetLatestConversationQueryValidator().Validate(new GetLatestConversationQuery(Guid.Empty)).IsValid.Should().BeFalse();
        new GetLatestConversationQueryValidator().Validate(new GetLatestConversationQuery(Guid.NewGuid(), Guid.Empty)).IsValid.Should().BeFalse();
        new DeleteConversationCommandValidator().Validate(new DeleteConversationCommand(Guid.NewGuid(), Guid.Empty)).IsValid.Should().BeFalse();
        new DeleteConversationCommandValidator().Validate(new DeleteConversationCommand(Guid.Empty, Guid.NewGuid())).IsValid.Should().BeFalse();
        new GetConversationImageQueryValidator().Validate(new GetConversationImageQuery(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty)).IsValid.Should().BeFalse();
    }
}
