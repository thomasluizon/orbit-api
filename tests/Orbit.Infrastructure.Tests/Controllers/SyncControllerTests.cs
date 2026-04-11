using FluentAssertions;
using static Orbit.Api.Controllers.SyncController;

namespace Orbit.Infrastructure.Tests.Controllers;

public class SyncControllerTests
{
    [Fact]
    public void SyncDeletedRef_SetsProperties()
    {
        var id = Guid.NewGuid();
        var deletedAt = DateTime.UtcNow;
        var deletedRef = new SyncDeletedRef(id, deletedAt);

        deletedRef.Id.Should().Be(id);
        deletedRef.DeletedAtUtc.Should().Be(deletedAt);
    }

    [Fact]
    public void SyncEntitySet_SetsProperties()
    {
        var updated = new List<object> { "item1" };
        var deleted = new List<SyncDeletedRef> { new(Guid.NewGuid(), DateTime.UtcNow) };
        var set = new SyncEntitySet(updated, deleted);

        set.Updated.Should().HaveCount(1);
        set.Deleted.Should().HaveCount(1);
    }

    [Fact]
    public void SyncMutation_SetsProperties()
    {
        var id = Guid.NewGuid();
        var mutation = new SyncMutation("habit", "delete", id, null);

        mutation.Entity.Should().Be("habit");
        mutation.Action.Should().Be("delete");
        mutation.Id.Should().Be(id);
        mutation.Data.Should().BeNull();
    }

    [Fact]
    public void SyncBatchRequest_SetsProperties()
    {
        var mutations = new List<SyncMutation> { new("habit", "delete", Guid.NewGuid(), null) };
        var request = new SyncBatchRequest(mutations);
        request.Mutations.Should().HaveCount(1);
    }

    [Fact]
    public void SyncBatchResponse_SetsProperties()
    {
        var results = new List<SyncMutationResult> { new(0, "success") };
        var response = new SyncBatchResponse(1, 0, results);

        response.Processed.Should().Be(1);
        response.Failed.Should().Be(0);
        response.Results.Should().HaveCount(1);
    }

    [Fact]
    public void SyncMutationResult_SetsProperties()
    {
        var result = new SyncMutationResult(0, "success");
        result.Index.Should().Be(0);
        result.Status.Should().Be("success");
        result.Error.Should().BeNull();
    }

    [Fact]
    public void SyncMutationResult_WithError_SetsError()
    {
        var result = new SyncMutationResult(1, "failed", "Something went wrong");
        result.Index.Should().Be(1);
        result.Status.Should().Be("failed");
        result.Error.Should().Be("Something went wrong");
    }

    [Fact]
    public void SyncChangesResponse_SetsAllProperties()
    {
        var emptySet = new SyncEntitySet([], []);
        var timestamp = DateTime.UtcNow;
        var response = new SyncChangesResponse(
            emptySet, emptySet, emptySet, emptySet,
            emptySet, emptySet, emptySet, timestamp);

        response.Habits.Updated.Should().BeEmpty();
        response.Goals.Deleted.Should().BeEmpty();
        response.ServerTimestamp.Should().Be(timestamp);
    }

    [Fact]
    public void SyncMutation_WithData_SetsDataDictionary()
    {
        var data = new Dictionary<string, object?> { ["title"] = "New Habit", ["description"] = null };
        var mutation = new SyncMutation("habit", "create", Guid.NewGuid(), data);

        mutation.Data.Should().NotBeNull();
        mutation.Data!["title"].Should().Be("New Habit");
        mutation.Data["description"].Should().BeNull();
    }

    [Fact]
    public void SyncEntitySet_EmptyCollections_WorksCorrectly()
    {
        var set = new SyncEntitySet([], []);
        set.Updated.Should().BeEmpty();
        set.Deleted.Should().BeEmpty();
    }
}
