using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Commands;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class FeatureFlagManagementServiceTests : IDisposable
{
    private readonly SqliteOrbitDbContextFactory _factory = new();
    private readonly OrbitDbContext _context;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ILogger<FeatureFlagManagementService> _logger = Substitute.For<ILogger<FeatureFlagManagementService>>();
    private readonly FeatureFlagService _resolver;
    private readonly FeatureFlagManagementService _service;
    private readonly User _admin = User.Create("Admin", "admin@example.com").Value;
    private readonly User _account = User.Create("Account", "account@example.com").Value;
    private readonly User _other = User.Create("Other", "other@example.com").Value;

    public FeatureFlagManagementServiceTests()
    {
        _context = _factory.Context;
        _context.AppFeatureFlags.RemoveRange(_context.AppFeatureFlags);
        _admin.GrantAdmin();
        _context.Users.AddRange(_admin, _account, _other);
        _context.SaveChanges();
        _resolver = new FeatureFlagService(_context, _cache);
        _service = new FeatureFlagManagementService(_context, _resolver, _logger);
    }

    [Fact]
    public async Task Actions_CreateTargetToggleAndListThroughOneResolver()
    {
        var created = await Manage(FeatureFlagActions.Create, description: "A feature");
        created.Value.EnabledForEveryone.Should().BeFalse();
        (await _resolver.GetEnabledKeysForUserAsync(_account.Id)).Should().BeEmpty();
        (await Manage(FeatureFlagActions.EnableForAccounts, ["ACCOUNT@EXAMPLE.COM", "account@example.com"])).IsSuccess.Should().BeTrue();
        (await _resolver.GetEnabledKeysForUserAsync(_account.Id)).Should().Contain("flag");
        (await _resolver.GetEnabledKeysForUserAsync(_other.Id)).Should().BeEmpty();
        (await Manage(FeatureFlagActions.EnableForAccounts, ["account@example.com"])).IsSuccess.Should().BeTrue();
        (await _context.AppFeatureFlagAccounts.CountAsync()).Should().Be(1);
        var flags = (await _service.ListAsync(_admin.Id, CancellationToken.None)).Value;
        flags.Should().ContainSingle().Which.Should().BeEquivalentTo(new AdminFeatureFlag("flag", "A feature", false, null, ["account@example.com"]));

        await Manage(FeatureFlagActions.EnableForEveryone);
        (await _resolver.GetEnabledKeysForUserAsync(_other.Id)).Should().Contain("flag");
        await Manage(FeatureFlagActions.DisableForEveryone);
        (await _resolver.GetEnabledKeysForUserAsync(_other.Id)).Should().BeEmpty();
        (await _resolver.GetEnabledKeysForUserAsync(_account.Id)).Should().Contain("flag");
        await Manage(FeatureFlagActions.DisableForAccounts, ["ACCOUNT@EXAMPLE.COM"]);
        (await _resolver.GetEnabledKeysForUserAsync(_account.Id)).Should().BeEmpty();
        await Manage(FeatureFlagActions.DisableForAccounts, ["account@example.com"]);
        _context.AppFeatureFlagAccounts.Should().BeEmpty();
        _logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Log").Should().Be(7);
    }

    [Theory]
    [InlineData(FeatureFlagActions.EnableForAccounts)]
    [InlineData(FeatureFlagActions.DisableForAccounts)]
    public async Task UnknownEmails_AreReportedAndDoNotPartiallyWrite(string action)
    {
        await Manage(FeatureFlagActions.Create, description: "A feature");
        var result = await Manage(action, ["account@example.com", "UNKNOWN@example.com"]);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("unknown@example.com");
        _context.AppFeatureFlagAccounts.Should().BeEmpty();
    }

    [Fact]
    public async Task NonAdminAndRevokedAdmin_CannotReadOrWrite()
    {
        (await _service.ListAsync(_account.Id, CancellationToken.None)).IsFailure.Should().BeTrue();
        (await _service.ManageAsync(new(_account.Id, FeatureFlagActions.Create, "flag", Description: "Description"), CancellationToken.None))
            .IsFailure.Should().BeTrue();
        typeof(User).GetProperty(nameof(User.IsAdmin))!.SetValue(_admin, false);
        await _context.SaveChangesAsync();
        (await _service.ListAsync(_admin.Id, CancellationToken.None)).IsFailure.Should().BeTrue();
        (await Manage(FeatureFlagActions.Create, description: "Description")).IsFailure.Should().BeTrue();
        _context.AppFeatureFlags.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingAndDuplicateFlags_ReturnExplicitErrors()
    {
        (await Manage(FeatureFlagActions.EnableForEveryone)).ErrorCode.Should().Be(FeatureFlagActions.NotFound);
        await Manage(FeatureFlagActions.Create, description: "A feature");
        (await Manage(FeatureFlagActions.Create, description: "A feature")).ErrorCode.Should().Be(FeatureFlagActions.AlreadyExists);
    }

    private Task<Orbit.Domain.Common.Result<AdminFeatureFlag>> Manage(string action, string[]? emails = null, string? description = null) =>
        _service.ManageAsync(new ManageFeatureFlagsCommand(_admin.Id, action, "flag", emails, description), CancellationToken.None);

    public void Dispose()
    {
        _factory.Dispose();
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }
}
