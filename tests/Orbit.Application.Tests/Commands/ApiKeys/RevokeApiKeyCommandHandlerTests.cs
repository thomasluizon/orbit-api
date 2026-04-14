using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.ApiKeys;

public class RevokeApiKeyCommandHandlerTests
{
    private readonly IGenericRepository<ApiKey> _apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly RevokeApiKeyCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public RevokeApiKeyCommandHandlerTests()
    {
        _payGate.CanManageApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _handler = new RevokeApiKeyCommandHandler(_apiKeyRepo, _payGate, _unitOfWork);
    }

    [Fact]
    public async Task Handle_ValidCommand_RevokesKey()
    {
        var (apiKey, _) = ApiKey.Create(UserId, "My Key").Value;

        _apiKeyRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { apiKey });

        var command = new RevokeApiKeyCommand(UserId, apiKey.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        apiKey.IsRevoked.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_KeyNotFound_ReturnsFailure()
    {
        _apiKeyRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey>());

        var command = new RevokeApiKeyCommand(UserId, Guid.NewGuid());
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.ApiKeyNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.ApiKeyNotFound);
    }
}
