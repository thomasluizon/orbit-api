using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.UserFacts.Commands;
using Orbit.Application.UserFacts.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Mcp;

public class UserFactToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly UserFactTools _tools;
    private readonly ClaimsPrincipal _user;

    public UserFactToolsTests()
    {
        _tools = new UserFactTools(_mediator);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task GetUserFacts_Success_ReturnsFormattedFacts()
    {
        var facts = new List<UserFactDto>
        {
            new(Guid.NewGuid(), "Prefers morning workouts", "Fitness", DateTime.UtcNow, null)
        };
        _mediator.Send(Arg.Any<GetUserFactsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<UserFactDto>>(facts));

        var result = await _tools.GetUserFacts(_user);

        result.Should().Contain("Prefers morning workouts");
        result.Should().Contain("[Fitness]");
        result.Should().Contain("User Facts (1)");
    }

    [Fact]
    public async Task GetUserFacts_Empty_ReturnsNoFactsMessage()
    {
        _mediator.Send(Arg.Any<GetUserFactsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<UserFactDto>>([]));

        var result = await _tools.GetUserFacts(_user);

        result.Should().Contain("No user facts stored");
    }

    [Fact]
    public async Task GetUserFacts_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetUserFactsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<UserFactDto>>("Error"));

        var result = await _tools.GetUserFacts(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task DeleteUserFact_Success_ReturnsDeletedMessage()
    {
        _mediator.Send(Arg.Any<DeleteUserFactCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var factId = Guid.NewGuid();
        var result = await _tools.DeleteUserFact(_user, factId.ToString());

        result.Should().Contain("Deleted user fact");
        result.Should().Contain(factId.ToString());
    }

    [Fact]
    public async Task DeleteUserFact_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<DeleteUserFactCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Fact not found"));

        var result = await _tools.DeleteUserFact(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }
}
