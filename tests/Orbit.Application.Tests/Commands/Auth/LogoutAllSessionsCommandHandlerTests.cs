using FluentAssertions;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class LogoutAllSessionsCommandHandlerTests
{
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();
    private readonly LogoutAllSessionsCommandHandler _handler;

    public LogoutAllSessionsCommandHandlerTests()
    {
        var keys = Substitute.For<IGenericRepository<ApiKey>>();
        keys.FindTrackedAsync(Arg.Any<System.Linq.Expressions.Expression<Func<ApiKey, bool>>>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ApiKey>());
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result>>>()(call.Arg<CancellationToken>()));
        _handler = new LogoutAllSessionsCommandHandler(_authSessionService, keys, unitOfWork);
    }

    [Fact]
    public async Task Handle_PropagatesFailure()
    {
        var userId = Guid.NewGuid();
        _authSessionService.RevokeAllSessionsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("User ID is required.", "USER_ID_REQUIRED"));

        var result = await _handler.Handle(new LogoutAllSessionsCommand(userId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("USER_ID_REQUIRED");
    }
}
