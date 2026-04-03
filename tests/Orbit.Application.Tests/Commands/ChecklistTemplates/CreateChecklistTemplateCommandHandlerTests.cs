using FluentAssertions;
using NSubstitute;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.ChecklistTemplates;

public class CreateChecklistTemplateCommandHandlerTests
{
    private readonly IGenericRepository<ChecklistTemplate> _repo = Substitute.For<IGenericRepository<ChecklistTemplate>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CreateChecklistTemplateCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public CreateChecklistTemplateCommandHandlerTests()
    {
        _handler = new CreateChecklistTemplateCommandHandler(_repo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_ValidCommand_CreatesTemplateAndReturnsId()
    {
        var command = new CreateChecklistTemplateCommand(UserId, "Morning Routine", ["Brush teeth", "Shower"]);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        await _repo.Received(1).AddAsync(
            Arg.Is<ChecklistTemplate>(t => t.Name == "Morning Routine" && t.UserId == UserId),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyName_ReturnsFailure()
    {
        var command = new CreateChecklistTemplateCommand(UserId, "", ["Item 1"]);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("name");
        await _repo.DidNotReceive().AddAsync(Arg.Any<ChecklistTemplate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyItems_ReturnsFailure()
    {
        var command = new CreateChecklistTemplateCommand(UserId, "Template", []);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("item");
        await _repo.DidNotReceive().AddAsync(Arg.Any<ChecklistTemplate>(), Arg.Any<CancellationToken>());
    }
}
