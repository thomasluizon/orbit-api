using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class SyncChangesIndexConfigurationTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;

    public SyncChangesIndexConfigurationTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(databaseName: $"SyncIndexes_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    public static IEnumerable<object[]> ChangeQueryIndexCases()
    {
        yield return new object[] { typeof(Habit), new[] { "UserId", "UpdatedAtUtc" } };
        yield return new object[] { typeof(HabitLog), new[] { "HabitId", "UpdatedAtUtc" } };
        yield return new object[] { typeof(Goal), new[] { "UserId", "UpdatedAtUtc" } };
        yield return new object[] { typeof(GoalProgressLog), new[] { "GoalId", "UpdatedAtUtc" } };
        yield return new object[] { typeof(Tag), new[] { "UserId", "UpdatedAtUtc" } };
        yield return new object[] { typeof(Notification), new[] { "UserId", "UpdatedAtUtc" } };
        yield return new object[] { typeof(ChecklistTemplate), new[] { "UserId", "UpdatedAtUtc" } };
    }

    [Theory]
    [MemberData(nameof(ChangeQueryIndexCases))]
    public void SyncedEntity_DeclaresCompositeIndexMatchingChangeQuery(Type entityType, string[] expectedColumns)
    {
        var entity = _dbContext.Model.FindEntityType(entityType);
        entity.Should().NotBeNull();

        var indexColumnSets = entity!.GetIndexes()
            .Select(index => index.Properties.Select(property => property.Name).ToArray())
            .ToList();

        indexColumnSets.Should().ContainSingle(columns => columns.SequenceEqual(expectedColumns),
            $"GetChangesV2 filters {entityType.Name} by ({string.Join(", ", expectedColumns)}) and needs a leading-column btree index in that exact order");
    }

    [Fact]
    public void ProcessedRequest_ForeignKeyIsCoveredByCompositeIndexLeadingWithUserId()
    {
        var entity = _dbContext.Model.FindEntityType(typeof(ProcessedRequest));
        entity.Should().NotBeNull();

        var indexLeadingWithUserId = entity!.GetIndexes()
            .Any(index => index.Properties.Count > 1 && index.Properties[0].Name == "UserId");

        indexLeadingWithUserId.Should().BeTrue(
            "the composite unique index (UserId, IdempotencyKey, RequestType) already serves the UserId FK cascade, so no dedicated single-column index is needed");
    }
}
