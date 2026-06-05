using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class McpMutationExecutorRoutingTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"mcp-mutation-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private Guid _userId;

    public McpMutationExecutorRoutingTests(IntegrationTestWebApplicationFactory factory)
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

    private async Task AssertAuditRowAsync(IServiceScope scope, string capabilityId, string sourceName)
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var auditRow = await dbContext.AgentAuditLogs
            .Where(log => log.UserId == _userId
                && log.CapabilityId == capabilityId
                && log.Surface == AgentExecutionSurface.Mcp
                && log.OutcomeStatus == AgentOperationStatus.Succeeded
                && log.SourceName == sourceName)
            .OrderByDescending(log => log.CreatedAtUtc)
            .FirstOrDefaultAsync();

        auditRow.Should().NotBeNull();
    }

    // --- Tag: create_tag → tags.write (not plan-gated) ---

    [Fact]
    public async Task CreateTag_ViaMcpSurface_WritesAuditRow()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_tag",
            BuildArguments(new { name = $"Routed-{Guid.NewGuid():N}", color = "#FF5733" }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await AssertAuditRowAsync(scope, "tags.write", "create_tag");
    }

    // --- Tag (hard case #1): assign_tags id path → tags.write ---

    [Fact]
    public async Task AssignTags_ViaMcpSurface_ReplacesTagsByIdAndWritesAuditRow()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var habitId = await SeedHabitAsync(executor);
        var tagId = await SeedTagAsync(executor);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "assign_tags",
            BuildArguments(new { habit_id = habitId.ToString(), tag_ids = new[] { tagId.ToString() } }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await AssertAuditRowAsync(scope, "tags.write", "assign_tags");

        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var habit = await dbContext.Habits
            .Include(h => h.Tags)
            .FirstAsync(h => h.Id == habitId);
        habit.Tags.Select(t => t.Id).Should().ContainSingle().Which.Should().Be(tagId);
    }

    // --- Profile (mapped): update_profile_preferences → profile.preferences.write (not gated) ---

    [Fact]
    public async Task UpdateProfilePreferences_ViaMcpSurface_WritesAuditRow()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "update_profile_preferences",
            BuildArguments(new { action = "set_timezone", timezone = "America/Sao_Paulo" }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await AssertAuditRowAsync(scope, "profile.preferences.write", "update_profile_preferences");
    }

    // --- Notification (mapped): update_notifications → notifications.write (not gated) ---

    [Fact]
    public async Task UpdateNotifications_ViaMcpSurface_WritesAuditRow()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "update_notifications",
            BuildArguments(new { action = "mark_all_read" }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await AssertAuditRowAsync(scope, "notifications.write", "update_notifications");
    }

    // --- Referral (hard case #2): get_referral_code → referrals.write ---

    [Fact]
    public async Task GetReferralCode_ViaMcpSurface_WritesAuditRowAndReturnsCode()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "get_referral_code",
            BuildArguments(new { }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.Operation.TargetName.Should().NotBeNullOrWhiteSpace();
        await AssertAuditRowAsync(scope, "referrals.write", "get_referral_code");
    }

    // --- UserFact delete is Destructive: without a token → PendingConfirmation ---

    [Fact]
    public async Task DeleteUserFacts_ViaMcpSurface_WithoutToken_IsPendingConfirmation()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        // Seed an owned fact so the ownership pre-check passes and the confirmation gate is what fires.
        var factId = await SeedUserFactAsync(scope);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "delete_user_facts",
            BuildArguments(new { fact_id = factId.ToString() }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
    }

    // --- Read-only credential is denied across newly-routed toolsets (fires before any gate) ---

    [Fact]
    public async Task ReadOnlyCredential_IsDeniedAcrossNewlyRoutedToolsets()
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IAgentOperationExecutor>();

        var scopes = AgentScopes.ClaudeDefaultScopes;

        // assign_tags carries a habit target; seed an owned habit so the ownership pre-check passes
        // and the read-only-credential denial (the assertion target) is what fires. delete_user_facts
        // is invoked target-free so its ownership check is skipped for the same reason.
        var ownedHabitId = await SeedHabitAsync(executor);

        var cases = new (string OperationId, object Arguments)[]
        {
            ("create_tag", new { name = "Denied", color = "#FF0000" }),
            ("assign_tags", new { habit_id = ownedHabitId.ToString(), tag_ids = Array.Empty<string>() }),
            ("update_profile_preferences", new { action = "set_timezone", timezone = "America/Sao_Paulo" }),
            ("update_notifications", new { action = "mark_all_read" }),
            ("delete_user_facts", new { }),
            ("get_referral_code", new { })
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

    private async Task<Guid> SeedTagAsync(IAgentOperationExecutor executor)
    {
        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_tag",
            BuildArguments(new { name = $"Seed-{Guid.NewGuid():N}", color = "#123456" }),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            IsReadOnlyCredential: false));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        Guid.TryParse(response.Operation.TargetId, out var tagId).Should().BeTrue();
        return tagId;
    }

    private async Task<Guid> SeedUserFactAsync(IServiceScope scope)
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var fact = UserFact.Create(_userId, "Prefers morning workouts", "Fitness").Value;
        dbContext.UserFacts.Add(fact);
        await dbContext.SaveChangesAsync();
        return fact.Id;
    }
}
