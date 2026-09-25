using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.ApiKeys.Services;
using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Services.ApiKeys;

public class ApiKeyManagementAuthorizationTests
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IAppConfigService _appConfigService = Substitute.For<IAppConfigService>();
    private readonly EmailChallengeService _challengeService;
    private readonly ApiKeyManagementAuthorization _authorization;

    private static readonly Guid UserId = Guid.NewGuid();

    public ApiKeyManagementAuthorizationTests()
    {
        _challengeService = new EmailChallengeService(_cache, TimeProvider.System);
        _authorization = new ApiKeyManagementAuthorization(_appConfigService, _challengeService);
        SwitchOn(true);
    }

    private void SwitchOn(bool enabled) => _appConfigService.GetAsync(
            AppConfigKeys.RequireApiKeyCreationStepUp,
            false,
            Arg.Any<CancellationToken>())
        .Returns(enabled);

    [Theory]
    [InlineData(AgentCapabilityIds.ApiKeysRead)]
    [InlineData(AgentCapabilityIds.ApiKeysManage)]
    public async Task GetRequiredConfirmationAsync_SwitchedOn_RaisesApiKeyCapabilitiesToStepUp(string capabilityId)
    {
        var requirement = await _authorization.GetRequiredConfirmationAsync(capabilityId, CancellationToken.None);

        requirement.Should().Be(AgentConfirmationRequirement.StepUp);
    }

    [Theory]
    [InlineData(AgentCapabilityIds.ApiKeysRead)]
    [InlineData(AgentCapabilityIds.ApiKeysManage)]
    public async Task GetRequiredConfirmationAsync_SwitchedOff_LeavesTheCatalogRequirement(string capabilityId)
    {
        SwitchOn(false);

        var requirement = await _authorization.GetRequiredConfirmationAsync(capabilityId, CancellationToken.None);

        requirement.Should().BeNull();
    }

    [Fact]
    public async Task GetRequiredConfirmationAsync_OtherCapability_NeverReadsTheSwitch()
    {
        var requirement = await _authorization.GetRequiredConfirmationAsync(
            AgentCapabilityIds.HabitsRead,
            CancellationToken.None);

        requirement.Should().BeNull();
        await _appConfigService.DidNotReceive().GetAsync(
            AppConfigKeys.RequireApiKeyCreationStepUp,
            false,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(AgentCapabilityIds.ApiKeysRead)]
    [InlineData(AgentCapabilityIds.ApiKeysManage)]
    public void OnStepUpVerified_ApiKeyCapability_OpensTheSameGrantTheHttpDoorOpens(string capabilityId)
    {
        _authorization.OnStepUpVerified(capabilityId, UserId);

        _authorization.HasGrant(UserId).Should().BeTrue();
        _challengeService.HasAuthorization(EmailChallengeOperation.ApiKeyManagement, UserId).Should().BeTrue();
    }

    [Fact]
    public void OnStepUpVerified_OtherCapability_OpensNothing()
    {
        _authorization.OnStepUpVerified(AgentCapabilityIds.HabitsDelete, UserId);

        _authorization.HasGrant(UserId).Should().BeFalse();
    }

    [Fact]
    public void TryConsumeGrant_SpendsTheGrantExactlyOnce()
    {
        _authorization.Grant(UserId, TimeSpan.FromMinutes(10));

        _authorization.TryConsumeGrant(UserId).Should().BeTrue();
        _authorization.TryConsumeGrant(UserId).Should().BeFalse();
        _authorization.HasGrant(UserId).Should().BeFalse();
    }

    [Fact]
    public void HasGrant_ReadingDoesNotSpendTheGrant()
    {
        _authorization.Grant(UserId, TimeSpan.FromMinutes(10));

        _authorization.HasGrant(UserId).Should().BeTrue();
        _authorization.HasGrant(UserId).Should().BeTrue();
        _authorization.TryConsumeGrant(UserId).Should().BeTrue();
    }
}
