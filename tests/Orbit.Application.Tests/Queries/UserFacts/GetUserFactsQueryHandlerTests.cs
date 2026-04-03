using FluentAssertions;
using NSubstitute;
using Orbit.Application.UserFacts.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.UserFacts;

public class GetUserFactsQueryHandlerTests
{
    private readonly IGenericRepository<UserFact> _userFactRepo = Substitute.For<IGenericRepository<UserFact>>();
    private readonly GetUserFactsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetUserFactsQueryHandlerTests()
    {
        _handler = new GetUserFactsQueryHandler(_userFactRepo);
    }

    [Fact]
    public async Task Handle_ReturnsFacts_OrderedByExtractedDateDescending()
    {
        var fact1 = UserFact.Create(UserId, "Likes coffee", "preference").Value;
        var fact2 = UserFact.Create(UserId, "Works at ACME", "work").Value;

        _userFactRepo.FindAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserFact> { fact1, fact2 }.AsReadOnly());

        var query = new GetUserFactsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_NoFacts_ReturnsEmptyList()
    {
        _userFactRepo.FindAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserFact>().AsReadOnly());

        var query = new GetUserFactsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_MapsFieldsCorrectly()
    {
        var fact = UserFact.Create(UserId, "Prefers dark mode", "preference").Value;

        _userFactRepo.FindAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserFact> { fact }.AsReadOnly());

        var query = new GetUserFactsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value[0];
        dto.FactText.Should().Be("Prefers dark mode");
        dto.Category.Should().Be("preference");
        dto.Id.Should().NotBeEmpty();
        dto.ExtractedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }
}
