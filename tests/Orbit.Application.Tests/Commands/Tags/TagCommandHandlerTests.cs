using FluentAssertions;
using NSubstitute;
using Orbit.Application.Tags.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Tags;

public class TagCommandHandlerTests
{
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IAppConfigService _appConfigService = Substitute.For<IAppConfigService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    // ----- CreateTag -----

    [Fact]
    public async Task CreateTag_Valid_CreatesAndSaves()
    {
        _tagRepo.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tag>());

        var handler = new CreateTagCommandHandler(_tagRepo, _unitOfWork);
        var command = new CreateTagCommand(UserId, "Fitness", "#ff0000");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        await _tagRepo.Received(1).AddAsync(
            Arg.Is<Tag>(t => t.Name == "Fitness" && t.UserId == UserId),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTag_DuplicateName_ReturnsFailure()
    {
        var existingTag = Tag.Create(UserId, "Fitness", "#00ff00").Value;
        _tagRepo.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tag> { existingTag });

        var handler = new CreateTagCommandHandler(_tagRepo, _unitOfWork);
        var command = new CreateTagCommand(UserId, "Fitness", "#ff0000");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
    }

    // ----- UpdateTag -----

    [Fact]
    public async Task UpdateTag_Valid_UpdatesAndSaves()
    {
        var tag = Tag.Create(UserId, "Old Name", "#000000").Value;
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(tag);
        _tagRepo.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tag>());

        var handler = new UpdateTagCommandHandler(_tagRepo, _unitOfWork);
        var command = new UpdateTagCommand(UserId, tag.Id, "New Name", "#ffffff");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        tag.Name.Should().Be("New name");
        tag.Color.Should().Be("#ffffff");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTag_NotFound_ReturnsFailure()
    {
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Tag?)null);

        var handler = new UpdateTagCommandHandler(_tagRepo, _unitOfWork);
        var command = new UpdateTagCommand(UserId, Guid.NewGuid(), "Name", "#fff");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Tag not found.");
    }

    // ----- DeleteTag -----

    [Fact]
    public async Task DeleteTag_Valid_RemovesAndSaves()
    {
        var tag = Tag.Create(UserId, "ToDelete", "#ff0000").Value;
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(tag);

        var handler = new DeleteTagCommandHandler(_tagRepo, _unitOfWork);
        var command = new DeleteTagCommand(UserId, tag.Id);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _tagRepo.Received(1).Remove(tag);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteTag_NotFound_ReturnsFailure()
    {
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Tag?)null);

        var handler = new DeleteTagCommandHandler(_tagRepo, _unitOfWork);
        var command = new DeleteTagCommand(UserId, Guid.NewGuid());

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Tag not found.");
    }

    // ----- AssignTags -----

    [Fact]
    public async Task AssignTags_Valid_AssignsAndSaves()
    {
        var tag = Tag.Create(UserId, "Health", "#00ff00").Value;
        var habit = Habit.Create(new HabitCreateParams(UserId, "Exercise", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _appConfigService.GetAsync("MaxTagsPerHabit", 5, Arg.Any<CancellationToken>())
            .Returns(5);
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _tagRepo.FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tag> { tag });

        var handler = new AssignTagsCommandHandler(_habitRepo, _tagRepo, _appConfigService, _unitOfWork);
        var command = new AssignTagsCommand(UserId, habit.Id, new List<Guid> { tag.Id });

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.Tags.Should().Contain(tag);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignTags_ExceedsMaxTags_ReturnsFailure()
    {
        _appConfigService.GetAsync("MaxTagsPerHabit", 5, Arg.Any<CancellationToken>())
            .Returns(2);

        var tagIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var handler = new AssignTagsCommandHandler(_habitRepo, _tagRepo, _appConfigService, _unitOfWork);
        var command = new AssignTagsCommand(UserId, Guid.NewGuid(), tagIds);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("at most 2 tags");
    }

    [Fact]
    public async Task AssignTags_HabitNotFound_ReturnsFailure()
    {
        _appConfigService.GetAsync("MaxTagsPerHabit", 5, Arg.Any<CancellationToken>())
            .Returns(5);
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var handler = new AssignTagsCommandHandler(_habitRepo, _tagRepo, _appConfigService, _unitOfWork);
        var command = new AssignTagsCommand(UserId, Guid.NewGuid(), new List<Guid> { Guid.NewGuid() });

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Habit not found.");
    }
}
