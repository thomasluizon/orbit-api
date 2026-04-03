using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Mcp;

public class TagToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly TagTools _tools;
    private readonly ClaimsPrincipal _user;

    public TagToolsTests()
    {
        _tools = new TagTools(_mediator);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task ListTags_Success_ReturnsFormattedList()
    {
        var tags = new List<TagResponse>
        {
            new(Guid.NewGuid(), "Health", "#FF0000")
        };
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<TagResponse>>(tags));

        var result = await _tools.ListTags(_user);

        result.Should().Contain("Health");
        result.Should().Contain("#FF0000");
        result.Should().Contain("Tags (1)");
    }

    [Fact]
    public async Task ListTags_Empty_ReturnsNoTagsMessage()
    {
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<TagResponse>>([]));

        var result = await _tools.ListTags(_user);

        result.Should().Contain("No tags found");
    }

    [Fact]
    public async Task ListTags_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<TagResponse>>("Error"));

        var result = await _tools.ListTags(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task CreateTag_Success_ReturnsCreatedMessage()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var result = await _tools.CreateTag(_user, "Work", "#0000FF");

        result.Should().Contain("Created tag 'Work'");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task CreateTag_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Duplicate name"));

        var result = await _tools.CreateTag(_user, "Work", "#0000FF");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task UpdateTag_Success_ReturnsUpdatedMessage()
    {
        var tagId = Guid.NewGuid();
        _mediator.Send(Arg.Any<UpdateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.UpdateTag(_user, tagId.ToString(), "Updated", "#00FF00");

        result.Should().Contain("Updated tag");
    }

    [Fact]
    public async Task UpdateTag_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<UpdateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Tag not found"));

        var result = await _tools.UpdateTag(_user, Guid.NewGuid().ToString(), "Name", "#000");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task DeleteTag_Success_ReturnsDeletedMessage()
    {
        _mediator.Send(Arg.Any<DeleteTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.DeleteTag(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Deleted tag");
    }

    [Fact]
    public async Task DeleteTag_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<DeleteTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Tag not found"));

        var result = await _tools.DeleteTag(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task AssignTags_Success_WithTags_ReturnsAssignedMessage()
    {
        _mediator.Send(Arg.Any<AssignTagsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var tagId = Guid.NewGuid();
        var result = await _tools.AssignTags(_user, Guid.NewGuid().ToString(), tagId.ToString());

        result.Should().Contain("Assigned 1 tags");
    }

    [Fact]
    public async Task AssignTags_Success_Empty_ReturnsRemovedMessage()
    {
        _mediator.Send(Arg.Any<AssignTagsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.AssignTags(_user, Guid.NewGuid().ToString(), "");

        result.Should().Contain("Removed all tags");
    }

    [Fact]
    public async Task AssignTags_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<AssignTagsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Not found"));

        var result = await _tools.AssignTags(_user, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }
}
