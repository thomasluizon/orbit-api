using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class OrbitDbContextTests
{
    [Fact]
    public void Model_WithoutEncryptionService_UsesValueConvertersForSerializedCollections()
    {
        using var context = CreateContext();

        var habit = context.Model.FindEntityType(typeof(Habit))!;
        var daysConverter = habit.FindProperty(nameof(Habit.Days))!.GetValueConverter();

        daysConverter.Should().NotBeNull();
        daysConverter!.ConvertToProvider(new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday })
            .Should().Be("[1,3]");
        habit.FindProperty(nameof(Habit.ChecklistItems))!.GetValueComparer().Should().NotBeNull();
        habit.FindProperty(nameof(Habit.ReminderTimes))!.GetValueComparer().Should().NotBeNull();
        habit.FindProperty(nameof(Habit.ScheduledReminders))!.GetValueComparer().Should().NotBeNull();
    }

    [Fact]
    public void Model_WithEncryptionService_ConfiguresEncryptedValueConverters()
    {
        var encryptionService = Substitute.For<IEncryptionService>();
        encryptionService.Encrypt(Arg.Any<string>()).Returns(call => call.Arg<string>());
        encryptionService.Decrypt(Arg.Any<string>()).Returns(call => call.Arg<string>());
        encryptionService.EncryptNullable(Arg.Any<string?>()).Returns(call => call.Arg<string?>());
        encryptionService.DecryptNullable(Arg.Any<string?>()).Returns(call => call.Arg<string?>());

        using var context = CreateContext(encryptionService);

        context.Model.FindEntityType(typeof(User))!.FindProperty(nameof(User.GoogleAccessToken))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(Habit))!.FindProperty(nameof(Habit.Title))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(Habit))!.FindProperty(nameof(Habit.Description))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(HabitLog))!.FindProperty(nameof(HabitLog.Note))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(UserFact))!.FindProperty(nameof(UserFact.FactText))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(Goal))!.FindProperty(nameof(Goal.Title))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(GoalProgressLog))!.FindProperty(nameof(GoalProgressLog.Note))!.GetValueConverter().Should().NotBeNull();
    }

    [Fact]
    public void Model_WithPostgresProvider_UsesArrayAndJsonbColumnMetadata()
    {
        using var context = CreatePostgresContext();

        var habit = context.Model.FindEntityType(typeof(Habit))!;

        habit.FindProperty(nameof(Habit.Days))!.GetColumnType().Should().Be("text[]");
        habit.FindProperty(nameof(Habit.ChecklistItems))!.GetDefaultValueSql().Should().Be("'[]'::jsonb");
        habit.FindProperty(nameof(Habit.ReminderTimes))!.GetDefaultValueSql().Should().Be("'[15]'::jsonb");
        habit.FindProperty(nameof(Habit.ScheduledReminders))!.GetDefaultValueSql().Should().Be("'[]'::jsonb");
    }

    private static OrbitDbContext CreateContext(IEncryptionService? encryptionService = null)
    {
        // EF Core caches the compiled model per DbContext type by default. Since these
        // tests build the model differently based on whether an encryption service is
        // injected, we replace the model cache key factory so each configuration gets
        // its own cached model and tests don't leak state through ordering.
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ReplaceService<IModelCacheKeyFactory, EncryptionAwareModelCacheKeyFactory>()
            .Options;

        return new OrbitDbContext(options, encryptionService);
    }

    private sealed class EncryptionAwareModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
        {
            var hasEncryption = context is OrbitDbContext orbit && orbit.HasEncryptionService;
            return (context.GetType(), hasEncryption, designTime);
        }
    }

    private static OrbitDbContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=localhost;Database=orbit-tests;Username=test;Password=test")
            .Options;

        return new OrbitDbContext(options);
    }
}
