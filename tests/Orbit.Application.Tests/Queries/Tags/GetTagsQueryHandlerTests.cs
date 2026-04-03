using FluentAssertions;
using NSubstitute;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Tags;

public class GetTagsQueryHandlerTests
{
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly GetTagsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetTagsQueryHandlerTests()
    {
        _handler = new GetTagsQueryHandler(_tagRepo);
    }

    [Fact]
    public async Task Handle_ReturnsTags_OrderedByName()
    {
        var tags = new List<Tag>
        {
            Tag.Create(UserId, "Zzz", "#FF0000").Value,
            Tag.Create(UserId, "Aaa", "#00FF00").Value,
            Tag.Create(UserId, "Mmm", "#0000FF").Value
        };

        _tagRepo.FindAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(tags.AsReadOnly());

        var query = new GetTagsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
        result.Value[0].Name.Should().Be("Aaa");
        result.Value[1].Name.Should().Be("Mmm");
        result.Value[2].Name.Should().Be("Zzz");
    }

    [Fact]
    public async Task Handle_NoTags_ReturnsEmptyList()
    {
        _tagRepo.FindAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Tag>().AsReadOnly());

        var query = new GetTagsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_MapsFieldsCorrectly()
    {
        var tag = Tag.Create(UserId, "health", "#00FF00").Value;

        _tagRepo.FindAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Tag> { tag }.AsReadOnly());

        var query = new GetTagsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var response = result.Value[0];
        response.Name.Should().Be("Health");
        response.Color.Should().Be("#00FF00");
        response.Id.Should().NotBeEmpty();
    }
}
