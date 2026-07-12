using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NSubstitute;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class FriendGraphServiceDeactivationTests
{
    [Fact]
    public async Task ResolveTargetAsync_ByHandle_ReturnsNullForDeactivatedUser()
    {
        var deactivated = DeactivatedUser("Gone", "handle@example.com", "gonehandle");
        var service = await CreateServiceAsync(deactivated);

        var resolved = await service.ResolveTargetAsync("gonehandle", null, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTargetAsync_ByReferralCode_ReturnsNullForDeactivatedUser()
    {
        var deactivated = DeactivatedUser("Gone", "referral@example.com", "gonehandle2");
        deactivated.SetReferralCode("REF12345");
        var service = await CreateServiceAsync(deactivated);

        var resolved = await service.ResolveTargetAsync(null, "REF12345", CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTargetAsync_ByHandle_ResolvesActiveUser()
    {
        var active = ActiveUser("Active", "active@example.com", "activehandle");
        var service = await CreateServiceAsync(active);

        var resolved = await service.ResolveTargetAsync("activehandle", null, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(active.Id);
    }

    [Fact]
    public async Task ResolveTargetAsync_ByReferralCode_ResolvesActiveUser()
    {
        var active = ActiveUser("Active", "active-ref@example.com", "activehandle2");
        active.SetReferralCode("ACTIVE01");
        var service = await CreateServiceAsync(active);

        var resolved = await service.ResolveTargetAsync(null, "ACTIVE01", CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(active.Id);
    }

    private static User ActiveUser(string name, string email, string handle)
    {
        var user = User.Create(name, email).Value;
        user.SetHandle(handle).IsSuccess.Should().BeTrue();
        return user;
    }

    private static User DeactivatedUser(string name, string email, string handle)
    {
        var user = ActiveUser(name, email, handle);
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        return user;
    }

    private static async Task<FriendGraphService> CreateServiceAsync(params User[] users)
    {
        var context = CreateContext();
        context.Users.AddRange(users);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return new FriendGraphService(
            new GenericRepository<User>(context),
            Substitute.For<IGenericRepository<Friendship>>(),
            Substitute.For<IGenericRepository<BlockedUser>>());
    }

    private static OrbitDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ReplaceService<IModelCacheKeyFactory, EncryptionAwareModelCacheKeyFactory>()
            .Options;

        return new OrbitDbContext(options);
    }

    private sealed class EncryptionAwareModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
        {
            var hasEncryption = context is OrbitDbContext orbit && orbit.HasEncryptionService;
            return (context.GetType(), hasEncryption, designTime);
        }
    }
}
