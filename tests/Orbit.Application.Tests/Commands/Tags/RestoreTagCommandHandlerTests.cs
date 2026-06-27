using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Tags.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Tags;

public class RestoreTagCommandHandlerTests
{
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly RestoreTagCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public RestoreTagCommandHandlerTests()
    {
        _handler = new RestoreTagCommandHandler(_tagRepo, _unitOfWork);
    }

    private void SetupTags(params Tag[] tags)
    {
        _tagRepo.FindTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(tags.ToList());
    }

    [Fact]
    public async Task Handle_RestoresTagAndSaves()
    {
        var tag = Tag.Create(UserId, "Fitness", "#FF0000").Value;
        tag.SoftDelete();
        SetupTags(tag);

        var result = await _handler.Handle(new RestoreTagCommand(UserId, tag.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        tag.IsDeleted.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TagNotDeleted_ReturnsFailure()
    {
        var tag = Tag.Create(UserId, "Fitness", "#FF0000").Value;
        SetupTags(tag);

        var result = await _handler.Handle(new RestoreTagCommand(UserId, tag.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Tag not found.");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TagNotFound_ReturnsFailure()
    {
        SetupTags();

        var result = await _handler.Handle(new RestoreTagCommand(UserId, Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Tag not found.");
    }
}
