using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.UserFacts.Commands;
using Orbit.Application.UserFacts.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class UserFactsControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<UserFactsController> _logger = Substitute.For<ILogger<UserFactsController>>();
    private readonly UserFactsController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public UserFactsControllerTests()
    {
        _controller = new UserFactsController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- GetUserFacts ---

    [Fact]
    public async Task GetUserFacts_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetUserFactsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<UserFactDto>>([]));

        var result = await _controller.GetUserFacts(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetUserFacts_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetUserFactsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<UserFactDto>>("Error"));

        var result = await _controller.GetUserFacts(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- DeleteUserFact ---

    [Fact]
    public async Task DeleteUserFact_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<DeleteUserFactCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.DeleteUserFact(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteUserFact_NotFound_ReturnsNotFound()
    {
        _mediator.Send(Arg.Any<DeleteUserFactCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Fact not found"));

        var result = await _controller.DeleteUserFact(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // --- BulkDeleteUserFacts ---

    [Fact]
    public async Task BulkDeleteUserFacts_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<BulkDeleteUserFactsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(3));

        var request = new UserFactsController.BulkDeleteUserFactsRequest([Guid.NewGuid(), Guid.NewGuid()]);
        var result = await _controller.BulkDeleteUserFacts(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task BulkDeleteUserFacts_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<BulkDeleteUserFactsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<int>("Error"));

        var request = new UserFactsController.BulkDeleteUserFactsRequest([Guid.NewGuid()]);
        var result = await _controller.BulkDeleteUserFacts(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
