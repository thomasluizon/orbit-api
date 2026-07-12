using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

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
    public void Model_WithEncryptionService_WiresUserGoogleTokensToNullableEncryptingConverter()
    {
        var encryptionService = Substitute.For<IEncryptionService>();

        using var context = CreateContext(encryptionService);
        var user = context.Model.FindEntityType(typeof(User))!;

        user.FindProperty(nameof(User.GoogleAccessToken))!.GetValueConverter()
            .Should().BeOfType<NullableEncryptionValueConverter>();
        user.FindProperty(nameof(User.GoogleRefreshToken))!.GetValueConverter()
            .Should().BeOfType<NullableEncryptionValueConverter>();
    }

    [Fact]
    public void NullableEncryptingConverter_WithRealEncryption_ProducesCiphertextAtRestAndDecryptsOnRead()
    {
        var encryptionService = new EncryptionService(
            Options.Create(new EncryptionSettings { Key = "DdyUCjjdK326cB9lY00tyUvRDpCQcYJOJIpu21I1D8c=" }),
            NullLogger<EncryptionService>.Instance);
        var converter = new NullableEncryptionValueConverter(encryptionService);

        const string plaintext = "ya29.PLAINTEXT-GOOGLE-TOKEN";
        var atRest = (string?)converter.ConvertToProvider(plaintext);

        atRest.Should().StartWith("enc:").And.NotBe(plaintext);
        converter.ConvertFromProvider(atRest).Should().Be(plaintext);
        converter.ConvertToProvider(null).Should().BeNull();
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

    [Fact]
    public void Model_SentReminder_UniqueIndexIncludesReminderTimeUtcAndWhen()
    {
        using var context = CreateContext();

        var sentReminder = context.Model.FindEntityType(typeof(SentReminder))!;
        var uniqueIndex = sentReminder.GetIndexes().Single(i => i.IsUnique);

        uniqueIndex.Properties.Select(p => p.Name).Should().Equal(
            nameof(SentReminder.HabitId),
            nameof(SentReminder.Date),
            nameof(SentReminder.MinutesBefore),
            nameof(SentReminder.ReminderTimeUtc),
            nameof(SentReminder.When));
    }

    [Fact]
    public void Model_GoogleEventIdColumns_AreWidenedForLongCalendarEventIds()
    {
        using var context = CreateContext();

        var habitGoogleEventId = context.Model.FindEntityType(typeof(Habit))!
            .FindProperty(nameof(Habit.GoogleEventId))!;
        var suggestionGoogleEventId = context.Model.FindEntityType(typeof(GoogleCalendarSyncSuggestion))!
            .FindProperty(nameof(GoogleCalendarSyncSuggestion.GoogleEventId))!;

        habitGoogleEventId.GetMaxLength().Should().Be(1024);
        suggestionGoogleEventId.GetMaxLength().Should().Be(1024);
    }

    [Fact]
    public void Model_TagUniqueIndex_FiltersSoftDeletedRowsSoNamesCanBeReusedAfterDeletion()
    {
        using var context = CreatePostgresContext();

        var tag = context.Model.FindEntityType(typeof(Tag))!;
        var uniqueIndex = tag.GetIndexes().Single(i =>
            i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(Tag.UserId), nameof(Tag.Name) }));

        uniqueIndex.GetFilter().Should().Be("\"IsDeleted\" = FALSE");
    }

    [Fact]
    public void Model_UnboundedStringColumns_AreConstrainedToMaxLengths()
    {
        using var context = CreatePostgresContext();

        var tag = context.Model.FindEntityType(typeof(Tag))!;
        tag.FindProperty(nameof(Tag.Name))!.GetMaxLength().Should().Be(50);
        tag.FindProperty(nameof(Tag.Color))!.GetMaxLength().Should().Be(50);

        var user = context.Model.FindEntityType(typeof(User))!;
        user.FindProperty(nameof(User.ColorScheme))!.GetMaxLength().Should().Be(50);
        user.FindProperty(nameof(User.Language))!.GetMaxLength().Should().Be(10);
        user.FindProperty(nameof(User.ThemePreference))!.GetMaxLength().Should().Be(10);
        user.FindProperty(nameof(User.TimeZone))!.GetMaxLength().Should().Be(100);

        context.Model.FindEntityType(typeof(Goal))!.FindProperty(nameof(Goal.Unit))!.GetMaxLength().Should().Be(50);
    }

    [Fact]
    public void Model_EncryptedTextColumns_CarryAdvisoryMaxLengthWithoutNarrowingStorageType()
    {
        var encryptionService = Substitute.For<IEncryptionService>();

        using var context = CreatePostgresContext(encryptionService);

        var goalTitle = context.Model.FindEntityType(typeof(Goal))!.FindProperty(nameof(Goal.Title))!;
        goalTitle.GetMaxLength().Should().Be(200);
        goalTitle.GetColumnType().Should().Be("text");
        goalTitle.GetValueConverter().Should().NotBeNull();

        var note = context.Model.FindEntityType(typeof(HabitLog))!.FindProperty(nameof(HabitLog.Note))!;
        note.GetMaxLength().Should().Be(1000);
        note.GetColumnType().Should().Be("text");
        note.GetValueConverter().Should().NotBeNull();
    }

    [Fact]
    public void Model_HabitsAndTags_CascadeDeleteWithTheirOwningUser()
    {
        using var context = CreateContext();

        AssertCascadingUserForeignKey(context.Model.FindEntityType(typeof(Habit))!, nameof(Habit.UserId));
        AssertCascadingUserForeignKey(context.Model.FindEntityType(typeof(Tag))!, nameof(Tag.UserId));
    }

    private static void AssertCascadingUserForeignKey(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType,
        string foreignKeyPropertyName)
    {
        var userForeignKey = entityType.GetForeignKeys().Single(fk =>
            fk.PrincipalEntityType.ClrType == typeof(User)
            && fk.Properties.Count == 1
            && fk.Properties[0].Name == foreignKeyPropertyName);

        userForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }

    private static OrbitDbContext CreateContext(IEncryptionService? encryptionService = null)
    {
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

    private static OrbitDbContext CreatePostgresContext(IEncryptionService? encryptionService = null)
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=localhost;Database=orbit-tests;Username=test;Password=test")
            .ReplaceService<IModelCacheKeyFactory, EncryptionAwareModelCacheKeyFactory>()
            .Options;

        return new OrbitDbContext(options, encryptionService);
    }
}
