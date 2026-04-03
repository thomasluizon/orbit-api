using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ChatControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IImageValidationService _imageValidation = Substitute.For<IImageValidationService>();
    private readonly ILogger<ChatController> _logger = Substitute.For<ILogger<ChatController>>();
    private readonly ChatController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public ChatControllerTests()
    {
        _controller = new ChatController(_mediator, _imageValidation, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- ProcessChat ---

    [Fact]
    public async Task ProcessChat_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(ChatResponse)!));

        var result = await _controller.ProcessChat("Hello", null, null, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ProcessChat_EmptyMessage_ReturnsBadRequest()
    {
        var result = await _controller.ProcessChat("", null, null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ProcessChat_MessageTooLong_ReturnsBadRequest()
    {
        var longMessage = new string('a', 4001);
        var result = await _controller.ProcessChat(longMessage, null, null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ProcessChat_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<ChatResponse>("Pro required"));

        var result = await _controller.ProcessChat("Hello", null, null, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task ProcessChat_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ChatResponse>("Error"));

        var result = await _controller.ProcessChat("Hello", null, null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ProcessChat_InvalidChatHistory_ReturnsBadRequest()
    {
        var result = await _controller.ProcessChat("Hello", "not-valid-json{{{", null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ProcessChat_InvalidImage_ReturnsBadRequest()
    {
        var file = Substitute.For<IFormFile>();
        file.Length.Returns(100);
        file.FileName.Returns("test.txt");

        _imageValidation.ValidateAsync(file)
            .Returns(Result.Failure<(string MimeType, long Size)>("Invalid image format"));

        var result = await _controller.ProcessChat("Hello", null, file, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
