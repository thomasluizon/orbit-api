using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AuthControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IAgentAuditService _auditService = Substitute.For<IAgentAuditService>();
    private readonly ILogger<AuthController> _logger = Substitute.For<ILogger<AuthController>>();
    private readonly AuthController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public AuthControllerTests()
    {
        _controller = new AuthController(_mediator, _auditService, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- SendCode ---

    [Fact]
    public async Task SendCode_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new AuthController.SendCodeRequest("test@example.com");
        var result = await _controller.SendCode(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SendCode_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Failed to send"));

        var request = new AuthController.SendCodeRequest("test@example.com");
        var result = await _controller.SendCode(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- VerifyCode ---

    [Fact]
    public async Task VerifyCode_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(LoginResponse)!));

        var request = new AuthController.VerifyCodeRequest("test@example.com", "123456");
        var result = await _controller.VerifyCode(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task VerifyCode_Failure_ReturnsUnauthorized()
    {
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>("Invalid code"));

        var request = new AuthController.VerifyCodeRequest("test@example.com", "000000");
        var result = await _controller.VerifyCode(request, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    // --- GoogleAuth ---

    [Fact]
    public async Task GoogleAuth_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GoogleAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(LoginResponse)!));

        var request = new AuthController.GoogleAuthRequest("google-access-token");
        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GoogleAuth_Failure_ReturnsUnauthorized()
    {
        _mediator.Send(Arg.Any<GoogleAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>("Google auth failed"));

        var request = new AuthController.GoogleAuthRequest("invalid-token");
        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    // --- RequestDeletion ---

    [Fact]
    public async Task RequestDeletion_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<RequestAccountDeletionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.RequestDeletion(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RequestDeletion_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<RequestAccountDeletionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var result = await _controller.RequestDeletion(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- ConfirmDeletion ---

    [Fact]
    public async Task ConfirmDeletion_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<ConfirmAccountDeletionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(DateTime.UtcNow.AddDays(30)));

        var request = new AuthController.ConfirmDeletionRequest("123456");
        var result = await _controller.ConfirmDeletion(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ConfirmDeletion_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<ConfirmAccountDeletionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DateTime>("Invalid code"));

        var request = new AuthController.ConfirmDeletionRequest("000000");
        var result = await _controller.ConfirmDeletion(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
