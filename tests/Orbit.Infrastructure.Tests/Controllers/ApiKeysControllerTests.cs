using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ApiKeysControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<ApiKeysController> _logger = Substitute.For<ILogger<ApiKeysController>>();
    private readonly ApiKeysController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public ApiKeysControllerTests()
    {
        _controller = new ApiKeysController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- CreateApiKey ---

    [Fact]
    public async Task CreateApiKey_Success_ReturnsCreated()
    {
        var response = new CreateApiKeyResponse(Guid.NewGuid(), "Test Key", "orb_abc123", "orb_abc", DateTime.UtcNow);
        _mediator.Send(Arg.Any<CreateApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var request = new ApiKeysController.CreateApiKeyRequest("Test Key");
        var result = await _controller.CreateApiKey(request, CancellationToken.None);

        result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task CreateApiKey_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<CreateApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<CreateApiKeyResponse>("Pro required"));

        var request = new ApiKeysController.CreateApiKeyRequest("Test Key");
        var result = await _controller.CreateApiKey(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task CreateApiKey_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<CreateApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CreateApiKeyResponse>("Max keys reached"));

        var request = new ApiKeysController.CreateApiKeyRequest("Test Key");
        var result = await _controller.CreateApiKey(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- GetApiKeys ---

    [Fact]
    public async Task GetApiKeys_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetApiKeysQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ApiKeyResponse>>([]));

        var result = await _controller.GetApiKeys(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetApiKeys_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetApiKeysQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<ApiKeyResponse>>("Error"));

        var result = await _controller.GetApiKeys(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- RevokeApiKey ---

    [Fact]
    public async Task RevokeApiKey_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<RevokeApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.RevokeApiKey(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task RevokeApiKey_NotFound_ReturnsNotFound()
    {
        _mediator.Send(Arg.Any<RevokeApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("API key not found"));

        var result = await _controller.RevokeApiKey(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }
}
