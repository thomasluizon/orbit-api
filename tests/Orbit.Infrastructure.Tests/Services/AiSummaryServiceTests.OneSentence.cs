using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Services;

public partial class AiSummaryServiceTests
{
    [Theory]
    [InlineData("en", "Your morning routine is nearly done. Next is your psyllium dose.", "Your morning routine is nearly done; next is your psyllium dose.")]
    [InlineData("pt-BR", "Sua rotina da manhã está quase pronta. O próximo passo é tomar psyllium.", "Sua rotina da manhã está quase pronta; o próximo passo é tomar psyllium.")]
    [InlineData("en", "Your morning routine is nearly done? Next is your psyllium dose.", "Your morning routine is nearly done, with your psyllium dose next.")]
    [InlineData("en", "Your morning routine is nearly done! Next is your psyllium dose.", "Your morning routine is nearly done, with your psyllium dose next.")]
    [InlineData("en", "Your morning routine is nearly done.Next is your psyllium dose.", "Your morning routine is nearly done, with your psyllium dose next.")]
    [InlineData("en", "Your morning routine is nearly done.\nNext is your psyllium dose.", "Your morning routine is nearly done, with your psyllium dose next.")]
    [InlineData("en", "**Your morning routine is nearly done.** Next is your psyllium dose.", "Your morning routine is nearly done, with your psyllium dose next.")]
    public async Task DailySummary_MultipleSentencesRetryThenAcceptOneSentence(string language, string invalid, string valid)
    {
        var (habits, context) = await OwnerDay(language);
        var next = DailySummaryPendingProjection.SelectNextStep(context.PendingHabits)!;
        var calls = 0;
        var service = BuildSummaryService(attempt => JsonSerializer.Serialize(new
        {
            nextStepHabitId = next.Id,
            summary = attempt == 1 ? invalid : valid
        }), _ => calls++);

        var result = await service.GenerateSummaryAsync(habits, context);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Summary.Should().Be(valid);
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData("en", "Your morning routine is nearly done. Next is your psyllium dose.")]
    [InlineData("pt-BR", "Sua rotina da manhã está quase pronta. O próximo passo é tomar psyllium.")]
    public async Task DailySummary_MultipleSentencesTwiceReturnUnavailable(string language, string summary)
    {
        var (habits, context) = await OwnerDay(language);
        var next = DailySummaryPendingProjection.SelectNextStep(context.PendingHabits)!;
        var calls = 0;
        var service = BuildSummaryService(JsonSerializer.Serialize(new { nextStepHabitId = next.Id, summary }), _ => calls++);

        var result = await service.GenerateSummaryAsync(habits, context);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiSummaryUnavailable.Message);
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData("en", "Psyllium 5.5g", "Your routine is nearly done, with Psyllium 5.5g next.", true)]
    [InlineData("pt-BR", "Psyllium 5.5g", "Sua rotina está quase pronta, com Psyllium 5.5g a seguir.", true)]
    [InlineData("en", "Psyllium 5.5g", "Psyllium 5.5g is next.", true)]
    [InlineData("en", "Psyllium 5.5g", "Psyllium 5.5g is next?", true)]
    [InlineData("en", "Psyllium 5.5g", "Psyllium 5.5g is next!", true)]
    [InlineData("en", "Psyllium 5.5g", "Your routine is nearly done. Psyllium 5.5g is next.", false)]
    [InlineData("en", "Psyllium 5.5g", "Psyllium 5.5g is next. Your routine is nearly done.", false)]
    [InlineData("en", "Psyllium 5.5g", "Your routine is nearly done, with Psyllium 6.5g next.", false)]
    [InlineData("en", "Read. Rest", "Your next step is Read. Rest.", false)]
    public async Task DailySummary_OnlyDecimalsInsideExactStoredTitlesAreExempt(string language, string title, string summary, bool accepted)
    {
        var habit = CreateHabit(title);
        var context = new DailySummaryContext(Today, Today, Today, language, null, 0, 0, new Dictionary<Guid, DateOnly>());
        var calls = 0;
        var service = BuildSummaryService(JsonSerializer.Serialize(new { summary, nextStepHabitId = (Guid?)null }), _ => calls++);

        var result = await service.GenerateSummaryAsync([habit], context);

        result.IsSuccess.Should().Be(accepted);
        calls.Should().Be(accepted ? 1 : 2);
        if (accepted)
            result.Value.Summary.Should().Be(summary);
        else
            result.Error.Should().Be(ErrorMessages.AiSummaryUnavailable.Message);
    }
}
