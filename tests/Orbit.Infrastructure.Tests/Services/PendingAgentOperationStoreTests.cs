using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class PendingAgentOperationStoreTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;
    private readonly PendingAgentOperationStore _store;
    private readonly AgentCatalogService _catalogService = new();
    private readonly Guid _userId;

    public PendingAgentOperationStoreTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"PendingAgentOperationStoreTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
        var user = User.Create("Thomas", "thomas@test.com").Value;
        _userId = user.Id;
        _dbContext.Users.Add(user);
        _dbContext.SaveChanges();

        _store = new PendingAgentOperationStore(
            _dbContext,
            Options.Create(new AgentPlatformSettings()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetExecution_ReturnsStoredOperationPayload()
    {
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsDelete)!;
        var pendingOperation = _store.Create(
            _userId,
            capability,
            "delete_habit",
            "{\"habit_id\":\"habit-123\"}",
            "Delete habit",
            "delete_habit:{\"habit_id\":\"habit-123\"}",
            AgentExecutionSurface.Chat);

        var execution = _store.GetExecution(_userId, pendingOperation.Id);

        execution.Should().NotBeNull();
        execution!.PendingOperationId.Should().Be(pendingOperation.Id);
        execution.CapabilityId.Should().Be(AgentCapabilityIds.HabitsDelete);
        execution.OperationId.Should().Be("delete_habit");
        execution.Surface.Should().Be(AgentExecutionSurface.Chat);
        execution.Arguments.GetProperty("habit_id").GetString().Should().Be("habit-123");
    }
}
