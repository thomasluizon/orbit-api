using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class UserDeactivationQueryFilterTests
{
    private static User ActiveUser(string name, string email)
    {
        var user = User.Create(name, email).Value;
        user.SeedDefaultHandle();
        return user;
    }

    private static User DeactivatedUser(string name, string email)
    {
        var user = ActiveUser(name, email);
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        return user;
    }

    private static async Task<(OrbitDbContext Context, GenericRepository<User> Repository)> SeedAsync(
        params User[] users)
    {
        var context = CreateContext();
        context.Users.AddRange(users);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return (context, new GenericRepository<User>(context));
    }

    [Fact]
    public void Model_User_HasDeactivationQueryFilter()
    {
        using var context = CreateContext();

        context.Model.FindEntityType(typeof(User))!.GetQueryFilter().Should().NotBeNull();
    }

    [Fact]
    public async Task FindAsync_ById_ExcludesDeactivatedUser()
    {
        var active = ActiveUser("Active", "active@example.com");
        var deactivated = DeactivatedUser("Gone", "gone@example.com");
        var (context, repository) = await SeedAsync(active, deactivated);
        using var _ = context;

        var ids = new[] { active.Id, deactivated.Id };
        var found = await repository.FindAsync(u => ids.Contains(u.Id));

        found.Select(u => u.Id).Should().ContainSingle().Which.Should().Be(active.Id);
    }

    [Fact]
    public async Task FindAsync_ByPublicProfileSlug_ReturnsNothingForDeactivatedOwner()
    {
        var deactivated = DeactivatedUser("Gone", "slug@example.com");
        deactivated.SetPublicProfileSlug("PUBLICSLUG123456");
        var (context, repository) = await SeedAsync(deactivated);
        using var _ = context;

        var matches = await repository.FindAsync(u => u.PublicProfileSlug == "PUBLICSLUG123456");

        matches.Should().BeEmpty();
    }

    [Fact]
    public async Task FindAsync_MarketingAudience_ExcludesDeactivatedConsenters()
    {
        var active = ActiveUser("Active", "consent-active@example.com");
        active.SetMarketingConsent(true);
        var deactivated = DeactivatedUser("Gone", "consent-gone@example.com");
        deactivated.SetMarketingConsent(true);
        var (context, repository) = await SeedAsync(active, deactivated);
        using var _ = context;

        var audience = await repository.FindAsync(u => u.MarketingEmailConsent == true);

        audience.Select(u => u.Id).Should().Equal(active.Id);
    }

    [Fact]
    public async Task AnyAsync_And_CountAsync_ExcludeDeactivatedUser()
    {
        var deactivated = DeactivatedUser("Gone", "count@example.com");
        var (context, repository) = await SeedAsync(deactivated);
        using var _ = context;

        (await repository.AnyAsync(u => u.Id == deactivated.Id)).Should().BeFalse();
        (await repository.CountAsync(u => u.Id == deactivated.Id)).Should().Be(0);
    }

    [Fact]
    public async Task GetByIdAsync_ExcludesDeactivatedUser_SoCurrentUserPathsTreatThemAsGone()
    {
        var deactivated = DeactivatedUser("Gone", "byid@example.com");
        var (context, repository) = await SeedAsync(deactivated);
        using var _ = context;

        var found = await repository.GetByIdAsync(deactivated.Id);

        found.Should().BeNull();
    }

    [Fact]
    public async Task FindOneTrackedIgnoringFiltersAsync_ByEmail_ResolvesDeactivatedUserForReactivation()
    {
        var deactivated = DeactivatedUser("Gone", "login@example.com");
        var (context, repository) = await SeedAsync(deactivated);
        using var _ = context;

        var found = await repository.FindOneTrackedIgnoringFiltersAsync(u => u.Email == "login@example.com");

        found.Should().NotBeNull();
        found!.CancelDeactivation();
        found.IsDeactivated.Should().BeFalse();
    }

    [Fact]
    public async Task AnyIgnoringFiltersAsync_CountsDeactivatedUser()
    {
        var deactivated = DeactivatedUser("Gone", "guard@example.com");
        var (context, repository) = await SeedAsync(deactivated);
        using var _ = context;

        (await repository.AnyIgnoringFiltersAsync(u => u.Id == deactivated.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task IgnoreQueryFilters_FindsUsersScheduledForDeletion()
    {
        var active = ActiveUser("Active", "keep@example.com");
        var due = DeactivatedUser("DueForDeletion", "due@example.com");
        var (context, _) = await SeedAsync(active, due);
        using var _ctx = context;

        var scheduled = await context.Users
            .IgnoreQueryFilters()
            .Where(u => u.IsDeactivated && u.ScheduledDeletionAt.HasValue)
            .Select(u => u.Id)
            .ToListAsync();

        scheduled.Should().ContainSingle().Which.Should().Be(due.Id);
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
