using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.ChecklistTemplates;

public class UpdateChecklistTemplateCommandHandlerTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Handle_PartialUpdate_PreservesOmittedFieldsAndInvalidatesCache(bool rename, bool replaceItems)
    {
        var template = ChecklistTemplate.Create(Guid.NewGuid(), "Morning", ["Water"]).Value;
        var repo = Substitute.For<IGenericRepository<ChecklistTemplate>>();
        repo.FindOneTrackedAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(),
            cancellationToken: Arg.Any<CancellationToken>()).Returns(template);
        var work = Substitute.For<IUnitOfWork>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var key = ReferenceCacheKeys.ChecklistTemplates(template.UserId);
        cache.Set(key, "stale");
        var command = new UpdateChecklistTemplateCommand(template.UserId, template.Id,
            rename ? "Evening" : null, replaceItems ? ["Read"] : null);

        var result = await new UpdateChecklistTemplateCommandHandler(repo, work, cache).Handle(command, default);

        result.IsSuccess.Should().BeTrue(result.Error);
        template.Name.Should().Be(rename ? "Evening" : "Morning");
        template.Items.Should().Equal(replaceItems ? ["Read"] : ["Water"]);
        cache.TryGetValue(key, out _).Should().BeFalse();
        await work.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ForeignOrMissingTemplate_ReturnsNotFoundWithoutSaving()
    {
        var foreign = ChecklistTemplate.Create(Guid.NewGuid(), "Foreign", ["Water"]).Value;
        var repo = Substitute.For<IGenericRepository<ChecklistTemplate>>();
        repo.FindOneTrackedAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(),
            cancellationToken: Arg.Any<CancellationToken>()).Returns(call =>
                call.Arg<Expression<Func<ChecklistTemplate, bool>>>().Compile()(foreign) ? foreign : null);
        var work = Substitute.For<IUnitOfWork>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await new UpdateChecklistTemplateCommandHandler(repo, work, cache).Handle(
            new(Guid.NewGuid(), foreign.Id, "Changed"), default);
        result.IsFailure.Should().BeTrue();
        foreign.Name.Should().Be("Foreign");
        await work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_InvalidUpdate_DoesNotMutateOrSave(bool emptyItems)
    {
        var template = ChecklistTemplate.Create(Guid.NewGuid(), "Morning", ["Water"]).Value;
        var repo = Substitute.For<IGenericRepository<ChecklistTemplate>>();
        repo.FindOneTrackedAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(),
            cancellationToken: Arg.Any<CancellationToken>()).Returns(template);
        var work = Substitute.For<IUnitOfWork>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await new UpdateChecklistTemplateCommandHandler(repo, work, cache).Handle(
            new(template.UserId, template.Id, emptyItems ? "New" : null, emptyItems ? [] : null), default);
        result.IsFailure.Should().BeTrue();
        template.Name.Should().Be("Morning");
        template.Items.Should().Equal("Water");
        await work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
