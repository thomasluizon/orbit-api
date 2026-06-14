using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.ChecklistTemplates;

public class DeleteChecklistTemplateCommandHandlerTests
{
    private readonly IGenericRepository<ChecklistTemplate> _repo = Substitute.For<IGenericRepository<ChecklistTemplate>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly DeleteChecklistTemplateCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public DeleteChecklistTemplateCommandHandlerTests()
    {
        _handler = new DeleteChecklistTemplateCommandHandler(_repo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_ExistingTemplate_DeletesAndReturnsSuccess()
    {
        var template = ChecklistTemplate.Create(UserId, "My Template", ["Item 1"]).Value;
        _repo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(),
            Arg.Any<Func<IQueryable<ChecklistTemplate>, IQueryable<ChecklistTemplate>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(template);

        var command = new DeleteChecklistTemplateCommand(UserId, template.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        template.IsDeleted.Should().BeTrue();
        template.DeletedAtUtc.Should().NotBeNull();
        _repo.DidNotReceive().Remove(Arg.Any<ChecklistTemplate>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TemplateNotFound_ReturnsFailure()
    {
        var command = new DeleteChecklistTemplateCommand(UserId, Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
        _repo.DidNotReceive().Remove(Arg.Any<ChecklistTemplate>());
    }
}
