using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.UserFacts.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.UserFacts;

public class UserFactCommandHandlerTests
{
    private readonly IGenericRepository<UserFact> _factRepo = Substitute.For<IGenericRepository<UserFact>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private static readonly Guid UserId = Guid.NewGuid();

    public UserFactCommandHandlerTests()
    {
        _payGate.CanManageUserFacts(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
    }

    [Fact]
    public async Task DeleteFact_Valid_SoftDeletesAndSaves()
    {
        var fact = UserFact.Create(UserId, "To delete", null).Value;
        _factRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<Func<IQueryable<UserFact>, IQueryable<UserFact>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(fact);

        var handler = new DeleteUserFactCommandHandler(_factRepo, _payGate, _unitOfWork, new MemoryCache(new MemoryCacheOptions()));
        var command = new DeleteUserFactCommand(UserId, fact.Id);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fact.IsDeleted.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDeleteFacts_Valid_SoftDeletesAllAndSaves()
    {
        var fact1 = UserFact.Create(UserId, "Fact one", null).Value;
        var fact2 = UserFact.Create(UserId, "Fact two", null).Value;

        _factRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserFact> { fact1, fact2 }.AsReadOnly());

        var handler = new BulkDeleteUserFactsCommandHandler(_factRepo, _payGate, _unitOfWork, new MemoryCache(new MemoryCacheOptions()));
        var command = new BulkDeleteUserFactsCommand(UserId, new List<Guid> { fact1.Id, fact2.Id });

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);
        fact1.IsDeleted.Should().BeTrue();
        fact2.IsDeleted.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
