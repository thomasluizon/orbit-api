using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Common;

/// <summary>
/// Validates that ErrorCodes and ErrorMessages are consistent
/// and follow the expected naming/value patterns.
/// </summary>
public class ErrorCodesAndMessagesTests
{
    [Fact]
    public void ErrorCodes_AllConstantsAreUpperSnakeCase()
    {
        var fields = typeof(ErrorCodes).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        foreach (var field in fields)
        {
            var value = (string)field.GetValue(null)!;
            value.Should().MatchRegex(@"^[A-Z_]+$",
                $"ErrorCode '{field.Name}' should be UPPER_SNAKE_CASE but was '{value}'");
        }
    }

    [Fact]
    public void ErrorMessages_AllConstantsEndWithPeriodOrHaveNoTrailingSpace()
    {
        var fields = typeof(ErrorMessages).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        foreach (var field in fields)
        {
            var value = ((AppError)field.GetValue(null)!).Message;
            value.Should().NotEndWith(" ",
                $"ErrorMessage '{field.Name}' should not end with a space");
        }
    }

    [Fact]
    public void ErrorCodes_AllNonEmpty()
    {
        var fields = typeof(ErrorCodes).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        foreach (var field in fields)
        {
            var value = (string)field.GetValue(null)!;
            value.Should().NotBeNullOrWhiteSpace(
                $"ErrorCode '{field.Name}' should not be empty");
        }
    }

    [Fact]
    public void ErrorMessages_AllNonEmpty()
    {
        var fields = typeof(ErrorMessages).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        foreach (var field in fields)
        {
            var value = ((AppError)field.GetValue(null)!).Message;
            value.Should().NotBeNullOrWhiteSpace(
                $"ErrorMessage '{field.Name}' should not be empty");
        }
    }

    [Fact]
    public void ErrorCodes_NoDuplicateValues()
    {
        var fields = typeof(ErrorCodes).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        var values = fields.Select(f => (string)f.GetValue(null)!).ToList();
        values.Should().OnlyHaveUniqueItems("each error code should be unique");
    }

    [Fact]
    public void ErrorMessages_EveryCodeIsAKnownErrorCodeOrDomainError()
    {
        var knownCodes = GetStringConstants(typeof(ErrorCodes))
            .Concat(GetAppErrorCodes(typeof(DomainErrors)))
            .ToHashSet();

        var messageFields = typeof(ErrorMessages).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        foreach (var field in messageFields)
        {
            var code = ((AppError)field.GetValue(null)!).Code;
            knownCodes.Should().Contain(code,
                $"ErrorMessage '{field.Name}' uses code '{code}', which must be declared in ErrorCodes or DomainErrors");
        }
    }

    private static IEnumerable<string> GetStringConstants(Type type) =>
        type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!);

    private static IEnumerable<string> GetAppErrorCodes(Type type) =>
        type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(AppError))
            .Select(f => ((AppError)f.GetValue(null)!).Code);

    [Fact]
    public void AppConstants_SupportedLanguages_ContainsEnglishAndPortuguese()
    {
        AppConstants.SupportedLanguages.Should().Contain("en");
        AppConstants.SupportedLanguages.Should().Contain("pt-BR");
    }

    [Fact]
    public void AppConstants_Limits_ArePositive()
    {
        AppConstants.MaxSubHabits.Should().BePositive();
        AppConstants.MaxHabitTitleLength.Should().BePositive();
        AppConstants.MaxHabitDescriptionLength.Should().BePositive();
        AppConstants.MaxChecklistItemTextLength.Should().BePositive();
        AppConstants.MaxHabitDepth.Should().BePositive();
        AppConstants.MaxTagsPerHabit.Should().BePositive();
        AppConstants.MaxUserFacts.Should().BePositive();
        AppConstants.MaxRangeDays.Should().BePositive();
        AppConstants.MaxBulkOperationSize.Should().BePositive();
        AppConstants.MaxGoalsPerHabit.Should().BePositive();
        AppConstants.MaxHabitsPerGoal.Should().BePositive();
        AppConstants.MaxPushSubscriptionsPerUser.Should().BePositive();
        AppConstants.MaxNotificationsReturned.Should().BePositive();
        AppConstants.MaxChecklistItems.Should().BePositive();
        AppConstants.MaxTagNameLength.Should().BePositive();
        AppConstants.MaxPageSize.Should().BePositive();
        AppConstants.DefaultOverdueWindowDays.Should().BePositive();
    }

    [Fact]
    public void PaginatedResponse_CanBeCreated()
    {
        var response = new PaginatedResponse<string>(
            Items: new List<string> { "a", "b" },
            Page: 1,
            PageSize: 10,
            TotalCount: 2,
            TotalPages: 1);

        response.Items.Should().HaveCount(2);
        response.Page.Should().Be(1);
        response.PageSize.Should().Be(10);
        response.TotalCount.Should().Be(2);
        response.TotalPages.Should().Be(1);
    }
}
