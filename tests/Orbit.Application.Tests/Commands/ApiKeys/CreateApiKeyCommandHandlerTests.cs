using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.ApiKeys;

public class CreateApiKeyCommandHandlerTests
{
    private readonly IGenericRepository<ApiKey> _apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CreateApiKeyCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public CreateApiKeyCommandHandlerTests()
    {
        _handler = new CreateApiKeyCommandHandler(_apiKeyRepo, _payGate, _unitOfWork);

        _payGate.CanCreateApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Default: no existing keys
        _apiKeyRepo.FindAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey>());
    }

    [Fact]
    public async Task Handle_ValidCommand_CreatesKeyAndReturnsResponse()
    {
        var command = new CreateApiKeyCommand(UserId, "My API Key");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("My API Key");
        result.Value.Key.Should().StartWith("orb_");
        result.Value.KeyPrefix.Should().NotBeNullOrEmpty();
        result.Value.Id.Should().NotBeEmpty();
        await _apiKeyRepo.Received(1).AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PayGateFailure_ReturnsPayGateError()
    {
        _payGate.CanCreateApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("API keys are a Pro feature"));

        var command = new CreateApiKeyCommand(UserId, "My Key");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        await _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MaxKeysReached_ReturnsFailure()
    {
        // Create 5 existing keys (the max)
        var existingKeys = Enumerable.Range(0, 5)
            .Select(_ => ApiKey.Create(UserId, "Key").Value.Entity)
            .ToList();

        _apiKeyRepo.FindAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(existingKeys.AsReadOnly());

        var command = new CreateApiKeyCommand(UserId, "One too many");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("at most 5");
        await _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyName_ReturnsFailure()
    {
        var command = new CreateApiKeyCommand(UserId, "");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("name");
        await _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }
}
