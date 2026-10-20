using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Api.Controllers;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class HabitEnumContractTests
{
    public static IEnumerable<object[]> AcceptedInputs()
    {
        foreach (var type in new[] { typeof(FrequencyUnit), typeof(DayOfWeek) })
        {
            var tokens = Enum.GetNames(type).SelectMany(name => new[]
                { name, name.ToLowerInvariant(), name.ToUpperInvariant(), " " + name + " ", "\u0085" + name + "\u0085" })
                .Select(name => JsonSerializer.Serialize(name))
                .Concat(Enum.GetValues(type).Cast<object>().Select(value => Convert.ToInt32(value).ToString()))
                .Concat(["99", "-1", "2147483647", "-2147483648", "\"0\"", "\"+0001\"", "\" -1 \"", "\"99\""])
                .Concat(type == typeof(FrequencyUnit)
                    ? ["\"dAy\"", "\" Day, wEEk \"", "\"Day,\"", "\"Day,  \""]
                    : ["\"mOnDaY\"", "\" Monday, SUNDAY \"", "\"Monday,\"", "\"Monday,  \""]);
            foreach (var token in tokens)
                yield return type == typeof(FrequencyUnit) ? [token, "[]"] : ["\"Day\"", $"[{token}]"];
        }
        yield return ["null", "[]"];
        yield return ["null", "null"];
    }

    [Theory]
    [MemberData(nameof(AcceptedInputs))]
    public void MvcRequests_PreserveBindingAcrossEveryOwningRequest(string frequency, string days)
    {
        var json = RequestJson(frequency, days);
        var expectedFrequency = JsonSerializer.Deserialize<FrequencyUnit?>(frequency, Mvc);
        var expectedDays = JsonSerializer.Deserialize<DayOfWeek[]?>(days, Mvc);
        foreach (var type in RequestTypes)
        {
            var bound = JsonSerializer.Deserialize(json, type, Mvc)!;
            type.GetProperty("FrequencyUnit")!.GetValue(bound).Should().Be(expectedFrequency);
            ((IReadOnlyList<DayOfWeek>?)type.GetProperty("Days")!.GetValue(bound)).Should().BeEquivalentTo(expectedDays);
        }
        var bulk = JsonSerializer.Deserialize<HabitsController.BulkCreateHabitsRequest>(
            $$"""{"habits":[{{json[..^1]}},"subHabits":[{{json}}]}]}""", Mvc)!;
        bulk.Habits.Single().SubHabits!.Single().FrequencyUnit.Should().Be(expectedFrequency);
        var onboarding = JsonSerializer.Deserialize<ProfileController.ApplyOnboardingRequest>(
            $$"""{"habits":[{{json}}]}""", Mvc)!;
        onboarding.Habits!.Single().Days.Should().BeEquivalentTo(expectedDays);
    }

    [Theory]
    [InlineData("\"Unknown\"")]
    [InlineData("\"\"")]
    [InlineData("\"Day,,Week\"")]
    [InlineData("\"1,2\"")]
    [InlineData("\"2147483648\"")]
    [InlineData("2147483648")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("{}")]
    public void MvcRequests_KeepRejectingUnbindableInputs(string token)
    {
        foreach (var type in RequestTypes)
        {
            Action frequency = () => JsonSerializer.Deserialize(RequestJson(token, "[]"), type, Mvc);
            Action days = () => JsonSerializer.Deserialize(RequestJson("\"Day\"", $"[{token}]"), type, Mvc);
            frequency.Should().Throw<JsonException>();
            days.Should().Throw<JsonException>();
            Action nullDay = () => JsonSerializer.Deserialize(RequestJson("null", "[null]"), type, Mvc);
            nullDay.Should().Throw<JsonException>();
        }
    }

    [Fact]
    public void MvcRequests_OmittedNullAndEmptyCadenceKeepTheirMeaning()
    {
        foreach (var type in RequestTypes)
        {
            foreach (var json in new[] { "{\"title\":\"Demo\"}", RequestJson("null", "null"), RequestJson("null", "[]") })
            {
                var bound = JsonSerializer.Deserialize(json, type, Mvc)!;
                type.GetProperty("FrequencyUnit")!.GetValue(bound).Should().BeNull();
                var days = (IReadOnlyList<DayOfWeek>?)type.GetProperty("Days")!.GetValue(bound);
                if (json.Contains("[]", StringComparison.Ordinal)) days.Should().BeEmpty();
                else days.Should().BeNull();
            }
        }
    }

    [Theory]
    [InlineData("\"dAy\"", "[\" mOnDaY \"]", true)]
    [InlineData("99", "[]", true)]
    [InlineData("-1", "[]", true)]
    [InlineData("\"Day\"", "[99,-1]", true)]
    [InlineData("\"Week\"", "[\"Monday\"]", false)]
    [InlineData("null", "[]", true)]
    public async Task BoundValues_KeepValidatorAndDomainResults(string frequency, string days, bool valid)
    {
        var json = RequestJson(frequency, days);
        var request = JsonSerializer.Deserialize<HabitsController.CreateHabitRequest>(json, Mvc)!;
        var user = Guid.NewGuid();
        var quantity = request.FrequencyUnit.HasValue ? 1 : (int?)null;
        var unit = request.FrequencyUnit;
        var boundDays = request.Days;
        new CreateHabitCommandValidator().Validate(new CreateHabitCommand(user, request.Title, null, unit, quantity,
            Options: new HabitCommandOptions(Days: boundDays))).IsValid.Should().Be(valid);
        (await new UpdateHabitCommandValidator(Substitute.For<IGenericRepository<Habit>>()).ValidateAsync(new UpdateHabitCommand(user, Guid.NewGuid(), request.Title, null, unit, quantity,
            Options: new UpdateHabitCommandOptions(Days: boundDays)))).IsValid.Should().Be(valid);
        new CreateSubHabitCommandValidator().Validate(new CreateSubHabitCommand(user, Guid.NewGuid(), request.Title, null, unit, quantity,
            Options: new HabitCommandOptions(Days: boundDays))).IsValid.Should().Be(valid);
        new BulkCreateHabitsCommandValidator().Validate(new BulkCreateHabitsCommand(user,
            [new BulkHabitItem(request.Title, null, unit, quantity, Days: boundDays)])).IsValid.Should().BeTrue();
        new ApplyHabitInputValidator().Validate(new ApplyHabitInput(request.Title, null, null, unit, quantity, Days: boundDays))
            .IsValid.Should().Be(valid);
        Habit.Create(new HabitCreateParams(user, request.Title, unit, quantity, new DateOnly(2026, 10, 21), Days: boundDays))
            .IsSuccess.Should().Be(valid);
    }

    private static string RequestJson(string frequency, string days) =>
        $$"""{"title":"Demo","frequencyUnit":{{frequency}},"frequencyQuantity":1,"days":{{days}}}""";
}
