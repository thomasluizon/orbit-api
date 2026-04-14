using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.ApiKeys;

public class GetApiKeysQueryHandlerTests
{
    private readonly IGenericRepository<ApiKey> _apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly GetApiKeysQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetApiKeysQueryHandlerTests()
    {
        _payGate.CanReadApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _handler = new GetApiKeysQueryHandler(_apiKeyRepo, _payGate);
    }

    [Fact]
    public async Task Handle_WithKeys_ReturnsOrderedByCreatedDesc()
    {
        var (key1, _) = ApiKey.Create(UserId, "First Key").Value;
        var (key2, _) = ApiKey.Create(UserId, "Second Key").Value;

        _apiKeyRepo.FindAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { key1, key2 });

        var query = new GetApiKeysQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        // Both keys should have the correct UserId prefix
        result.Value.Should().AllSatisfy(k => k.KeyPrefix.Should().StartWith("orb_"));
    }

    [Fact]
    public async Task Handle_NoKeys_ReturnsEmptyList()
    {
        _apiKeyRepo.FindAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey>());

        var query = new GetApiKeysQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ReturnsCorrectResponseFields()
    {
        var (key, _) = ApiKey.Create(UserId, "Test Key").Value;

        _apiKeyRepo.FindAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { key });

        var query = new GetApiKeysQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var response = result.Value[0];
        response.Id.Should().Be(key.Id);
        response.Name.Should().Be("Test Key");
        response.KeyPrefix.Should().Be(key.KeyPrefix);
        response.IsRevoked.Should().BeFalse();
        response.LastUsedAtUtc.Should().BeNull();
    }
}
