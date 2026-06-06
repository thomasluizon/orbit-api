using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.IntegrationTests;

/// <summary>
/// Routing, audit, step-up, and read-only-credential coverage for the manager MCP tools newly routed
/// through <see cref="IAgentOperationExecutor"/> (subscription, account, api-keys, support,
/// calendar-sync, push, checklist-templates) plus the previously-bypassing bulk log/skip habit ops.
/// Drives the executor directly with <see cref="AgentExecutionSurface.Mcp"/>, mirroring
/// <c>McpMutationExecutorRoutingTests</c>.
/// </summary>
[Collection("Sequential")]
public class McpManagerExecutorRoutingTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"mcp-manager-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private Guid _userId;

    public McpManagerExecutorRoutingTests(IntegrationTestWebApplicationFactory factory)
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

    private static JsonElement BuildArguments(object value) => JsonSerializer.SerializeToElement(value);

    private async Task AssertAuditRowAsync(IServiceScope scope, string capabilityId, string sourceName, AgentOperationStatus outcomeStatus)
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var auditRow = await dbContext.AgentAuditLogs
            .Where(log => log.UserId == _userId
                && log.CapabilityId == capabilityId
                && log.Surface == AgentExecutionSurface.Mcp
                && log.OutcomeStatus == outcomeStatus
                && log.SourceName == sourceName)
            .OrderByDescending(log => log.CreatedAtUtc)
            .FirstOrDefaultAsync();

        auditRow.Should().NotBeNull();
    }

    [Fact]
    public async Task SendSupportRequest_ViaMcpSurface_WritesAuditRow()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "send_support_request",
            BuildArguments(new
            {
                name = "Test User",
                email = _email,
                subject = "MCP routed support",
                message = "Routed through the executor."
            }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await AssertAuditRowAsync(scope, "support.write", "send_support_request", AgentOperationStatus.Succeeded);
    }

    [Fact]
    public async Task CreateAndDeleteChecklistTemplate_ViaMcpSurface_WritesAuditRows()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var createResponse = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_checklist_template",
            BuildArguments(new { name = $"Routed-{Guid.NewGuid():N}", items = new[] { "step one", "step two" } }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        createResponse.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await AssertAuditRowAsync(scope, "checklist-templates.write", "create_checklist_template", AgentOperationStatus.Succeeded);

        Guid.TryParse(createResponse.Operation.TargetId, out var templateId).Should().BeTrue();

        var deleteResponse = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "delete_checklist_template",
            BuildArguments(new { template_id = templateId.ToString() }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        deleteResponse.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await AssertAuditRowAsync(scope, "checklist-templates.write", "delete_checklist_template", AgentOperationStatus.Succeeded);
    }

    [Fact]
    public async Task SubscribePush_ViaMcpSurface_WritesAuditRow()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "update_notifications",
            BuildArguments(new
            {
                action = "subscribe_push",
                endpoint = $"https://push.example.test/{Guid.NewGuid():N}",
                p256dh = "fake-p256dh-not-a-real-key",
                auth = "fake-auth-not-a-real-key"
            }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await AssertAuditRowAsync(scope, "notifications.write", "update_notifications", AgentOperationStatus.Succeeded);
    }

    [Fact]
    public async Task BulkLogHabits_ViaMcpSurface_EnforcesConfirmationGateAndWritesAuditRow()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var habitId = await SeedHabitAsync(executor);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "bulk_log_habits",
            BuildArguments(new { habit_ids = new[] { habitId.ToString() } }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        await AssertAuditRowAsync(scope, "habits.bulk.write", "bulk_log_habits", AgentOperationStatus.PendingConfirmation);
    }

    [Fact]
    public async Task ManageSubscription_ViaMcpSurface_WithoutToken_RequiresStepUp()
    {
        await GrantProAccessAsync();

        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "manage_subscription",
            BuildArguments(new { action = "create_portal" }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        response.Operation.PolicyReason.Should().Be("step_up_required");
    }

    [Fact]
    public async Task ReadOnlyCredential_IsDeniedAcrossManagerToolsets_IncludingBulkLogAndStepUp()
    {
        await GrantProAccessAsync();

        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var ownedHabitId = await SeedHabitAsync(executor);

        var scopes = AgentScopes.ClaudeDefaultScopes
            .Append(AgentScopes.ManageSubscriptions)
            .Append(AgentScopes.WriteSupport)
            .ToList();

        var cases = new (string OperationId, object Arguments)[]
        {
            ("bulk_log_habits", new { habit_ids = new[] { ownedHabitId.ToString() } }),
            ("manage_subscription", new { action = "create_portal" }),
            ("send_support_request", new { name = "Test User", email = _email, subject = "Denied", message = "Denied." })
        };

        foreach (var (operationId, arguments) in cases)
        {
            var denial = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
                _userId,
                operationId,
                BuildArguments(arguments),
                AgentExecutionSurface.Mcp,
                AgentAuthMethod.ApiKey,
                scopes,
                IsReadOnlyCredential: true));

            denial.Operation.Status.Should().Be(AgentOperationStatus.Denied, $"{operationId} should be denied for a read-only credential");
            denial.Operation.PolicyReason.Should().Be("read_only_credential", $"{operationId} denial reason");
        }
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

    private async Task GrantProAccessAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var user = await dbContext.Users.SingleAsync(item => item.Id == _userId);
        user.StartTrial(DateTime.UtcNow.AddDays(1));
        await dbContext.SaveChangesAsync();
    }
}
