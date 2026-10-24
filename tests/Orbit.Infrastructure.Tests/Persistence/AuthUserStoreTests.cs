using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AuthUserStoreTests
{
    [Fact]
    public async Task NewAndExistingAccounts_ExecuteIdenticalSqlInOrder()
    {
        var commands = new CommandRecorder();
        using var database = new SqliteOrbitDbContextFactory(commands);
        var context = database.Context;
        var store = new AuthUserStore(context);
        var candidate = Candidate("New", "pt-BR");
        commands.Sql.Clear();

        var first = await store.UpsertAsync(candidate, CancellationToken.None);
        await context.SaveChangesAsync();
        first.ProactiveAstraEnabled.Should().Be(true);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT \"ProactiveAstraEnabled\" FROM \"Users\"";
            Convert.ToBoolean(await command.ExecuteScalarAsync()).Should().BeTrue();
        }
        var signupSql = commands.Sql.ToArray();
        commands.Sql.Clear();
        var returning = await store.UpsertAsync(Candidate("Replacement", "en"), CancellationToken.None);
        await context.SaveChangesAsync();

        commands.Sql.Should().Equal(signupSql);
        signupSql.Should().HaveCount(3);
        signupSql[0].Should().Contain("ON CONFLICT (\"Email\") DO UPDATE");
        signupSql[0].Should().NotContain("\"Handle\"");
        signupSql.Should().OnlyContain(sql => !sql.Contains("\"Handle\"", StringComparison.Ordinal));
        signupSql[1].Should().StartWith("SELECT");
        signupSql[2].Should().StartWith("UPDATE");
        returning.Should().BeEquivalentTo(first);
        returning.Should().BeEquivalentTo(candidate);
        (await context.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Insert_IncludesEveryRequiredMappedColumnWithoutDatabaseDefault()
    {
        var commands = new CommandRecorder();
        using var database = new SqliteOrbitDbContextFactory(commands);
        commands.Sql.Clear();

        await new AuthUserStore(database.Context).UpsertAsync(Candidate("New", "pt-BR"), CancellationToken.None);

        var entity = database.Context.Model.FindEntityType(typeof(User))!;
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var requiredColumns = entity.GetProperties()
            .Where(property => !property.IsNullable && property.GetBeforeSaveBehavior() == PropertySaveBehavior.Save
                && property.GetDefaultValueSql() is null && !property.TryGetDefaultValue(out _))
            .Select(property => property.GetColumnName(table)!)
            .ToArray();
        var insert = commands.Sql.Single(sql => sql.StartsWith("INSERT", StringComparison.Ordinal));
        var columns = insert[(insert.IndexOf('(') + 1)..insert.IndexOf(')')]
            .Split(',').Select(column => column.Trim().Trim('"'));

        columns.Should().Contain(requiredColumns);
    }

    [Fact]
    public async Task MappedInsert_DoesNotReferenceHandle()
    {
        var commands = new CommandRecorder();
        using var database = new SqliteOrbitDbContextFactory(commands);
        var user = Candidate("New", "pt-BR");
        commands.Sql.Clear();

        database.Context.Users.Add(user);
        await database.Context.SaveChangesAsync();

        commands.Sql.Should().ContainSingle().Which.Should().NotContain("\"Handle\"");
        database.Context.ChangeTracker.Clear();
        (await database.Context.Users.SingleAsync()).Name.Should().Be("New");
        commands.Sql.Should().OnlyContain(sql => !sql.Contains("\"Handle\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MappedUpdate_DoesNotReferenceHandle(bool updateWholeEntity)
    {
        var commands = new CommandRecorder();
        using var database = new SqliteOrbitDbContextFactory(commands);
        var context = database.Context;
        var user = Candidate("Original", "pt-BR");
        context.Users.Add(user);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        user = await context.Users.SingleAsync();
        user.SetLanguage("en");
        if (updateWholeEntity)
            context.Users.Update(user);
        commands.Sql.Clear();

        await context.SaveChangesAsync();

        commands.Sql.Should().ContainSingle().Which.Should().NotContain("\"Handle\"");
        context.ChangeTracker.Clear();
        user = await context.Users.SingleAsync();
        user.Language.Should().Be("en");
        commands.Sql.Should().OnlyContain(sql => !sql.Contains("\"Handle\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignInWithoutHandleColumn_ProfileRetainsNullHandleKey()
    {
        var commands = new CommandRecorder();
        using var database = new SqliteOrbitDbContextFactory(commands);
        var context = database.Context;
        var store = new AuthUserStore(context);
        await store.UpsertAsync(Candidate("Original", "pt-BR"), CancellationToken.None);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var returning = await store.UpsertAsync(Candidate("Replacement", "en"), CancellationToken.None);
        await context.SaveChangesAsync();
        var users = new GenericRepository<User>(context);
        var dateService = Substitute.For<IUserDateService>();
        dateService.GetUserTodayAsync(returning.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(returning.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        var handler = new GetProfileQueryHandler(users, new GenericRepository<ApiKey>(context),
            new GenericRepository<StreakFreeze>(context), Substitute.For<IHabitLogReader>(),
            new ProfileStreakServices(dateService, Substitute.For<IUserStreakService>()),
            flags, Substitute.For<IPayGateService>());

        var profile = await handler.Handle(new GetProfileQuery(returning.Id), CancellationToken.None);

        profile.IsSuccess.Should().BeTrue();
        var payload = JsonSerializer.SerializeToElement(profile.Value, JsonSerializerOptions.Web);
        payload.GetProperty("handle").ValueKind.Should().Be(JsonValueKind.Null);
        payload.GetProperty("name").GetString().Should().Be("Original");
        (await context.Users.AsNoTracking().SingleAsync()).Id.Should().Be(returning.Id);
        commands.Sql.Should().OnlyContain(sql => !sql.Contains("\"Handle\"", StringComparison.Ordinal));
    }

    [Fact]
    public void RemovedHandleMapping_HasNoPendingMigration()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);

        context.Model.FindEntityType(typeof(User))!.FindProperty("Handle").Should().BeNull();
        typeof(User).GetProperty("Handle").Should().BeNull();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task Conflict_PreservesDeactivatedAccountAndItsPreferences()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var context = database.Context;
        var original = Candidate("Original", "pt-BR");
        original.SetGoogleTokens("calendar-access", "calendar-refresh");
        original.SetProactiveAstraEnabled(false);
        original.Deactivate(DateTime.UtcNow.AddDays(7));
        context.Users.Add(original);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var user = await new AuthUserStore(context).UpsertAsync(Candidate("Replacement", "en"), CancellationToken.None);

        user.Should().BeEquivalentTo(original);
        context.Entry(user).Property(u => u.Email).IsModified.Should().BeTrue();
    }

    private static User Candidate(string name, string language)
    {
        var user = User.Create(name, "person@example.com").Value;
        user.SetLanguage(language);
        user.BeginSignup(language, name);
        return user;
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
