using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AgentStepUpServiceTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;
    private readonly PendingAgentOperationStore _pendingOperationStore;
    private readonly AgentStepUpService _stepUpService;
    private readonly TestEmailService _emailService = new();
    private readonly AgentCatalogService _catalogService = new();
    private readonly Guid _userId;

    public AgentStepUpServiceTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"AgentStepUpServiceTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);

        var user = User.Create("Thomas", "thomas@test.com").Value;
        _userId = user.Id;

        _dbContext.Users.Add(user);
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("api_keys", true, "Pro", "API keys"));
        _dbContext.SaveChanges();

        var settings = Options.Create(new AgentPlatformSettings());
        _pendingOperationStore = new PendingAgentOperationStore(_dbContext, settings);
        _stepUpService = new AgentStepUpService(_dbContext, _emailService, settings);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task IssueAndVerifyChallenge_MarksPendingOperationStepUpSatisfied()
    {
        var capability = _catalogService.GetCapability(AgentCapabilityIds.ApiKeysManage)!;
        var pendingOperation = _pendingOperationStore.Create(
            _userId,
            capability,
            "create_api_key",
            "{\"name\":\"Claude\"}",
            "Create API key",
            "create_api_key:{\"name\":\"Claude\"}",
            AgentExecutionSurface.Chat);

        var challengeResult = await _stepUpService.IssueChallengeAsync(
            _userId,
            pendingOperation.Id,
            "en",
            CancellationToken.None);

        challengeResult.IsSuccess.Should().BeTrue();
        _emailService.LastVerificationCode.Should().NotBeNull();

        var verifyResult = await _stepUpService.VerifyChallengeAsync(
            _userId,
            pendingOperation.Id,
            challengeResult.Value.ChallengeId,
            _emailService.LastVerificationCode!,
            CancellationToken.None);

        verifyResult.IsSuccess.Should().BeTrue();
        verifyResult.Value.Id.Should().Be(pendingOperation.Id);

        var storedPendingOperation = await _dbContext.PendingAgentOperations.FirstAsync(item => item.Id == pendingOperation.Id);
        storedPendingOperation.StepUpSatisfiedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task VerifyChallenge_WithInvalidCode_Fails()
    {
        var capability = _catalogService.GetCapability(AgentCapabilityIds.ApiKeysManage)!;
        var pendingOperation = _pendingOperationStore.Create(
            _userId,
            capability,
            "create_api_key",
            "{\"name\":\"Claude\"}",
            "Create API key",
            "create_api_key:{\"name\":\"Claude\"}",
            AgentExecutionSurface.Chat);

        var challengeResult = await _stepUpService.IssueChallengeAsync(
            _userId,
            pendingOperation.Id,
            "en",
            CancellationToken.None);

        var verifyResult = await _stepUpService.VerifyChallengeAsync(
            _userId,
            pendingOperation.Id,
            challengeResult.Value.ChallengeId,
            "000000",
            CancellationToken.None);

        verifyResult.IsFailure.Should().BeTrue();
        verifyResult.Error.Should().Be("Invalid step-up code.");
    }

    private sealed class TestEmailService : IEmailService
    {
        public string? LastVerificationCode { get; private set; }

        public Task SendWelcomeEmailAsync(string toEmail, string userName, string language = "en", CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendVerificationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
        {
            LastVerificationCode = code;
            return Task.CompletedTask;
        }

        public Task SendSupportEmailAsync(string fromName, string fromEmail, string subject, string message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendAccountDeletionCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
