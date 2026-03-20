using FluentAssertions;
using NSubstitute;
using Orbit.Application.UserFacts.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.UserFacts;

public class UserFactCommandHandlerTests
{
    private readonly IGenericRepository<UserFact> _factRepo = Substitute.For<IGenericRepository<UserFact>>();
    private readonly IAppConfigService _appConfigService = Substitute.For<IAppConfigService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private static readonly Guid UserId = Guid.NewGuid();

    // ----- CreateUserFact -----

    [Fact]
    public async Task CreateFact_Valid_CreatesAndSaves()
    {
        _appConfigService.GetAsync("MaxUserFacts", 50, Arg.Any<CancellationToken>())
            .Returns(50);
        _factRepo.FindAsync(Arg.Any<Expression<Func<UserFact, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserFact>());

        var handler = new CreateUserFactCommandHandler(_factRepo, _appConfigService, _unitOfWork);
        var command = new CreateUserFactCommand(UserId, "Likes running", "Hobbies");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        await _factRepo.Received(1).AddAsync(
            Arg.Is<UserFact>(f => f.FactText == "Likes running"),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFact_ExceedsMax50_ReturnsFailure()
    {
        _appConfigService.GetAsync("MaxUserFacts", 50, Arg.Any<CancellationToken>())
            .Returns(50);

        // Return 50 existing facts
        var existingFacts = Enumerable.Range(0, 50)
            .Select(i => UserFact.Create(UserId, $"Fact {i}", null).Value)
            .ToList();
        _factRepo.FindAsync(Arg.Any<Expression<Func<UserFact, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(existingFacts);

        var handler = new CreateUserFactCommandHandler(_factRepo, _appConfigService, _unitOfWork);
        var command = new CreateUserFactCommand(UserId, "One more fact", null);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("maximum");
    }

    [Fact]
    public async Task CreateFact_Duplicate_ReturnsFailure()
    {
        _appConfigService.GetAsync("MaxUserFacts", 50, Arg.Any<CancellationToken>())
            .Returns(50);

        var existingFact = UserFact.Create(UserId, "Likes running", null).Value;
        _factRepo.FindAsync(Arg.Any<Expression<Func<UserFact, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserFact> { existingFact });

        var handler = new CreateUserFactCommandHandler(_factRepo, _appConfigService, _unitOfWork);
        var command = new CreateUserFactCommand(UserId, "Likes running", null);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("similar fact already exists");
    }

    // ----- UpdateUserFact -----

    [Fact]
    public async Task UpdateFact_Valid_UpdatesAndSaves()
    {
        var fact = UserFact.Create(UserId, "Old text", "Category").Value;
        _factRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<Func<IQueryable<UserFact>, IQueryable<UserFact>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(fact);

        var handler = new UpdateUserFactCommandHandler(_factRepo, _unitOfWork);
        var command = new UpdateUserFactCommand(UserId, fact.Id, "New text", "Updated");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fact.FactText.Should().Be("New text");
        fact.Category.Should().Be("Updated");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateFact_NotFound_ReturnsFailure()
    {
        _factRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<Func<IQueryable<UserFact>, IQueryable<UserFact>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((UserFact?)null);

        var handler = new UpdateUserFactCommandHandler(_factRepo, _unitOfWork);
        var command = new UpdateUserFactCommand(UserId, Guid.NewGuid(), "Text", null);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Fact not found.");
    }

    // ----- DeleteUserFact -----

    [Fact]
    public async Task DeleteFact_Valid_SoftDeletesAndSaves()
    {
        var fact = UserFact.Create(UserId, "To delete", null).Value;
        _factRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<Func<IQueryable<UserFact>, IQueryable<UserFact>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(fact);

        var handler = new DeleteUserFactCommandHandler(_factRepo, _unitOfWork);
        var command = new DeleteUserFactCommand(UserId, fact.Id);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fact.IsDeleted.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ----- BulkDeleteUserFacts -----

    [Fact]
    public async Task BulkDeleteFacts_Valid_SoftDeletesAllAndSaves()
    {
        var fact1 = UserFact.Create(UserId, "Fact one", null).Value;
        var fact2 = UserFact.Create(UserId, "Fact two", null).Value;

        _factRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<Func<IQueryable<UserFact>, IQueryable<UserFact>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(fact1, fact2);

        var handler = new BulkDeleteUserFactsCommandHandler(_factRepo, _unitOfWork);
        var command = new BulkDeleteUserFactsCommand(UserId, new List<Guid> { fact1.Id, fact2.Id });

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);
        fact1.IsDeleted.Should().BeTrue();
        fact2.IsDeleted.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
