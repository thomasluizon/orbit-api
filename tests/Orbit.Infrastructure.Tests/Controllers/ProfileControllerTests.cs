using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Models;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ProfileControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<ProfileController> _logger = Substitute.For<ILogger<ProfileController>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly ProfileController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public ProfileControllerTests()
    {
        _controller = new ProfileController(_mediator, _logger, _userDateService);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

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
            .Returns(Result.Failure<ProfileResponse>(ErrorMessages.UserNotFound));

        var result = await _controller.GetProfile(CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ExportUserData_Names_The_File_With_The_Users_Today()
    {
        _mediator.Send(Arg.Any<ExportUserDataQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(UserDataExport)!));
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 12, 31));

        var result = await _controller.ExportUserData(CancellationToken.None);

        result.Should().BeOfType<FileContentResult>()
            .Which.FileDownloadName.Should().Be("orbit-data-export-2026-12-31.json");
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ApplyOnboarding_Success_ReturnsOkWithResponse()
    {
        var response = new ApplyOnboardingResponse(true, 2, false, true);
        _mediator.Send(Arg.Any<ApplyOnboardingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var request = new ProfileController.ApplyOnboardingRequest(
            [new ApplyHabitInput("Drink water", null, null, null, null)], null, null, 1, "purple");
        var result = await _controller.ApplyOnboarding(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(response);
    }

    [Fact]
    public async Task ApplyOnboarding_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<ApplyOnboardingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ApplyOnboardingResponse>("Error"));

        var request = new ProfileController.ApplyOnboardingRequest(null, null, null, null, null);
        var result = await _controller.ApplyOnboarding(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task DismissImportPrompt_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<DismissImportPromptCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.DismissImportPrompt(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DismissImportPrompt_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<DismissImportPromptCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var result = await _controller.DismissImportPrompt(CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }
}
