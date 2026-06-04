using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class PendingOperationsControllerTests : IAsyncLifetime
{
    private const string CreateApiKeyOperationId = "manage_api_keys";
    private const string CreateApiKeyArgumentsJson = "{\"action\":\"create\",\"name\":\"Claude\"}";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"pendingops-test-{Guid.NewGuid()}@integration.test";
    private Guid _userId;

    public PendingOperationsControllerTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        IntegrationTestHelpers.RegisterTestAccount(_email, TestCode);
    }

    public async Task InitializeAsync()
    {
        var login = await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _email, TestCode, JsonOptions);
        _userId = login.UserId;

        // The api_keys.manage capability is plan-gated to Pro; an active trial grants Pro access
        // so policy evaluation reaches the step-up gate instead of denying on plan.
        await GrantProAccessAsync(_factory, _userId);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    // ── confirm ──────────────────────────────────────────────

    [Fact]
    public async Task Confirm_PersistsConfirmationToken()
    {
        var pendingOperationId = SeedStepUpPendingOperation(_factory, _userId);

        var response = await _client.PostAsync($"/api/ai/pending-operations/{pendingOperationId}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmation = await response.Content.ReadFromJsonAsync<ConfirmResponse>(JsonOptions);
        confirmation.Should().NotBeNull();
        confirmation!.PendingOperationId.Should().Be(pendingOperationId);
        confirmation.ConfirmationToken.Should().NotBeNullOrEmpty();

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var stored = await dbContext.PendingAgentOperations
            .AsNoTracking()
            .SingleAsync(item => item.Id == pendingOperationId);
        stored.ConfirmedAtUtc.Should().NotBeNull();
        stored.ConfirmationTokenHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Confirm_UnknownId_ReturnsNotFound()
    {
        var response = await _client.PostAsync($"/api/ai/pending-operations/{Guid.NewGuid()}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        body!.Error.Should().NotBeNullOrEmpty();
    }

    // ── step-up ──────────────────────────────────────────────

    [Fact]
    public async Task StepUp_IssuesChallenge_AndEmailsCode()
    {
        var pendingOperationId = SeedStepUpPendingOperation(_factory, _userId);

        var response = await PostStepUpAsync(pendingOperationId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var challenge = await response.Content.ReadFromJsonAsync<StepUpChallengeResponse>(JsonOptions);
        challenge.Should().NotBeNull();
        challenge!.ChallengeId.Should().NotBeEmpty();
        challenge.PendingOperationId.Should().Be(pendingOperationId);

        _factory.Email.LastVerificationCode.Should().NotBeNull();
        _factory.Email.LastVerificationCode.Should().HaveLength(6);
    }

    [Fact]
    public async Task StepUp_SecondRequestWithinCooldown_ReturnsBadRequest()
    {
        var pendingOperationId = SeedStepUpPendingOperation(_factory, _userId);

        var first = await PostStepUpAsync(pendingOperationId);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await PostStepUpAsync(pendingOperationId);

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await second.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        body!.Error.Should().Be("Please wait before requesting another step-up code.");
    }

    // ── step-up/verify ───────────────────────────────────────

    [Fact]
    public async Task StepUpVerify_ValidCode_Succeeds()
    {
        var pendingOperationId = SeedStepUpPendingOperation(_factory, _userId);
        var challenge = await IssueChallengeAsync(pendingOperationId);

        var response = await PostStepUpVerifyAsync(pendingOperationId, challenge.ChallengeId, _factory.Email.LastVerificationCode!);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var stored = await dbContext.PendingAgentOperations
            .AsNoTracking()
            .SingleAsync(item => item.Id == pendingOperationId);
        stored.StepUpSatisfiedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task StepUpVerify_InvalidCode_ReturnsBadRequest()
    {
        var pendingOperationId = SeedStepUpPendingOperation(_factory, _userId);
        var challenge = await IssueChallengeAsync(pendingOperationId);

        var response = await PostStepUpVerifyAsync(pendingOperationId, challenge.ChallengeId, "000000");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        body!.Error.Should().Be("Invalid step-up code.");
    }

    // ── execute ──────────────────────────────────────────────

    [Fact]
    public async Task Execute_WithoutStepUpSatisfied_IsNotExecuted()
    {
        var pendingOperationId = SeedStepUpPendingOperation(_factory, _userId);
        var confirmationToken = await ConfirmAsync(pendingOperationId);

        var response = await PostExecuteAsync(pendingOperationId, confirmationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var execution = await response.Content.ReadFromJsonAsync<ExecuteResponse>(JsonOptions);
        execution.Should().NotBeNull();
        execution!.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        execution.Operation.PolicyReason.Should().Be("step_up_required");
    }

    [Fact]
    public async Task Execute_AfterConfirmAndStepUp_Succeeds()
    {
        var pendingOperationId = SeedStepUpPendingOperation(_factory, _userId);
        var confirmationToken = await ConfirmAsync(pendingOperationId);
        var challenge = await IssueChallengeAsync(pendingOperationId);

        var verifyResponse = await PostStepUpVerifyAsync(pendingOperationId, challenge.ChallengeId, _factory.Email.LastVerificationCode!);
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await PostExecuteAsync(pendingOperationId, confirmationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var execution = await response.Content.ReadFromJsonAsync<ExecuteResponse>(JsonOptions);
        execution.Should().NotBeNull();
        execution!.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, "policy reason was: {0}", execution.Operation.PolicyReason);

        var keysResponse = await _client.GetAsync("/api/api-keys");
        keysResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var keys = await keysResponse.Content.ReadFromJsonAsync<List<ApiKeyListItem>>(JsonOptions);
        keys.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Execute_UnknownId_ReturnsNotFound()
    {
        var response = await PostExecuteAsync(Guid.NewGuid(), "x");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        body!.Error.Should().NotBeNullOrEmpty();
    }

    // ── helpers ──────────────────────────────────────────────

    private static Guid SeedStepUpPendingOperation(IntegrationTestWebApplicationFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IAgentCatalogService>();
        var store = scope.ServiceProvider.GetRequiredService<IPendingAgentOperationStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        // ArgumentsJson is a jsonb column, so reading it back yields Postgres-normalized text
        // (reordered keys, spaces after separators). The execute-time fingerprint is derived
        // from that normalized text, so the seed must store args already in jsonb-canonical
        // form for the fingerprint to match and confirmation to be consumable.
        var canonicalArguments = NormalizeToJsonb(dbContext, CreateApiKeyArgumentsJson);

        var capability = catalog.GetCapability(AgentCapabilityIds.ApiKeysManage)!;
        var pendingOperation = store.Create(
            userId,
            capability,
            CreateApiKeyOperationId,
            canonicalArguments,
            "Create API key",
            $"{CreateApiKeyOperationId}:{canonicalArguments}",
            AgentExecutionSurface.Chat);

        return pendingOperation.Id;
    }

    private static string NormalizeToJsonb(OrbitDbContext dbContext, string json)
    {
        var connection = dbContext.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
            connection.Open();

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT (@value)::jsonb::text";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@value";
            parameter.Value = json;
            command.Parameters.Add(parameter);
            return (string)command.ExecuteScalar()!;
        }
        finally
        {
            if (wasClosed)
                connection.Close();
        }
    }

    private static async Task GrantProAccessAsync(IntegrationTestWebApplicationFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var user = await dbContext.Users.SingleAsync(item => item.Id == userId);
        user.StartTrial(DateTime.UtcNow.AddDays(1));
        await dbContext.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> PostStepUpAsync(Guid pendingOperationId)
        => _client.PostAsJsonAsync($"/api/ai/pending-operations/{pendingOperationId}/step-up", new { });

    private Task<HttpResponseMessage> PostStepUpVerifyAsync(Guid pendingOperationId, Guid challengeId, string code)
        => _client.PostAsJsonAsync(
            $"/api/ai/pending-operations/{pendingOperationId}/step-up/verify",
            new { challengeId, code });

    private Task<HttpResponseMessage> PostExecuteAsync(Guid pendingOperationId, string confirmationToken)
        => _client.PostAsJsonAsync(
            $"/api/ai/pending-operations/{pendingOperationId}/execute",
            new { confirmationToken });

    private async Task<string> ConfirmAsync(Guid pendingOperationId)
    {
        var response = await _client.PostAsync($"/api/ai/pending-operations/{pendingOperationId}/confirm", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmation = await response.Content.ReadFromJsonAsync<ConfirmResponse>(JsonOptions);
        return confirmation!.ConfirmationToken;
    }

    private async Task<StepUpChallengeResponse> IssueChallengeAsync(Guid pendingOperationId)
    {
        var response = await PostStepUpAsync(pendingOperationId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var challenge = await response.Content.ReadFromJsonAsync<StepUpChallengeResponse>(JsonOptions);
        return challenge!;
    }

    // ── DTOs ─────────────────────────────────────────────────

    private record ConfirmResponse(Guid PendingOperationId, string ConfirmationToken, DateTime ExpiresAtUtc);
    private record StepUpChallengeResponse(Guid ChallengeId, Guid PendingOperationId, DateTime ExpiresAtUtc);
    private record OperationResult(AgentOperationStatus Status, string? PolicyReason);
    private record ExecuteResponse(OperationResult Operation);
    private record ApiKeyListItem(Guid Id, string Name);
    private record ErrorResponse(string Error);

    // The auth limiter (5/min, partitioned by user) is shared across every step-up and
    // verify call. Exhausting it on the class-wide account would bleed 429s into the other
    // pending-ops tests whenever they share a clock-minute, so this case gets its own user
    // and its own exhausted bucket.
    [Collection("Sequential")]
    public sealed class RateLimitTests : IAsyncLifetime
    {
        private readonly IntegrationTestWebApplicationFactory _factory;
        private readonly HttpClient _client;
        private readonly string _email = $"pendingops-ratelimit-test-{Guid.NewGuid()}@integration.test";
        private Guid _userId;
        private Guid _pendingOperationId;
        private Guid _challengeId;

        public RateLimitTests(IntegrationTestWebApplicationFactory factory)
        {
            _factory = factory;
            _client = factory.CreateClient();
            IntegrationTestHelpers.RegisterTestAccount(_email, TestCode);
        }

        public async Task InitializeAsync()
        {
            var login = await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _email, TestCode, JsonOptions);
            _userId = login.UserId;
            await GrantProAccessAsync(_factory, _userId);

            _pendingOperationId = SeedStepUpPendingOperation(_factory, _userId);

            var challengeResponse = await _client.PostAsJsonAsync(
                $"/api/ai/pending-operations/{_pendingOperationId}/step-up",
                new { });
            challengeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var challenge = await challengeResponse.Content.ReadFromJsonAsync<StepUpChallengeResponse>(JsonOptions);
            _challengeId = challenge!.ChallengeId;
        }

        public Task DisposeAsync()
        {
            _client.Dispose();
            return Task.CompletedTask;
        }

        [Fact]
        public async Task StepUpVerify_ExceedsAuthRateLimit_ReturnsTooManyRequests()
        {
            // The auth limiter trips before the service-level max-attempts gate can ever be
            // reached over HTTP, so the HTTP boundary's repeated-verify protection is the rate
            // limiter, not max-attempts.
            HttpStatusCode? rateLimited = null;
            for (var attempt = 0; attempt < 6 && rateLimited is null; attempt++)
            {
                var response = await _client.PostAsJsonAsync(
                    $"/api/ai/pending-operations/{_pendingOperationId}/step-up/verify",
                    new { challengeId = _challengeId, code = "000000" });
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    rateLimited = response.StatusCode;
                else
                    response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }

            rateLimited.Should().Be(HttpStatusCode.TooManyRequests);
        }
    }
}
