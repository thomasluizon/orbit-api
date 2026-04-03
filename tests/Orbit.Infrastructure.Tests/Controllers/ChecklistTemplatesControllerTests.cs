using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ChecklistTemplatesControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<ChecklistTemplatesController> _logger = Substitute.For<ILogger<ChecklistTemplatesController>>();
    private readonly ChecklistTemplatesController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public ChecklistTemplatesControllerTests()
    {
        _controller = new ChecklistTemplatesController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- GetTemplates ---

    [Fact]
    public async Task GetTemplates_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetChecklistTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ChecklistTemplateResponse>>([]));

        var result = await _controller.GetTemplates(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetTemplates_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetChecklistTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<ChecklistTemplateResponse>>("Error"));

        var result = await _controller.GetTemplates(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- CreateTemplate ---

    [Fact]
    public async Task CreateTemplate_Success_ReturnsCreated()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateChecklistTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var request = new ChecklistTemplatesController.CreateTemplateRequest("Morning Routine", ["Brush teeth", "Exercise"]);
        var result = await _controller.CreateTemplate(request, CancellationToken.None);

        result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task CreateTemplate_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<CreateChecklistTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Validation failed"));

        var request = new ChecklistTemplatesController.CreateTemplateRequest("", []);
        var result = await _controller.CreateTemplate(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- DeleteTemplate ---

    [Fact]
    public async Task DeleteTemplate_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<DeleteChecklistTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.DeleteTemplate(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteTemplate_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<DeleteChecklistTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Template not found"));

        var result = await _controller.DeleteTemplate(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
