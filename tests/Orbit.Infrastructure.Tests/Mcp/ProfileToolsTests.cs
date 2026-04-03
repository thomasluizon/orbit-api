using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Mcp;

public class ProfileToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ProfileTools _tools;
    private readonly ClaimsPrincipal _user;

    public ProfileToolsTests()
    {
        _tools = new ProfileTools(_mediator);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task GetProfile_Success_ReturnsFormattedProfile()
    {
        var profile = new ProfileResponse(
            "Thomas", "thomas@example.com", "America/Sao_Paulo",
            true, true, true, "pt-BR", "Pro", true, false, null, null,
            5, 100, false, false, null, false, 1, 500, 5, "Achiever",
            0, 10, 2, null, null);

        _mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(profile));

        var result = await _tools.GetProfile(_user);

        result.Should().Contain("Thomas");
        result.Should().Contain("thomas@example.com");
        result.Should().Contain("Pro");
        result.Should().Contain("Monday");
    }

    [Fact]
    public async Task GetProfile_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ProfileResponse>("User not found"));

        var result = await _tools.GetProfile(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SetTimezone_Success_ReturnsConfirmation()
    {
        _mediator.Send(Arg.Any<SetTimezoneCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.SetTimezone(_user, "America/New_York");

        result.Should().Contain("Timezone set to America/New_York");
    }

    [Fact]
    public async Task SetTimezone_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<SetTimezoneCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid timezone"));

        var result = await _tools.SetTimezone(_user, "Invalid/Zone");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SetLanguage_Success_ReturnsConfirmation()
    {
        _mediator.Send(Arg.Any<SetLanguageCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.SetLanguage(_user, "pt-BR");

        result.Should().Contain("Language set to pt-BR");
    }

    [Fact]
    public async Task SetLanguage_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<SetLanguageCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid language"));

        var result = await _tools.SetLanguage(_user, "xx");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SetAiMemory_Enabled_ReturnsEnabledMessage()
    {
        _mediator.Send(Arg.Any<SetAiMemoryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.SetAiMemory(_user, true);

        result.Should().Be("AI memory enabled");
    }

    [Fact]
    public async Task SetAiMemory_Disabled_ReturnsDisabledMessage()
    {
        _mediator.Send(Arg.Any<SetAiMemoryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.SetAiMemory(_user, false);

        result.Should().Be("AI memory disabled");
    }

    [Fact]
    public async Task SetAiMemory_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<SetAiMemoryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Not found"));

        var result = await _tools.SetAiMemory(_user, true);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SetAiSummary_Enabled_ReturnsEnabledMessage()
    {
        _mediator.Send(Arg.Any<SetAiSummaryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.SetAiSummary(_user, true);

        result.Should().Be("AI summary enabled");
    }

    [Fact]
    public async Task SetAiSummary_Disabled_ReturnsDisabledMessage()
    {
        _mediator.Send(Arg.Any<SetAiSummaryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.SetAiSummary(_user, false);

        result.Should().Be("AI summary disabled");
    }

    [Fact]
    public async Task SetAiSummary_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<SetAiSummaryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Not found"));

        var result = await _tools.SetAiSummary(_user, true);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SetWeekStartDay_Sunday_ReturnsSundayMessage()
    {
        _mediator.Send(Arg.Any<SetWeekStartDayCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.SetWeekStartDay(_user, 0);

        result.Should().Contain("Sunday");
    }

    [Fact]
    public async Task SetWeekStartDay_Monday_ReturnsMondayMessage()
    {
        _mediator.Send(Arg.Any<SetWeekStartDayCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.SetWeekStartDay(_user, 1);

        result.Should().Contain("Monday");
    }

    [Fact]
    public async Task SetWeekStartDay_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<SetWeekStartDayCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid"));

        var result = await _tools.SetWeekStartDay(_user, 5);

        result.Should().StartWith("Error: ");
    }
}
