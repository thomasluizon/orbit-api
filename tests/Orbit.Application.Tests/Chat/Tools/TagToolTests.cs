using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class TagToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private static readonly Guid UserId = Guid.NewGuid();

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // --- ListTagsTool ---

    [Fact]
    public async Task ListTags_Success_ReturnsPayload()
    {
        IReadOnlyList<TagResponse> tags = [new TagResponse(Guid.NewGuid(), "Health", "#fff")];
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(tags));

        var result = await new ListTagsTool(_mediator).ExecuteAsync(Args("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Payload.Should().BeSameAs(tags);
    }

    [Fact]
    public async Task ListTags_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<TagResponse>>("boom"));

        var result = await new ListTagsTool(_mediator).ExecuteAsync(Args("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("boom");
    }

    // --- CreateTagTool ---

    [Fact]
    public async Task CreateTag_Success_ReturnsIdAndName()
    {
        var tagId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(tagId));

        var result = await new CreateTagTool(_mediator)
            .ExecuteAsync(Args("""{"name":"Health","color":"#FF0000"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(tagId.ToString());
        result.EntityName.Should().Be("Health");
    }

    [Fact]
    public async Task CreateTag_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("duplicate"));

        var result = await new CreateTagTool(_mediator)
            .ExecuteAsync(Args("""{"name":"Health","color":"#FF0000"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("duplicate");
    }

    [Fact]
    public async Task CreateTag_MissingColor_ReturnsErrorWithoutCallingMediator()
    {
        var result = await new CreateTagTool(_mediator)
            .ExecuteAsync(Args("""{"name":"Health"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>());
    }

    // --- UpdateTagTool ---

    [Fact]
    public async Task UpdateTag_Success_ReturnsIdAndName()
    {
        var tagId = Guid.NewGuid();
        _mediator.Send(Arg.Any<UpdateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await new UpdateTagTool(_mediator)
            .ExecuteAsync(Args($$"""{"tag_id":"{{tagId}}","name":"Renamed","color":"#00FF00"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(tagId.ToString());
        result.EntityName.Should().Be("Renamed");
    }

    [Fact]
    public async Task UpdateTag_InvalidId_ReturnsErrorWithoutCallingMediator()
    {
        var result = await new UpdateTagTool(_mediator)
            .ExecuteAsync(Args("""{"tag_id":"not-a-guid","name":"X","color":"#000"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("tag_id");
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateTagCommand>(), Arg.Any<CancellationToken>());
    }

    // --- DeleteTagTool ---

    [Fact]
    public async Task DeleteTag_Success_ReturnsId()
    {
        var tagId = Guid.NewGuid();
        _mediator.Send(Arg.Any<DeleteTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await new DeleteTagTool(_mediator)
            .ExecuteAsync(Args($$"""{"tag_id":"{{tagId}}"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(tagId.ToString());
    }

    [Fact]
    public async Task DeleteTag_MissingId_ReturnsErrorWithoutCallingMediator()
    {
        var result = await new DeleteTagTool(_mediator)
            .ExecuteAsync(Args("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<DeleteTagCommand>(), Arg.Any<CancellationToken>());
    }

    // --- ReorderGoalsTool ---

    [Fact]
    public async Task ReorderGoals_Success_ReturnsCount()
    {
        _mediator.Send(Arg.Any<ReorderGoalsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var goalId = Guid.NewGuid();

        var result = await new ReorderGoalsTool(_mediator)
            .ExecuteAsync(Args($$"""{"positions":[{"goal_id":"{{goalId}}","position":0}]}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("1 goals");
    }

    [Fact]
    public async Task ReorderGoals_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<ReorderGoalsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("paygate"));
        var goalId = Guid.NewGuid();

        var result = await new ReorderGoalsTool(_mediator)
            .ExecuteAsync(Args($$"""{"positions":[{"goal_id":"{{goalId}}","position":0}]}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("paygate");
    }

    [Fact]
    public async Task ReorderGoals_MissingPositions_ReturnsErrorWithoutCallingMediator()
    {
        var result = await new ReorderGoalsTool(_mediator)
            .ExecuteAsync(Args("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<ReorderGoalsCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReorderGoals_InvalidGoalId_ReturnsErrorWithoutCallingMediator()
    {
        var result = await new ReorderGoalsTool(_mediator)
            .ExecuteAsync(Args("""{"positions":[{"goal_id":"nope","position":0}]}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<ReorderGoalsCommand>(), Arg.Any<CancellationToken>());
    }

    // --- GetReferralCodeTool ---

    [Fact]
    public async Task GetReferralCode_Success_ReturnsCode()
    {
        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("ABC12345"));

        var result = await new GetReferralCodeTool(_mediator).ExecuteAsync(Args("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("ABC12345");
        var payload = JsonSerializer.Serialize(result.Payload);
        payload.Should().Contain("ABC12345");
    }

    [Fact]
    public async Task GetReferralCode_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("no user"));

        var result = await new GetReferralCodeTool(_mediator).ExecuteAsync(Args("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("no user");
    }
}
