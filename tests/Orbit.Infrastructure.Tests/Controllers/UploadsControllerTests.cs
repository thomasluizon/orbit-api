using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Uploads.Commands;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class UploadsControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<UploadsController> _logger = Substitute.For<ILogger<UploadsController>>();
    private readonly UploadsController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public UploadsControllerTests()
    {
        _controller = new UploadsController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public async Task SignUpload_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<SignUploadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SignUploadResponse("key", "https://signed", "https://public")));

        var request = new UploadsController.SignUploadRequest("image/png", 1024);
        var result = await _controller.SignUpload(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SignUpload_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SignUploadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SignUploadResponse>("Image too large"));

        var request = new UploadsController.SignUploadRequest("image/png", 99999999);
        var result = await _controller.SignUpload(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SignUpload_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<SignUploadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<SignUploadResponse>("Pro required"));

        var request = new UploadsController.SignUploadRequest("image/png", 1024);
        var result = await _controller.SignUpload(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(403);
    }
}
