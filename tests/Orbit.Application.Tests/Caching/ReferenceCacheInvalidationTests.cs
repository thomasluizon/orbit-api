using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;
using Orbit.Application.UserFacts.Commands;
using Orbit.Application.UserFacts.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Caching;

public class ReferenceCacheInvalidationTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task Tags_Read_IsCached_AndMutationInvalidatesIt()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = Substitute.For<IGenericRepository<Tag>>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var tag = Tag.Create(UserId, "Health", "#00ff00").Value;

        repo.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tag> { tag }.AsReadOnly());
        repo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Tag, bool>>>(),
                Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(tag);

        var read = new GetTagsQueryHandler(repo, cache);
        await read.Handle(new GetTagsQuery(UserId), CancellationToken.None);
        await read.Handle(new GetTagsQuery(UserId), CancellationToken.None);
        await repo.Received(1).FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>());

        var delete = new DeleteTagCommandHandler(repo, unitOfWork, cache);
        (await delete.Handle(new DeleteTagCommand(UserId, tag.Id), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        await read.Handle(new GetTagsQuery(UserId), CancellationToken.None);
        await repo.Received(2).FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChecklistTemplates_Read_IsCached_AndCreateInvalidatesIt()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = Substitute.For<IGenericRepository<ChecklistTemplate>>();
        var unitOfWork = Substitute.For<IUnitOfWork>();

        repo.FindAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<ChecklistTemplate>)[]);

        var read = new GetChecklistTemplatesQueryHandler(repo, cache);
        await read.Handle(new GetChecklistTemplatesQuery(UserId), CancellationToken.None);
        await read.Handle(new GetChecklistTemplatesQuery(UserId), CancellationToken.None);
        await repo.Received(1).FindAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(), Arg.Any<CancellationToken>());

        var create = new CreateChecklistTemplateCommandHandler(repo, unitOfWork, cache);
        (await create.Handle(
                new CreateChecklistTemplateCommand(UserId, "Morning", ["Stretch", "Water"]),
                CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        await read.Handle(new GetChecklistTemplatesQuery(UserId), CancellationToken.None);
        await repo.Received(2).FindAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UserFacts_Read_IsCached_AndDeleteInvalidatesIt()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = Substitute.For<IGenericRepository<UserFact>>();
        var payGate = Substitute.For<IPayGateService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var fact = UserFact.Create(UserId, "User is a vegetarian", "context").Value;

        payGate.CanReadUserFacts(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        payGate.CanManageUserFacts(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        repo.FindAsync(Arg.Any<Expression<Func<UserFact, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserFact> { fact }.AsReadOnly());
        repo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<UserFact, bool>>>(),
                Arg.Any<Func<IQueryable<UserFact>, IQueryable<UserFact>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(fact);

        var read = new GetUserFactsQueryHandler(repo, payGate, cache);
        await read.Handle(new GetUserFactsQuery(UserId), CancellationToken.None);
        await read.Handle(new GetUserFactsQuery(UserId), CancellationToken.None);
        await repo.Received(1).FindAsync(Arg.Any<Expression<Func<UserFact, bool>>>(), Arg.Any<CancellationToken>());

        var delete = new DeleteUserFactCommandHandler(repo, payGate, unitOfWork, cache);
        (await delete.Handle(new DeleteUserFactCommand(UserId, fact.Id), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        await read.Handle(new GetUserFactsQuery(UserId), CancellationToken.None);
        await repo.Received(2).FindAsync(Arg.Any<Expression<Func<UserFact, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApiKeys_Read_IsCached_AndRevokeInvalidatesIt()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = Substitute.For<IGenericRepository<ApiKey>>();
        var payGate = Substitute.For<IPayGateService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var (apiKey, _) = ApiKey.Create(UserId, "Agent Key").Value;

        payGate.CanReadApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        payGate.CanManageApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        repo.FindAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { apiKey }.AsReadOnly());
        repo.FindTrackedAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { apiKey });

        var read = new GetApiKeysQueryHandler(repo, payGate, cache);
        await read.Handle(new GetApiKeysQuery(UserId), CancellationToken.None);
        await read.Handle(new GetApiKeysQuery(UserId), CancellationToken.None);
        await repo.Received(1).FindAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<CancellationToken>());

        var revoke = new RevokeApiKeyCommandHandler(repo, payGate, unitOfWork, cache);
        (await revoke.Handle(new RevokeApiKeyCommand(UserId, apiKey.Id), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        await read.Handle(new GetApiKeysQuery(UserId), CancellationToken.None);
        await repo.Received(2).FindAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<CancellationToken>());
    }
}
