using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.ChecklistTemplates;

public class GetChecklistTemplatesQueryHandlerTests
{
    private readonly IGenericRepository<ChecklistTemplate> _repo = Substitute.For<IGenericRepository<ChecklistTemplate>>();
    private readonly GetChecklistTemplatesQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetChecklistTemplatesQueryHandlerTests()
    {
        _handler = new GetChecklistTemplatesQueryHandler(_repo);
    }

    [Fact]
    public async Task Handle_WithTemplates_ReturnsOrderedList()
    {
        var t1 = ChecklistTemplate.Create(UserId, "Template A", ["Item 1"]).Value;
        var t2 = ChecklistTemplate.Create(UserId, "Template B", ["Item 2", "Item 3"]).Value;

        _repo.FindAsync(
            Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ChecklistTemplate> { t1, t2 });

        var query = new GetChecklistTemplatesQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].Name.Should().Be("Template A");
        result.Value[1].Name.Should().Be("Template B");
    }

    [Fact]
    public async Task Handle_NoTemplates_ReturnsEmptyList()
    {
        _repo.FindAsync(
            Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ChecklistTemplate>());

        var query = new GetChecklistTemplatesQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}
