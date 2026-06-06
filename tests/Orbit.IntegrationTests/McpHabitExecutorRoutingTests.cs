using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class McpHabitExecutorRoutingTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"mcp-executor-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private Guid _userId;

    public McpHabitExecutorRoutingTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        IntegrationTestHelpers.RegisterTestAccount(_email, TestCode);
    }

    public async Task InitializeAsync()
    {
        var login = await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _email, TestCode, JsonOptions);
        _userId = login.UserId;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    private static JsonElement BuildArguments(object value) =>
        JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task CreateHabit_ViaMcpSurface_WritesAuditRow()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            BuildArguments(new
            {
                title = "MCP routed habit",
                due_date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                frequency_unit = "Day"
            }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);

        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var auditRow = await dbContext.AgentAuditLogs
            .Where(log => log.UserId == _userId
                && log.CapabilityId == "habits.write"
                && log.Surface == AgentExecutionSurface.Mcp
                && log.OutcomeStatus == AgentOperationStatus.Succeeded)
            .OrderByDescending(log => log.CreatedAtUtc)
            .FirstOrDefaultAsync();

        auditRow.Should().NotBeNull();
        auditRow!.SourceName.Should().Be("create_habit");
    }

    [Fact]
    public async Task ReadOnlyCredential_IsDeniedAcrossDistinctToolsets()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var seededHabitId = await SeedHabitAsync(executor);

        var grantedScopes = AgentScopes.ClaudeDefaultScopes;

        var habitsWriteDenial = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            BuildArguments(new
            {
                title = "Denied habit",
                due_date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                frequency_unit = "Day"
            }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.ApiKey,
            grantedScopes,
            IsReadOnlyCredential: true));

        var goalsWriteDenial = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_goal",
            BuildArguments(new { title = "Denied goal" }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.ApiKey,
            grantedScopes,
            IsReadOnlyCredential: true));

        var bulkDeleteDenial = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "bulk_delete_habits",
            BuildArguments(new { habit_ids = new[] { seededHabitId.ToString() } }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.ApiKey,
            grantedScopes,
            IsReadOnlyCredential: true));

        habitsWriteDenial.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        habitsWriteDenial.Operation.PolicyReason.Should().Be("read_only_credential");

        goalsWriteDenial.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        goalsWriteDenial.Operation.PolicyReason.Should().Be("read_only_credential");

        bulkDeleteDenial.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        bulkDeleteDenial.Operation.PolicyReason.Should().Be("read_only_credential");
    }

    private async Task<Guid> SeedHabitAsync(IAgentOperationExecutor executor)
    {
        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            BuildArguments(new
            {
                title = "Seed habit",
                due_date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                frequency_unit = "Day"
            }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        Guid.TryParse(response.Operation.TargetId, out var habitId).Should().BeTrue();
        return habitId;
    }
}
