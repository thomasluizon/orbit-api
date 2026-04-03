using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ProfileControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<ProfileController> _logger = Substitute.For<ILogger<ProfileController>>();
    private readonly ProfileController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public ProfileControllerTests()
    {
        _controller = new ProfileController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- GetProfile ---

    [Fact]
    public async Task GetProfile_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(ProfileResponse)!));

        var result = await _controller.GetProfile(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetProfile_NotFound_ReturnsNotFound()
    {
        _mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ProfileResponse>("User not found"));

        var result = await _controller.GetProfile(CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // --- SetTimezone ---

    [Fact]
    public async Task SetTimezone_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SetTimezoneCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new ProfileController.SetTimezoneRequest("America/Sao_Paulo");
        var result = await _controller.SetTimezone(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SetTimezone_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SetTimezoneCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid timezone"));

        var request = new ProfileController.SetTimezoneRequest("Invalid/TZ");
        var result = await _controller.SetTimezone(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- SetAiMemory ---

    [Fact]
    public async Task SetAiMemory_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SetAiMemoryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new ProfileController.SetAiMemoryRequest(true);
        var result = await _controller.SetAiMemory(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SetAiMemory_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SetAiMemoryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new ProfileController.SetAiMemoryRequest(true);
        var result = await _controller.SetAiMemory(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- SetAiSummary ---

    [Fact]
    public async Task SetAiSummary_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SetAiSummaryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new ProfileController.SetAiSummaryRequest(true);
        var result = await _controller.SetAiSummary(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SetAiSummary_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SetAiSummaryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new ProfileController.SetAiSummaryRequest(true);
        var result = await _controller.SetAiSummary(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- SetLanguage ---

    [Fact]
    public async Task SetLanguage_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SetLanguageCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new ProfileController.SetLanguageRequest("pt-BR");
        var result = await _controller.SetLanguage(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SetLanguage_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SetLanguageCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new ProfileController.SetLanguageRequest("invalid");
        var result = await _controller.SetLanguage(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- SetWeekStartDay ---

    [Fact]
    public async Task SetWeekStartDay_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SetWeekStartDayCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new ProfileController.SetWeekStartDayRequest(1);
        var result = await _controller.SetWeekStartDay(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SetWeekStartDay_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SetWeekStartDayCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new ProfileController.SetWeekStartDayRequest(99);
        var result = await _controller.SetWeekStartDay(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- SetThemePreference ---

    [Fact]
    public async Task SetThemePreference_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SetThemePreferenceCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new ProfileController.SetThemePreferenceRequest("dark");
        var result = await _controller.SetThemePreference(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SetThemePreference_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SetThemePreferenceCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new ProfileController.SetThemePreferenceRequest("invalid");
        var result = await _controller.SetThemePreference(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- SetColorScheme ---

    [Fact]
    public async Task SetColorScheme_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SetColorSchemeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new ProfileController.SetColorSchemeRequest("blue");
        var result = await _controller.SetColorScheme(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SetColorScheme_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SetColorSchemeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new ProfileController.SetColorSchemeRequest("invalid");
        var result = await _controller.SetColorScheme(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- CompleteOnboarding ---

    [Fact]
    public async Task CompleteOnboarding_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<CompleteOnboardingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.CompleteOnboarding(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task CompleteOnboarding_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<CompleteOnboardingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var result = await _controller.CompleteOnboarding(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- ResetAccount ---

    [Fact]
    public async Task ResetAccount_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<ResetAccountCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.ResetAccount(CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task ResetAccount_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<ResetAccountCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var result = await _controller.ResetAccount(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
