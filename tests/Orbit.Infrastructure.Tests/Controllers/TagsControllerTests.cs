using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class TagsControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<TagsController> _logger = Substitute.For<ILogger<TagsController>>();
    private readonly TagsController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public TagsControllerTests()
    {
        _controller = new TagsController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- GetTags ---

    [Fact]
    public async Task GetTags_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<TagResponse>>([]));

        var result = await _controller.GetTags(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetTags_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<TagResponse>>("Error"));

        var result = await _controller.GetTags(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- CreateTag ---

    [Fact]
    public async Task CreateTag_Success_ReturnsCreated()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var request = new TagsController.CreateTagRequest("Work", "#FF0000");
        var result = await _controller.CreateTag(request, CancellationToken.None);

        result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task CreateTag_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Validation failed"));

        var request = new TagsController.CreateTagRequest("", "#FF0000");
        var result = await _controller.CreateTag(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- UpdateTag ---

    [Fact]
    public async Task UpdateTag_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<UpdateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new TagsController.UpdateTagRequest("Updated", "#00FF00");
        var result = await _controller.UpdateTag(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task UpdateTag_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<UpdateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Tag not found"));

        var request = new TagsController.UpdateTagRequest("Updated", "#00FF00");
        var result = await _controller.UpdateTag(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- DeleteTag ---

    [Fact]
    public async Task DeleteTag_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<DeleteTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.DeleteTag(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteTag_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<DeleteTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var result = await _controller.DeleteTag(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- AssignTags ---

    [Fact]
    public async Task AssignTags_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<AssignTagsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new TagsController.AssignTagsRequest([Guid.NewGuid()]);
        var result = await _controller.AssignTags(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task AssignTags_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<AssignTagsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new TagsController.AssignTagsRequest([Guid.NewGuid()]);
        var result = await _controller.AssignTags(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
