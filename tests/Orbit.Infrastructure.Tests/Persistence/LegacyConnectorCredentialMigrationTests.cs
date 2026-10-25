using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class LegacyConnectorCredentialMigrationTests
{
    internal static readonly Guid LegacyKeyId = Guid.Parse("a427b057-ad89-40ff-a56c-87f4f3c51daa");
    private static readonly Guid PredecessorKeyId = Guid.Parse("84140ea7-8394-4a41-beb8-2d55de79e8d8");

    internal static (ApiKey Entity, string RawKey) CreateLegacyCredential(Guid userId,
        bool readOnly = false, IReadOnlyList<string>? scopes = null)
    {
        var (key, raw) = ApiKey.Create(userId, "Stored credential",
            scopes ?? [AgentScopes.DeleteHabits, AgentScopes.ReadHabits], isReadOnly: readOnly).Value;
        typeof(ApiKey).GetProperty(nameof(ApiKey.Id))!.SetValue(key, LegacyKeyId);
        typeof(ApiKey).GetProperty(nameof(ApiKey.Name))!.SetValue(key, "Claude.ai");
        return (key, raw);
    }

    internal static ApiKey MigrateCredential(ApiKey key)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        Seed(db, key);
        ApplyMigration(db);
        db.ChangeTracker.Clear();
        return db.ApiKeys.Single();
    }

    [Fact]
    public void Up_PromotesOnlyVerifiedActiveFamilylessConnector_AndIsIdempotent()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var userId = Guid.NewGuid();
        var legacy = CreateLegacyCredential(userId).Entity;
        var revoked = CreateLegacyCredential(userId).Entity;
        typeof(ApiKey).GetProperty(nameof(ApiKey.Id))!.SetValue(revoked, PredecessorKeyId);
        revoked.Revoke();
        var family = ApiKey.Create(userId, "Claude.ai", oauthFamilyId: Guid.NewGuid()).Value.Entity;
        var manual = ApiKey.Create(userId, "Manual key").Value.Entity;
        var sameNameManual = CreateLegacyCredential(Guid.NewGuid()).Entity;
        typeof(ApiKey).GetProperty(nameof(ApiKey.Id))!.SetValue(sameNameManual, Guid.NewGuid());
        ApiKey[] originals = [legacy, revoked, family, manual, sameNameManual];
        Seed(db, originals);

        ApplyMigration(db).Should().Be(1);
        ApplyMigration(db).Should().Be(0);
        db.ChangeTracker.Clear();

        var stored = db.ApiKeys.AsNoTracking().ToList();
        stored.Should().BeEquivalentTo(originals, options => options.ComparingByMembers<ApiKey>()
            .Excluding(key => key.OAuthFamilyId));
        stored.Single(key => key.Id == legacy.Id).OAuthFamilyId.Should().Be(legacy.Id);
        stored.Single(key => key.Id == family.Id).OAuthFamilyId.Should().Be(family.OAuthFamilyId);
        stored.Where(key => key.Id != legacy.Id && key.Id != family.Id)
            .Should().OnlyContain(key => key.OAuthFamilyId == null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Up_VerifiedIdWithExistingFamilyOrDifferentName_IsUnchanged(bool existingFamily)
    {
        var key = existingFamily
            ? ApiKey.Create(Guid.NewGuid(), "Claude.ai", oauthFamilyId: Guid.NewGuid()).Value.Entity
            : ApiKey.Create(Guid.NewGuid(), "Manual key").Value.Entity;
        typeof(ApiKey).GetProperty(nameof(ApiKey.Id))!.SetValue(key, LegacyKeyId);

        MigrateCredential(key).Should().BeEquivalentTo(key, options => options.ComparingByMembers<ApiKey>());
    }

    [Fact]
    public void Down_DoesNotRemoveCredentialProvenance()
    {
        new RecognizeVerifiedLegacyConnectorCredential().DownOperations.Should().BeEmpty();
    }

    private static int ApplyMigration(OrbitDbContext db) =>
        new RecognizeVerifiedLegacyConnectorCredential().UpOperations.OfType<SqlOperation>()
            .Sum(operation => db.Database.ExecuteSqlRaw(operation.Sql));

    private static void Seed(OrbitDbContext db, params ApiKey[] keys)
    {
        foreach (var userId in keys.Select(key => key.UserId).Distinct())
        {
            var user = User.Create("Connector", $"{userId}@example.com").Value;
            typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, userId);
            db.Users.Add(user);
        }
        db.ApiKeys.AddRange(keys);
        db.SaveChanges();
    }
}
