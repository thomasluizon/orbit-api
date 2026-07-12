using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Models;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

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
    public async Task VerifyCode_Failure_ReturnsUnauthorizedWithErrorCode()
    {
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>(ErrorMessages.InvalidVerificationCode));

        var request = new AuthController.VerifyCodeRequest("test@example.com", "000000");
        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(401);
        objectResult.Value.Should().BeEquivalentTo(new
        {
            error = ErrorMessages.InvalidVerificationCode.Message,
            errorCode = ErrorMessages.InvalidVerificationCode.Code
        });
    }

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
    public async Task GoogleAuth_Failure_ReturnsUnauthorizedWithErrorCode()
    {
        _mediator.Send(Arg.Any<GoogleAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>(ErrorMessages.InvalidGoogleToken));

        var request = new AuthController.GoogleAuthRequest("invalid-token");
        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(401);
        objectResult.Value.Should().BeEquivalentTo(new
        {
            error = ErrorMessages.InvalidGoogleToken.Message,
            errorCode = ErrorMessages.InvalidGoogleToken.Code
        });
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Refresh_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<RefreshSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RefreshSessionResponse("token", "refresh")));

        var request = new AuthController.RefreshSessionRequest("refresh");
        var result = await _controller.Refresh(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Refresh_Failure_ReturnsUnauthorized()
    {
        _mediator.Send(Arg.Any<RefreshSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RefreshSessionResponse>("Invalid refresh token"));

        var request = new AuthController.RefreshSessionRequest("bad");
        var result = await _controller.Refresh(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Logout_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<LogoutSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new AuthController.LogoutSessionRequest("refresh");
        var result = await _controller.Logout(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Logout_Failure_ReturnsUnauthorized()
    {
        _mediator.Send(Arg.Any<LogoutSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid refresh token"));

        var request = new AuthController.LogoutSessionRequest("bad");
        var result = await _controller.Logout(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public void Logout_IsAnnotatedAllowAnonymous_SoItIsReachableWithoutAuth()
    {
        var method = typeof(AuthController).GetMethod(nameof(AuthController.Logout));

        method.Should().NotBeNull();
        method!.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true)
            .Should().NotBeNull("logout must be reachable without an authenticated session");
        method.GetCustomAttribute<AuthorizeAttribute>(inherit: true)
            .Should().BeNull("logout must not require authentication");
    }

    [Fact]
    public async Task SendCodeOperation_Success_ReturnsOkAndRecordsAudit()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new AuthController.SendCodeOperationRequest("test@example.com");
        var result = await _controller.SendCodeOperation(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _auditService.Received(1).RecordAsync(Arg.Any<AgentAuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendCodeOperation_Failure_ReturnsBadRequestAndRecordsAudit()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Failed to send"));

        var request = new AuthController.SendCodeOperationRequest("test@example.com");
        var result = await _controller.SendCodeOperation(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
        await _auditService.Received(1).RecordAsync(Arg.Any<AgentAuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyCodeOperation_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(Guid.NewGuid(), "token", "Name", "e@x.com", false, "refresh")));

        var request = new AuthController.VerifyCodeOperationRequest("test@example.com", "123456");
        var result = await _controller.VerifyCodeOperation(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _auditService.Received(1).RecordAsync(Arg.Any<AgentAuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyCodeOperation_Failure_ReturnsUnauthorized()
    {
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>("Invalid code"));

        var request = new AuthController.VerifyCodeOperationRequest("test@example.com", "000000");
        var result = await _controller.VerifyCodeOperation(request, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task GoogleAuthOperation_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GoogleAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(Guid.NewGuid(), "token", "Name", "e@x.com", false, "refresh")));

        var request = new AuthController.GoogleAuthOperationRequest("google-token");
        var result = await _controller.GoogleAuthOperation(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _auditService.Received(1).RecordAsync(Arg.Any<AgentAuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleAuthOperation_Failure_ReturnsUnauthorized()
    {
        _mediator.Send(Arg.Any<GoogleAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>("Invalid token"));

        var request = new AuthController.GoogleAuthOperationRequest("invalid-token");
        var result = await _controller.GoogleAuthOperation(request, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task RefreshOperation_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<RefreshSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RefreshSessionResponse("token", "refresh")));

        var request = new AuthController.RefreshSessionOperationRequest("refresh");
        var result = await _controller.RefreshOperation(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RefreshOperation_Failure_ReturnsUnauthorized()
    {
        _mediator.Send(Arg.Any<RefreshSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RefreshSessionResponse>("Invalid refresh token"));

        var request = new AuthController.RefreshSessionOperationRequest("bad");
        var result = await _controller.RefreshOperation(request, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task LogoutOperation_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<LogoutSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new AuthController.LogoutSessionOperationRequest("refresh");
        var result = await _controller.LogoutOperation(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task LogoutOperation_Failure_ReturnsUnauthorized()
    {
        _mediator.Send(Arg.Any<LogoutSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid refresh token"));

        var request = new AuthController.LogoutSessionOperationRequest("bad");
        var result = await _controller.LogoutOperation(request, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }
}
